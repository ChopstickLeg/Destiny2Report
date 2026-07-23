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
using StackExchange.Redis;
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
builder.Services.AddSingleton<ICrawlerJobQueue, CrawlerJobQueue>();
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

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.PublicRead, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy(RateLimitPolicies.PublicWrite, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

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

app.UseRateLimiter();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).ShortCircuit();

var api = app.MapGroup("/api")
    .RequireRateLimiting(RateLimitPolicies.PublicRead);

api.MapStatusEndpoints();
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
