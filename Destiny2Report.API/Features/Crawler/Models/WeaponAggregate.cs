using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record WeaponAggregate
{
    public int OwnerMembershipType { get; set; }
    public long OwnerMembershipId { get; set; }
    public string ActivityMode { get; set; } = "";
    public string WeaponKey { get; set; } = "";
    public string WeaponName { get; set; } = "";
    public long? ReferenceId { get; set; }
    public string IconUrl { get; set; } = "";
    public string CategoryKey { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public int Kills { get; set; }
}
