using Destiny2Report.API.Features.Crawler.Models;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StackExchange.Redis;

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
    private static readonly TimeSpan LockExpiry = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly IMongoCollection<LeaderboardBoard> boards = mongoDatabase.GetCollection<LeaderboardBoard>(CollectionName);
    private readonly IDatabase redisDatabase = redis.GetDatabase();

    public async Task PublishPlayerAsync(DestinyReport report, IReadOnlyCollection<LeaderboardMetric> metrics, CancellationToken cancellationToken)
    {
        var positiveMetrics = metrics.Where(metric => metric.Score > 0).ToDictionary(metric => metric.Key, StringComparer.Ordinal);
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
    }

    public async Task<LeaderboardCatalogResponse> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var completedFilter = Builders<DestinyReport>.Filter.Eq(report => report.HasCompletedCrawl, true)
            & Builders<DestinyReport>.Filter.Ne(report => report.CrawlState, DestinyReport.CrawlStatePrivate);
        var completed = await mongoDatabase.GetCollection<DestinyReport>("destiny_reports")
            .CountDocumentsAsync(completedFilter, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var definitions = await boards.Find(FilterDefinition<LeaderboardBoard>.Empty)
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
            result.Add(value.ToString());
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
                MetricKey = key, Category = definition.Category, Title = definition.Title, Description = definition.Description,
                Unit = definition.Unit, DisplayOrder = definition.DisplayOrder, UpdatedAtUtc = DateTime.UtcNow,
                IsRepairing = false, Entries = LeaderboardRanking.SortAndLimit(result.Entries)
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
                MembershipTypeId = report.PlatformId, MembershipId = report.PlayerMembershipId, DisplayName = report.DisplayName,
                DisplayCode = report.DisplayCode, EmblemBackgroundUrl = report.MostUsedEmblems.FirstOrDefault()?.BackgroundUrl ?? "",
                Score = metric.Score, SourceCrawledAtUtc = report.LastCrawledAtUtc ?? report.CrawledAt
            });
        }
        entries = LeaderboardRanking.SortAndLimit(entries);
        var definition = metric ?? new LeaderboardMetric(key, board!.Category, board.Title, board.Description, board.Unit, board.DisplayOrder, 0);
        var replacement = new LeaderboardBoard
        {
            MetricKey = key, Category = definition.Category, Title = definition.Title, Description = definition.Description,
            // Seed new boards from the metric definition, but preserve database-managed ordering thereafter.
            Unit = definition.Unit, DisplayOrder = board?.DisplayOrder ?? definition.DisplayOrder, UpdatedAtUtc = DateTime.UtcNow,
            IsRepairing = board?.IsRepairing ?? false, RepairError = board?.RepairError ?? "", Entries = entries
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
