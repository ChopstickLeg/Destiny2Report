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
using ReportPlayer = Destiny2Report.API.Features.Crawler.Models.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private async Task<WeaponDefinitionSummary?> GetInventoryItemSummaryAsync(
        DestinyManifest manifest,
        int itemHash,
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

    private static string ToUnsignedHashIdentifier(int hash)
    {
        return unchecked((uint)hash).ToString();
    }

    private async Task<Dictionary<int, WeaponDefinitionSummary>> GetInventoryItemSummariesAsync(
        DestinyManifest manifest,
        IEnumerable<int> itemHashes,
        CancellationToken cancellationToken)
    {
        var tasks = itemHashes
            .Distinct()
            .Select(async itemHash => new
            {
                ItemHash = itemHash,
                Summary = await GetInventoryItemSummaryAsync(manifest, itemHash, cancellationToken).ConfigureAwait(false)
            });

        var summaries = await Task.WhenAll(tasks).ConfigureAwait(false);
        return summaries
            .Where(item => item.Summary is not null)
            .ToDictionary(item => item.ItemHash, item => item.Summary!);
    }

    private async Task ApplyWeaponStatsAsync(
        DestinyReport report,
        IReadOnlyDictionary<long, ICollection<DestinyHistoricalWeaponStats>> uniqueWeaponHistory,
        ManifestContext manifest,
        CancellationToken cancellationToken)
    {
        var fallback = uniqueWeaponHistory.Values
            .SelectMany(weapons => weapons)
            .GroupBy(weapon => weapon.ReferenceId)
            .ToDictionary(group => group.Key, group => group.Sum(weapon => (int)GetStat(weapon.Values, "uniqueWeaponKills")));

        if (report.PvETopWeapons.Count == 0)
        {
            var weaponDefinitions = await GetInventoryItemSummariesAsync(manifest.Manifest, TopWeaponHashes(fallback), cancellationToken)
                .ConfigureAwait(false);

            report.PvETopWeapons = BuildWeaponReports(fallback, weaponDefinitions);
        }
    }

    private async Task ApplyWeaponAggregateDeltasAsync(
        DestinyReport report,
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<int, int> pveWeaponDeltas,
        IReadOnlyDictionary<int, int> crucibleWeaponDeltas,
        IReadOnlyDictionary<int, int> gambitWeaponDeltas,
        DestinyManifest manifest,
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

        var allHashes = pveWeaponDeltas.Keys
            .Concat(crucibleWeaponDeltas.Keys)
            .Concat(gambitWeaponDeltas.Keys)
            .Distinct()
            .ToArray();
        var weaponDefinitions = await GetInventoryItemSummariesAsync(manifest, allHashes, cancellationToken)
            .ConfigureAwait(false);

        var writes = BuildWeaponAggregateWrites(ownerMembershipType, ownerMembershipId, "PvE", pveWeaponDeltas, weaponDefinitions)
            .Concat(BuildWeaponAggregateWrites(ownerMembershipType, ownerMembershipId, "Crucible", crucibleWeaponDeltas, weaponDefinitions))
            .Concat(BuildWeaponAggregateWrites(ownerMembershipType, ownerMembershipId, "Gambit", gambitWeaponDeltas, weaponDefinitions))
            .ToArray();

        if (writes.Length > 0)
        {
            await weapons.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }

        report.PvETopWeapons = await GetTopWeaponReportsAsync(weapons, ownerFilter, "PvE", cancellationToken).ConfigureAwait(false);
        report.CrucibleTopWeapons = await GetTopWeaponReportsAsync(weapons, ownerFilter, "Crucible", cancellationToken).ConfigureAwait(false);
        report.GambitTopWeapons = await GetTopWeaponReportsAsync(weapons, ownerFilter, "Gambit", cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<WriteModel<WeaponAggregate>> BuildWeaponAggregateWrites(
        int ownerMembershipType,
        long ownerMembershipId,
        string activityMode,
        IReadOnlyDictionary<int, int> weaponDeltas,
        IReadOnlyDictionary<int, WeaponDefinitionSummary> weaponDefinitions)
    {
        return weaponDeltas
            .Where(item => item.Value > 0)
            .Select(item =>
            {
                weaponDefinitions.TryGetValue(item.Key, out var definition);
                var weaponName = definition?.Name ?? item.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var weaponKey = NormalizeWeaponKey(weaponName);
                return new
                {
                    WeaponName = weaponName,
                    WeaponKey = weaponKey,
                    IconUrl = definition?.IconUrl ?? "",
                    Kills = item.Value
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
                    .Set(weapon => weapon.IconUrl, first.IconUrl)
                    .Inc(weapon => weapon.TotalKills, group.Sum(item => item.Kills));

                return new UpdateOneModel<WeaponAggregate>(filter, update)
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
            .SortByDescending(weapon => weapon.TotalKills)
            .Limit(10)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return topWeapons
            .Select(weapon => new WeaponReport
            {
                Name = weapon.WeaponName,
                IconUrl = weapon.IconUrl,
                TotalKills = weapon.TotalKills
            })
            .ToList();
    }

    private static void TrackRivals(
        IDictionary<long, RivalAggregate> rivals,
        DestinyPostGameCarnageReportData pgcr,
        DestinyPostGameCarnageReportEntry playerEntry,
        long playerMembershipId,
        double playerKills,
        double playerDeaths)
    {
        var playerTeam = GetTeam(playerEntry);
        if (playerTeam is null)
        {
            return;
        }

        var opponents = (pgcr.Entries ?? [])
            .Where(entry => entry.Player?.DestinyUserInfo?.MembershipId is > 0)
            .Where(entry => entry.Player.DestinyUserInfo.MembershipId != playerMembershipId)
            .Where(entry => GetTeam(entry) is { } team && team != playerTeam)
            .Select(entry => ToReportPlayer(entry.Player, ""))
            .GroupBy(player => player.MembershipId)
            .Select(group => group.First());

        foreach (var opponent in opponents)
        {
            var aggregate = rivals.TryGetValue(opponent.MembershipId, out var existing)
                ? existing
                : rivals[opponent.MembershipId] = new RivalAggregate(opponent);

            aggregate.Matches++;
            aggregate.Kills += playerKills;
            aggregate.Deaths += playerDeaths;
            aggregate.Wins += playerEntry.Standing == 0 ? 1 : 0;
            aggregate.Losses += playerEntry.Standing > 0 ? 1 : 0;
        }
    }

    private static double? GetTeam(DestinyPostGameCarnageReportEntry entry)
    {
        return entry.Values?.TryGetValue("team", out var teamValue) == true
            ? teamValue.Basic?.Value
            : null;
    }

    private static void ApplyRival(DestinyReport report, IDictionary<long, RivalAggregate> rivals, bool isGambit)
    {
        var rival = rivals.Values.OrderByDescending(item => item.Matches).FirstOrDefault();
        if (rival is null)
        {
            return;
        }

        var kd = rival.Deaths > 0 ? Math.Round(rival.Kills / rival.Deaths, 3) : rival.Kills;
        if (isGambit)
        {
            report.GambitRival = rival.Player;
            report.KdAgainstGambitRival = kd;
        }
        else
        {
            report.CrucibleRival = rival.Player;
            report.KdAgainstCrucibleRival = kd;
        }
    }

    private static void AddWeapons(IDictionary<int, int> weapons, DestinyPostGameCarnageReportEntry entry)
    {
        foreach (var weapon in entry.Extended?.Weapons ?? [])
        {
            var kills = (int)GetStat(weapon.Values, "uniqueWeaponKills");
            if (kills <= 0)
            {
                kills = (int)GetStat(weapon.Values, "kills");
            }

            weapons.TryGetValue(weapon.ReferenceId, out var currentKills);
            weapons[weapon.ReferenceId] = currentKills + kills;
        }
    }

    private static double GetMoteStat(DestinyPostGameCarnageReportEntry entry, params string[] needles)
    {
        return EnumerateValueDictionaries(entry)
            .SelectMany(dictionary => dictionary)
            .Where(item => needles.Any(needle => item.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .Sum(item => item.Value.Basic?.Value ?? 0);
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

    private static IEnumerable<int> TopWeaponHashes(IDictionary<int, int> weaponKills)
    {
        return weaponKills
            .Where(item => item.Value > 0)
            .OrderByDescending(item => item.Value)
            .Take(10)
            .Select(item => item.Key);
    }

    private static List<WeaponReport> BuildWeaponReports(
        IDictionary<int, int> weaponKills,
        IReadOnlyDictionary<int, WeaponDefinitionSummary>? weaponDefinitions = null)
    {
        return weaponKills
            .Where(item => item.Value > 0)
            .Select(item =>
            {
                WeaponDefinitionSummary? definition = null;
                weaponDefinitions?.TryGetValue(item.Key, out definition);
                return new
                {
                    Name = definition?.Name ?? item.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IconUrl = definition?.IconUrl ?? "",
                    Kills = item.Value
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

    private static WeaponDefinitionSummary ToWeaponDefinitionSummary(DestinyDefinition definition)
    {
        var displayProperties = TryGetJObject(definition.AdditionalProperties, "displayProperties");
        return new WeaponDefinitionSummary(
            displayProperties?["name"]?.Value<string>() ?? definition.Hash.ToString(),
            BungieUrl(displayProperties?["icon"]?.Value<string>()));
    }
}
