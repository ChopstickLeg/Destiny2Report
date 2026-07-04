using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Microsoft.Extensions.Caching.Hybrid;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;

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
        CancellationToken cancellationToken)
    {
        var tasks = emblemHashes
            .Distinct()
            .Select(async emblemHash => new
            {
                EmblemHash = emblemHash,
                Summary = await GetEmblemDefinitionSummaryAsync(manifest, emblemHash, cancellationToken).ConfigureAwait(false)
            });

        var summaries = await Task.WhenAll(tasks).ConfigureAwait(false);
        return summaries
            .Where(item => item.Summary is not null)
            .ToDictionary(item => item.EmblemHash, item => item.Summary!);
    }

    private async Task ApplyEmblemAggregateDeltasAsync(
        DestinyReport report,
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<long, long> emblemSecondsDeltas,
        DestinyManifest manifest,
        bool resetEmblemAggregates,
        CancellationToken cancellationToken)
    {
        var emblems = mongoDatabase.GetCollection<EmblemAggregate>("emblem_aggregates");
        var ownerFilter = Builders<EmblemAggregate>.Filter.Eq(emblem => emblem.OwnerMembershipType, ownerMembershipType)
            & Builders<EmblemAggregate>.Filter.Eq(emblem => emblem.OwnerMembershipId, ownerMembershipId);

        if (resetEmblemAggregates)
        {
            await emblems.DeleteManyAsync(ownerFilter, cancellationToken).ConfigureAwait(false);
        }

        var emblemDefinitions = await GetEmblemDefinitionSummariesAsync(
                manifest,
                emblemSecondsDeltas.Keys.Where(hash => hash > 0),
                cancellationToken)
            .ConfigureAwait(false);

        var writes = BuildEmblemAggregateWrites(ownerMembershipType, ownerMembershipId, emblemSecondsDeltas, emblemDefinitions)
            .ToArray();

        if (writes.Length > 0)
        {
            await emblems.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
        }

        report.MostUsedEmblems = await GetTopEmblemReportsAsync(emblems, ownerFilter, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<WriteModel<EmblemAggregate>> BuildEmblemAggregateWrites(
        int ownerMembershipType,
        long ownerMembershipId,
        IReadOnlyDictionary<long, long> emblemSecondsDeltas,
        IReadOnlyDictionary<long, EmblemDefinitionSummary> emblemDefinitions)
    {
        return emblemSecondsDeltas
            .Where(item => item.Key > 0 && item.Value > 0)
            .Select(item =>
            {
                emblemDefinitions.TryGetValue(item.Key, out var definition);
                return new
                {
                    EmblemHash = item.Key,
                    EmblemName = definition?.Name ?? SyntheticEmblemName(item.Key),
                    IconUrl = definition?.IconUrl ?? "",
                    BackgroundUrl = definition?.BackgroundUrl ?? "",
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
                    .Set(emblem => emblem.EmblemName, item.EmblemName)
                    .Set(emblem => emblem.IconUrl, item.IconUrl)
                    .Set(emblem => emblem.BackgroundUrl, item.BackgroundUrl)
                    .Inc(emblem => emblem.TotalSeconds, item.Seconds);

                return new UpdateOneModel<EmblemAggregate>(filter, update)
                {
                    IsUpsert = true
                };
            });
    }

    private static async Task<List<EmblemReport>> GetTopEmblemReportsAsync(
        IMongoCollection<EmblemAggregate> emblems,
        FilterDefinition<EmblemAggregate> ownerFilter,
        CancellationToken cancellationToken)
    {
        var topEmblems = await emblems
            .Find(ownerFilter)
            .SortByDescending(emblem => emblem.TotalSeconds)
            .Limit(DestinyReport.MostUsedEmblemsLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return topEmblems
            .Select(emblem => new EmblemReport
            {
                Name = emblem.EmblemName,
                IconUrl = emblem.IconUrl,
                BackgroundUrl = emblem.BackgroundUrl,
                TotalPlaytime = TimeSpan.FromSeconds(emblem.TotalSeconds)
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
        var displayProperties = TryGetJObject(definition.AdditionalProperties, "displayProperties");
        var secondaryIcon = TryGetJObject(definition.AdditionalProperties, "secondaryIcon");
        return new EmblemDefinitionSummary(
            displayProperties?["name"]?.Value<string>() ?? ToUnsignedHashIdentifier(definition.Hash),
            BungieUrl(displayProperties?["icon"]?.Value<string>()),
            BungieUrl(secondaryIcon?.Value<string>()));
    }
}
