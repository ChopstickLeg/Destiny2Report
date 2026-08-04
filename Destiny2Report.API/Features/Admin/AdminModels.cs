using Destiny2Report.API.Features.Crawler;

namespace Destiny2Report.API.Features.Admin;

public sealed record AdminActiveCrawlResponse(
    int MembershipTypeId,
    long MembershipId,
    string DisplayName,
    DateTimeOffset? QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc,
    string LeaseOwner,
    bool QueuedInRedis,
    string RunId = "",
    long Fence = 0,
    CrawlProgressSnapshot? Progress = null);

public sealed record AdminQueueStatusCountResponse(string Status, long Count);

public sealed record AdminOverviewResponse(
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AdminActiveCrawlResponse> ActiveCrawls,
    IReadOnlyList<AdminQueueStatusCountResponse> StatusCounts);

public sealed record AdminFullRecrawlRequest(string Reason);

public sealed record AdminCrawlerQueueItem(
    int MembershipTypeId,
    long MembershipId,
    bool ForceFullCrawl = false);

public sealed record AdminMutationResponse(long AffectedPlayers, string Message);

internal sealed record CrawlStatusCount(string? Status, long Count);
