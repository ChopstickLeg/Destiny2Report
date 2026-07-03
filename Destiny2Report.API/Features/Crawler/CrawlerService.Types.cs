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
    private const int MinPersistedPlayerEncounterCount = 2;

    private sealed record ActivityCompletionAggregate(string ActivityName)
    {
        public int CompletionCount { get; set; }
        public bool ContestClear { get; set; }
        public bool FlawlessClear { get; set; }
        public bool SoloClear { get; set; }
        public bool SoloFlawlessClear { get; set; }
    }

    private sealed record CompletedRaidActivity(string RaidName, DateTimeOffset CompletedAt, long InstanceId);

    private sealed record SherpaCheck(
        long InstanceId,
        string NormalizedRaidName,
        DateTimeOffset CompletedAt,
        IReadOnlyCollection<SherpaCandidate> Candidates);

    private sealed record SherpaCandidate(int MembershipType, long MembershipId);

    private sealed record SherpaCandidateCheck(
        long InstanceId,
        string NormalizedRaidName,
        DateTimeOffset CompletedAt,
        int MembershipType,
        long MembershipId);

    private sealed class PrivateProfileUnavailableException(string operation, BungieResponse response)
        : InvalidOperationException($"{operation} failed because the Destiny profile is not public. Bungie error code {response.ErrorCode}: {response.Message}");

    private sealed record WeaponDefinitionSummary(string Name, string IconUrl, string CategoryName, string CategoryKey);

    private sealed class WeaponKillDelta
    {
        public int UniqueWeaponKills { get; set; }
        public int WeaponKills { get; set; }
        public int GrenadeKills { get; set; }
        public int MeleeKills { get; set; }
        public int SuperKills { get; set; }
        public int TotalKills => UniqueWeaponKills + WeaponKills + GrenadeKills + MeleeKills + SuperKills;

        public void Add(WeaponKillDelta delta)
        {
            UniqueWeaponKills += delta.UniqueWeaponKills;
            WeaponKills += delta.WeaponKills;
            GrenadeKills += delta.GrenadeKills;
            MeleeKills += delta.MeleeKills;
            SuperKills += delta.SuperKills;
        }
    }

    private static CrawlAccumulator NewAccumulator(int platformId, long playerMembershipId)
    {
        return new CrawlAccumulator
        {
            PlatformId = platformId,
            PlayerMembershipId = playerMembershipId,
            NeedsFullRecrawl = false,
            FullRecrawlReason = ""
        };
    }

    private static void UpdateAccumulatorCrawlState(
        CrawlAccumulator accumulator,
        IReadOnlyCollection<DestinyHistoricalStatsPeriodGroup> fetchedActivities,
        IEnumerable<long> processedActivityIds)
    {
        accumulator.LastSuccessfulCrawlAt = DateTimeOffset.UtcNow;
        accumulator.NeedsFullRecrawl = false;
        accumulator.FullRecrawlReason = "";

        var newestFetchedActivity = fetchedActivities
            .OrderByDescending(activity => activity.Period)
            .FirstOrDefault();
        if (newestFetchedActivity is not null && newestFetchedActivity.Period > accumulator.NewestActivityPeriod)
        {
            accumulator.NewestActivityPeriod = newestFetchedActivity.Period;
        }

        accumulator.RecentActivityInstanceIds = fetchedActivities
            .Where(activity => activity.ActivityDetails.InstanceId > 0)
            .OrderByDescending(activity => activity.Period)
            .Select(activity => activity.ActivityDetails.InstanceId)
            .Concat(processedActivityIds.Where(instanceId => instanceId > 0))
            .Concat(accumulator.RecentActivityInstanceIds)
            .Distinct()
            .Take(RecentActivityInstanceIdLimit)
            .ToList();
    }

    private static string PlayerKey(int membershipType, long membershipId)
    {
        return $"{membershipType}:{membershipId}";
    }

    private static bool IsPersistablePlayerEncounter(int membershipType, long membershipId, int count)
    {
        return membershipType > 0
            && membershipId > 0
            && count >= MinPersistedPlayerEncounterCount;
    }

    private static bool IsCountablePlayerEncounter(int membershipType, long membershipId, int count)
    {
        return membershipType > 0
            && membershipId > 0
            && count > 0;
    }

    private static Dictionary<string, ActivityCompletionAggregate> ToCompletionAggregates(
        IReadOnlyDictionary<string, ActivityCompletionAccumulator> source)
    {
        return source.ToDictionary(
            item => item.Key,
            item => new ActivityCompletionAggregate(item.Key)
            {
                CompletionCount = item.Value.CompletionCount,
                ContestClear = item.Value.ContestClear,
                FlawlessClear = item.Value.FlawlessClear,
                SoloClear = item.Value.SoloClear,
                SoloFlawlessClear = item.Value.SoloFlawlessClear
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveCompletionAggregates(
        Dictionary<string, ActivityCompletionAccumulator> target,
        IReadOnlyDictionary<string, ActivityCompletionAggregate> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target[item.Key] = new ActivityCompletionAccumulator
            {
                CompletionCount = item.Value.CompletionCount,
                ContestClear = item.Value.ContestClear,
                FlawlessClear = item.Value.FlawlessClear,
                SoloClear = item.Value.SoloClear,
                SoloFlawlessClear = item.Value.SoloFlawlessClear
            };
        }
    }

    private static class ActivityModes
    {
        public const int Raid = 4;
        public const int AllPvP = 5;
        public const int Patrol = 6;
        public const int AllPvE = 7;
        public const int AllPvECompetitive = 64;
        public const int Gambit = 63;
        public const int GambitPrime = 75;
        public const int Dungeon = 82;
    }

    private sealed class ManifestContext(DestinyManifest manifest, CrawlerService service)
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<JObject>>> _tables = new(StringComparer.Ordinal);

        public DestinyManifest Manifest => manifest;

        public JObject PresentationNodes => GetTable("DestinyPresentationNodeDefinition");
        public JObject Records => GetTable("DestinyRecordDefinition");

        public async Task<JObject> GetTableAsync(string tableName, CancellationToken cancellationToken)
        {
            return await _tables.GetOrAdd(tableName, name => new Lazy<Task<JObject>>(() => service.GetManifestTableAsync(manifest, name, cancellationToken)))
                .Value
                .ConfigureAwait(false);
        }

        public uint? FindMetricHash(params string[] terms)
        {
            var metrics = GetTable("DestinyMetricDefinition");
            foreach (var property in metrics.Properties())
            {
                var name = property.Value["displayProperties"]?["name"]?.Value<string>() ?? "";
                var description = property.Value["displayProperties"]?["description"]?.Value<string>() ?? "";
                if (terms.All(term =>
                        name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || description.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    return uint.Parse(property.Name);
                }
            }

            return null;
        }

        private JObject GetTable(string tableName)
        {
            return GetTableAsync(tableName, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
