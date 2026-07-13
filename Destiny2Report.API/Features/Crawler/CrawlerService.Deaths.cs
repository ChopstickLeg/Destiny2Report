using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
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

    private async Task ApplyDeathAggregateDeltasAsync(
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<int, long> pveDeathDeltas,
        IReadOnlyDictionary<int, long> pvpDeathDeltas,
        IReadOnlyDictionary<int, long> gambitDeathDeltas,
        bool resetDeathAggregates,
        CancellationToken cancellationToken)
    {
        var deaths = mongoDatabase.GetCollection<DeathAggregate>("death_aggregates");
        var ownerFilter = Builders<DeathAggregate>.Filter.Eq(death => death.OwnerMembershipType, ownerMembershipType)
            & Builders<DeathAggregate>.Filter.Eq(death => death.OwnerMembershipId, ownerMembershipId);

        if (resetDeathAggregates)
        {
            await deaths.DeleteManyAsync(ownerFilter, cancellationToken).ConfigureAwait(false);
        }

        var writes = BuildDeathAggregateWrites(ownerMembershipType, ownerMembershipId, "PvE", pveDeathDeltas)
            .Concat(BuildDeathAggregateWrites(ownerMembershipType, ownerMembershipId, "Crucible", pvpDeathDeltas))
            .Concat(BuildDeathAggregateWrites(ownerMembershipType, ownerMembershipId, "Gambit", gambitDeathDeltas))
            .ToList();
        if (writes.Count > 0)
        {
            await deaths.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<WriteModel<DeathAggregate>> BuildDeathAggregateWrites(
        int ownerMembershipType,
        long ownerMembershipId,
        string activityMode,
        IReadOnlyDictionary<int, long> deathDeltasByMode)
    {
        return deathDeltasByMode
            .Where(item => item.Value > 0)
            .Select(item =>
            {
                var filter = Builders<DeathAggregate>.Filter.Eq(death => death.OwnerMembershipType, ownerMembershipType)
                    & Builders<DeathAggregate>.Filter.Eq(death => death.OwnerMembershipId, ownerMembershipId)
                    & Builders<DeathAggregate>.Filter.Eq(death => death.ActivityMode, activityMode)
                    & Builders<DeathAggregate>.Filter.Eq(death => death.SpecificActivityMode, item.Key);
                var update = Builders<DeathAggregate>.Update
                    .SetOnInsert(death => death.OwnerMembershipType, ownerMembershipType)
                    .SetOnInsert(death => death.OwnerMembershipId, ownerMembershipId)
                    .SetOnInsert(death => death.ActivityMode, activityMode)
                    .SetOnInsert(death => death.SpecificActivityMode, item.Key)
                    .Inc(death => death.Deaths, item.Value);
                return (WriteModel<DeathAggregate>)new UpdateOneModel<DeathAggregate>(filter, update) { IsUpsert = true };
            });
    }

    private static string ToStoredDeathActivityMode(DeathActivityMode activityMode) => activityMode switch
    {
        DeathActivityMode.PvP => "Crucible",
        DeathActivityMode.PvE => "PvE",
        DeathActivityMode.Gambit => "Gambit",
        _ => ""
    };
}
