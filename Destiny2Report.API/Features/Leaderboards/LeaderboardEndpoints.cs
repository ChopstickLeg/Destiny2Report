namespace Destiny2Report.API.Features.Leaderboards;

public static class LeaderboardEndpoints
{
    public static RouteGroupBuilder MapLeaderboardEndpoints(this RouteGroupBuilder api)
    {
        var leaderboards = api.MapGroup("/leaderboards").WithTags("Leaderboards");
        leaderboards.MapGet("", LeaderboardHandlers.GetCatalog)
            .WithName("GetLeaderboards")
            .WithSummary("Returns leaderboard readiness and the available catalog.");
        leaderboards.MapGet("/{metricKey}", LeaderboardHandlers.GetBoard)
            .WithName("GetLeaderboard")
            .WithSummary("Returns one page from a bounded top-1,000 leaderboard.");
        return api;
    }
}
