using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Destiny2Report.API.Features.Admin;
using Microsoft.Extensions.Options;

namespace Destiny2Report.API.Features.Auth;

public static class AuthHandlers
{
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(1);

    public static async Task<Results<Ok<SignedInPlayerResponse>, BadRequest<ProblemDetails>, StatusCodeHttpResult>> ExchangeBungieCode(
        BungieOAuthCodeRequest request,
        HttpResponse httpResponse,
        IBungieAuthService authService,
        IAuthSessionStore sessionStore,
        IOptions<AdminOptions> adminOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await authService.ExchangeCodeAsync(request, cancellationToken).ConfigureAwait(false);
            var profile = await authService.GetCurrentUserAsync(response.AccessToken, cancellationToken).ConfigureAwait(false);
            if (!profile.SignedIn)
            {
                return TypedResults.StatusCode(StatusCodes.Status502BadGateway);
            }

            await sessionStore.CreateAsync(httpResponse, response, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(AdminAccess.WithAdminAccess(profile, adminOptions.Value));
        }
        catch (BungieAuthException ex) when (ex.Error is "invalid_oauth_request" or "bungie_oauth_exchange_failed")
        {
            return TypedResults.BadRequest(ToProblemDetails(ex, StatusCodes.Status400BadRequest));
        }
        catch (BungieAuthException ex) when (ex.Error is "bungie_oauth_not_configured")
        {
            return TypedResults.BadRequest(ToProblemDetails(ex, StatusCodes.Status400BadRequest));
        }
    }

    public static async Task<Results<Ok<SignedInPlayerResponse>, StatusCodeHttpResult>> WhoAmI(
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        IBungieAuthService authService,
        IAuthSessionStore sessionStore,
        TimeProvider timeProvider,
        IOptions<AdminOptions> adminOptions,
        CancellationToken cancellationToken)
    {
        var session = await sessionStore.GetAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return TypedResults.Ok(new SignedInPlayerResponse(false, null, [], null));
        }

        try
        {
            if (session.AccessTokenExpiresAt <= timeProvider.GetUtcNow().Add(RefreshBeforeExpiry))
            {
                session = await RefreshSessionAsync(
                    httpRequest,
                    session,
                    authService,
                    sessionStore,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }

            var profile = await authService.GetCurrentUserAsync(session.AccessToken, cancellationToken).ConfigureAwait(false);
            if (profile.SignedIn)
            {
                return TypedResults.Ok(AdminAccess.WithAdminAccess(profile, adminOptions.Value));
            }

            session = await RefreshSessionAsync(
                httpRequest,
                session,
                authService,
                sessionStore,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
            profile = await authService.GetCurrentUserAsync(session.AccessToken, cancellationToken).ConfigureAwait(false);
            if (!profile.SignedIn)
            {
                await sessionStore.DeleteAsync(httpRequest, httpResponse, cancellationToken).ConfigureAwait(false);
            }

            return TypedResults.Ok(AdminAccess.WithAdminAccess(profile, adminOptions.Value));
        }
        catch (BungieAuthException ex) when (
            ex.Error is "bungie_session_expired"
            || ex.BungieStatusCode is System.Net.HttpStatusCode.BadRequest
                or System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
        {
            await sessionStore.DeleteAsync(httpRequest, httpResponse, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new SignedInPlayerResponse(false, null, [], null));
        }
        catch (BungieAuthException)
        {
            return TypedResults.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    public static async Task<NoContent> SignOut(
        HttpRequest request,
        HttpResponse response,
        IAuthSessionStore sessionStore,
        CancellationToken cancellationToken)
    {
        await sessionStore.DeleteAsync(request, response, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<AuthSession> RefreshSessionAsync(
        HttpRequest request,
        AuthSession session,
        IBungieAuthService authService,
        IAuthSessionStore sessionStore,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            throw new BungieAuthException("bungie_session_expired", "The Bungie session can no longer be refreshed.");
        }

        BungieOAuthTokenResponse tokens;
        try
        {
            tokens = await authService.RefreshTokenAsync(session.RefreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (BungieAuthException ex) when (
            ex.BungieStatusCode is System.Net.HttpStatusCode.BadRequest
                or System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
        {
            // Another tab may already have rotated this one-time refresh token.
            var latest = await sessionStore.GetAsync(request, cancellationToken).ConfigureAwait(false);
            if (latest is not null && latest.AccessToken != session.AccessToken)
            {
                return latest;
            }

            throw;
        }

        var refreshed = session with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken ?? session.RefreshToken,
            AccessTokenExpiresAt = timeProvider.GetUtcNow().AddSeconds(tokens.ExpiresIn)
        };
        await sessionStore.UpdateAsync(request, refreshed, cancellationToken).ConfigureAwait(false);
        return refreshed;
    }

    private static ProblemDetails ToProblemDetails(BungieAuthException exception, int statusCode)
    {
        return new ProblemDetails
        {
            Title = exception.Error,
            Detail = exception.Message,
            Status = statusCode
        };
    }
}
