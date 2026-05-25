namespace Destiny2Report.API.Features.Reports;

using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

public static class ReportHandlers
{
    private const int AllMembershipTypes = 254;

    public static async Task<Results<Ok<ReportSummaryResponse>, BadRequest<ProblemDetails>>> GetSummary(
        long membershipId,
        int? season,
        ID2ReportClient bungieClient,
        CancellationToken cancellationToken)
    {
        if (membershipId <= 0)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Invalid membership id",
                Detail = "membershipId must be a positive integer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var linkedProfilesResponse = await bungieClient
            .Destiny2_GetLinkedProfilesAsync(
                getAllMemberships: true,
                membershipId: membershipId,
                membershipType: AllMembershipTypes,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var linkedProfiles = linkedProfilesResponse.Response.Profiles ?? [];
        var displayName = GetDisplayName(linkedProfilesResponse.Response, membershipId);

        var response = new ReportSummaryResponse(
            MembershipId: membershipId,
            Season: season,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            DisplayName: displayName,
            ActivityCount: linkedProfiles.Count);

        return TypedResults.Ok(response);
    }

    public static async Task<Results<Accepted<ReportQueueResponse>, BadRequest<ProblemDetails>>> QueueCrawl(
        ReportQueueRequest request,
        IConnectionMultiplexer redis,
        CancellationToken cancellationToken)
    {
        if (request.MembershipTypeId <= 0)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Invalid membership type id",
                Detail = "membershipTypeId must be a positive integer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (request.BungieMembershipId <= 0)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Invalid Bungie membership id",
                Detail = "bungieMembershipId must be a positive integer.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var queuedAtUtc = DateTimeOffset.UtcNow;
        var redisDatabase = redis.GetDatabase();
        var jobId = await redisDatabase.StreamAddAsync(
                CrawlerQueue.StreamName,
                [
                    new NameValueEntry("membershipTypeId", request.MembershipTypeId),
                    new NameValueEntry("bungieMembershipId", request.BungieMembershipId),
                    new NameValueEntry("queuedAtUtc", queuedAtUtc.ToString("O"))
                ])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var response = new ReportQueueResponse(
            JobId: jobId.ToString(),
            MembershipTypeId: request.MembershipTypeId,
            BungieMembershipId: request.BungieMembershipId,
            QueuedAtUtc: queuedAtUtc);

        return TypedResults.Accepted($"/api/reports/jobs/{jobId}", response);
    }

    private static string GetDisplayName(DestinyLinkedProfilesResponse response, long membershipId)
    {
        var profile = response.Profiles?
            .FirstOrDefault(profile => profile.MembershipId == membershipId)
            ?? response.Profiles?.FirstOrDefault(profile => profile.IsCrossSavePrimary)
            ?? response.Profiles?.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(profile?.BungieGlobalDisplayName))
        {
            return profile.BungieGlobalDisplayName;
        }

        if (!string.IsNullOrWhiteSpace(profile?.DisplayName))
        {
            return profile.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(response.BnetMembership?.DisplayName))
        {
            return response.BnetMembership.DisplayName;
        }

        return membershipId.ToString();
    }
}
