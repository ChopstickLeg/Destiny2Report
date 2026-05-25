using Destiny2Report.API.Bungie;
using Destiny2Report.API.Features.Reports;
using Destiny2Report.API.Features.Status;
using Destiny2Report.API.RateLimiting;
using Destiny2Report.API.Features.Crawler;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddBungieClient(builder.Configuration);
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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseRateLimiter();

var api = app.MapGroup("/api")
    .RequireRateLimiting(RateLimitPolicies.PublicRead);

api.MapStatusEndpoints();
api.MapReportEndpoints();

app.Run();
