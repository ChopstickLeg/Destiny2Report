using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models.Bungie;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerReadService
{
    private async Task<EmblemDefinitionSummary?> GetEmblemDefinitionSummaryAsync(
        DestinyManifest manifest,
        long emblemHash,
        CancellationToken cancellationToken)
    {
        try
        {
            var definition = await bungieClient.Destiny2_GetDestinyEntityDefinitionAsync(
                    "DestinyInventoryItemDefinition",
                    emblemHash.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken)
                .ConfigureAwait(false);
            return definition.Response is null ? null : ToEmblemDefinitionSummary(definition.Response);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not resolve Destiny emblem definition {EmblemHash}.", emblemHash);
            return null;
        }
    }

    private async Task<Dictionary<long, EmblemDefinitionSummary>> GetEmblemDefinitionSummariesAsync(
        DestinyManifest manifest,
        IEnumerable<long> emblemHashes,
        object? progress,
        CancellationToken cancellationToken)
    {
        _ = progress;
        var summaries = new ConcurrentDictionary<long, EmblemDefinitionSummary>();
        await Parallel.ForEachAsync(
            emblemHashes.Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount)), CancellationToken = cancellationToken },
            async (hash, ct) =>
            {
                var summary = await GetEmblemDefinitionSummaryAsync(manifest, hash, ct).ConfigureAwait(false);
                if (summary is not null) summaries[hash] = summary;
            }).ConfigureAwait(false);
        return summaries.ToDictionary();
    }

    private static EmblemDefinitionSummary ToEmblemDefinitionSummary(DestinyDefinition definition)
    {
        var displayProperties = GetDefinitionProperty<ManifestDisplayProperties>(definition.AdditionalProperties, "displayProperties");
        return new EmblemDefinitionSummary(
            displayProperties?.Name ?? "Unknown emblem",
            BungieUrl(displayProperties?.Icon),
            BungieUrl(GetDefinitionString(definition.AdditionalProperties, "secondaryIcon")));
    }
}
