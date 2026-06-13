using Destiny2Report.API.Bungie;
using Destiny2Report.API.Features.Reports;
using Destiny2Report.API.Features.Status;
using Destiny2Report.API.RateLimiting;
using Destiny2Report.API.Features.Crawler;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAppOpenTelemetry();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddBungieClient(builder.Configuration);
builder.Services.Configure<ContestModeOptions>(builder.Configuration.GetSection(ContestModeOptions.SectionName));
builder.Services.Configure<ActivityTriumphRecordOptions>(builder.Configuration.GetSection(ActivityTriumphRecordOptions.SectionName));
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");
    options.InstanceName = "Destiny2Report:";
});
builder.Services.AddHybridCache(options =>
{
    options.MaximumPayloadBytes = 200 * 1024 * 1024; // 200 MB
});
builder.Services.AddScoped<ICrawlerService, CrawlerService>();
builder.Services.AddHostedService<CrawlerBackgroundJob>();

builder.Services.AddSingleton<IMongoClient>(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("MongoDb")
        ?? throw new InvalidOperationException("ConnectionStrings:MongoDb is required.");

    return new MongoClient(connectionString);
});

builder.Services.AddSingleton(provider =>
{
    var databaseName = builder.Configuration["Mongo:DatabaseName"]
        ?? throw new InvalidOperationException("Mongo:DatabaseName is required.");

    var mongoClient = provider.GetRequiredService<IMongoClient>();

    return mongoClient.GetDatabase(databaseName);
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");

    var options = ConfigurationOptions.Parse(connectionString);
    options.AbortOnConnectFail = false;

    return ConnectionMultiplexer.Connect(options);
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
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

await EnsureMongoIndexesAsync(app.Services);

app.UseRateLimiter();

var api = app.MapGroup("/api")
    .RequireRateLimiting(RateLimitPolicies.PublicRead);

api.MapStatusEndpoints();
api.MapReportEndpoints();

app.Run();

static async Task EnsureMongoIndexesAsync(IServiceProvider serviceProvider)
{
    var mongoDatabase = serviceProvider.GetRequiredService<IMongoDatabase>();
    var reports = mongoDatabase.GetCollection<DestinyReport>("destiny_reports");
    var reportIndexKeys = Builders<DestinyReport>.IndexKeys
        .Ascending(report => report.PlatformId)
        .Ascending(report => report.PlayerMembershipId);
    var reportIndexModel = new CreateIndexModel<DestinyReport>(
        reportIndexKeys,
        new CreateIndexOptions
        {
            Name = "ux_destiny_reports_platform_player",
            Unique = true
        });

    await reports.Indexes.CreateOneAsync(reportIndexModel);

    var encounters = mongoDatabase.GetCollection<PlayerEncounterAggregate>("player_encounters");
    var encounterPairIndexKeys = Builders<PlayerEncounterAggregate>.IndexKeys
        .Ascending(encounter => encounter.OwnerMembershipType)
        .Ascending(encounter => encounter.OwnerMembershipId)
        .Ascending(encounter => encounter.EncounteredMembershipType)
        .Ascending(encounter => encounter.EncounteredMembershipId);
    var encounterTopIndexKeys = Builders<PlayerEncounterAggregate>.IndexKeys
        .Ascending(encounter => encounter.OwnerMembershipType)
        .Ascending(encounter => encounter.OwnerMembershipId)
        .Descending(encounter => encounter.Count);

    await encounters.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<PlayerEncounterAggregate>(
                encounterPairIndexKeys,
                new CreateIndexOptions
                {
                    Name = "ux_player_encounters_owner_encountered",
                    Unique = true
                }),
            new CreateIndexModel<PlayerEncounterAggregate>(
                encounterTopIndexKeys,
                new CreateIndexOptions
                {
                    Name = "ix_player_encounters_owner_count"
                })
        ]);
}
