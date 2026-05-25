namespace Destiny2Report.API.Features.Reports;

public sealed record ReportQueueResponse(
    string JobId,
    int MembershipTypeId,
    long BungieMembershipId,
    DateTimeOffset QueuedAtUtc);
