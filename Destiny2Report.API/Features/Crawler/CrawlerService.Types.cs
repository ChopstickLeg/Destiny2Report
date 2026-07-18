using System.Buffers.Binary;
using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Crawler.Models.Bungie;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Diagnostics;
using BungiePlayer = D2Report.BungieClient.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private const int MinPersistedPlayerEncounterCount = 2;
    private const int EncounteredPlayerKeySize = 9;

    private sealed record ActivityCompletionAggregate(string ActivityName)
    {
        public int ActivityCount { get; set; }
        public int CompletionCount { get; set; }
        public RaidFirstCompletion? FirstCompletion { get; set; }
        public RaidFirstCompletion? LastCompletion { get; set; }
        public ActivityFastestCompletion? FastestCompletion { get; set; }
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

    private readonly record struct ActivityReference(long InstanceId, DateTimeOffset Period);

    private sealed class ActivityCrawlState
    {
        private readonly List<ActivityReference> _recentActivities = [];

        public DateTimeOffset NewestActivityPeriod { get; private set; }

        public IReadOnlyCollection<ActivityReference> RecentActivities => _recentActivities;

        public void AddFetched(IEnumerable<DestinyHistoricalStatsPeriodGroup> activities)
        {
            foreach (var activity in activities)
            {
                AddFetched(activity);
            }
        }

        public void AddFetched(DestinyHistoricalStatsPeriodGroup activity)
        {
            var instanceId = activity.ActivityDetails.InstanceId;
            if (instanceId <= 0)
            {
                return;
            }

            if (activity.Period > NewestActivityPeriod)
            {
                NewestActivityPeriod = activity.Period;
            }

            _recentActivities.Add(new ActivityReference(instanceId, activity.Period));
        }
    }

    private sealed class PrivatePlayerUnavailableException(string operation, string resource, BungieResponse response)
        : InvalidOperationException($"{operation} failed because the Destiny {resource} is not public. Bungie error code {response.ErrorCode}: {response.Message}");

    private sealed record WeaponDefinitionSummary(string Name, string IconUrl, string CategoryName, string CategoryKey);

    private sealed record EmblemDefinitionSummary(string Name, string IconUrl, string BackgroundUrl);

    private sealed class WeaponKillDelta
    {
        public int UniqueWeaponKills { get; set; }
        public int WeaponKills { get; set; }
        public int GrenadeKills { get; set; }
        public int MeleeKills { get; set; }
        public int SuperKills { get; set; }
        public int UnknownKills { get; set; }
        public int TotalKills => UniqueWeaponKills + WeaponKills + GrenadeKills + MeleeKills + SuperKills + UnknownKills;

        public void Add(WeaponKillDelta delta)
        {
            UniqueWeaponKills += delta.UniqueWeaponKills;
            WeaponKills += delta.WeaponKills;
            GrenadeKills += delta.GrenadeKills;
            MeleeKills += delta.MeleeKills;
            SuperKills += delta.SuperKills;
            UnknownKills += delta.UnknownKills;
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
        accumulator.LastSuccessfulCrawlAt = DateTime.UtcNow;
        accumulator.NeedsFullRecrawl = false;
        accumulator.FullRecrawlReason = "";

        var newestFetchedActivity = fetchedActivities
            .OrderByDescending(activity => activity.Period)
            .FirstOrDefault();
        if (newestFetchedActivity is not null && newestFetchedActivity.Period > accumulator.NewestActivityPeriod)
        {
            accumulator.NewestActivityPeriod = newestFetchedActivity.Period.UtcDateTime;
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

    private static void UpdateAccumulatorCrawlStateFromState(
        CrawlAccumulator accumulator,
        ActivityCrawlState crawlState,
        IEnumerable<long> processedActivityIds)
    {
        accumulator.LastSuccessfulCrawlAt = DateTime.UtcNow;
        accumulator.NeedsFullRecrawl = false;
        accumulator.FullRecrawlReason = "";

        if (crawlState.NewestActivityPeriod > accumulator.NewestActivityPeriod)
        {
            accumulator.NewestActivityPeriod = crawlState.NewestActivityPeriod.UtcDateTime;
        }

        accumulator.RecentActivityInstanceIds = crawlState.RecentActivities
            .OrderByDescending(activity => activity.Period)
            .Select(activity => activity.InstanceId)
            .Concat(processedActivityIds.Where(instanceId => instanceId > 0))
            .Concat(accumulator.RecentActivityInstanceIds)
            .Distinct()
            .Take(RecentActivityInstanceIdLimit)
            .ToList();
    }

    private static HashSet<(int MembershipType, long MembershipId)> ReadEncounteredPlayerKeys(CrawlAccumulator accumulator)
    {
        var keys = new HashSet<(int MembershipType, long MembershipId)>();
        var encoded = accumulator.EncounteredPlayerKeys;
        for (var offset = 0; offset + EncounteredPlayerKeySize <= encoded.Length; offset += EncounteredPlayerKeySize)
        {
            var membershipType = encoded[offset];
            var membershipId = BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(offset + 1, sizeof(long)));
            if (IsCountablePlayerEncounter(membershipType, membershipId, 1))
            {
                keys.Add((membershipType, membershipId));
            }
        }

        return keys;
    }

    private static void SaveEncounteredPlayerKeys(
        CrawlAccumulator accumulator,
        IEnumerable<(int MembershipType, long MembershipId)> encounteredPlayers)
    {
        var keys = encounteredPlayers
            .Where(player => IsCountablePlayerEncounter(player.MembershipType, player.MembershipId, 1))
            .Distinct()
            .OrderBy(player => player.MembershipType)
            .ThenBy(player => player.MembershipId)
            .ToArray();
        var encoded = new byte[keys.Length * EncounteredPlayerKeySize];

        for (var index = 0; index < keys.Length; index++)
        {
            var offset = index * EncounteredPlayerKeySize;
            encoded[offset] = checked((byte)keys[index].MembershipType);
            BinaryPrimitives.WriteInt64LittleEndian(encoded.AsSpan(offset + 1, sizeof(long)), keys[index].MembershipId);
        }

        accumulator.EncounteredPlayerKeys = encoded;
        accumulator.UniquePlayersPlayedWith = keys.Length;
    }

    private static bool IsPersistablePlayerEncounter(int membershipType, long membershipId, int count)
    {
        return membershipType > 0
            && membershipId > 0
            && count >= MinPersistedPlayerEncounterCount;
    }

    private static bool IsCountablePlayerEncounter(int membershipType, long membershipId, int count)
    {
        return membershipType is > 0 and <= byte.MaxValue
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
                ActivityCount = item.Value.ActivityCount,
                CompletionCount = item.Value.CompletionCount,
                FirstCompletion = item.Value.FirstCompletion,
                LastCompletion = item.Value.LastCompletion,
                FastestCompletion = item.Value.FastestCompletion,
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
                ActivityCount = item.Value.ActivityCount,
                CompletionCount = item.Value.CompletionCount,
                FirstCompletion = item.Value.FirstCompletion,
                LastCompletion = item.Value.LastCompletion,
                FastestCompletion = item.Value.FastestCompletion,
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
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _tables = new(StringComparer.Ordinal);

        public DestinyManifest Manifest => manifest;

        public IReadOnlyDictionary<string, ManifestPresentationNodeDefinition> PresentationNodes => GetTable<ManifestPresentationNodeDefinition>("DestinyPresentationNodeDefinition");
        public IReadOnlyDictionary<string, ManifestRecordDefinition> Records => GetTable<ManifestRecordDefinition>("DestinyRecordDefinition");

        public Task<IReadOnlyDictionary<string, ManifestActivityDefinition>> GetActivityDefinitionsAsync(CancellationToken cancellationToken) => GetTableAsync<ManifestActivityDefinition>("DestinyActivityDefinition", cancellationToken);

        public Task<IReadOnlyDictionary<string, ManifestActivityModeDefinition>> GetActivityModeDefinitionsAsync(CancellationToken cancellationToken) => GetTableAsync<ManifestActivityModeDefinition>("DestinyActivityModeDefinition", cancellationToken);

        public Task<IReadOnlyDictionary<string, ManifestDestinationDefinition>> GetDestinationDefinitionsAsync(CancellationToken cancellationToken) => GetTableAsync<ManifestDestinationDefinition>("DestinyDestinationDefinition", cancellationToken);

        public Task<IReadOnlyDictionary<string, ManifestCharacterIdentityDefinition>> GetClassDefinitionsAsync(CancellationToken cancellationToken) => GetTableAsync<ManifestCharacterIdentityDefinition>("DestinyClassDefinition", cancellationToken);

        public Task<IReadOnlyDictionary<string, ManifestCharacterIdentityDefinition>> GetRaceDefinitionsAsync(CancellationToken cancellationToken) => GetTableAsync<ManifestCharacterIdentityDefinition>("DestinyRaceDefinition", cancellationToken);

        public async Task<IReadOnlyDictionary<string, TDefinition>> GetTableAsync<TDefinition>(string tableName, CancellationToken cancellationToken)
        {
            var table = await _tables.GetOrAdd(tableName, name => new Lazy<Task<object>>(async () => await service.GetManifestTableAsync<TDefinition>(manifest, name, CancellationToken.None).ConfigureAwait(false)))
                .Value
                .ConfigureAwait(false);
            return (IReadOnlyDictionary<string, TDefinition>)table;
        }

        public uint? FindMetricHash(params string[] terms)
        {
            var metrics = GetTable<ManifestMetricDefinition>("DestinyMetricDefinition");
            foreach (var metric in metrics)
            {
                var name = metric.Value.DisplayProperties?.Name ?? "";
                var description = metric.Value.DisplayProperties?.Description ?? "";
                if (terms.All(term =>
                        name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || description.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    return uint.Parse(metric.Key);
                }
            }

            return null;
        }

        private IReadOnlyDictionary<string, TDefinition> GetTable<TDefinition>(string tableName)
        {
            return GetTableAsync<TDefinition>(tableName, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
