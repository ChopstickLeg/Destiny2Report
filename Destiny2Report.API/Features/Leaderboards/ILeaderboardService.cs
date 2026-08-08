using Destiny2Report.API.Features.Crawler.Models;

namespace Destiny2Report.API.Features.Leaderboards;

public interface ILeaderboardService
{
    Task PublishPlayerAsync(DestinyReport report, IReadOnlyCollection<LeaderboardMetric> metrics, CancellationToken cancellationToken);
    Task RemovePlayerAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken);
    Task<LeaderboardCatalogResponse> GetCatalogAsync(CancellationToken cancellationToken);
    Task<LeaderboardBoard?> GetBoardAsync(string metricKey, CancellationToken cancellationToken);
    Task<PlayerLeaderboardStandingsResponse> GetPlayerStandingsAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken);
    Task RefreshPercentileThresholdsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> TakeRepairsAsync(int count, CancellationToken cancellationToken);
    Task RequeueRepairsAsync(IEnumerable<string> metricKeys);
    Task MarkRepairingAsync(IEnumerable<string> metricKeys, bool isRepairing, string? error, CancellationToken cancellationToken);
    Task ReplaceRepairedBoardsAsync(IReadOnlyDictionary<string, RepairedLeaderboard> boards, CancellationToken cancellationToken);
}

public sealed record RepairedLeaderboard(LeaderboardMetric Definition, IReadOnlyCollection<LeaderboardStoredEntry> Entries);
