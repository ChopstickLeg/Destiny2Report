using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace Destiny2Report.API.Features.Auth;

public sealed class AuthSessionOptions
{
    public const string SectionName = "AuthSession";

    public string CookieName { get; set; } = "d2r.session";

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(30);
}

public interface IAuthSessionStore
{
    Task<AuthSession?> GetAsync(HttpRequest request, CancellationToken cancellationToken);

    Task CreateAsync(HttpResponse response, BungieOAuthTokenResponse tokens, CancellationToken cancellationToken);

    Task UpdateAsync(HttpRequest request, AuthSession session, CancellationToken cancellationToken);

    Task DeleteAsync(HttpRequest request, HttpResponse response, CancellationToken cancellationToken);
}

public sealed class AuthSessionStore(
    IAuthSessionPersistence persistence,
    IOptions<AuthSessionOptions> options,
    TimeProvider timeProvider,
    IWebHostEnvironment environment) : IAuthSessionStore
{
    private const string CacheKeyPrefix = "AuthSession:";

    public async Task<AuthSession?> GetAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.Cookies.TryGetValue(options.Value.CookieName, out var sessionId)
            || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var session = await persistence.GetAsync(CacheKey(sessionId), cancellationToken).ConfigureAwait(false);
        return session is not null && session.ExpiresAt > timeProvider.GetUtcNow() ? session : null;
    }

    public async Task CreateAsync(
        HttpResponse response,
        BungieOAuthTokenResponse tokens,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(options.Value.Lifetime);
        if (tokens.RefreshExpiresIn is > 0)
        {
            expiresAt = Min(expiresAt, now.AddSeconds(tokens.RefreshExpiresIn.Value));
        }

        var session = new AuthSession(
            tokens.AccessToken,
            tokens.RefreshToken,
            now.AddSeconds(tokens.ExpiresIn),
            expiresAt);
        var sessionId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        await SaveAsync(sessionId, session, cancellationToken).ConfigureAwait(false);
        response.Cookies.Append(options.Value.CookieName, sessionId, CookieOptions(expiresAt));
    }

    public Task UpdateAsync(HttpRequest request, AuthSession session, CancellationToken cancellationToken)
    {
        return request.Cookies.TryGetValue(options.Value.CookieName, out var sessionId)
            && !string.IsNullOrWhiteSpace(sessionId)
            ? SaveAsync(sessionId, session, cancellationToken)
            : Task.CompletedTask;
    }

    public async Task DeleteAsync(
        HttpRequest request,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (request.Cookies.TryGetValue(options.Value.CookieName, out var sessionId)
            && !string.IsNullOrWhiteSpace(sessionId))
        {
            await persistence.DeleteAsync(CacheKey(sessionId), cancellationToken).ConfigureAwait(false);
        }

        response.Cookies.Delete(options.Value.CookieName, CookieOptions(timeProvider.GetUtcNow()));
    }

    private Task SaveAsync(string sessionId, AuthSession session, CancellationToken cancellationToken)
    {
        var remainingLifetime = session.ExpiresAt - timeProvider.GetUtcNow();
        if (remainingLifetime <= TimeSpan.Zero)
        {
            return persistence.DeleteAsync(CacheKey(sessionId), cancellationToken);
        }

        return persistence.SaveAsync(CacheKey(sessionId), session, cancellationToken);
    }

    private CookieOptions CookieOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = !environment.IsDevelopment(),
        Path = "/",
        Expires = expiresAt,
        MaxAge = expiresAt - timeProvider.GetUtcNow()
    };

    private static string CacheKey(string sessionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
        return $"{CacheKeyPrefix}{Convert.ToHexString(hash)}";
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) =>
        first <= second ? first : second;
}

public sealed record AuthSession(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset ExpiresAt);
