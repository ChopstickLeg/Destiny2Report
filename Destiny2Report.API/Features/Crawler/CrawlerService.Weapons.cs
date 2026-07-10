using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using BungiePlayer = D2Report.BungieClient.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private const long GrenadeKillsReferenceId = -1;
    private const long MeleeKillsReferenceId = -2;
    private const long SuperKillsReferenceId = -3;
    private const long UnknownKillsReferenceId = -4;
    private const string AbilityCategoryName = "Abilities";
    private const string AbilityCategoryKey = "ABILITIES";
    private const string UnknownWeaponCategoryName = "Unknown";
    private const int WeaponItemType = 3;
    private static readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<long, WeaponDefinitionSummary>>>> WeaponDefinitionCaches = new();

    public async Task WarmReportReadModelsAsync(CancellationToken cancellationToken)
    {
        var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        await GetWeaponDefinitionCacheAsync(manifest.Manifest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WeaponActivityModeAggregateReport?> GetWeaponActivityModeReportAsync(
        int membershipTypeId,
        long membershipId,
        WeaponActivityMode activityMode,
        CancellationToken cancellationToken)
    {
        var storedActivityMode = activityMode switch
        {
            WeaponActivityMode.PvP => "Crucible",
            WeaponActivityMode.PvE => "PvE",
            WeaponActivityMode.Gambit => "Gambit",
            _ => ""
        };
        var weapons = mongoDatabase.GetCollection<WeaponAggregate>("weapon_aggregates");
        var filter = Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipType, membershipTypeId)
            & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipId, membershipId)
            & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.ActivityMode, storedActivityMode);
        var aggregates = await weapons.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (aggregates.Count == 0)
        {
            return null;
        }

        var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        var definitions = await GetInventoryItemSummariesAsync(
                manifest,
                aggregates.Select(weapon => weapon.WeaponHash),
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        var resolved = aggregates.Select(weapon =>
        {
            definitions.TryGetValue(weapon.WeaponHash, out var definition);
            var (categoryName, categoryKey) = WeaponCategory(weapon.WeaponHash, definition);
            return new
            {
                Weapon = weapon,
                Name = definition?.Name ?? SyntheticWeaponName(weapon.WeaponHash),
                IconUrl = definition?.IconUrl ?? "",
                CategoryName = categoryName,
                CategoryKey = categoryKey
            };
        });

        return new WeaponActivityModeAggregateReport
        {
            ActivityMode = storedActivityMode,
            Classes = resolved
                .GroupBy(item => string.IsNullOrWhiteSpace(item.Weapon.ClassName) ? "Unknown" : item.Weapon.ClassName)
                .OrderByDescending(group => group.Sum(item => item.Weapon.Kills))
                .Select(classGroup => new WeaponClassAggregateReport
                {
                    ClassName = classGroup.Key,
                    Modes = classGroup
                        .GroupBy(item => item.Weapon.SpecificActivityMode)
                        .OrderByDescending(group => group.Sum(item => item.Weapon.Kills))
                        .Select(modeGroup => new WeaponModeAggregateReport
                        {
                            SpecificActivityMode = GetSpecificActivityModeName(modeGroup.Key),
                            Categories = modeGroup
                                .GroupBy(item => (item.CategoryKey, item.CategoryName))
                                .OrderByDescending(group => group.Sum(item => item.Weapon.Kills))
                                .Select(categoryGroup => new WeaponCategoryAggregateReport
                                {
                                    OwnerMembershipType = membershipTypeId,
                                    OwnerMembershipId = membershipId,
                                    ActivityMode = storedActivityMode,
                                    ClassName = classGroup.Key,
                                    SpecificActivityMode = GetSpecificActivityModeName(modeGroup.Key),
                                    CategoryKey = categoryGroup.Key.CategoryKey,
                                    CategoryName = categoryGroup.Key.CategoryName,
                                    Kills = categoryGroup.Sum(item => item.Weapon.Kills),
                                    Weapons = categoryGroup
                                        .OrderByDescending(item => item.Weapon.Kills)
                                        .ThenBy(item => item.Name, StringComparer.Ordinal)
                                        .Select(item => new WeaponAggregateDetailReport
                                        {
                                            OwnerMembershipType = item.Weapon.OwnerMembershipType,
                                            OwnerMembershipId = item.Weapon.OwnerMembershipId,
                                            ActivityMode = item.Weapon.ActivityMode,
                                            WeaponKey = NormalizeWeaponKey(item.Name),
                                            WeaponName = item.Name,
                                            ReferenceId = item.Weapon.WeaponHash,
                                            IconUrl = item.IconUrl,
                                            CategoryKey = item.CategoryKey,
                                            CategoryName = item.CategoryName,
                                            Kills = item.Weapon.Kills
                                        })
                                        .ToList()
                                })
                                .ToList()
                        })
                        .ToList()
                })
                .ToList()
        };
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
        ManifestContext manifest,
        IEnumerable<long> itemHashes,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        var distinctHashes = itemHashes.Distinct().ToArray();
        var definitions = await GetWeaponDefinitionCacheAsync(manifest.Manifest, cancellationToken).ConfigureAwait(false);
        var summaries = new Dictionary<long, WeaponDefinitionSummary>();

        if (progress is not null)
        {
            await progress.StartPhaseAsync("weapon-definitions", "Resolving weapon definitions", distinctHashes.Length, cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < distinctHashes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemHash = distinctHashes[index];
            if (definitions.TryGetValue(itemHash, out var definition))
            {
                summaries[itemHash] = definition;
            }

            if (progress is not null)
            {
                await progress.ReportAsync(index + 1, distinctHashes.Length, cancellationToken).ConfigureAwait(false);
            }
        }

        return summaries;
    }

    private async Task<IReadOnlyDictionary<long, WeaponDefinitionSummary>> GetWeaponDefinitionCacheAsync(
        DestinyManifest manifest,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{manifest.Version}:{InventoryItemDefinitionType}";
        var lazyCache = WeaponDefinitionCaches.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<IReadOnlyDictionary<long, WeaponDefinitionSummary>>>(
                async () =>
                {
                    var json = await GetManifestTableJsonAsync(manifest, InventoryItemDefinitionType, CancellationToken.None)
                        .ConfigureAwait(false);
                    return ParseWeaponDefinitions(json);
                },
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazyCache.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            WeaponDefinitionCaches.TryRemove(KeyValuePair.Create(cacheKey, lazyCache));
            throw;
        }
    }

    private static IReadOnlyDictionary<long, WeaponDefinitionSummary> ParseWeaponDefinitions(string json)
    {
        var definitions = new Dictionary<long, WeaponDefinitionSummary>();
        using var stringReader = new StringReader(json);
        using var reader = new JsonTextReader(stringReader);

        if (reader.Read() is false || reader.TokenType != JsonToken.StartObject)
        {
            throw new JsonReaderException("The DestinyInventoryItemDefinition manifest table must be a JSON object.");
        }

        while (reader.Read())
        {
            if (reader.TokenType != JsonToken.PropertyName
                || long.TryParse(reader.Value?.ToString(), out var itemHash) is false
                || reader.Read() is false)
            {
                continue;
            }

            if (reader.TokenType != JsonToken.StartObject)
            {
                reader.Skip();
                continue;
            }

            var definition = JObject.Load(reader);
            if (definition["itemType"]?.Value<int>() == WeaponItemType)
            {
                definitions[itemHash] = ToWeaponDefinitionSummary(definition, itemHash);
            }
        }

        return definitions;
    }

    private async Task ApplyWeaponAggregateDeltasAsync(
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<(string ClassName, int SpecificActivityMode), Dictionary<long, WeaponKillDelta>> pveWeaponDeltas,
        IReadOnlyDictionary<(string ClassName, int SpecificActivityMode), Dictionary<long, WeaponKillDelta>> crucibleWeaponDeltas,
        IReadOnlyDictionary<(string ClassName, int SpecificActivityMode), Dictionary<long, WeaponKillDelta>> gambitWeaponDeltas,
        bool resetWeaponAggregates,
        CancellationToken cancellationToken)
    {
        var weapons = mongoDatabase.GetCollection<WeaponAggregate>("weapon_aggregates");
        var ownerFilter = Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipType, ownerMembershipType)
            & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipId, ownerMembershipId);

        if (resetWeaponAggregates)
        {
            await weapons.DeleteManyAsync(ownerFilter, cancellationToken).ConfigureAwait(false);
        }

        var writes = BuildWeaponAggregateWrites(ownerMembershipType, ownerMembershipId, "PvE", pveWeaponDeltas)
            .Concat(BuildWeaponAggregateWrites(ownerMembershipType, ownerMembershipId, "Crucible", crucibleWeaponDeltas))
            .Concat(BuildWeaponAggregateWrites(ownerMembershipType, ownerMembershipId, "Gambit", gambitWeaponDeltas))
            .ToArray();

        if (writes.Length > 0)
        {
            await weapons.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }

        var pveFilter = ownerFilter & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.ActivityMode, "PvE");
        if (await weapons.Find(pveFilter).Limit(1).AnyAsync(cancellationToken).ConfigureAwait(false) is false)
        {
            logger.LogWarning(
                "No PvE weapon aggregates were produced for membership {MembershipType}/{MembershipId}. The report will not include a fallback top-weapons summary.",
                ownerMembershipType,
                ownerMembershipId);
        }
    }

    private static IEnumerable<WriteModel<WeaponAggregate>> BuildWeaponAggregateWrites(
        int ownerMembershipType,
        long ownerMembershipId,
        string activityMode,
        IReadOnlyDictionary<(string ClassName, int SpecificActivityMode), Dictionary<long, WeaponKillDelta>> weaponDeltasByClassAndMode)
    {
        return weaponDeltasByClassAndMode.SelectMany(mode => mode.Value.Select(weapon => (mode.Key.ClassName, mode.Key.SpecificActivityMode, Weapon: weapon)))
            .Where(item => item.Weapon.Value.TotalKills > 0)
            .Select(item =>
            {
                return new
                {
                    item.SpecificActivityMode,
                    item.ClassName,
                    WeaponHash = item.Weapon.Key,
                    Delta = item.Weapon.Value
                };
            })
            .GroupBy(item => (item.ClassName, item.SpecificActivityMode, item.WeaponHash))
            .Select(group =>
            {
                var first = group.First();
                var filter = Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipType, ownerMembershipType)
                    & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.OwnerMembershipId, ownerMembershipId)
                    & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.ActivityMode, activityMode)
                    & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.ClassName, first.ClassName)
                    & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.SpecificActivityMode, first.SpecificActivityMode)
                    & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.WeaponHash, first.WeaponHash);
                var update = Builders<WeaponAggregate>.Update
                    .SetOnInsert(weapon => weapon.OwnerMembershipType, ownerMembershipType)
                    .SetOnInsert(weapon => weapon.OwnerMembershipId, ownerMembershipId)
                    .SetOnInsert(weapon => weapon.ActivityMode, activityMode)
                    .SetOnInsert(weapon => weapon.ClassName, first.ClassName)
                    .SetOnInsert(weapon => weapon.SpecificActivityMode, first.SpecificActivityMode)
                    .SetOnInsert(weapon => weapon.WeaponHash, first.WeaponHash)
                    .Inc(weapon => weapon.Kills, group.Sum(item => item.Delta.TotalKills));

                return new UpdateOneModel<WeaponAggregate>(filter, update) { IsUpsert = true };
            });
    }

    private async Task<List<WeaponReport>> GetTopWeaponReportsAsync(
        IMongoCollection<WeaponAggregate> weapons,
        FilterDefinition<WeaponAggregate> ownerFilter,
        string activityMode,
        ManifestContext manifest,
        CancellationToken cancellationToken)
    {
        var modeFilter = ownerFilter & Builders<WeaponAggregate>.Filter.Eq(weapon => weapon.ActivityMode, activityMode);
        var weaponAggregates = await weapons
            .Find(modeFilter)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var topWeapons = weaponAggregates
            .GroupBy(weapon => weapon.WeaponHash)
            .Select(group => new { WeaponHash = group.Key, TotalKills = group.Sum(weapon => weapon.Kills) })
            .OrderByDescending(weapon => weapon.TotalKills)
            .Take(10)
            .ToList();
        var definitions = await GetInventoryItemSummariesAsync(
                manifest,
                topWeapons.Select(weapon => weapon.WeaponHash),
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);

        return topWeapons
            .Select(weapon =>
            {
                definitions.TryGetValue(weapon.WeaponHash, out var definition);
                return new WeaponReport
                {
                    Name = definition?.Name ?? SyntheticWeaponName(weapon.WeaponHash),
                    IconUrl = definition?.IconUrl ?? "",
                    TotalKills = weapon.TotalKills
                };
            })
            .OrderByDescending(weapon => weapon.TotalKills)
            .ThenBy(weapon => weapon.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddWeapons(
        IDictionary<long, WeaponKillDelta> weapons,
        IEnumerable<DestinyPostGameCarnageReportEntry> entries)
    {
        foreach (var entry in entries)
        {
            var extendedKills = 0;
            foreach (var weapon in entry.Extended?.Weapons ?? [])
            {
                var uniqueWeaponKills = (int)GetStat(weapon.Values, "uniqueWeaponKills");
                var fallbackWeaponKills = 0;
                if (uniqueWeaponKills <= 0)
                {
                    fallbackWeaponKills = (int)GetStat(weapon.Values, "kills");
                }

                extendedKills += uniqueWeaponKills + fallbackWeaponKills;

                AddWeaponDelta(
                    weapons,
                    ToUnsignedWeaponReferenceId(weapon.ReferenceId),
                    new WeaponKillDelta
                    {
                        UniqueWeaponKills = uniqueWeaponKills,
                        WeaponKills = fallbackWeaponKills
                    });
            }

            extendedKills += AddAbilityDelta(weapons, entry, GrenadeKillsReferenceId, "weaponKillsGrenade");
            extendedKills += AddAbilityDelta(weapons, entry, MeleeKillsReferenceId, "weaponKillsMelee");
            extendedKills += AddAbilityDelta(weapons, entry, SuperKillsReferenceId, "weaponKillsSuper");

            var unknownKills = (int)GetStat(entry.Values, "kills") - extendedKills;
            if (unknownKills > 0)
            {
                AddWeaponDelta(weapons, UnknownKillsReferenceId, new WeaponKillDelta { UnknownKills = unknownKills });
            }
        }
    }

    private static int AddAbilityDelta(
        IDictionary<long, WeaponKillDelta> weapons,
        DestinyPostGameCarnageReportEntry entry,
        long referenceId,
        string statId)
    {
        var kills = (int)GetStat(entry.Extended?.Values, statId);
        if (kills <= 0)
        {
            return 0;
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
        return kills;
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
            UnknownKillsReferenceId => "Unknown",
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

    private static WeaponDefinitionSummary ToWeaponDefinitionSummary(JObject definition, long itemHash)
    {
        var displayProperties = definition["displayProperties"] as JObject;
        var itemTypeDisplayName = definition["itemTypeDisplayName"]?.Value<string>();
        var categoryName = string.IsNullOrWhiteSpace(itemTypeDisplayName)
            ? WeaponSubTypeName(definition["itemSubType"])
            : itemTypeDisplayName;
        return new WeaponDefinitionSummary(
            displayProperties?["name"]?.Value<string>() ?? ToUnsignedHashIdentifier(itemHash),
            BungieUrl(displayProperties?["icon"]?.Value<string>()),
            categoryName,
            NormalizeWeaponKey(categoryName));
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
