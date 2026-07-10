using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record WeaponCategoryAggregate
{
    public int OwnerMembershipType { get; set; }
    public long OwnerMembershipId { get; set; }
    public string ActivityMode { get; set; } = "";
    [JsonIgnore]
    public string ClassName { get; set; } = "";
    [JsonIgnore]
    public int SpecificActivityMode { get; set; }
    public string CategoryKey { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public int Kills { get; set; }
}
