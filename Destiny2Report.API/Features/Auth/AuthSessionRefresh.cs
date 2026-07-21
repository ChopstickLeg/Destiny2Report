namespace Destiny2Report.API.Features.Auth;

public static class AuthSessionRefresh
{
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(1);

    public static bool IsRequired(AuthSession session, TimeProvider timeProvider) =>
        session.AccessTokenExpiresAt <= timeProvider.GetUtcNow().Add(RefreshBeforeExpiry);

    public static async Task<AuthSession> RefreshAsync(
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
}
