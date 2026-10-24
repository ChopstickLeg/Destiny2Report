using Destiny2Report.API.Mongo;
using Destiny2Report.API.Features.Auth;
using Destiny2Report.API.Features.Leaderboards;
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
    public async Task AuthSessionsRemainPersistentForThirtyDaysAcrossInstances()
    {
        var first = new MongoAuthSessionPersistence(Database, clock);
        var second = new MongoAuthSessionPersistence(Database, clock);
        await first.EnsureIndexesAsync(CancellationToken.None);
        var now = clock.GetUtcNow();
        var session = new AuthSession("access", "refresh", now.AddHours(1), now.AddDays(30));
        await first.SaveAsync("session", session, CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(session, await second.GetAsync("session", CancellationToken.None));
        clock.Advance(TimeSpan.FromDays(28));
        Assert.Null(await first.GetAsync("session", CancellationToken.None));
    }

    [MongoCacheFact]
    public async Task LeaderboardRepairsReuseScoresAndPersistOnlyMissingOrStaleCalculations()
    {
        var source = DateTime.UtcNow.AddDays(-1);
        var players = new[]
        {
            new RepairPlayer(1, 1, "one", 0, [], source),
            new RepairPlayer(1, 2, "two", 0, [], source),
            new RepairPlayer(1, 3, "three", 0, [], source)
        };
        var scores = Database.GetCollection<PlayerLeaderboardSnapshot>("leaderboard_player_scores");
        await scores.InsertManyAsync([
            new PlayerLeaderboardSnapshot { PlayerKey = "1:1", MembershipTypeId = 1, MembershipId = 1,
                UpdatedAtUtc = source.AddHours(1), Scores = [new("combat.kills.total", 100)] },
            new PlayerLeaderboardSnapshot { PlayerKey = "1:2", MembershipTypeId = 1, MembershipId = 2,
                UpdatedAtUtc = source.AddDays(-1), Scores = [new("combat.kills.total", 1)] }
        ]);
        var calls = new List<long>();
        Task<IReadOnlyCollection<LeaderboardMetric>> Calculate(int type, long id, CancellationToken token)
        {
            calls.Add(id);
            return Task.FromResult<IReadOnlyCollection<LeaderboardMetric>>([new("combat.kills.total", "Combat", "Kills", "", "count", 0, id * 100)]);
        }
        var repair = new LeaderboardRepairScores(Database);
        var initial = await repair.GetBatchAsync(players, Calculate, CancellationToken.None);
        Assert.Equal(new long[] { 2, 3 }, calls);
        Assert.Equal(100, initial["1:1"][0].Score);
        Assert.Equal(200, initial["1:2"][0].Score);
        calls.Clear();
        await repair.GetBatchAsync(players, Calculate, CancellationToken.None);
        Assert.Empty(calls);
        Assert.Equal(TimeSpan.FromSeconds(15), LeaderboardRepairBackgroundService.RetryDelay(0));
        Assert.Equal(TimeSpan.FromMinutes(5), LeaderboardRepairBackgroundService.RetryDelay(20));
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
