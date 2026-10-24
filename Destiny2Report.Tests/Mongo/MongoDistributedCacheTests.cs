using Destiny2Report.API.Mongo;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Destiny2Report.Tests.Mongo;

public sealed class MongoCacheFactAttribute : FactAttribute
{
    public MongoCacheFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_CACHE_TEST_URI")))
            Skip = "Set MONGODB_CACHE_TEST_URI to run against an isolated MongoDB test database.";
    }
}

public sealed class MongoDistributedCacheTests : IAsyncLifetime
{
    private readonly MongoClient client = new(Environment.GetEnvironmentVariable("MONGODB_CACHE_TEST_URI") ?? "mongodb://localhost:27018");
    private readonly string databaseName = $"d2r_cache_tests_{Guid.NewGuid():N}";
    private readonly TestClock clock = new();
    private IMongoDatabase Database => client.GetDatabase(databaseName);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_CACHE_TEST_URI")))
            await client.DropDatabaseAsync(databaseName);
        client.Dispose();
    }

    [MongoCacheFact]
    public async Task HybridCacheSharesValuesAndInvalidationWithoutLocalCaching()
    {
        using var first = CreateServices();
        using var second = CreateServices();
        var a = first.GetRequiredService<HybridCache>();
        var b = second.GetRequiredService<HybridCache>();
        IReadOnlyDictionary<long, Definition> expected = new Dictionary<long, Definition> { [123] = new("Weapon") };
        await a.SetAsync("weapons", expected);
        var actual = await b.GetOrCreateAsync<IReadOnlyDictionary<long, Definition>>("weapons", _ => throw new InvalidOperationException("Cache miss"));
        Assert.Equal("Weapon", actual[123].Name);
        await b.RemoveAsync("weapons");
        var rebuilt = await a.GetOrCreateAsync<IReadOnlyDictionary<long, Definition>>("weapons", _ => ValueTask.FromResult<IReadOnlyDictionary<long, Definition>>(new Dictionary<long, Definition>()));
        Assert.Empty(rebuilt);
    }

    [MongoCacheFact]
    public async Task LargeEntriesArePublishedAtomicallyAcrossConcurrentWriters()
    {
        var a = new MongoDistributedCache(Database, clock);
        var b = new MongoDistributedCache(Database, clock);
        await a.EnsureIndexesAsync(CancellationToken.None);
        var first = new byte[17 * 1024 * 1024];
        var second = new byte[first.Length + 3];
        Random.Shared.NextBytes(first);
        Random.Shared.NextBytes(second);
        var options = new DistributedCacheEntryOptions();
        await a.SetAsync("manifest", first, options);
        Assert.Equal(first, await b.GetAsync("manifest"));
        var writes = Task.WhenAll(a.SetAsync("manifest", first, options), b.SetAsync("manifest", second, options));
        while (!writes.IsCompleted)
        {
            var read = await b.GetAsync("manifest");
            Assert.True(read is not null && (read.AsSpan().SequenceEqual(first) || read.AsSpan().SequenceEqual(second)));
        }
        await writes;
        await b.RemoveAsync("manifest");
        Assert.Null(await a.GetAsync("manifest"));
    }

    [MongoCacheFact]
    public async Task ExpirationAndSlidingRefreshNeverOutliveOneDayEvenBeforeTtlDeletion()
    {
        var cache = new MongoDistributedCache(Database, clock);
        await cache.SetAsync("short", [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) });
        await cache.SetAsync("long", [2], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(365) });
        await cache.SetAsync("sliding", [3], new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(12) });
        clock.Advance(TimeSpan.FromHours(10));
        Assert.Null(await cache.GetAsync("short"));
        Assert.Equal(new byte[] { 2 }, await cache.GetAsync("long"));
        await cache.RefreshAsync("sliding");
        clock.Advance(TimeSpan.FromHours(10));
        Assert.Equal(new byte[] { 3 }, await cache.GetAsync("sliding"));
        clock.Advance(TimeSpan.FromHours(4));
        Assert.Null(await cache.GetAsync("long"));
        Assert.Null(await cache.GetAsync("sliding"));
    }

    private ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache>(new MongoDistributedCache(Database, clock));
        services.AddHybridCache(options => options.DefaultEntryOptions = new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableLocalCache,
            Expiration = TimeSpan.FromDays(1)
        });
        return services.BuildServiceProvider();
    }

    private sealed record Definition(string Name);

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
