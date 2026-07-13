using Destiny2Report.API.Bungie;
using Destiny2Report.API.Features.Auth;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.PlayerSearch;
using Destiny2Report.API.Features.Reports;
using Destiny2Report.API.Features.Status;
using Destiny2Report.API.Mongo;
using Destiny2Report.API.Observability;
using Destiny2Report.API.RateLimiting;
using StackExchange.Redis;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAppOpenTelemetry();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddBungieClient(builder.Configuration);
builder.Services.AddHttpClient<IBungieAuthService, BungieAuthService>(httpClient =>
{
    httpClient.BaseAddress = new Uri("https://www.bungie.net/Platform/");
});
builder.Services.Configure<ContestModeOptions>(builder.Configuration.GetSection(ContestModeOptions.SectionName));
builder.Services.Configure<ConquestOptions>(builder.Configuration.GetSection(ConquestOptions.SectionName));
builder.Services.Configure<ActivityTriumphRecordOptions>(builder.Configuration.GetSection(ActivityTriumphRecordOptions.SectionName));
builder.Services.Configure<CrawlerOptions>(builder.Configuration.GetSection(CrawlerOptions.SectionName));
builder.Services.AddSingleton<CrawlerPgcrThrottler>();
builder.Services.AddSingleton<CrawlerSherpaHistoryThrottler>();
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
builder.Services.AddScoped<ICrawlerService, CrawlerService>();
builder.Services.AddHostedService<CrawlerBackgroundJob>();
builder.Services.AddMongo(builder.Configuration);

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");

    return ConnectionMultiplexer.Connect(CreateRedisConfigurationOptions(connectionString));
});

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
    var crawlerService = scope.ServiceProvider.GetRequiredService<ICrawlerService>();
    await crawlerService.WarmReportReadModelsAsync(CancellationToken.None);
}

app.UseRateLimiter();

var api = app.MapGroup("/api")
    .RequireRateLimiting(RateLimitPolicies.PublicRead);

api.MapStatusEndpoints();
api.MapAuthEndpoints();
api.MapPlayerSearchEndpoints();
api.MapReportEndpoints();

app.Run();

static ConfigurationOptions CreateRedisConfigurationOptions(string connectionString)
{
    var options = ConfigurationOptions.Parse(connectionString);
    options.AbortOnConnectFail = false;
    options.AsyncTimeout = 60_000;
    options.SyncTimeout = 60_000;
    return options;
}
