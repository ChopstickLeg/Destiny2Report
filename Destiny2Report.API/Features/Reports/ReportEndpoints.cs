using Destiny2Report.API.RateLimiting;

namespace Destiny2Report.API.Features.Reports;

public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports")
            .WithTags("Reports");

        reports.MapGet("/{membershipTypeId:int}/{membershipId:long}", ReportHandlers.GetReport)
            .WithName("GetReport")
            .WithSummary("Returns a crawled Destiny player report from MongoDB.");

        reports.MapGet("/{membershipTypeId:int}/{membershipId:long}/weapons/{activityMode}", ReportHandlers.GetWeapons)
            .WithName("GetReportWeapons")
            .WithSummary("Returns weapon category aggregates with all weapon aggregates for a crawled Destiny player and activity mode.");

        reports.MapPost("/queue", ReportHandlers.QueueCrawl)
            .WithName("QueueReportCrawl")
            .WithSummary("Queues a Destiny player report crawl.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        reports.MapGet("/{membershipTypeId:int}/{membershipId:long}/queue", ReportHandlers.StreamQueuePosition)
            .WithName("StreamReportQueuePosition")
            .WithSummary("Streams a queued Destiny report crawl position until the report is available.");

        return api;
    }
}
