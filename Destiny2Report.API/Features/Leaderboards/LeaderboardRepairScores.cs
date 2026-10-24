using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Leaderboards;

internal sealed record RepairPlayer(int MembershipTypeId, long MembershipId, string DisplayName, int DisplayCode, IReadOnlyList<EmblemReport> MostUsedEmblems, DateTime SourceCrawledAtUtc)
{
    public string PlayerKey => $"{MembershipTypeId}:{MembershipId}";
}

/// <summary>Reads persisted scores in batches, rebuilding only missing or stale player snapshots.</summary>
internal sealed class LeaderboardRepairScores(IMongoDatabase database)
{
    private readonly IMongoCollection<PlayerLeaderboardSnapshot> snapshots = database.GetCollection<PlayerLeaderboardSnapshot>("leaderboard_player_scores");

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<PlayerLeaderboardScore>>> GetBatchAsync(
        IReadOnlyCollection<RepairPlayer> players,
        Func<int, long, CancellationToken, Task<IReadOnlyCollection<LeaderboardMetric>>> calculate,
        CancellationToken cancellationToken)
    {
        var ids = players.Select(player => player.PlayerKey).ToArray();
        var existing = (await snapshots.Find(Builders<PlayerLeaderboardSnapshot>.Filter.In(item => item.PlayerKey, ids))
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(item => item.PlayerKey, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<PlayerLeaderboardScore>>(StringComparer.Ordinal);
        foreach (var player in players)
        {
            existing.TryGetValue(player.PlayerKey, out var snapshot);
            if (snapshot?.IsRemoved == true)
            {
                result[player.PlayerKey] = [];
                continue;
            }
            if (snapshot is not null && CanReuse(snapshot, player.SourceCrawledAtUtc))
            {
                result[player.PlayerKey] = snapshot.Scores;
                continue;
            }
            var startedAt = DateTime.UtcNow;
            var metrics = await calculate(player.MembershipTypeId, player.MembershipId, cancellationToken).ConfigureAwait(false);
            var scores = metrics.Where(metric => LeaderboardMetricRules.IsPublishedMetric(metric.Key) && metric.Score > 0)
                .Select(metric => new PlayerLeaderboardScore(metric.Key, metric.Score)).ToList();
            result[player.PlayerKey] = scores;
            var replacement = new PlayerLeaderboardSnapshot
            {
                PlayerKey = player.PlayerKey,
                MembershipTypeId = player.MembershipTypeId,
                MembershipId = player.MembershipId,
                UpdatedAtUtc = startedAt,
                SourceCrawledAtUtc = player.SourceCrawledAtUtc,
                MetricVersion = PlayerLeaderboardSnapshot.CurrentMetricVersion,
                Scores = scores
            };
            if (snapshot is not null)
            {
                // A concurrent publication owns newer scores. Its repair follow-up remains queued.
                var filter = Builders<PlayerLeaderboardSnapshot>.Filter.Eq(item => item.PlayerKey, player.PlayerKey)
                    & Builders<PlayerLeaderboardSnapshot>.Filter.Ne(item => item.IsRemoved, true)
                    & Builders<PlayerLeaderboardSnapshot>.Filter.Eq(item => item.UpdatedAtUtc, snapshot.UpdatedAtUtc);
                var write = await snapshots.ReplaceOneAsync(filter,
                    replacement, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (write.MatchedCount == 0) result[player.PlayerKey] = await ReadWinningScoresAsync(player.PlayerKey, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try { await snapshots.InsertOneAsync(replacement, cancellationToken: cancellationToken).ConfigureAwait(false); }
                catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
                {
                    result[player.PlayerKey] = await ReadWinningScoresAsync(player.PlayerKey, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<PlayerLeaderboardScore>> ReadWinningScoresAsync(string key, CancellationToken cancellationToken)
    {
        var current = await snapshots.Find(item => item.PlayerKey == key).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return current is null || current.IsRemoved ? [] : current.Scores;
    }

    internal static bool CanReuse(PlayerLeaderboardSnapshot? snapshot, DateTime sourceCrawledAtUtc) =>
        snapshot is not null && snapshot.MetricVersion == PlayerLeaderboardSnapshot.CurrentMetricVersion
        && (snapshot.SourceCrawledAtUtc is { } source
            ? source.Ticks / TimeSpan.TicksPerMillisecond == sourceCrawledAtUtc.Ticks / TimeSpan.TicksPerMillisecond
            : snapshot.UpdatedAtUtc >= sourceCrawledAtUtc);
}
