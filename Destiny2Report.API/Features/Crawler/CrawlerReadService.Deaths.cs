using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerReadService
{
    public async Task<DeathActivityModeAggregateReport?> GetDeathActivityModeReportAsync(
        int membershipTypeId,
        long membershipId,
        DeathActivityMode activityMode,
        CancellationToken cancellationToken)
    {
        var storedActivityMode = ToStoredDeathActivityMode(activityMode);
        var deaths = mongoDatabase.GetCollection<DeathAggregate>("death_aggregates");
        var filter = Builders<DeathAggregate>.Filter.Eq(death => death.OwnerMembershipType, membershipTypeId)
            & Builders<DeathAggregate>.Filter.Eq(death => death.OwnerMembershipId, membershipId)
            & Builders<DeathAggregate>.Filter.Eq(death => death.ActivityMode, storedActivityMode);
        var aggregates = await deaths.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (aggregates.Count == 0)
        {
            return null;
        }

        return new DeathActivityModeAggregateReport
        {
            ActivityMode = storedActivityMode,
            Deaths = aggregates.Sum(aggregate => aggregate.Deaths),
            Modes = aggregates
                .OrderByDescending(aggregate => aggregate.Deaths)
                .ThenBy(aggregate => aggregate.SpecificActivityMode)
                .Select(aggregate => new DeathModeAggregateReport
                {
                    SpecificActivityModeId = aggregate.SpecificActivityMode,
                    SpecificActivityMode = GetSpecificActivityModeName(aggregate.SpecificActivityMode),
                    Deaths = aggregate.Deaths
                })
                .ToList()
        };
    }

    private static string ToStoredDeathActivityMode(DeathActivityMode activityMode) => activityMode switch
    {
        DeathActivityMode.PvP => "Crucible",
        DeathActivityMode.PvE => "PvE",
        DeathActivityMode.Gambit => "Gambit",
        _ => ""
    };
}
