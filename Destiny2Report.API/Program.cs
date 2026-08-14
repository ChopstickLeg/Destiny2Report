using Destiny2Report.API.Bungie;
using Destiny2Report.API.Features.Auth;
using Destiny2Report.API.Features.Admin;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.PlayerSearch;
using Destiny2Report.API.Features.PushNotifications;
using Destiny2Report.API.Features.Reports;
using Destiny2Report.API.Features.Leaderboards;
using Destiny2Report.API.Features.Status;
using Destiny2Report.API.Mongo;
using Destiny2Report.API.Observability;
using Destiny2Report.API.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using StackExchange.Redis;
using System.Diagnostics;
using System.Threading.RateLimiting;
using WebPush;

if (args.Contains("--generate-vapid-keys", StringComparer.Ordinal))
{
    var keys = VapidHelper.GenerateVapidKeys();
    Console.WriteLine($"WEB_PUSH_PUBLIC_KEY={keys.PublicKey}");
    Console.WriteLine($"WEB_PUSH_PRIVATE_KEY={keys.PrivateKey}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.AddAppOpenTelemetry();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddBungieClient(builder.Configuration);
builder.Services.AddHttpClient<IBungieAuthService, BungieAuthService>(httpClient =>
{
    httpClient.BaseAddress = new Uri("https://www.bungie.net/Platform/");
});
builder.Services.AddOptions<TurnstileOptions>()
    .Bind(builder.Configuration.GetSection(TurnstileOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.SecretKey), "Turnstile:SecretKey is required.")
    .Validate(options => options.AllowedHostnames.Length > 0, "Turnstile:AllowedHostnames must not be empty.")
    .ValidateOnStart();
builder.Services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>(httpClient =>
{
    httpClient.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddOptions<QueueAdmissionOptions>()
    .Bind(builder.Configuration.GetSection(QueueAdmissionOptions.SectionName))
    .Validate(options => options.MaxRequestsPerAccountPerDay >= 0, "QueueAdmission:MaxRequestsPerAccountPerDay cannot be negative.")
    .Validate(options => options.MaxNewReportsPerAccountPerDay >= 0, "QueueAdmission:MaxNewReportsPerAccountPerDay cannot be negative.")
    .Validate(options => options.MaxRequestsGloballyPerHour >= 0, "QueueAdmission:MaxRequestsGloballyPerHour cannot be negative.")
    .Validate(options => options.MaxNewReportsGloballyPerDay >= 0, "QueueAdmission:MaxNewReportsGloballyPerDay cannot be negative.")
    .Validate(options => options.HasValidBlockedBungieMembershipIds(), "QueueAdmission:BlockedBungieMembershipIds must be a comma-separated list of positive integers.")
    .ValidateOnStart();
builder.Services.AddSingleton<IQueueAdmissionQuotaStore, RedisQueueAdmissionQuotaStore>();
builder.Services.AddScoped<IQueueAdmissionService, QueueAdmissionService>();
builder.Services.AddSingleton<ICrawlerJobQueue, CrawlerJobQueue>();
builder.Services.AddSingleton<QueueEventBroker>(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");

    // Keep the long-lived pub/sub reader from sharing the command multiplexer
    // used by queue admission, status, and crawler dispatch.
    return new QueueEventBroker(
        ConnectionMultiplexer.Connect(CreateRedisConfigurationOptions(connectionString)),
        _.GetRequiredService<ILogger<QueueEventBroker>>(),
        ownsRedis: true);
});
builder.Services.AddSingleton<ICrawlGenerationStore, CrawlGenerationStore>();
builder.Services.AddOptions<LeaderboardsOptions>()
    .Bind(builder.Configuration.GetSection(LeaderboardsOptions.SectionName))
    .Validate(options => options.MinimumCompletedPlayers >= 0, "Leaderboards:MinimumCompletedPlayers cannot be negative.")
    .ValidateOnStart();
builder.Services.AddSingleton<ILeaderboardService, LeaderboardService>();
builder.Services.AddHostedService<LeaderboardRepairBackgroundService>();
builder.Services.AddOptions<WebPushOptions>()
    .Bind(builder.Configuration.GetSection(WebPushOptions.SectionName))
    .Validate(options =>
        (string.IsNullOrWhiteSpace(options.Subject)
            && string.IsNullOrWhiteSpace(options.PublicKey)
            && string.IsNullOrWhiteSpace(options.PrivateKey))
        || options.Enabled,
        "WebPush must specify Subject, PublicKey, and PrivateKey together.")
    .ValidateOnStart();
builder.Services.AddSingleton<IReportPushNotificationService, ReportPushNotificationService>();
builder.Services.AddStackExchangeRedisCache(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");
    options.ConfigurationOptions = CreateRedisConfigurationOptions(connectionString);
    options.InstanceName = "Destiny2Report:";
});
builder.Services.AddHybridCache(options =>
{
    options.MaximumPayloadBytes = 200 * 1024 * 1024; // 200 MB
});
builder.Services.AddScoped<CrawlerReadService>();
builder.Services.AddScoped<ICrawlerReadService>(provider => provider.GetRequiredService<CrawlerReadService>());
builder.Services.AddOptions<CrawlerOptions>()
    .Bind(builder.Configuration.GetSection(CrawlerOptions.SectionName))
    .Validate(options => options.BackgroundConcurrency > 0, "Crawler:BackgroundConcurrency must be positive.")
    .ValidateOnStart();
builder.Services.AddHostedService<CrawlerIdleMongoScheduler>();
builder.Services.AddHostedService<CrawlerFinalizerBackgroundService>();
builder.Services.AddHostedService<CrawlGenerationCleanupService>();
builder.Services.AddMongo(builder.Configuration);

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");

    return ConnectionMultiplexer.Connect(CreateRedisConfigurationOptions(connectionString));
});
builder.Services.AddOptions<AuthSessionOptions>()
    .Bind(builder.Configuration.GetSection(AuthSessionOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.CookieName), "AuthSession:CookieName is required.")
    .Validate(options => options.Lifetime > TimeSpan.Zero, "AuthSession:Lifetime must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAuthSessionStore, AuthSessionStore>();
builder.Services.AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.AddScoped<AdminAuthorizationFilter>();
builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
    CloudflareForwardedHeaders.Configure(
        options,
        builder.Configuration.GetSection("RateLimiting:TrustedProxyNetworks").Get<string[]>() ?? []));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
    {
        var diagnostics = ClientRateLimitPartition.GetDiagnostics(context.HttpContext);
        var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : EnsureRetryAfterHeaderMiddleware.DefaultRetryAfterSeconds;
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Destiny2Report.API.RateLimiting");

        logger.LogWarning(
            "Rate limit rejected {Method} {Path} for partition {RateLimitPartitionKey} " +
            "resolved from {RateLimitPartitionSource}; proxy peer {ProxyPeerAddress}; " +
            "Cloudflare client {CloudflareClientAddress}.",
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path,
            diagnostics.PartitionKey,
            diagnostics.Source,
            diagnostics.ProxyPeerAddress,
            diagnostics.CloudflareClientAddress);

        context.HttpContext.Response.Headers.RetryAfter =
            retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Results.Problem(
            title: "Too many requests",
            detail: $"Try again in {retryAfterSeconds} seconds.",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "rate_limited",
                ["retryAfterSeconds"] = retryAfterSeconds
            }).ExecuteAsync(context.HttpContext);
    };

    options.AddPolicy(RateLimitPolicies.PublicRead, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ClientRateLimitPartition.GetKey(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy(RateLimitPolicies.PublicWrite, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ClientRateLimitPartition.GetKey(httpContext),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();
var rateLimitLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Destiny2Report.API.RateLimiting");

if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

await app.EnsureMongoIndexesAsync();
using (var scope = app.Services.CreateScope())
{
    var crawlerService = scope.ServiceProvider.GetRequiredService<ICrawlerReadService>();
    await crawlerService.WarmReportReadModelsAsync(CancellationToken.None);
}

app.UseForwardedHeaders();
app.UseMiddleware<EnsureRetryAfterHeaderMiddleware>();
app.Use(async (httpContext, next) =>
{
    if (!httpContext.Request.Path.StartsWithSegments("/api"))
    {
        await next(httpContext);
        return;
    }

    var diagnostics = ClientRateLimitPartition.GetDiagnostics(httpContext);
    Activity.Current?.SetTag("client.address", diagnostics.PartitionKey);
    Activity.Current?.SetTag("rate_limit.partition.key", diagnostics.PartitionKey);
    Activity.Current?.SetTag("rate_limit.partition.source", diagnostics.Source);
    Activity.Current?.SetTag("proxy.peer.address", diagnostics.ProxyPeerAddress);
    Activity.Current?.SetTag("cloudflare.client.address", diagnostics.CloudflareClientAddress);

    rateLimitLogger.LogDebug(
        "Resolved rate limit partition {RateLimitPartitionKey} from {RateLimitPartitionSource}; " +
        "proxy peer {ProxyPeerAddress}; Cloudflare client {CloudflareClientAddress}.",
        diagnostics.PartitionKey,
        diagnostics.Source,
        diagnostics.ProxyPeerAddress,
        diagnostics.CloudflareClientAddress);

    if (httpContext.Request.Path.Equals("/api/auth/bungie/oauth", StringComparison.OrdinalIgnoreCase))
    {
        rateLimitLogger.LogInformation(
            "OAuth request uses rate limit partition {RateLimitPartitionKey} from " +
            "{RateLimitPartitionSource}; proxy peer {ProxyPeerAddress}; " +
            "Cloudflare client {CloudflareClientAddress}.",
            diagnostics.PartitionKey,
            diagnostics.Source,
            diagnostics.ProxyPeerAddress,
            diagnostics.CloudflareClientAddress);
    }

    await next(httpContext);
});
app.UseRateLimiter();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).ShortCircuit();

var api = app.MapGroup("/api")
    .RequireRateLimiting(RateLimitPolicies.PublicRead);

api.MapAuthEndpoints();
api.MapAdminEndpoints();
api.MapPlayerSearchEndpoints();
api.MapReportEndpoints();
api.MapLeaderboardEndpoints();
api.MapPushNotificationEndpoints();

app.Run();

static ConfigurationOptions CreateRedisConfigurationOptions(string connectionString)
{
    var options = ConfigurationOptions.Parse(connectionString);
    options.AbortOnConnectFail = false;
    options.AsyncTimeout = 60_000;
    options.SyncTimeout = 60_000;
    return options;
}
