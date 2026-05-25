namespace Destiny2Report.API.Features.Reports;

public sealed record ReportSummaryResponse(
    long MembershipId,
    int? Season,
    DateTimeOffset GeneratedAtUtc,
    string DisplayName,
    int ActivityCount);
