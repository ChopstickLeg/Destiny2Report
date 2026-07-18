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
