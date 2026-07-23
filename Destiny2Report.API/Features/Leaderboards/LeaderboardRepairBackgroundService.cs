using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Leaderboards;

public sealed class LeaderboardRepairBackgroundService(
    IServiceProvider serviceProvider,
    IMongoDatabase mongoDatabase,
    ILeaderboardService leaderboardService,
    ILogger<LeaderboardRepairBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var keys = await leaderboardService.TakeRepairsAsync(20, stoppingToken).ConfigureAwait(false);
                if (keys.Count > 0) await RepairAsync(keys, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Leaderboard repair polling failed; retrying."); }

            try { await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RepairAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        await leaderboardService.MarkRepairingAsync(keys, true, null, cancellationToken).ConfigureAwait(false);
        try
        {
            var definitions = new Dictionary<string, LeaderboardMetric>(StringComparer.Ordinal);
            var candidates = keys.ToDictionary(key => key, _ => new SortedSet<LeaderboardStoredEntry>(BestEntryComparer.Instance), StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var board = await leaderboardService.GetBoardAsync(key, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Leaderboard '{key}' disappeared during repair.");
                definitions[key] = new LeaderboardMetric(key, board.Category, board.Title, board.Description, board.Unit, board.DisplayOrder, 0);
            }

            using var scope = serviceProvider.CreateScope();
            var crawler = scope.ServiceProvider.GetRequiredService<ICrawlerReadService>();
            var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
            var filter = Builders<DestinyReport>.Filter.Eq(report => report.HasCompletedCrawl, true)
                & Builders<DestinyReport>.Filter.Ne(report => report.CrawlState, DestinyReport.CrawlStatePrivate);
            using var cursor = await reports.Find(filter)
                .Project(report => new RepairPlayer(report.PlatformId, report.PlayerMembershipId, report.DisplayName, report.DisplayCode, report.MostUsedEmblems, report.LastCrawledAtUtc ?? report.CrawledAt))
                .ToCursorAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var player in cursor.Current)
                {
                    var metrics = await crawler.GetLeaderboardMetricsAsync(player.MembershipTypeId, player.MembershipId, cancellationToken).ConfigureAwait(false);
                    foreach (var metric in metrics)
                    {
                        if (!candidates.TryGetValue(metric.Key, out var set) || metric.Score <= 0) continue;
                        set.Add(new LeaderboardStoredEntry
                        {
                            MembershipTypeId = player.MembershipTypeId, MembershipId = player.MembershipId,
                            DisplayName = player.DisplayName, DisplayCode = player.DisplayCode,
                            EmblemBackgroundUrl = player.MostUsedEmblems.FirstOrDefault()?.BackgroundUrl ?? "",
                            Score = metric.Score, SourceCrawledAtUtc = player.SourceCrawledAtUtc
                        });
                        if (set.Count > LeaderboardBoard.MaximumEntries) set.Remove(set.Max!);
                    }
                }
            }

            var repaired = keys.ToDictionary(
                key => key,
                key => new RepairedLeaderboard(definitions[key], candidates[key].ToArray()),
                StringComparer.Ordinal);
            await leaderboardService.ReplaceRepairedBoardsAsync(repaired, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Repaired {LeaderboardCount} leaderboard(s).", keys.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not repair leaderboards {MetricKeys}.", string.Join(", ", keys));
            await leaderboardService.MarkRepairingAsync(keys, false, ex.Message, cancellationToken).ConfigureAwait(false);
            await leaderboardService.RequeueRepairsAsync(keys).ConfigureAwait(false);
        }
    }

    private sealed record RepairPlayer(int MembershipTypeId, long MembershipId, string DisplayName, int DisplayCode, IReadOnlyList<EmblemReport> MostUsedEmblems, DateTime SourceCrawledAtUtc);

    private sealed class BestEntryComparer : IComparer<LeaderboardStoredEntry>
    {
        public static readonly BestEntryComparer Instance = new();
        public int Compare(LeaderboardStoredEntry? left, LeaderboardStoredEntry? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return 1;
            if (right is null) return -1;
            var score = right.Score.CompareTo(left.Score);
            if (score != 0) return score;
            var name = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            if (name != 0) return name;
            var code = left.DisplayCode.CompareTo(right.DisplayCode);
            if (code != 0) return code;
            var type = left.MembershipTypeId.CompareTo(right.MembershipTypeId);
            return type != 0 ? type : left.MembershipId.CompareTo(right.MembershipId);
        }
    }
}
