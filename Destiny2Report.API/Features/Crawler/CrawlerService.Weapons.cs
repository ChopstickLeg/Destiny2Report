using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using BungiePlayer = D2Report.BungieClient.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private const long GrenadeKillsReferenceId = -1;
    private const long MeleeKillsReferenceId = -2;
    private const long SuperKillsReferenceId = -3;
    private const string AbilityCategoryName = "Abilities";
    private const string AbilityCategoryKey = "ABILITIES";
    private const string UnknownWeaponCategoryName = "Unknown";

    private async Task<WeaponDefinitionSummary?> GetInventoryItemSummaryAsync(
        DestinyManifest manifest,
        long itemHash,
        CancellationToken cancellationToken)
    {
        try
        {
            var hashIdentifier = ToUnsignedHashIdentifier(itemHash);
            var cacheKey = $"bungie:destiny2:manifest:{manifest.Version}:{InventoryItemDefinitionType}:{hashIdentifier}";
            return await cache.GetOrCreateAsync(
                    cacheKey,
                    async ct =>
                    {
                        var operation = $"GetDestinyEntityDefinition:{InventoryItemDefinitionType}:{hashIdentifier}";
                        var response = await bungieClient.Destiny2_GetDestinyEntityDefinitionAsync(
                                InventoryItemDefinitionType,
                                hashIdentifier,
                                ct)
                            .ConfigureAwait(false);
                        var definition = EnsureSuccess(response, item => item.Response, operation);
                        return ToWeaponDefinitionSummary(definition);
                    },
                    new HybridCacheEntryOptions
                    {
                        Expiration = ManifestCacheDuration,
                        LocalCacheExpiration = ManifestCacheDuration
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve Destiny inventory item definition {ItemHash}.", itemHash);
            return null;
        }
    }

    private static string ToUnsignedHashIdentifier(long hash)
    {
        return unchecked((uint)hash).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long ToUnsignedWeaponReferenceId(int referenceId)
    {
        return unchecked((uint)referenceId);
    }

    private async Task<Dictionary<long, WeaponDefinitionSummary>> GetInventoryItemSummariesAsync(
        DestinyManifest manifest,
        IEnumerable<long> itemHashes,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        var distinctHashes = itemHashes.Distinct().ToArray();
        var summaries = new ConcurrentDictionary<long, WeaponDefinitionSummary>();
        var processed = 0L;

        if (progress is not null)
        {
            await progress.StartPhaseAsync("weapon-definitions", "Resolving weapon definitions", distinctHashes.Length, cancellationToken).ConfigureAwait(false);
        }

        await Parallel.ForEachAsync(
                distinctHashes,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentDefinitionRequests,
                    CancellationToken = cancellationToken
                },
                async (itemHash, ct) =>
                {
                    var summary = await GetInventoryItemSummaryAsync(manifest, itemHash, ct).ConfigureAwait(false);
                    if (summary is not null)
                    {
                        summaries[itemHash] = summary;
                    }

                    var current = Interlocked.Increment(ref processed);
                    if (progress is not null)
                    {
                        await progress.ReportAsync(current, distinctHashes.Length, ct).ConfigureAwait(false);
                    }
                })
            .ConfigureAwait(false);

        return summaries.ToDictionary(item => item.Key, item => item.Value);
    }

    private async Task ApplyWeaponStatsAsync(
        DestinyReport report,
        IReadOnlyDictionary<long, ICollection<DestinyHistoricalWeaponStats>> uniqueWeaponHistory,
        ManifestContext manifest,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        var fallback = uniqueWeaponHistory.Values
            .SelectMany(weapons => weapons)
            .GroupBy(weapon => ToUnsignedWeaponReferenceId(weapon.ReferenceId))
            .ToDictionary(
                group => group.Key,
                group => new WeaponKillDelta
                {
                    UniqueWeaponKills = group.Sum(weapon => (int)GetStat(weapon.Values, "uniqueWeaponKills"))
                });

        if (report.PvETopWeapons.Count == 0)
        {
            var weaponDefinitions = await GetInventoryItemSummariesAsync(manifest.Manifest, TopWeaponHashes(fallback), progress, cancellationToken)
                .ConfigureAwait(false);

            report.PvETopWeapons = BuildWeaponReports(fallback, weaponDefinitions);
        }
    }

    private async Task ApplyWeaponAggregateDeltasAsync(
        DestinyReport report,
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<long, WeaponKillDelta> pveWeaponDeltas,
        IReadOnlyDictionary<long, WeaponKillDelta> crucibleWeaponDeltas,
        IReadOnlyDictionary<long, WeaponKillDelta> gambitWeaponDeltas,
        DestinyManifest manifest,
        bool resetWeaponAggregates,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        var weapons = mongoDatabase.GetCollection<WeaponAggregate>("weapon_aggregates");
        var weaponCategories = mongoDatabase.GetCollection<WeaponCategoryAggregate>("weapon_category_aggregates");
        var ownerFilter = Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipType, ownerMembershipType)
            & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipId, ownerMembershipId);
        var categoryOwnerFilter = Builders<WeaponCategoryAggregate>.Filter.Eq(category => category.OwnerMembershipType, ownerMembershipType)
            & Builders<WeaponCategoryAggregate>.Filter.Eq(category => category.OwnerMembershipId, ownerMembershipId);

        if (resetWeaponAggregates)
        {
            await weapons.DeleteManyAsync(ownerFilter, cancellationToken).ConfigureAwait(false);
            await weaponCategories.DeleteManyAsync(categoryOwnerFilter, cancellationToken).ConfigureAwait(false);
        }

        var allHashes = pveWeaponDeltas.Keys
            .Concat(crucibleWeaponDeltas.Keys)
            .Concat(gambitWeaponDeltas.Keys)
            .Where(hash => hash > 0)
            .Distinct()
            .ToArray();
        var weaponDefinitions = await GetInventoryItemSummariesAsync(manifest, allHashes, progress, cancellationToken)
            .ConfigureAwait(false);

        var writes = BuildWeaponAggregateWrites(ownerMembershipType, ownerMembershipId, "PvE", pveWeaponDeltas, weaponDefinitions)
            .Concat(BuildWeaponAggregateWrites(ownerMembershipType, ownerMembershipId, "Crucible", crucibleWeaponDeltas, weaponDefinitions))
            .Concat(BuildWeaponAggregateWrites(ownerMembershipType, ownerMembershipId, "Gambit", gambitWeaponDeltas, weaponDefinitions))
            .ToArray();

        if (writes.Length > 0)
        {
            await weapons.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }

        var categoryWrites = BuildWeaponCategoryAggregateWrites(ownerMembershipType, ownerMembershipId, "PvE", pveWeaponDeltas, weaponDefinitions)
            .Concat(BuildWeaponCategoryAggregateWrites(ownerMembershipType, ownerMembershipId, "Crucible", crucibleWeaponDeltas, weaponDefinitions))
            .Concat(BuildWeaponCategoryAggregateWrites(ownerMembershipType, ownerMembershipId, "Gambit", gambitWeaponDeltas, weaponDefinitions))
            .ToArray();

        if (categoryWrites.Length > 0)
        {
            await weaponCategories.BulkWriteAsync(categoryWrites, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }

        report.PvETopWeapons = await GetTopWeaponReportsAsync(weapons, ownerFilter, "PvE", cancellationToken).ConfigureAwait(false);
        report.CrucibleTopWeapons = await GetTopWeaponReportsAsync(weapons, ownerFilter, "Crucible", cancellationToken).ConfigureAwait(false);
        report.GambitTopWeapons = await GetTopWeaponReportsAsync(weapons, ownerFilter, "Gambit", cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<WriteModel<WeaponAggregate>> BuildWeaponAggregateWrites(
        int ownerMembershipType,
        long ownerMembershipId,
        string activityMode,
        IReadOnlyDictionary<long, WeaponKillDelta> weaponDeltas,
        IReadOnlyDictionary<long, WeaponDefinitionSummary> weaponDefinitions)
    {
        return weaponDeltas
            .Where(item => item.Value.TotalKills > 0)
            .Select(item =>
            {
                weaponDefinitions.TryGetValue(item.Key, out var definition);
                var weaponName = definition?.Name ?? SyntheticWeaponName(item.Key);
                var weaponKey = NormalizeWeaponKey(weaponName);
                var (categoryName, categoryKey) = WeaponCategory(item.Key, definition);
                return new
                {
                    ReferenceId = item.Key,
                    WeaponName = weaponName,
                    WeaponKey = weaponKey,
                    IconUrl = definition?.IconUrl ?? "",
                    CategoryName = categoryName,
                    CategoryKey = categoryKey,
                    Delta = item.Value
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.WeaponKey))
            .GroupBy(item => item.WeaponKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var filter = Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipType, ownerMembershipType)
                    & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipId, ownerMembershipId)
                    & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.ActivityMode, activityMode)
                    & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.WeaponKey, group.Key);
                var update = Builders<WeaponAggregate>.Update
                    .SetOnInsert(weapon => weapon.OwnerMembershipType, ownerMembershipType)
                    .SetOnInsert(weapon => weapon.OwnerMembershipId, ownerMembershipId)
                    .SetOnInsert(weapon => weapon.ActivityMode, activityMode)
                    .SetOnInsert(weapon => weapon.WeaponKey, group.Key)
                    .Set(weapon => weapon.WeaponName, first.WeaponName)
                    .Set(weapon => weapon.ReferenceId, first.ReferenceId)
                    .Set(weapon => weapon.IconUrl, first.IconUrl)
                    .Set(weapon => weapon.CategoryKey, first.CategoryKey)
                    .Set(weapon => weapon.CategoryName, first.CategoryName)
                    .Inc(weapon => weapon.Kills, group.Sum(item => item.Delta.TotalKills));

                return new UpdateOneModel<WeaponAggregate>(filter, update)
                {
                    IsUpsert = true
                };
            });
    }

    private static IEnumerable<WriteModel<WeaponCategoryAggregate>> BuildWeaponCategoryAggregateWrites(
        int ownerMembershipType,
        long ownerMembershipId,
        string activityMode,
        IReadOnlyDictionary<long, WeaponKillDelta> weaponDeltas,
        IReadOnlyDictionary<long, WeaponDefinitionSummary> weaponDefinitions)
    {
        return weaponDeltas
            .Where(item => item.Value.TotalKills > 0)
            .Select(item =>
            {
                weaponDefinitions.TryGetValue(item.Key, out var definition);
                var (categoryName, categoryKey) = WeaponCategory(item.Key, definition);
                return new
                {
                    CategoryName = categoryName,
                    CategoryKey = categoryKey,
                    Delta = item.Value
                };
            })
            .GroupBy(item => item.CategoryKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var filter = Builders<WeaponCategoryAggregate>.Filter.Eq(category => category.OwnerMembershipType, ownerMembershipType)
                    & Builders<WeaponCategoryAggregate>.Filter.Eq(category => category.OwnerMembershipId, ownerMembershipId)
                    & Builders<WeaponCategoryAggregate>.Filter.Eq(category => category.ActivityMode, activityMode)
                    & Builders<WeaponCategoryAggregate>.Filter.Eq(category => category.CategoryKey, group.Key);
                var update = Builders<WeaponCategoryAggregate>.Update
                    .SetOnInsert(category => category.OwnerMembershipType, ownerMembershipType)
                    .SetOnInsert(category => category.OwnerMembershipId, ownerMembershipId)
                    .SetOnInsert(category => category.ActivityMode, activityMode)
                    .SetOnInsert(category => category.CategoryKey, group.Key)
                    .Set(category => category.CategoryName, first.CategoryName)
                    .Inc(category => category.Kills, group.Sum(item => item.Delta.TotalKills));

                return new UpdateOneModel<WeaponCategoryAggregate>(filter, update)
                {
                    IsUpsert = true
                };
            });
    }

    private static async Task<List<WeaponReport>> GetTopWeaponReportsAsync(
        IMongoCollection<WeaponAggregate> weapons,
        FilterDefinition<WeaponAggregate> ownerFilter,
        string activityMode,
        CancellationToken cancellationToken)
    {
        var modeFilter = ownerFilter & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.ActivityMode, activityMode);
        var topWeapons = await weapons
            .Find(modeFilter)
            .SortByDescending(weapon => weapon.Kills)
            .Limit(10)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return topWeapons
            .Select(weapon => new WeaponReport
            {
                Name = weapon.WeaponName,
                IconUrl = weapon.IconUrl,
                TotalKills = weapon.Kills
            })
            .ToList();
    }

    private static void AddWeapons(
        IDictionary<long, WeaponKillDelta> weapons,
        IEnumerable<DestinyPostGameCarnageReportEntry> entries)
    {
        foreach (var entry in entries)
        {
            foreach (var weapon in entry.Extended?.Weapons ?? [])
            {
                var uniqueWeaponKills = (int)GetStat(weapon.Values, "uniqueWeaponKills");
                var fallbackWeaponKills = 0;
                if (uniqueWeaponKills <= 0)
                {
                    fallbackWeaponKills = (int)GetStat(weapon.Values, "kills");
                }

                AddWeaponDelta(
                    weapons,
                    ToUnsignedWeaponReferenceId(weapon.ReferenceId),
                    new WeaponKillDelta
                    {
                        UniqueWeaponKills = uniqueWeaponKills,
                        WeaponKills = fallbackWeaponKills
                    });
            }

            AddAbilityDelta(weapons, entry, GrenadeKillsReferenceId, "weaponKillsGrenade");
            AddAbilityDelta(weapons, entry, MeleeKillsReferenceId, "weaponKillsMelee");
            AddAbilityDelta(weapons, entry, SuperKillsReferenceId, "weaponKillsSuper");
        }
    }

    private static void AddAbilityDelta(
        IDictionary<long, WeaponKillDelta> weapons,
        DestinyPostGameCarnageReportEntry entry,
        long referenceId,
        string statId)
    {
        var kills = (int)GetStat(entry.Extended?.Values, statId);
        if (kills <= 0)
        {
            return;
        }

        var delta = new WeaponKillDelta();
        switch (referenceId)
        {
            case GrenadeKillsReferenceId:
                delta.GrenadeKills = kills;
                break;
            case MeleeKillsReferenceId:
                delta.MeleeKills = kills;
                break;
            case SuperKillsReferenceId:
                delta.SuperKills = kills;
                break;
        }

        AddWeaponDelta(weapons, referenceId, delta);
    }

    private static void AddWeaponDelta(
        IDictionary<long, WeaponKillDelta> weapons,
        long referenceId,
        WeaponKillDelta delta)
    {
        if (!weapons.TryGetValue(referenceId, out var current))
        {
            current = new WeaponKillDelta();
            weapons[referenceId] = current;
        }

        current.Add(delta);
    }

    private static void AddGambitMoteStats(
        CrawlAccumulator accumulator,
        DestinyPostGameCarnageReportData pgcr,
        DestinyPostGameCarnageReportEntry entry)
    {
        var mode = GetGambitMoteMode(pgcr);
        var modeKey = mode.ToString();
        var banked = (int)GetMoteStat(entry, "motesDeposited");
        var lost = (int)GetMoteStat(entry, "motesLost");
        var denied = (int)GetMoteStat(entry, "motesDenied");
        var overage = (int)GetMoteStat(entry, "bankOverage");

        accumulator.GambitMotesBanked += banked;
        accumulator.GambitMotesLost += lost;
        accumulator.GambitMotesDenied += denied;
        accumulator.GambitBankOverage += overage;
        accumulator.GambitMotesBankedByMode[modeKey] = accumulator.GambitMotesBankedByMode.GetValueOrDefault(modeKey) + banked;
        accumulator.GambitMotesLostByMode[modeKey] = accumulator.GambitMotesLostByMode.GetValueOrDefault(modeKey) + lost;
        accumulator.GambitMotesDeniedByMode[modeKey] = accumulator.GambitMotesDeniedByMode.GetValueOrDefault(modeKey) + denied;
        accumulator.GambitBankOverageByMode[modeKey] = accumulator.GambitBankOverageByMode.GetValueOrDefault(modeKey) + overage;
    }

    private static int GetGambitMoteMode(DestinyPostGameCarnageReportData pgcr)
    {
        return pgcr.ActivityDetails.Mode switch
        {
            ActivityModes.Gambit => ActivityModes.Gambit,
            ActivityModes.GambitPrime => ActivityModes.GambitPrime,
            ActivityModes.AllPvECompetitive => ActivityModes.AllPvECompetitive,
            _ when IncludesMode(pgcr, ActivityModes.GambitPrime) => ActivityModes.GambitPrime,
            _ when IncludesMode(pgcr, ActivityModes.Gambit) => ActivityModes.Gambit,
            _ when IncludesMode(pgcr, ActivityModes.AllPvECompetitive) => ActivityModes.AllPvECompetitive,
            _ => pgcr.ActivityDetails.Mode
        };
    }

    private static double GetMoteStat(DestinyPostGameCarnageReportEntry entry, string statId)
    {
        return EnumerateValueDictionaries(entry)
            .Select(dictionary => GetStat(dictionary, statId))
            .FirstOrDefault(value => value > 0);
    }

    private static IEnumerable<IDictionary<string, DestinyHistoricalStatsValue>> EnumerateValueDictionaries(DestinyPostGameCarnageReportEntry entry)
    {
        if (entry.Values is not null)
        {
            yield return entry.Values;
        }

        if (entry.Extended?.Values is not null)
        {
            yield return entry.Extended.Values;
        }

        if (entry.Extended?.ScoreboardValues is not null)
        {
            yield return entry.Extended.ScoreboardValues;
        }
    }

    private static IEnumerable<long> TopWeaponHashes(IDictionary<long, WeaponKillDelta> weaponKills)
    {
        return weaponKills
            .Where(item => item.Value.TotalKills > 0 && item.Key > 0)
            .OrderByDescending(item => item.Value.TotalKills)
            .Take(10)
            .Select(item => item.Key);
    }

    private static List<WeaponReport> BuildWeaponReports(
        IDictionary<long, WeaponKillDelta> weaponKills,
        IReadOnlyDictionary<long, WeaponDefinitionSummary>? weaponDefinitions = null)
    {
        return weaponKills
            .Where(item => item.Value.TotalKills > 0)
            .Select(item =>
            {
                WeaponDefinitionSummary? definition = null;
                weaponDefinitions?.TryGetValue(item.Key, out definition);
                return new
                {
                    Name = definition?.Name ?? SyntheticWeaponName(item.Key),
                    IconUrl = definition?.IconUrl ?? "",
                    Kills = item.Value.TotalKills
                };
            })
            .GroupBy(item => NormalizeWeaponKey(item.Name), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new WeaponReport
                {
                    Name = first.Name,
                    IconUrl = first.IconUrl,
                    TotalKills = group.Sum(item => item.Kills)
                };
            })
            .OrderByDescending(item => item.TotalKills)
            .Take(10)
            .ToList();
    }

    private static string NormalizeWeaponKey(string weaponName)
    {
        return weaponName.Trim().ToUpperInvariant();
    }

    private static string SyntheticWeaponName(long referenceId)
    {
        return referenceId switch
        {
            GrenadeKillsReferenceId => "Grenade",
            MeleeKillsReferenceId => "Melee",
            SuperKillsReferenceId => "Super",
            _ => referenceId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static (string CategoryName, string CategoryKey) WeaponCategory(long referenceId, WeaponDefinitionSummary? definition)
    {
        if (referenceId is GrenadeKillsReferenceId or MeleeKillsReferenceId or SuperKillsReferenceId)
        {
            return (AbilityCategoryName, AbilityCategoryKey);
        }

        var categoryName = string.IsNullOrWhiteSpace(definition?.CategoryName)
            ? UnknownWeaponCategoryName
            : definition.CategoryName;
        var categoryKey = string.IsNullOrWhiteSpace(definition?.CategoryKey)
            ? NormalizeWeaponKey(categoryName)
            : definition.CategoryKey;

        return (categoryName, categoryKey);
    }

    private static WeaponDefinitionSummary ToWeaponDefinitionSummary(DestinyDefinition definition)
    {
        var displayProperties = TryGetJObject(definition.AdditionalProperties, "displayProperties");
        var itemTypeDisplayName = definition.AdditionalProperties.TryGetValue("itemTypeDisplayName", out var itemTypeDisplayNameValue)
            ? itemTypeDisplayNameValue?.ToString()
            : null;
        var categoryName = string.IsNullOrWhiteSpace(itemTypeDisplayName)
            ? WeaponSubTypeName(definition.AdditionalProperties.TryGetValue("itemSubType", out var itemSubTypeValue) ? itemSubTypeValue : null)
            : itemTypeDisplayName!;
        return new WeaponDefinitionSummary(
            displayProperties?["name"]?.Value<string>() ?? ToUnsignedHashIdentifier(definition.Hash),
            BungieUrl(displayProperties?["icon"]?.Value<string>()),
            categoryName,
            NormalizeWeaponKey(categoryName));
    }

    private static string WeaponSubTypeName(object? itemSubType)
    {
        if (itemSubType is null || !int.TryParse(itemSubType.ToString(), out var value))
        {
            return UnknownWeaponCategoryName;
        }

        return value switch
        {
            6 => "Auto Rifle",
            7 => "Shotgun",
            8 => "Machine Gun",
            9 => "Hand Cannon",
            10 => "Rocket Launcher",
            11 => "Fusion Rifle",
            12 => "Sniper Rifle",
            13 => "Pulse Rifle",
            14 => "Scout Rifle",
            16 => "Sidearm",
            17 => "Sword",
            18 => "Mask",
            19 => "Shader",
            20 => "Ornament",
            21 => "Fusion Rifle",
            22 => "Grenade Launcher",
            23 => "Submachine Gun",
            24 => "Trace Rifle",
            25 => "Helmet Armor",
            26 => "Gauntlets Armor",
            27 => "Chest Armor",
            28 => "Leg Armor",
            29 => "Class Armor",
            30 => "Bow",
            31 => "Dummy Repeatable Bounty",
            32 => "Glaive",
            33 => "Episode Ticket",
            _ => UnknownWeaponCategoryName
        };
    }
}
