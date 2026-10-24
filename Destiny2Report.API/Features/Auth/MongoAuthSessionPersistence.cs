using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Auth;

public interface IAuthSessionPersistence
{
    Task<AuthSession?> GetAsync(string key, CancellationToken cancellationToken);
    Task SaveAsync(string key, AuthSession session, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public sealed class MongoAuthSessionPersistence(IMongoDatabase database, TimeProvider clock) : IAuthSessionPersistence
{
    private readonly IMongoCollection<SessionDocument> sessions = database.GetCollection<SessionDocument>("auth_sessions");

    public Task EnsureIndexesAsync(CancellationToken cancellationToken) => sessions.Indexes.CreateOneAsync(
        new CreateIndexModel<SessionDocument>(Builders<SessionDocument>.IndexKeys.Ascending(item => item.ExpiresAtUtc),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }), cancellationToken: cancellationToken);

    public async Task<AuthSession?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var document = await sessions.Find(item => item.Id == key && item.ExpiresAtUtc > now)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document?.Session;
    }

    public async Task SaveAsync(string key, AuthSession session, CancellationToken cancellationToken) =>
        await sessions.ReplaceOneAsync(item => item.Id == key,
            new SessionDocument(key, session, session.ExpiresAt.UtcDateTime),
            new ReplaceOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await sessions.DeleteOneAsync(item => item.Id == key, cancellationToken).ConfigureAwait(false);

    private sealed record SessionDocument([property: BsonId] string Id, AuthSession Session, DateTime ExpiresAtUtc);
}
