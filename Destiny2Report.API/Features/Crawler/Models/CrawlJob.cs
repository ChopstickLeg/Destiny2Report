using System.Buffers.Binary;
using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

[BsonIgnoreExtraElements]
public sealed class CrawlJob
{
    public const string StateQueued = "queued";
    public const string StateRunning = "running";
    public const string StateAwaitingFinalization = "awaiting_finalization";
    public const string StateCompleted = "completed";
    public const string StateFailed = "failed";
    public const string StatePrivate = "private";

    [BsonId]
    public byte[] PlayerKey { get; init; } = [];

    [BsonElement("mt")]
    public int MembershipTypeId { get; init; }

    [BsonElement("mi")]
    public long MembershipId { get; init; }

    [BsonElement("dn")]
    [BsonIgnoreIfDefault]
    public string DisplayName { get; set; } = "";

    [BsonElement("v")]
    public int ProtocolVersion { get; set; } = CrawlerQueue.ProtocolVersion;

    [BsonElement("r")]
    public string RunId { get; set; } = "";

    [BsonElement("s")]
    public string State { get; set; } = StateQueued;

    [BsonElement("d")]
    public bool DispatchedToRedis { get; set; }

    [BsonElement("se")]
    [BsonIgnoreIfDefault]
    public string StreamEntryId { get; set; } = "";

    [BsonElement("p")]
    [BsonIgnoreIfDefault]
    public bool IsPriority { get; set; }

    [BsonElement("f")]
    public long Fence { get; set; }

    [BsonElement("lo")]
    [BsonIgnoreIfDefault]
    public string LeaseOwner { get; set; } = "";

    [BsonElement("le")]
    [BsonIgnoreIfNull]
    public DateTime? LeaseExpiresAtUtc { get; set; }

    [BsonElement("qa")]
    public DateTime QueuedAtUtc { get; set; }

    [BsonElement("sa")]
    [BsonIgnoreIfNull]
    public DateTime? StartedAtUtc { get; set; }

    [BsonElement("ua")]
    public DateTime UpdatedAtUtc { get; set; }

    [BsonElement("fa")]
    [BsonIgnoreIfNull]
    public DateTime? FinishedAtUtc { get; set; }

    [BsonElement("ff")]
    [BsonIgnoreIfDefault]
    public bool ForceFullCrawl { get; set; }

    [BsonElement("e")]
    [BsonIgnoreIfDefault]
    public string Error { get; set; } = "";

    [BsonElement("ag")]
    [BsonIgnoreIfDefault]
    public string ActiveGeneration { get; set; } = "";

    [BsonElement("cg")]
    [BsonIgnoreIfDefault]
    public string CandidateGeneration { get; set; } = "";

    [BsonElement("fo")]
    [BsonIgnoreIfDefault]
    public string FinalizerOwner { get; set; } = "";

    [BsonElement("fe")]
    [BsonIgnoreIfNull]
    public DateTime? FinalizerLeaseExpiresAtUtc { get; set; }

    [BsonElement("ffn")]
    public long FinalizerFence { get; set; }

    [BsonElement("nr")]
    [BsonIgnoreIfDefault]
    public string NotifiedRunId { get; set; } = "";

    public static byte[] CreatePlayerKey(int membershipTypeId, long membershipId)
    {
        var key = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(key, membershipTypeId);
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(4), membershipId);
        return key;
    }

    public static bool IsTerminal(string state) => state is StateCompleted or StateFailed or StatePrivate;
}
