using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Destiny2Report.API.Features.Status;

public sealed class MongoHealthCheck(IMongoClient mongoClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await mongoClient
                .GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(
                    new BsonDocument("ping", 1),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("MongoDB did not respond to a ping.", exception);
        }
    }
}

public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Redis did not respond to a ping.", exception);
        }
    }
}
