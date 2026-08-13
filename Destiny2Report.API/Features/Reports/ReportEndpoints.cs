using Destiny2Report.API.RateLimiting;

namespace Destiny2Report.API.Features.Reports;

public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports")
            .WithTags("Reports");

        reports.MapGet("/story-assets", ReportHandlers.GetStoryVisualAssets)
            .WithName("GetStoryVisualAssets")
            .WithSummary("Returns official Bungie activity-mode icons used by the story experience.");

        reports.MapPost("/story-shares", ReportHandlers.CreateStoryShare)
            .WithName("CreateStoryShare")
            .WithSummary("Creates an unguessable story link for a membership owned by the signed-in player.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        reports.MapGet("/story-shares/{token}", ReportHandlers.ResolveStoryShare)
            .WithName("ResolveStoryShare")
            .WithSummary("Resolves an unguessable story link to its report identity.");

        reports.MapGet("/{membershipTypeId:int}/{membershipId:long}", ReportHandlers.GetReport)
            .WithName("GetReport")
            .WithSummary("Returns a crawled Destiny player report from MongoDB.");

        reports.MapGet("/{membershipTypeId:int}/{membershipId:long}/weapons/{activityMode}", ReportHandlers.GetWeapons)
            .WithName("GetReportWeapons")
            .WithSummary("Returns weapon and ability kill aggregates grouped by the requested activity bucket and its specific Destiny activity modes.");

        reports.MapGet("/{membershipTypeId:int}/{membershipId:long}/deaths/{activityMode}", ReportHandlers.GetDeaths)
            .WithName("GetReportDeaths")
            .WithSummary("Returns death aggregates grouped by the requested activity bucket and its specific Destiny activity modes.");

        reports.MapGet("/{membershipTypeId:int}/{membershipId:long}/playtime/{activityMode}", ReportHandlers.GetPlaytime)
            .WithName("GetReportPlaytime")
            .WithSummary("Returns playtime for PvE, PvP, or Gambit grouped by specific Destiny activity mode.");

        reports.MapPost("/queue", ReportHandlers.QueueCrawl)
            .WithName("QueueReportCrawl")
            .WithSummary("Queues one ticket-authorized Destiny player report crawl.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        reports.MapGet("/{membershipTypeId:int}/{membershipId:long}/queue", ReportHandlers.StreamQueuePosition)
            .WithName("StreamReportQueuePosition")
            .WithSummary("Streams a queued Destiny report crawl position until the report is available.");

        return api;
    }
}
