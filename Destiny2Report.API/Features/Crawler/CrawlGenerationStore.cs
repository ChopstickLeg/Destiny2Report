using System.Text.Json;
using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public interface ICrawlGenerationStore
{
    Task<DestinyReport?> ReadReportAsync(int membershipTypeId, long membershipId, CancellationToken cancellationToken);
    Task<CrawlAccumulator?> ReadStateAsync(CrawlJob job, string generation, CancellationToken cancellationToken);
    Task<CrawlGenerationFinalizationResult> TryFinalizeAsync(CrawlJob job, CancellationToken cancellationToken);
    Task<bool> TryCompleteFinalizationAsync(
        CrawlJob job,
        CrawlGenerationFinalizationResult finalization,
        CancellationToken cancellationToken);
}

public sealed record CrawlGenerationFinalizationResult(
    bool Promoted,
    DestinyReport? Report,
    CrawlAccumulator? State,
    string TerminalState,
    string Error)
{
    public static CrawlGenerationFinalizationResult LostOwnership { get; } =
        new(false, null, null, "", "");
}

public sealed class CrawlGenerationStore(IMongoClient mongoClient, IMongoDatabase mongoDatabase) : ICrawlGenerationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TransactionOptions FinalizationTransactionOptions = new(
        readConcern: ReadConcern.Snapshot,
        readPreference: ReadPreference.Primary,
        writeConcern: WriteConcern.WMajority);

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

    public async Task<CrawlGenerationFinalizationResult> TryFinalizeAsync(
        CrawlJob job,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.CandidateGeneration))
        {
            return CrawlGenerationFinalizationResult.LostOwnership;
        }

        using var session = await mongoClient.StartSessionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await session.WithTransactionAsync(
                async (transaction, token) =>
                {
                    var document = await mongoDatabase.GetCollection<CrawlReportDocument>("reports")
                        .Find(transaction, item => item.PlayerKey == job.PlayerKey
                            && item.Generation == job.CandidateGeneration)
                        .FirstAsync(token).ConfigureAwait(false);
                    var report = FromDocument<DestinyReport>(document.Data);
                    var stateDocument = await mongoDatabase.GetCollection<CrawlStateDocument>("crawl_state")
                        .Find(transaction, item => item.PlayerKey == job.PlayerKey
                            && item.Generation == job.CandidateGeneration)
                        .FirstOrDefaultAsync(token).ConfigureAwait(false);
                    var state = stateDocument is null
                        ? null
                        : FromDocument<CrawlAccumulator>(stateDocument.Data);

                    await mongoDatabase.GetCollection<DestinyReport>("destiny_reports").ReplaceOneAsync(
                        transaction,
                        item => item.PlatformId == job.MembershipTypeId
                            && item.PlayerMembershipId == job.MembershipId,
                        report,
                        new ReplaceOptions { IsUpsert = true },
                        token).ConfigureAwait(false);
                    if (state is not null)
                    {
                        await mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators").ReplaceOneAsync(
                            transaction,
                            item => item.PlatformId == job.MembershipTypeId
                                && item.PlayerMembershipId == job.MembershipId,
                            state,
                            new ReplaceOptions { IsUpsert = true },
                            token).ConfigureAwait(false);
                    }
                    else
                    {
                        await mongoDatabase.GetCollection<CrawlAccumulator>("crawl_accumulators").DeleteOneAsync(
                            transaction,
                            Builders<CrawlAccumulator>.Filter.Eq(item => item.PlatformId, job.MembershipTypeId)
                                & Builders<CrawlAccumulator>.Filter.Eq(item => item.PlayerMembershipId, job.MembershipId),
                            cancellationToken: token).ConfigureAwait(false);
                    }
                    await MaterializeArtifactsAsync(
                        transaction,
                        job,
                        job.CandidateGeneration,
                        token).ConfigureAwait(false);

                    var terminalState = report.CrawlState switch
                    {
                        DestinyReport.CrawlStatePrivate => CrawlJob.StatePrivate,
                        DestinyReport.CrawlStateFailed => CrawlJob.StateFailed,
                        _ => CrawlJob.StateCompleted
                    };
                    var error = report.CrawlError ?? "";
                    var now = DateTime.UtcNow;
                    var promotion = await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs").UpdateOneAsync(
                        transaction,
                        BuildFinalizerOwnershipFilter(job)
                            & Builders<CrawlJob>.Filter.Eq(item => item.State, CrawlJob.StateAwaitingFinalization)
                            & Builders<CrawlJob>.Filter.Eq(item => item.CandidateGeneration, job.CandidateGeneration),
                        Builders<CrawlJob>.Update
                            .Set(item => item.ActiveGeneration, job.CandidateGeneration)
                            .Set(item => item.UpdatedAtUtc, now),
                        cancellationToken: token).ConfigureAwait(false);
                    if (promotion.MatchedCount != 1)
                    {
                        throw new FinalizerOwnershipLostException();
                    }

                    return new CrawlGenerationFinalizationResult(
                        true,
                        report,
                        state,
                        terminalState,
                        error);
                },
                FinalizationTransactionOptions,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (FinalizerOwnershipLostException)
        {
            return CrawlGenerationFinalizationResult.LostOwnership;
        }
    }

    public async Task<bool> TryCompleteFinalizationAsync(
        CrawlJob job,
        CrawlGenerationFinalizationResult finalization,
        CancellationToken cancellationToken)
    {
        if (!finalization.Promoted || string.IsNullOrWhiteSpace(finalization.TerminalState))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var result = await mongoDatabase.GetCollection<CrawlJob>("crawl_jobs").UpdateOneAsync(
            BuildFinalizerOwnershipFilter(job)
                & Builders<CrawlJob>.Filter.Eq(item => item.State, CrawlJob.StateAwaitingFinalization)
                & Builders<CrawlJob>.Filter.Eq(item => item.ActiveGeneration, job.CandidateGeneration)
                & Builders<CrawlJob>.Filter.Eq(item => item.CandidateGeneration, job.CandidateGeneration),
            Builders<CrawlJob>.Update
                .Set(item => item.CandidateGeneration, "")
                .Set(item => item.State, finalization.TerminalState)
                .Set(item => item.Error, finalization.Error)
                .Set(item => item.FinalizerOwner, "")
                .Set(item => item.FinalizerLeaseExpiresAtUtc, null)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.FinishedAtUtc, now),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    internal static FilterDefinition<CrawlJob> BuildFinalizerOwnershipFilter(CrawlJob job) =>
        Builders<CrawlJob>.Filter.Eq(item => item.PlayerKey, job.PlayerKey)
        & Builders<CrawlJob>.Filter.Eq(item => item.RunId, job.RunId)
        & Builders<CrawlJob>.Filter.Eq(item => item.FinalizerOwner, job.FinalizerOwner)
        & Builders<CrawlJob>.Filter.Eq(item => item.FinalizerFence, job.FinalizerFence);

    private async Task MaterializeArtifactsAsync(
        IClientSessionHandle session,
        CrawlJob job,
        string generation,
        CancellationToken cancellationToken)
    {
        var values = await mongoDatabase.GetCollection<CrawlArtifactDocument>("crawl_artifacts")
            .Find(session, item => item.PlayerKey == job.PlayerKey && item.Generation == generation)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(session, "weapon_aggregates", job, values.Where(x => x.Kind == CrawlArtifactKind.Weapon).Select(x => new WeaponAggregate
        {
            OwnerMembershipType = job.MembershipTypeId, OwnerMembershipId = job.MembershipId,
            ActivityMode = CrawlStorageMappings.FromStoredMode(x.ActivityMode), SpecificActivityMode = x.SpecificActivityMode,
            ClassName = CrawlStorageMappings.FromStoredClass(x.CharacterClass), WeaponHash = x.Hash, Kills = checked((int)x.Value)
        }), cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(session, "death_aggregates", job, values.Where(x => x.Kind == CrawlArtifactKind.Death).Select(x => new DeathAggregate
        {
            OwnerMembershipType = job.MembershipTypeId, OwnerMembershipId = job.MembershipId,
            ActivityMode = CrawlStorageMappings.FromStoredMode(x.ActivityMode), SpecificActivityMode = x.SpecificActivityMode, Deaths = x.Value
        }), cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(session, "emblem_aggregates", job, values.Where(x => x.Kind == CrawlArtifactKind.Emblem).Select(x => new EmblemAggregate
        {
            OwnerMembershipType = job.MembershipTypeId, OwnerMembershipId = job.MembershipId, EmblemHash = x.Hash, TotalSeconds = x.Value
        }), cancellationToken).ConfigureAwait(false);
        await ReplaceAsync(session, "player_encounters", job, values.Where(x => x.Kind == CrawlArtifactKind.Encounter).Select(x => new PlayerEncounterAggregate
        {
            OwnerMembershipType = job.MembershipTypeId, OwnerMembershipId = job.MembershipId,
            EncounteredMembershipType = x.EncounteredMembershipType, EncounteredMembershipId = x.EncounteredMembershipId, Count = checked((int)x.Value)
        }), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceAsync<T>(
        IClientSessionHandle session,
        string collectionName,
        CrawlJob job,
        IEnumerable<T> values,
        CancellationToken cancellationToken)
    {
        var collection = mongoDatabase.GetCollection<T>(collectionName);
        await collection.DeleteManyAsync(
            session,
            Builders<T>.Filter.Eq("ot", job.MembershipTypeId)
                & Builders<T>.Filter.Eq("oi", job.MembershipId),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var array = values.ToArray();
        if (array.Length > 0)
        {
            await collection.InsertManyAsync(
                session,
                array,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static T FromDocument<T>(BsonDocument document)
    {
        return JsonSerializer.Deserialize<T>(document.ToJson(), JsonOptions)
            ?? throw new InvalidDataException("Crawler BSON document contained JSON null.");
    }

    private sealed class FinalizerOwnershipLostException : Exception;
}
