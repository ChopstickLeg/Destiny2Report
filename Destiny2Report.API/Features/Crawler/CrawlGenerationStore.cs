using System.Text.Json;
using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public interface ICrawlGenerationStore
{
    Task<DestinyReport?> ReadReportAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken);
    Task<CrawlAccumulator?> ReadStateAsync(CrawlJob job, string generation, CancellationToken cancellationToken);
    Task<(DestinyReport Report, CrawlAccumulator? State)> MaterializeAsync(CrawlJob job, string generation, CancellationToken cancellationToken);
}

public sealed class CrawlGenerationStore(IMongoDatabase mongoDatabase) : ICrawlGenerationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DestinyReport?> ReadReportAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken)
    {
        var key = CrawlJob.CreatePlayerKey(membershipTypeId, membershipId);
        var job = await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs").Find(item => item.PlayerKey == key)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (job is null || string.IsNullOrWhiteSpace(job.ActiveGeneration)) return null;
        var document = await mongoDatabase.GetCollection<CrawlReportDocument>("reports")
            .Find(item => item.PlayerKey == key && item.Generation == job.ActiveGeneration)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : FromDocument<DestinyReport>(document.Data);
    }

    public async Task<CrawlAccumulator?> ReadStateAsync(CrawlJob job, string generation, CancellationToken cancellationToken)
    {
        var document = await mongoDatabase.GetCollection<CrawlStateDocument>("crawl_state")
            .Find(item => item.PlayerKey == job.PlayerKey && item.Generation == generation)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : FromDocument<CrawlAccumulator>(document.Data);
    }

    public async Task<(DestinyReport Report, CrawlAccumulator? State)> MaterializeAsync(CrawlJob job, string generation, CancellationToken cancellationToken)
    {
        var document = await mongoDatabase.GetCollection<CrawlReportDocument>("reports")
            .Find(item => item.PlayerKey == job.PlayerKey && item.Generation == generation)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        var report = FromDocument<DestinyReport>(document.Data);
        var state = await ReadStateAsync(job, generation, cancellationToken).ConfigureAwait(false);

        await mongoDatabase.GetCollection<DestinyReport>("destiny_reports").ReplaceOneAsync(
            item => item.PlatformId == job.MembershipTypeId && item.PlayerMembershipId == job.MembershipId,
            report, new ReplaceOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
        if (state is not null)
        {
            await mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators").ReplaceOneAsync(
                item => item.PlatformId == job.MembershipTypeId && item.PlayerMembershipId == job.MembershipId,
                state, new ReplaceOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
        }
        await MaterializeArtifactsAsync(job, generation, cancellationToken).ConfigureAwait(false);
        return (report, state);
    }

    private async Task MaterializeArtifactsAsync(CrawlJob job, string generation, CancellationToken cancellationToken)
    {
        var values = await mongoDatabase.GetCollection<CrawlArtifactDocument>("crawl_artifacts")
            .Find(item => item.PlayerKey == job.PlayerKey && item.Generation == generation)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceAsync("weapon_aggregates", job, values.Where(x => x.Kind == CrawlArtifactKind.Weapon).Select(x => new WeaponAggregate
        {
            OwnerMembershipType = job.MembershipTypeId, OwnerMembershipId = job.MembershipId,
            ActivityMode = CrawlStorageMappings.FromStoredMode(x.ActivityMode), SpecificActivityMode = x.SpecificActivityMode,
            ClassName = CrawlStorageMappings.FromStoredClass(x.CharacterClass), WeaponHash = x.Hash, Kills = checked((int)x.Value)
        }), cancellationToken).ConfigureAwait(false);
        await ReplaceAsync("death_aggregates", job, values.Where(x => x.Kind == CrawlArtifactKind.Death).Select(x => new DeathAggregate
        {
            OwnerMembershipType = job.MembershipTypeId, OwnerMembershipId = job.MembershipId,
            ActivityMode = CrawlStorageMappings.FromStoredMode(x.ActivityMode), SpecificActivityMode = x.SpecificActivityMode, Deaths = x.Value
        }), cancellationToken).ConfigureAwait(false);
        await ReplaceAsync("emblem_aggregates", job, values.Where(x => x.Kind == CrawlArtifactKind.Emblem).Select(x => new EmblemAggregate
        {
            OwnerMembershipType = job.MembershipTypeId, OwnerMembershipId = job.MembershipId, EmblemHash = x.Hash, TotalSeconds = x.Value
        }), cancellationToken).ConfigureAwait(false);
        await ReplaceAsync("player_encounters", job, values.Where(x => x.Kind == CrawlArtifactKind.Encounter).Select(x => new PlayerEncounterAggregate
        {
            OwnerMembershipType = job.MembershipTypeId, OwnerMembershipId = job.MembershipId,
            EncounteredMembershipType = x.EncounteredMembershipType, EncounteredMembershipId = x.EncounteredMembershipId, Count = checked((int)x.Value)
        }), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceAsync<T>(string collectionName, CrawlJob job, IEnumerable<T> values, CancellationToken cancellationToken)
    {
        var collection = mongoDatabase.GetCollection<T>(collectionName);
        await collection.DeleteManyAsync(Builders<T>.Filter.Eq("ot", job.MembershipTypeId) & Builders<T>.Filter.Eq("oi", job.MembershipId), cancellationToken).ConfigureAwait(false);
        var array = values.ToArray();
        if (array.Length > 0) await collection.InsertManyAsync(array, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static T FromDocument<T>(BsonDocument document)
    {
        return JsonSerializer.Deserialize<T>(document.ToJson(), JsonOptions)
            ?? throw new InvalidDataException("Crawler BSON document contained JSON null.");
    }
}
