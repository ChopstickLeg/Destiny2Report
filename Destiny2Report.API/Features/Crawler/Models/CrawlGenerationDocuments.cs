using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public sealed class CrawlReportDocument
{
    [BsonId] public ObjectId Id { get; init; } = ObjectId.GenerateNewId();
    [BsonElement("p")] public byte[] PlayerKey { get; init; } = [];
    [BsonElement("g")] public string Generation { get; init; } = "";
    [BsonElement("d")] public BsonDocument Data { get; init; } = [];
    [BsonElement("ca")] public DateTime CreatedAtUtc { get; init; }
}

[BsonIgnoreExtraElements]
public sealed class CrawlStateDocument
{
    [BsonId] public ObjectId Id { get; init; } = ObjectId.GenerateNewId();
    [BsonElement("p")] public byte[] PlayerKey { get; init; } = [];
    [BsonElement("g")] public string Generation { get; init; } = "";
    [BsonElement("d")] public BsonDocument Data { get; init; } = [];
    [BsonElement("ca")] public DateTime CreatedAtUtc { get; init; }
}

public enum CrawlArtifactKind : byte
{
    Weapon = 1,
    Death = 2,
    Emblem = 3,
    Encounter = 4
}

public enum StoredActivityMode : byte
{
    None = 0,
    Pve = 1,
    Crucible = 2,
    Gambit = 3
}

public enum StoredCharacterClass : byte
{
    Unknown = 0,
    Titan = 1,
    Hunter = 2,
    Warlock = 3
}

[BsonIgnoreExtraElements]
public sealed class CrawlArtifactDocument
{
    [BsonId] public ObjectId Id { get; init; } = ObjectId.GenerateNewId();
    [BsonElement("p")] public byte[] PlayerKey { get; init; } = [];
    [BsonElement("g")] public string Generation { get; init; } = "";
    [BsonElement("k")] public CrawlArtifactKind Kind { get; init; }
    [BsonElement("m"), BsonIgnoreIfDefault] public StoredActivityMode ActivityMode { get; init; }
    [BsonElement("s"), BsonIgnoreIfDefault] public int SpecificActivityMode { get; init; }
    [BsonElement("c"), BsonIgnoreIfDefault] public StoredCharacterClass CharacterClass { get; init; }
    [BsonElement("h"), BsonIgnoreIfDefault] public long Hash { get; init; }
    [BsonElement("n")] public long Value { get; init; }
    [BsonElement("t"), BsonIgnoreIfDefault] public int EncounteredMembershipType { get; init; }
    [BsonElement("i"), BsonIgnoreIfDefault] public long EncounteredMembershipId { get; init; }
    [BsonElement("ca")] public DateTime CreatedAtUtc { get; init; }
}

public static class CrawlStorageMappings
{
    public static StoredActivityMode ToStoredMode(string value) => value switch
    {
        "PvE" => StoredActivityMode.Pve,
        "Crucible" => StoredActivityMode.Crucible,
        "Gambit" => StoredActivityMode.Gambit,
        _ => StoredActivityMode.None
    };

    public static string FromStoredMode(StoredActivityMode value) => value switch
    {
        StoredActivityMode.Pve => "PvE",
        StoredActivityMode.Crucible => "Crucible",
        StoredActivityMode.Gambit => "Gambit",
        _ => ""
    };

    public static StoredCharacterClass ToStoredClass(string value) => value switch
    {
        "Titan" => StoredCharacterClass.Titan,
        "Hunter" => StoredCharacterClass.Hunter,
        "Warlock" => StoredCharacterClass.Warlock,
        _ => StoredCharacterClass.Unknown
    };

    public static string FromStoredClass(StoredCharacterClass value) => value switch
    {
        StoredCharacterClass.Titan => "Titan",
        StoredCharacterClass.Hunter => "Hunter",
        StoredCharacterClass.Warlock => "Warlock",
        _ => "Unknown"
    };
}
