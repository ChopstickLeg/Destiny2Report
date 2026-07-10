using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record PlayerEncounterAggregate
{
    [BsonElement("ot")]
    public int OwnerMembershipType { get; init; }
    [BsonElement("oi")]
    public long OwnerMembershipId { get; init; }
    [BsonElement("et")]
    public int EncounteredMembershipType { get; init; }
    [BsonElement("ei")]
    public long EncounteredMembershipId { get; init; }
    [BsonElement("c")]
    public int Count { get; init; }
}
