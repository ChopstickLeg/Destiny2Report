using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public record WeaponAggregate
{
    [BsonElement("ot")]
    public int OwnerMembershipType { get; set; }
    [BsonElement("oi")]
    public long OwnerMembershipId { get; set; }
    [BsonElement("am")]
    public string ActivityMode { get; set; } = "";
    [JsonIgnore]
    [BsonElement("c")]
    public string ClassName { get; set; } = "";
    [JsonIgnore]
    [BsonElement("sm")]
    public int SpecificActivityMode { get; set; }
    [BsonElement("h")]
    public long WeaponHash { get; set; }
    [BsonElement("k")]
    public int Kills { get; set; }
}
