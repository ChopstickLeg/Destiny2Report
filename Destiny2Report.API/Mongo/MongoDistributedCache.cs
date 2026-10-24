using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Destiny2Report.API.Mongo;

/// <summary>Shared cache with a hard one-day lifetime, including sliding entries.</summary>
public sealed class MongoDistributedCache(IMongoDatabase database, TimeProvider timeProvider) : IDistributedCache
{
    internal static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(1);
    // Large manifest tables exceed MongoDB's 16 MiB document limit.
    private const int ChunkSize = 4 * 1024 * 1024;
    private readonly IMongoCollection<Entry> entries = database.GetCollection<Entry>("cache_entries");
    private readonly IMongoCollection<Chunk> chunks = database.GetCollection<Chunk>("cache_chunks");

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        await entries.Indexes.CreateOneAsync(new CreateIndexModel<Entry>(
            Builders<Entry>.IndexKeys.Ascending(entry => entry.ExpiresAtUtc),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: cancellationToken).ConfigureAwait(false);
        await chunks.Indexes.CreateOneAsync(new CreateIndexModel<Chunk>(
            Builders<Chunk>.IndexKeys.Ascending(chunk => chunk.ExpiresAtUtc),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => SetAsync(key, value, options).GetAwaiter().GetResult();
    public void Refresh(string key) => RefreshAsync(key).GetAwaiter().GetResult();
    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var entry = await ReadEntryAsync(key, token).ConfigureAwait(false);
        if (entry is null) return null;
        byte[] value;
        if (entry.Value is not null) value = entry.Value;
        else
        {
            var ids = Enumerable.Range(0, entry.ChunkCount).Select(index => ChunkId(entry.Generation, index));
            var parts = await chunks.Find(Builders<Chunk>.Filter.In(chunk => chunk.Id, ids))
                .SortBy(chunk => chunk.Index).ToListAsync(token).ConfigureAwait(false);
            if (parts.Count != entry.ChunkCount) return null;
            value = new byte[entry.Length];
            var offset = 0;
            foreach (var part in parts)
            {
                part.Value.CopyTo(value, offset);
                offset += part.Value.Length;
            }
        }
        // TTL deletion is asynchronous; never serve an expired entry.
        if (entry.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime) return null;
        await RefreshEntryAsync(entry, token).ConfigureAwait(false);
        return value;
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        var now = timeProvider.GetUtcNow();
        var hardExpiry = now.Add(MaximumLifetime);
        if (options.AbsoluteExpiration is { } absolute && absolute < hardExpiry) hardExpiry = absolute;
        if (options.AbsoluteExpirationRelativeToNow is { } relative && now.Add(relative) < hardExpiry) hardExpiry = now.Add(relative);
        var expiry = hardExpiry;
        if (options.SlidingExpiration is { } sliding && now.Add(sliding) < expiry) expiry = now.Add(sliding);
        var entry = new Entry
        {
            Id = Key(key),
            Generation = Guid.NewGuid().ToString("N"),
            Length = value.Length,
            HardExpiresAtUtc = hardExpiry.UtcDateTime,
            ExpiresAtUtc = expiry.UtcDateTime,
            SlidingTicks = options.SlidingExpiration?.Ticks
        };
        if (value.Length <= ChunkSize) entry.Value = value;
        else
        {
            entry.ChunkCount = (value.Length + ChunkSize - 1) / ChunkSize;
            // Publish the pointer only after all chunks exist. Concurrent writers use separate generations.
            for (var index = 0; index < entry.ChunkCount; index++)
            {
                var offset = index * ChunkSize;
                await chunks.InsertOneAsync(new Chunk
                {
                    Id = ChunkId(entry.Generation, index),
                    Index = index,
                    Value = value.AsSpan(offset, Math.Min(ChunkSize, value.Length - offset)).ToArray(),
                    ExpiresAtUtc = entry.HardExpiresAtUtc
                }, cancellationToken: token).ConfigureAwait(false);
            }
        }
        await entries.ReplaceOneAsync(item => item.Id == entry.Id, entry,
            new ReplaceOptions { IsUpsert = true }, token).ConfigureAwait(false);
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        var entry = await ReadEntryAsync(key, token).ConfigureAwait(false);
        if (entry is not null) await RefreshEntryAsync(entry, token).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key, CancellationToken token = default) =>
        await entries.DeleteOneAsync(entry => entry.Id == Key(key), token).ConfigureAwait(false);

    private async Task<Entry?> ReadEntryAsync(string key, CancellationToken token)
    {
        var id = Key(key);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await entries.Find(entry => entry.Id == id && entry.ExpiresAtUtc > now)
            .FirstOrDefaultAsync(token).ConfigureAwait(false);
    }

    private async Task RefreshEntryAsync(Entry entry, CancellationToken token)
    {
        if (entry.SlidingTicks is not { } ticks) return;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiry = now.AddTicks(ticks);
        if (expiry > entry.HardExpiresAtUtc) expiry = entry.HardExpiresAtUtc;
        await entries.UpdateOneAsync(item => item.Id == entry.Id && item.Generation == entry.Generation && item.ExpiresAtUtc > now,
            Builders<Entry>.Update.Max(item => item.ExpiresAtUtc, expiry), cancellationToken: token).ConfigureAwait(false);
    }

    private static string Key(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private static string ChunkId(string generation, int index) => $"{generation}:{index}";

    private sealed class Entry
    {
        [BsonId] public string Id { get; set; } = "";
        public string Generation { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime HardExpiresAtUtc { get; set; }
        public long? SlidingTicks { get; set; }
        public int Length { get; set; }
        public int ChunkCount { get; set; }
        public byte[]? Value { get; set; }
    }

    private sealed class Chunk
    {
        [BsonId] public string Id { get; set; } = "";
        public int Index { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public byte[] Value { get; set; } = [];
    }
}
