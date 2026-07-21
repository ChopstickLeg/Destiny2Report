using Destiny2Report.API.Features.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Destiny2Report.Tests.Features.Auth;

public sealed class AuthSessionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_SetsHttpOnlyThirtyDayCookieAndStoresTokensServerSide()
    {
        var store = CreateStore();
        var context = new DefaultHttpContext();
        var tokens = Tokens(refreshExpiresIn: 90 * 24 * 60 * 60);

        await store.CreateAsync(context.Response, tokens, CancellationToken.None);

        var setCookie = SetCookieHeaderValue.Parse(context.Response.Headers.SetCookie.ToString());
        Assert.Equal("d2r.session", setCookie.Name.ToString());
        Assert.True(setCookie.HttpOnly);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, setCookie.SameSite);
        Assert.Equal(TimeSpan.FromDays(30), setCookie.MaxAge);
        Assert.DoesNotContain(tokens.AccessToken, setCookie.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(tokens.RefreshToken!, setCookie.ToString(), StringComparison.Ordinal);

        var returnContext = new DefaultHttpContext();
        returnContext.Request.Headers.Cookie = $"{setCookie.Name}={setCookie.Value}";
        var session = await store.GetAsync(returnContext.Request, CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(tokens.AccessToken, session.AccessToken);
        Assert.Equal(tokens.RefreshToken, session.RefreshToken);
        Assert.Equal(Now.AddDays(30), session.ExpiresAt);
    }

    [Fact]
    public async Task CreateAsync_DoesNotOutliveBungieRefreshToken()
    {
        var store = CreateStore();
        var context = new DefaultHttpContext();

        await store.CreateAsync(context.Response, Tokens(refreshExpiresIn: 7 * 24 * 60 * 60), CancellationToken.None);

        var cookie = SetCookieHeaderValue.Parse(context.Response.Headers.SetCookie.ToString());
        Assert.Equal(TimeSpan.FromDays(7), cookie.MaxAge);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(61, false)]
    public void AuthSessionRefresh_IsRequiredWithinOneMinuteOfExpiry(int secondsUntilExpiry, bool expected)
    {
        var session = new AuthSession("access", "refresh", Now.AddSeconds(secondsUntilExpiry), Now.AddDays(1));

        var result = AuthSessionRefresh.IsRequired(session, new FixedTimeProvider(Now));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task AuthSessionRefresh_RefreshAsyncPersistsRotatedTokens()
    {
        var store = CreateStore();
        var context = new DefaultHttpContext();
        await store.CreateAsync(context.Response, Tokens(refreshExpiresIn: 7 * 24 * 60 * 60), CancellationToken.None);
        var cookie = SetCookieHeaderValue.Parse(context.Response.Headers.SetCookie.ToString());
        var requestContext = new DefaultHttpContext();
        requestContext.Request.Headers.Cookie = $"{cookie.Name}={cookie.Value}";
        var session = await store.GetAsync(requestContext.Request, CancellationToken.None);
        var refreshedTokens = new BungieOAuthTokenResponse(
            AccessToken: "rotated-access",
            RefreshToken: "rotated-refresh",
            TokenType: "Bearer",
            ExpiresIn: 7200,
            RefreshExpiresIn: 7 * 24 * 60 * 60,
            MembershipId: "123");

        var refreshed = await AuthSessionRefresh.RefreshAsync(
            requestContext.Request,
            Assert.IsType<AuthSession>(session),
            new StubBungieAuthService(refreshedTokens),
            store,
            new FixedTimeProvider(Now),
            CancellationToken.None);

        var persisted = await store.GetAsync(requestContext.Request, CancellationToken.None);
        Assert.Equal("rotated-access", refreshed.AccessToken);
        Assert.Equal("rotated-refresh", refreshed.RefreshToken);
        Assert.Equal(Now.AddHours(2), refreshed.AccessTokenExpiresAt);
        Assert.Equal(refreshed, persisted);
    }

    private static AuthSessionStore CreateStore()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        return new AuthSessionStore(
            cache,
            Options.Create(new AuthSessionOptions()),
            new FixedTimeProvider(Now),
            new TestWebHostEnvironment());
    }

    private static BungieOAuthTokenResponse Tokens(int refreshExpiresIn) => new(
        AccessToken: "access-secret",
        RefreshToken: "refresh-secret",
        TokenType: "Bearer",
        ExpiresIn: 3600,
        RefreshExpiresIn: refreshExpiresIn,
        MembershipId: "123");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubBungieAuthService(BungieOAuthTokenResponse refreshResponse) : IBungieAuthService
    {
        public Task<BungieOAuthTokenResponse> ExchangeCodeAsync(
            BungieOAuthCodeRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BungieOAuthTokenResponse> RefreshTokenAsync(
            string refreshToken,
            CancellationToken cancellationToken) => Task.FromResult(refreshResponse);

        public Task<SignedInPlayerResponse> GetCurrentUserAsync(
            string accessToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Destiny2Report.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
