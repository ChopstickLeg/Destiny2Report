using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record EmblemAggregate
{
    [BsonElement("ot")]
    public int OwnerMembershipType { get; set; }
    [BsonElement("oi")]
    public long OwnerMembershipId { get; set; }
    [BsonElement("h")]
    public long EmblemHash { get; set; }
    [BsonElement("s")]
    public long TotalSeconds { get; set; }
}
