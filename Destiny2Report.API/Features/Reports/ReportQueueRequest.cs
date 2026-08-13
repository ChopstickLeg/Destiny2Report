namespace Destiny2Report.API.Features.Reports;

public sealed record ReportQueueRequest(
    int MembershipTypeId,
    long MembershipId,
    string TurnstileToken);
