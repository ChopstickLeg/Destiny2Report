using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    public async Task<ActivityPlaytimeAggregateReport?> GetActivityPlaytimeReportAsync(
        int membershipTypeId,
        long membershipId,
        ActivityPlaytimeMode activityMode,
        CancellationToken cancellationToken)
    {
        var mode = activityMode switch
        {
            ActivityPlaytimeMode.PvE => ActivityModes.AllPvE,
            ActivityPlaytimeMode.PvP => ActivityModes.AllPvP,
            ActivityPlaytimeMode.Gambit => ActivityModes.AllPvECompetitive,
            _ => 0
        };
        var accumulators = mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators");
        var filter = Builders<CrawlAccumulator>.Filter.Eq(item => item.PlatformId, membershipTypeId)
            & Builders<CrawlAccumulator>.Filter.Eq(item => item.PlayerMembershipId, membershipId);
        var playtime = await accumulators.Find(filter)
            .Project(item => item.PlaytimeByActivityMode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (playtime is null || !playtime.TryGetValue(mode.ToString(), out var aggregate))
        {
            return null;
        }

        return new ActivityPlaytimeAggregateReport
        {
            ActivityMode = activityMode.ToString(),
            TotalPlaytime = TimeSpan.FromSeconds(aggregate.TotalSeconds),
            Modes = aggregate.MostSpecificModeSeconds
                .Select(item => new ActivityModePlaytimeBreakdown
                {
                    Mode = int.TryParse(item.Key, out var parsed) ? parsed : 0,
                    ModeName = GetSpecificActivityModeName(int.TryParse(item.Key, out var value) ? value : 0),
                    Playtime = TimeSpan.FromSeconds(item.Value)
                })
                .OrderByDescending(item => item.Playtime)
                .ThenBy(item => item.Mode)
                .ToList()
        };
    }
}
