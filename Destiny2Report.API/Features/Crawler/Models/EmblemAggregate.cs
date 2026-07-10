using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record EmblemAggregate
{
    public int OwnerMembershipType { get; set; }
    public long OwnerMembershipId { get; set; }
    public long EmblemHash { get; set; }
    public string EmblemName { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string BackgroundUrl { get; set; } = "";
    public long TotalSeconds { get; set; }
}
