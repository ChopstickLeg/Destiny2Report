using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Leaderboards;

namespace Destiny2Report.API.Features.Crawler;

public interface ICrawlerService
{
    Task WarmReportReadModelsAsync(CancellationToken cancellationToken);

    Task CrawlAsync(int platformId, long playerMembershipId, ICrawlProgress? progress, CancellationToken cancellationToken);

    Task<WeaponActivityModeAggregateReport?> GetWeaponActivityModeReportAsync(
        int membershipTypeId,
        long membershipId,
        WeaponActivityMode activityMode,
        CancellationToken cancellationToken);

    Task<DeathActivityModeAggregateReport?> GetDeathActivityModeReportAsync(
        int membershipTypeId,
        long membershipId,
        DeathActivityMode activityMode,
        CancellationToken cancellationToken);

    Task<ActivityPlaytimeAggregateReport?> GetActivityPlaytimeReportAsync(
        int membershipTypeId,
        long membershipId,
        ActivityPlaytimeMode activityMode,
        CancellationToken cancellationToken);

    Task<StoryVisualAssetsReport> GetStoryVisualAssetsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyCollection<LeaderboardMetric>> GetLeaderboardMetricsAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken);
}
