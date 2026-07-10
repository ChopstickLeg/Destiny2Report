using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record PlayerEncounterAggregate
{
    public int OwnerMembershipType { get; init; }
    public long OwnerMembershipId { get; init; }
    public int EncounteredMembershipType { get; init; }
    public long EncounteredMembershipId { get; init; }
    public int Count { get; init; }
}
