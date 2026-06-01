namespace Destiny2Report.API.Features.Reports;

public sealed record ReportJobEvent(
    int MembershipTypeId,
    long MembershipId,
    string Status,
    string? StreamEntryId,
    string? Error,
    DateTimeOffset UpdatedAtUtc);
