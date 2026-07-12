using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record DeathAggregate
{
    [BsonElement("ot")]
    public int OwnerMembershipType { get; set; }
    [BsonElement("oi")]
    public long OwnerMembershipId { get; set; }
    [BsonElement("am")]
    public string ActivityMode { get; set; } = "";
    [BsonElement("sm")]
    public int SpecificActivityMode { get; set; }
    [BsonElement("d")]
    public long Deaths { get; set; }
}
