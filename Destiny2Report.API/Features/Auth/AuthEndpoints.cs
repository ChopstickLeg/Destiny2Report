using Destiny2Report.API.RateLimiting;

namespace Destiny2Report.API.Features.Auth;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth")
            .WithTags("Auth");

        auth.MapPost("/bungie/oauth", AuthHandlers.ExchangeBungieCode)
            .WithName("ExchangeBungieOAuthCode")
            .WithSummary("Exchanges a Bungie OAuth authorization code for Bungie access and refresh tokens.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        auth.MapGet("/whoami", AuthHandlers.WhoAmI)
            .WithName("WhoAmI")
            .WithSummary("Returns the signed-in Bungie player for the session cookie, or signedIn=false.");

        auth.MapPost("/signout", AuthHandlers.SignOut)
            .WithName("SignOut")
            .WithSummary("Deletes the current signed-in session.")
            .RequireRateLimiting(RateLimitPolicies.PublicWrite);

        return api;
    }
}
