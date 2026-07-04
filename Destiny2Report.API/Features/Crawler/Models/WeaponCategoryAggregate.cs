using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record WeaponCategoryAggregate
{
    public int OwnerMembershipType { get; set; }
    public long OwnerMembershipId { get; set; }
    public string ActivityMode { get; set; } = "";
    public string CategoryKey { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public int Kills { get; set; }
}
