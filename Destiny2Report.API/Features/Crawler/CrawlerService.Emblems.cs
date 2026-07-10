using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Crawler.Models.Bungie;
using Microsoft.Extensions.Caching.Hybrid;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private async Task<EmblemDefinitionSummary?> GetEmblemDefinitionSummaryAsync(
        DestinyManifest manifest,
        long emblemHash,
        CancellationToken cancellationToken)
    {
        try
        {
            var hashIdentifier = ToUnsignedHashIdentifier(emblemHash);
            var cacheKey = $"bungie:destiny2:manifest:{manifest.Version}:{InventoryItemDefinitionType}:{hashIdentifier}:emblem";
            return await cache.GetOrCreateAsync(
                    cacheKey,
                    async ct =>
                    {
                        var operation = $"GetDestinyEntityDefinition:{InventoryItemDefinitionType}:{hashIdentifier}";
                        var response = await bungieClient.Destiny2_GetDestinyEntityDefinitionAsync(
                                InventoryItemDefinitionType,
                                hashIdentifier,
                                ct)
                            .ConfigureAwait(false);
                        var definition = EnsureSuccess(response, item => item.Response, operation);
                        return ToEmblemDefinitionSummary(definition);
                    },
                    new HybridCacheEntryOptions
                    {
                        Expiration = ManifestCacheDuration,
                        LocalCacheExpiration = ManifestCacheDuration
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resolve Destiny emblem definition {EmblemHash}.", emblemHash);
            return null;
        }
    }

    private async Task<Dictionary<long, EmblemDefinitionSummary>> GetEmblemDefinitionSummariesAsync(
        DestinyManifest manifest,
        IEnumerable<long> emblemHashes,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        var distinctHashes = emblemHashes.Distinct().ToArray();
        var summaries = new ConcurrentDictionary<long, EmblemDefinitionSummary>();
        var processed = 0L;

        if (progress is not null)
        {
            await progress.StartPhaseAsync("emblem-definitions", "Resolving emblem definitions", distinctHashes.Length, cancellationToken).ConfigureAwait(false);
        }

        await Parallel.ForEachAsync(
                distinctHashes,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentDefinitionRequests,
                    CancellationToken = cancellationToken
                },
                async (emblemHash, ct) =>
                {
                    var summary = await GetEmblemDefinitionSummaryAsync(manifest, emblemHash, ct).ConfigureAwait(false);
                    if (summary is not null)
                    {
                        summaries[emblemHash] = summary;
                    }

                    var current = Interlocked.Increment(ref processed);
                    if (progress is not null)
                    {
                        await progress.ReportAsync(current, distinctHashes.Length, ct).ConfigureAwait(false);
                    }
                })
            .ConfigureAwait(false);

        return summaries.ToDictionary(item => item.Key, item => item.Value);
    }

    private async Task ApplyEmblemAggregateDeltasAsync(
        DestinyReport report,
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<long, long> emblemSecondsDeltas,
        DestinyManifest manifest,
        bool resetEmblemAggregates,
        ICrawlProgress? progress,
        CancellationToken cancellationToken)
    {
        var emblems = mongoDatabase.GetCollection<EmblemAggregate>("emblem_aggregates");
        var ownerFilter = Builders<EmblemAggregate>.Filter.Eq(emblem => emblem.OwnerMembershipType, ownerMembershipType)
            & Builders<EmblemAggregate>.Filter.Eq(emblem => emblem.OwnerMembershipId, ownerMembershipId);

        if (resetEmblemAggregates)
        {
            await emblems.DeleteManyAsync(ownerFilter, cancellationToken).ConfigureAwait(false);
        }

        var writes = BuildEmblemAggregateWrites(ownerMembershipType, ownerMembershipId, emblemSecondsDeltas)
            .ToArray();

        if (writes.Length > 0)
        {
            await emblems.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }

        report.MostUsedEmblems = await GetTopEmblemReportsAsync(emblems, ownerFilter, manifest, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<WriteModel<EmblemAggregate>> BuildEmblemAggregateWrites(
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<long, long> emblemSecondsDeltas)
    {
        return emblemSecondsDeltas
            .Where(item => item.Key > 0 && item.Value > 0)
            .Select(item =>
            {
                return new
                {
                    EmblemHash = item.Key,
                    Seconds = item.Value
                };
            })
            .Select(item =>
            {
                var filter = Builders<EmblemAggregate>.Filter.Eq(emblem => emblem.OwnerMembershipType, ownerMembershipType)
                    & Builders<EmblemAggregate>.Filter.Eq(emblem => emblem.OwnerMembershipId, ownerMembershipId)
                    & Builders<EmblemAggregate>.Filter.Eq(emblem => emblem.EmblemHash, item.EmblemHash);
                var update = Builders<EmblemAggregate>.Update
                    .SetOnInsert(emblem => emblem.OwnerMembershipType, ownerMembershipType)
                    .SetOnInsert(emblem => emblem.OwnerMembershipId, ownerMembershipId)
                    .SetOnInsert(emblem => emblem.EmblemHash, item.EmblemHash)
                    .Inc(emblem => emblem.TotalSeconds, item.Seconds);

                return new UpdateOneModel<EmblemAggregate>(filter, update)
                {
                    IsUpsert = true
                };
            });
    }

    private async Task<List<EmblemReport>> GetTopEmblemReportsAsync(
        IMongoCollection<EmblemAggregate> emblems,
        FilterDefinition<EmblemAggregate> ownerFilter,
        DestinyManifest manifest,
        CancellationToken cancellationToken)
    {
        var topEmblems = await emblems
            .Find(ownerFilter)
            .SortByDescending(emblem => emblem.TotalSeconds)
            .Limit(DestinyReport.MostUsedEmblemsLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var definitions = await GetEmblemDefinitionSummariesAsync(
                manifest,
                topEmblems.Select(emblem => emblem.EmblemHash),
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);

        return topEmblems.Select(emblem =>
            {
                definitions.TryGetValue(emblem.EmblemHash, out var definition);
                return new EmblemReport
                {
                    Name = definition?.Name ?? SyntheticEmblemName(emblem.EmblemHash),
                    IconUrl = definition?.IconUrl ?? "",
                    BackgroundUrl = definition?.BackgroundUrl ?? "",
                    TotalPlaytime = TimeSpan.FromSeconds(emblem.TotalSeconds)
                };
            })
            .ToList();
    }

    private static void AddEmblemPlaytime(
        IDictionary<long, long> emblemSeconds,
        IEnumerable<DestinyPostGameCarnageReportEntry> entries)
    {
        foreach (var entry in entries)
        {
            var emblemHash = ToUnsignedHash(entry.Player?.EmblemHash ?? 0);
            if (emblemHash <= 0)
            {
                continue;
            }

            var seconds = (long)GetPlayerActivitySeconds(entry);
            if (seconds <= 0)
            {
                continue;
            }

            emblemSeconds.TryGetValue(emblemHash, out var currentSeconds);
            emblemSeconds[emblemHash] = currentSeconds + seconds;
        }
    }

    private static double GetPlayerActivitySeconds(DestinyPostGameCarnageReportEntry entry)
    {
        var seconds = GetStat(entry.Values, "timePlayedSeconds");
        return seconds > 0
            ? seconds
            : GetStat(entry.Values, "activityDurationSeconds");
    }

    private static string SyntheticEmblemName(long emblemHash)
    {
        return emblemHash.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static EmblemDefinitionSummary ToEmblemDefinitionSummary(DestinyDefinition definition)
    {
        var displayProperties = GetDefinitionProperty<ManifestDisplayProperties>(definition.AdditionalProperties, "displayProperties");
        return new EmblemDefinitionSummary(
            displayProperties?.Name ?? ToUnsignedHashIdentifier(definition.Hash),
            BungieUrl(displayProperties?.Icon),
            BungieUrl(displayProperties?.SecondaryIcon));
    }
}
