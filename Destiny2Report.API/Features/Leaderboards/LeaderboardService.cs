using Destiny2Report.API.Features.Crawler.Models;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Text.Json;

namespace Destiny2Report.API.Features.Leaderboards;

public sealed class LeaderboardService(
    IMongoDatabase mongoDatabase,
    IConnectionMultiplexer redis,
    HybridCache cache,
    IOptions<LeaderboardsOptions> options,
    ILogger<LeaderboardService> logger) : ILeaderboardService
{
    private const string CollectionName = "leaderboard_boards";
    private const string RepairSetKey = "leaderboards:repairs";
    private const string ThresholdsKey = "leaderboards:percentile-thresholds:v1";
    private static readonly TimeSpan LockExpiry = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly IMongoCollection<LeaderboardBoard> boards = mongoDatabase.GetCollection<LeaderboardBoard>(CollectionName);
    private readonly IMongoCollection<PlayerLeaderboardSnapshot> snapshots = mongoDatabase.GetCollection<PlayerLeaderboardSnapshot>("leaderboard_player_scores");
    private readonly IDatabase redisDatabase = redis.GetDatabase();

    public async Task PublishPlayerAsync(DestinyReport report, IReadOnlyCollection<LeaderboardMetric> metrics, CancellationToken cancellationToken)
    {
        var positiveMetrics = metrics
            .Where(metric => metric.Score > 0 && LeaderboardMetricRules.IsPublishedMetric(metric.Key))
            .ToDictionary(metric => metric.Key, StringComparer.Ordinal);
        var existingKeys = await boards.Find(board => board.Entries.Any(entry =>
                entry.MembershipTypeId == report.PlatformId && entry.MembershipId == report.PlayerMembershipId))
            .Project(board => board.MetricKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var keys = existingKeys.Concat(positiveMetrics.Keys).Distinct(StringComparer.Ordinal).ToArray();
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (key, ct) =>
        {
            positiveMetrics.TryGetValue(key, out var metric);
            await UpdatePlayerOnBoardAsync(report, key, metric, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var snapshot = new PlayerLeaderboardSnapshot
        {
            PlayerKey = PlayerKey(report.PlatformId, report.PlayerMembershipId),
            MembershipTypeId = report.PlatformId,
            MembershipId = report.PlayerMembershipId,
            UpdatedAtUtc = DateTime.UtcNow,
            Scores = positiveMetrics.Values
                .Select(metric => new PlayerLeaderboardScore(metric.Key, metric.Score))
                .OrderBy(score => score.MetricKey, StringComparer.Ordinal)
                .ToList()
        };
        await snapshots.ReplaceOneAsync(item => item.PlayerKey == snapshot.PlayerKey, snapshot,
            new ReplaceOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePlayerAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken)
    {
        var affected = await boards.Find(board => board.Entries.Any(entry => entry.MembershipTypeId == membershipTypeId && entry.MembershipId == membershipId))
            .Project(board => board.MetricKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await Parallel.ForEachAsync(affected, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (key, ct) =>
        {
            await using var boardLock = await AcquireBoardLockAsync(key, ct).ConfigureAwait(false);
            var board = await boards.Find(item => item.MetricKey == key).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (board is null) return;
            var entries = board.Entries.Where(entry => entry.MembershipTypeId != membershipTypeId || entry.MembershipId != membershipId).ToList();
            if (entries.Count == board.Entries.Count) return;
            await ReplaceBoardAsync(board with { Entries = entries, UpdatedAtUtc = DateTime.UtcNow }, ct).ConfigureAwait(false);
            await redisDatabase.SetAddAsync(RepairSetKey, key).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await snapshots.DeleteOneAsync(item => item.PlayerKey == PlayerKey(membershipTypeId, membershipId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlayerLeaderboardStandingsResponse> GetPlayerStandingsAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken)
    {
        var snapshotTask = snapshots.Find(item => item.PlayerKey == PlayerKey(membershipTypeId, membershipId))
            .FirstOrDefaultAsync(cancellationToken);
        var thresholdsTask = ReadThresholdsAsync();
        var exactBoardProjection = Builders<LeaderboardBoard>.Projection
            .Include(board => board.MetricKey)
            .Include(board => board.Category)
            .Include(board => board.Title)
            .Include(board => board.Unit)
            .Include("Entries.MembershipTypeId")
            .Include("Entries.MembershipId")
            .Include("Entries.Score");
        var exactBoardsTask = boards.Find(board => board.Entries.Any(entry =>
                entry.MembershipTypeId == membershipTypeId && entry.MembershipId == membershipId))
            .Project<LeaderboardBoard>(exactBoardProjection)
            .ToListAsync(cancellationToken);

        await Task.WhenAll(snapshotTask, thresholdsTask, exactBoardsTask).ConfigureAwait(false);
        var snapshot = await snapshotTask.ConfigureAwait(false);
        var thresholds = await thresholdsTask.ConfigureAwait(false);
        var exactBoards = await exactBoardsTask.ConfigureAwait(false);
        var candidates = new List<(PlayerLeaderboardStanding Standing, double Strength)>();

        foreach (var board in exactBoards.Where(board => LeaderboardMetricRules.IsPublishedMetric(board.MetricKey)))
        {
            var standing = LeaderboardRanking.FindPlayerStanding(board.Entries, membershipTypeId, membershipId);
            if (standing is null) continue;
            var playerCount = thresholds?.Values.GetValueOrDefault(board.MetricKey)?.PlayerCount;
            var strength = playerCount > 0 ? (double)standing.Value.Rank / playerCount.Value : standing.Value.Rank / 1000d;
            candidates.Add((
                new PlayerLeaderboardStanding(board.MetricKey, board.Category, board.Title, board.Unit, standing.Value.Score, "top-1000", standing.Value.Rank),
                strength));
        }

        var exactKeys = candidates.Select(item => item.Standing.MetricKey).ToHashSet(StringComparer.Ordinal);
        var remainingScores = (snapshot?.Scores ?? []).Where(score => !exactKeys.Contains(score.MetricKey)).ToArray();
        var remainingKeys = remainingScores.Select(score => score.MetricKey).ToArray();
        var definitionProjection = Builders<LeaderboardBoard>.Projection
            .Include(board => board.MetricKey)
            .Include(board => board.Category)
            .Include(board => board.Title)
            .Include(board => board.Unit);
        var definitions = remainingKeys.Length == 0
            ? new Dictionary<string, LeaderboardBoard>(StringComparer.Ordinal)
            : (await boards.Find(Builders<LeaderboardBoard>.Filter.In(board => board.MetricKey, remainingKeys))
                .Project<LeaderboardBoard>(definitionProjection)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(board => board.MetricKey, StringComparer.Ordinal);

        foreach (var score in remainingScores)
        {
            if (thresholds is null || !thresholds.Values.TryGetValue(score.MetricKey, out var cutoff)) continue;
            var tier = LeaderboardStandingRules.PercentileTier(score.Score, cutoff);
            if (tier is null || !definitions.TryGetValue(score.MetricKey, out var board)) continue;
            var strength = tier == "top-0.1" ? .001 : tier == "top-1" ? .01 : .05;
            candidates.Add((
                new PlayerLeaderboardStanding(score.MetricKey, board.Category, board.Title, board.Unit, score.Score, tier, null),
                strength));
        }

        return new PlayerLeaderboardStandingsResponse(thresholds?.UpdatedAtUtc,
            candidates
                .OrderBy(item => item.Standing.Rank is null ? 1 : 0)
                .ThenBy(item => item.Standing.Rank ?? int.MaxValue)
                .ThenBy(item => item.Strength)
                .ThenBy(item => item.Standing.Title, StringComparer.Ordinal)
                .Take(5)
                .Select(item => item.Standing)
                .ToArray());
    }

    public async Task RefreshPercentileThresholdsAsync(CancellationToken cancellationToken)
    {
        var scores = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        using var cursor = await snapshots.Find(FilterDefinition<PlayerLeaderboardSnapshot>.Empty)
            .Project(item => item.Scores).ToCursorAsync(cancellationToken).ConfigureAwait(false);
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            foreach (var playerScores in cursor.Current)
                foreach (var score in playerScores)
                    if (LeaderboardMetricRules.IsPublishedMetric(score.MetricKey) && score.Score > 0)
                    {
                        if (!scores.TryGetValue(score.MetricKey, out var values)) scores[score.MetricKey] = values = [];
                        values.Add(score.Score);
                    }

        var updatedAt = DateTimeOffset.UtcNow;
        var valuesByMetric = scores.ToDictionary(pair => pair.Key, pair =>
        {
            pair.Value.Sort((left, right) => right.CompareTo(left));
            return new LeaderboardPercentileThresholds(
                LeaderboardStandingRules.Cutoff(pair.Value, .001),
                LeaderboardStandingRules.Cutoff(pair.Value, .01),
                LeaderboardStandingRules.Cutoff(pair.Value, .05),
                pair.Value.Count,
                updatedAt);
        }, StringComparer.Ordinal);
        var payload = new CachedThresholds(updatedAt, valuesByMetric);
        await redisDatabase.StringSetAsync(ThresholdsKey, JsonSerializer.Serialize(payload), TimeSpan.FromDays(2)).ConfigureAwait(false);
        logger.LogInformation("Refreshed percentile thresholds for {MetricCount} leaderboard metrics.", valuesByMetric.Count);
    }

    public async Task<LeaderboardCatalogResponse> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var completedFilter = Builders<DestinyReport>.Filter.Eq(report => report.HasCompletedCrawl, true)
            & Builders<DestinyReport>.Filter.Ne(report => report.CrawlState, DestinyReport.CrawlStatePrivate);
        var completed = await mongoDatabase.GetCollection<DestinyReport>("destiny_reports")
            .CountDocumentsAsync(completedFilter, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var publishedBoards = Builders<LeaderboardBoard>.Filter.Nin(
            board => board.MetricKey,
            LeaderboardMetricRules.ExcludedMetricKeys);
        var definitions = await boards.Find(publishedBoards)
            .Project(board => new LeaderboardDefinitionResponse(board.MetricKey, board.Category, board.Title, board.Description, board.Unit, board.DisplayOrder, board.Entries.Count, board.IsRepairing))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        definitions.Sort((left, right) =>
        {
            var category = StringComparer.Ordinal.Compare(left.Category, right.Category);
            return category != 0 ? category : left.DisplayOrder != right.DisplayOrder ? left.DisplayOrder.CompareTo(right.DisplayOrder) : StringComparer.Ordinal.Compare(left.Title, right.Title);
        });
        return new LeaderboardCatalogResponse(completed >= options.Value.MinimumCompletedPlayers, completed, options.Value.MinimumCompletedPlayers, definitions);
    }

    public async Task<LeaderboardBoard?> GetBoardAsync(string metricKey, CancellationToken cancellationToken)
    {
        if (!LeaderboardMetricRules.IsPublishedMetric(metricKey)) return null;
        return await cache.GetOrCreateAsync<LeaderboardBoard?>(
            CacheKey(metricKey),
            async ct => await boards.Find(board => board.MetricKey == metricKey).FirstOrDefaultAsync(ct).ConfigureAwait(false),
            new HybridCacheEntryOptions { Expiration = CacheDuration, LocalCacheExpiration = CacheDuration },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> TakeRepairsAsync(int count, CancellationToken cancellationToken)
    {
        var result = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await redisDatabase.SetPopAsync(RepairSetKey).ConfigureAwait(false);
            if (value.IsNullOrEmpty) break;
            var key = value.ToString();
            if (!LeaderboardMetricRules.IsPublishedMetric(key))
            {
                index--;
                continue;
            }
            result.Add(key);
        }
        return result;
    }

    public async Task RequeueRepairsAsync(IEnumerable<string> metricKeys)
    {
        var values = metricKeys.Distinct(StringComparer.Ordinal).Select(key => (RedisValue)key).ToArray();
        if (values.Length > 0) await redisDatabase.SetAddAsync(RepairSetKey, values).ConfigureAwait(false);
    }

    public async Task MarkRepairingAsync(IEnumerable<string> metricKeys, bool isRepairing, string? error, CancellationToken cancellationToken)
    {
        foreach (var key in metricKeys.Distinct(StringComparer.Ordinal))
        {
            await using var boardLock = await AcquireBoardLockAsync(key, cancellationToken).ConfigureAwait(false);
            var update = Builders<LeaderboardBoard>.Update.Set(board => board.IsRepairing, isRepairing).Set(board => board.RepairError, error ?? "");
            await boards.UpdateOneAsync(board => board.MetricKey == key, update, cancellationToken: cancellationToken).ConfigureAwait(false);
            await cache.RemoveAsync(CacheKey(key), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReplaceRepairedBoardsAsync(IReadOnlyDictionary<string, RepairedLeaderboard> repaired, CancellationToken cancellationToken)
    {
        foreach (var (key, result) in repaired)
        {
            await using var boardLock = await AcquireBoardLockAsync(key, cancellationToken).ConfigureAwait(false);
            var definition = result.Definition;
            var replacement = new LeaderboardBoard
            {
                MetricKey = key,
                Category = definition.Category,
                Title = definition.Title,
                Description = definition.Description,
                Unit = definition.Unit,
                DisplayOrder = definition.DisplayOrder,
                UpdatedAtUtc = DateTime.UtcNow,
                IsRepairing = false,
                Entries = LeaderboardRanking.SortAndLimit(result.Entries)
            };
            await ReplaceBoardAsync(replacement, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdatePlayerOnBoardAsync(DestinyReport report, string key, LeaderboardMetric? metric, CancellationToken cancellationToken)
    {
        await using var boardLock = await AcquireBoardLockAsync(key, cancellationToken).ConfigureAwait(false);
        var board = await boards.Find(item => item.MetricKey == key).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var oldEntry = board?.Entries.FirstOrDefault(entry => entry.MembershipTypeId == report.PlatformId && entry.MembershipId == report.PlayerMembershipId);
        if (board is null && metric is null) return;

        var entries = (board?.Entries ?? []).Where(entry => entry.MembershipTypeId != report.PlatformId || entry.MembershipId != report.PlayerMembershipId).ToList();
        if (metric is not null)
        {
            entries.Add(new LeaderboardStoredEntry
            {
                MembershipTypeId = report.PlatformId,
                MembershipId = report.PlayerMembershipId,
                DisplayName = report.DisplayName,
                DisplayCode = report.DisplayCode,
                EmblemBackgroundUrl = report.MostUsedEmblems.FirstOrDefault()?.BackgroundUrl ?? "",
                Score = metric.Score,
                SourceCrawledAtUtc = report.LastCrawledAtUtc ?? report.CrawledAt
            });
        }
        entries = LeaderboardRanking.SortAndLimit(entries);
        var definition = metric ?? new LeaderboardMetric(key, board!.Category, board.Title, board.Description, board.Unit, board.DisplayOrder, 0);
        var replacement = new LeaderboardBoard
        {
            MetricKey = key,
            Category = definition.Category,
            Title = definition.Title,
            Description = definition.Description,
            // Seed new boards from the metric definition, but preserve database-managed ordering thereafter.
            Unit = definition.Unit,
            DisplayOrder = board?.DisplayOrder ?? definition.DisplayOrder,
            UpdatedAtUtc = DateTime.UtcNow,
            IsRepairing = board?.IsRepairing ?? false,
            RepairError = board?.RepairError ?? "",
            Entries = entries
        };
        await ReplaceBoardAsync(replacement, cancellationToken).ConfigureAwait(false);
        // A repair builds its replacement outside the board lock. If this publish
        // lands during that scan, schedule one follow-up repair so the stale
        // replacement cannot remain authoritative.
        var publishedDuringRepair = board?.IsRepairing == true;
        var existingEntryMayChangeTheCutoff = oldEntry is not null
            && (metric is null || metric.Score < oldEntry.Score
                || !string.Equals(oldEntry.DisplayName, report.DisplayName, StringComparison.Ordinal)
                || oldEntry.DisplayCode != report.DisplayCode
                || !string.Equals(oldEntry.EmblemBackgroundUrl, report.MostUsedEmblems.FirstOrDefault()?.BackgroundUrl ?? "", StringComparison.Ordinal));
        if (publishedDuringRepair || existingEntryMayChangeTheCutoff)
            await redisDatabase.SetAddAsync(RepairSetKey, key).ConfigureAwait(false);
    }

    private async Task ReplaceBoardAsync(LeaderboardBoard board, CancellationToken cancellationToken)
    {
        if (board.Entries.Count > LeaderboardBoard.MaximumEntries) throw new InvalidOperationException("A leaderboard board cannot persist more than 1,000 entries.");
        await boards.ReplaceOneAsync(item => item.MetricKey == board.MetricKey, board, new ReplaceOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
        await cache.RemoveAsync(CacheKey(board.MetricKey), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IAsyncDisposable> AcquireBoardLockAsync(string metricKey, CancellationToken cancellationToken)
    {
        var lockKey = $"leaderboards:lock:{metricKey}";
        var token = Guid.NewGuid().ToString("N");
        while (!await redisDatabase.StringSetAsync(lockKey, token, LockExpiry, When.NotExists).ConfigureAwait(false))
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return new RedisLock(redisDatabase, lockKey, token, logger);
    }

    private static string CacheKey(string metricKey) => $"leaderboards:board:{metricKey}";
    private static string PlayerKey(int membershipTypeId, long membershipId) => $"{membershipTypeId}:{membershipId}";

    private async Task<CachedThresholds?> ReadThresholdsAsync()
    {
        var value = await redisDatabase.StringGetAsync(ThresholdsKey).ConfigureAwait(false);
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<CachedThresholds>(value.ToString());
    }

    private sealed record CachedThresholds(DateTimeOffset UpdatedAtUtc, Dictionary<string, LeaderboardPercentileThresholds> Values);

    private sealed class RedisLock(IDatabase database, string key, string token, ILogger logger) : IAsyncDisposable
    {
        private const string ReleaseScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
        public async ValueTask DisposeAsync()
        {
            try { await database.ScriptEvaluateAsync(ReleaseScript, [(RedisKey)key], [(RedisValue)token]).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not release leaderboard lock {LockKey}.", key); }
        }
    }
}
