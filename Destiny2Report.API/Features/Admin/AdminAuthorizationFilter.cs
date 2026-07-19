using Destiny2Report.API.Features.Auth;
using Microsoft.Extensions.Options;

namespace Destiny2Report.API.Features.Admin;

public sealed class AdminAuthorizationFilter(
    IAuthSessionStore sessionStore,
    IBungieAuthService authService,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider) : IEndpointFilter
{
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(1);

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var cancellationToken = context.HttpContext.RequestAborted;
        var session = await sessionStore.GetAsync(context.HttpContext.Request, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return Results.Unauthorized();
        }

        SignedInPlayerResponse player;
        try
        {
            if (session.AccessTokenExpiresAt <= timeProvider.GetUtcNow().Add(RefreshBeforeExpiry))
            {
                if (string.IsNullOrWhiteSpace(session.RefreshToken))
                {
                    return Results.Unauthorized();
                }

                var tokens = await authService.RefreshTokenAsync(session.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
                session = session with
                {
                    AccessToken = tokens.AccessToken,
                    RefreshToken = tokens.RefreshToken ?? session.RefreshToken,
                    AccessTokenExpiresAt = timeProvider.GetUtcNow().AddSeconds(tokens.ExpiresIn)
                };
                await sessionStore.UpdateAsync(context.HttpContext.Request, session, cancellationToken)
                    .ConfigureAwait(false);
            }

            player = await authService.GetCurrentUserAsync(session.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            if (!player.SignedIn)
            {
                return Results.Unauthorized();
            }
        }
        catch (BungieAuthException ex) when (
            ex.Error is "invalid_oauth_request" or "bungie_session_expired"
            || ex.BungieStatusCode is System.Net.HttpStatusCode.BadRequest
                or System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
        {
            return Results.Unauthorized();
        }
        catch (BungieAuthException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        return AdminAccess.IsAdmin(player, options.Value)
            ? await next(context).ConfigureAwait(false)
            : Results.Forbid();
    }
}
