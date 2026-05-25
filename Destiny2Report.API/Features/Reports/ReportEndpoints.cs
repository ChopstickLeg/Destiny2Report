using Destiny2Report.API.RateLimiting;

namespace Destiny2Report.API.Features.Reports;

public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        var reports = api.MapGroup("/reports")
            .WithTags("Reports");

        reports.MapGet("/{membershipId:long}/summary", ReportHandlers.GetSummary)
            .WithName("GetReportSummary")
            .WithSummary("Example read endpoint for a public Destiny report summary.");

        reports.MapPost("/queue", ReportHandlers.QueueCrawl)
            .WithName("QueueReportCrawl")
            .WithSummary("Queues a Destiny player report crawl.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        return api;
    }
}
