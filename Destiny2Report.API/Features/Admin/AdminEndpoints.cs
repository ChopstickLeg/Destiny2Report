using Destiny2Report.API.RateLimiting;

namespace Destiny2Report.API.Features.Admin;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin")
            .WithTags("Admin")
            .AddEndpointFilter<AdminAuthorizationFilter>();

        admin.MapGet("/access", () => TypedResults.NoContent())
            .WithName("CheckAdminAccess")
            .WithSummary("Checks whether the signed-in player is the configured administrator.");

        admin.MapGet("/stream", AdminHandlers.StreamOverview)
            .WithName("StreamAdminOverview")
            .WithSummary("Streams active crawls and crawl status totals.");

        admin.MapPost("/queues/redis/flush", AdminHandlers.FlushRedisQueue)
            .WithName("FlushRedisCrawlerQueue")
            .WithSummary("Removes queued Redis crawler jobs without interrupting running crawls.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        admin.MapPost("/queues/mongo/flush", AdminHandlers.FlushMongoQueue)
            .WithName("FlushMongoCrawlerQueue")
            .WithSummary("Removes queued background Mongo crawler jobs without interrupting leased crawls.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        admin.MapPost("/reports/full-recrawl", AdminHandlers.SetFullRecrawl)
            .WithName("SetAllReportsFullRecrawl")
            .WithSummary("Marks every stored player report for a full recrawl with an audit reason.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        return api;
    }
}
