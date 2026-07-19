using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace Destiny2Report.API.Features.PushNotifications;

public sealed class ReportPushSubscription
{
    [BsonId]
    public ObjectId Id { get; set; }

    public required string EndpointHash { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public int MembershipTypeId { get; set; }
    public long MembershipId { get; set; }
    public required string State { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? DeliveryFailedAtUtc { get; set; }
}

public sealed record PushNotificationConfigResponse(bool Enabled, string? PublicKey);

public sealed record PushSubscriptionKeysRequest(
    [property: JsonPropertyName("p256dh")] string P256dh,
    [property: JsonPropertyName("auth")] string Auth);

public sealed record RegisterReportPushSubscriptionRequest(
    int MembershipTypeId,
    long MembershipId,
    string Endpoint,
    PushSubscriptionKeysRequest Keys);

public sealed record RemoveReportPushSubscriptionRequest(
    int MembershipTypeId,
    long MembershipId,
    string Endpoint);

public sealed record ReportPushSubscriptionStatusResponse(bool Registered);
