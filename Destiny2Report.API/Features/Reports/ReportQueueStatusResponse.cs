using Destiny2Report.API.Features.Crawler;

namespace Destiny2Report.API.Features.Reports;

public sealed record ReportQueueStatusResponse(
    int MembershipTypeId,
    long MembershipId,
    string Status,
    string? StreamEntryId,
    string? Error,
    long? Position,
    long QueueLength,
    DateTimeOffset UpdatedAtUtc,
    CrawlProgressSnapshot? Progress);
