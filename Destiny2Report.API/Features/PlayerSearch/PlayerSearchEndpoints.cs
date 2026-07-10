namespace Destiny2Report.API.Features.PlayerSearch;

public static class PlayerSearchEndpoints
{

    public static RouteGroupBuilder MapPlayerSearchEndpoints(this RouteGroupBuilder api)
    {
        var players = api.MapGroup("/players")
            .WithTags("Players");

        players.MapMethods("/search", new[] { HttpMethods.Query }, PlayerSearchHandlers.SearchPlayer)
            .WithName("SearchPlayer")
            .WithSummary("Searches for a Destiny player by Bungie global display name prefix.");

        return api;
    }
}
