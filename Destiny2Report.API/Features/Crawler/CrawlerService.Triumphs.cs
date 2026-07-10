using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using BungiePlayer = D2Report.BungieClient.DestinyPlayer;
using ReportPlayer = Destiny2Report.API.Features.Crawler.Models.DestinyPlayer;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private static void ApplyTriumphSeals(DestinyReport report, DestinyProfileResponse profile, ManifestContext manifest)
    {
        var profileRecords = profile.ProfileRecords?.Data;
        if (profileRecords?.Records is null)
        {
            return;
        }

        var seenCompletionRecordHashes = new HashSet<long>();
        foreach (var sealPresentationNodeHash in GetSealPresentationNodeHashes(manifest.PresentationNodes))
        {
            var sealNode = GetDefinition(manifest.PresentationNodes, sealPresentationNodeHash);
            var completionRecordHash = sealNode?["completionRecordHash"]?.Value<long>() ?? 0;
            if (completionRecordHash <= 0 || !seenCompletionRecordHashes.Add(completionRecordHash))
            {
                continue;
            }

            var definition = GetDefinition(manifest.Records, completionRecordHash);
            if (definition is null)
            {
                continue;
            }

            TryGetProfileRecord(profileRecords.Records, completionRecordHash, out var component);
            if (component is null || !IsRecordCompleted(component))
            {
                continue;
            }

            report.TriumphSeals.Add(new DestinyTriumphSeal
            {
                Name = definition["displayProperties"]?["name"]?.Value<string>() ?? "",
                Description = definition["displayProperties"]?["description"]?.Value<string>() ?? "",
                IconUrl = BungieUrl(sealNode?["displayProperties"]?["icon"]?.Value<string>()),
                IsCompleted = true
            });
        }
    }

    private void ApplyActivityTriumphRecords(DestinyReport report, DestinyProfileResponse profile)
    {
        var profileRecords = profile.ProfileRecords?.Data;
        if (profileRecords?.Records is null)
        {
            return;
        }

        foreach (var raid in activityTriumphRecords.Raids)
        {
            var flawlessClear = IsProfileRecordCompleted(profileRecords.Records, raid.RecordId);
            if (!flawlessClear)
            {
                continue;
            }

            UpdateActivityCompletionSummary(
                report.RaidCompletions,
                raid.ActivityName,
                summary => summary with { FlawlessClear = true });
        }

        foreach (var dungeon in activityTriumphRecords.Dungeons)
        {
            var soloClear = IsProfileRecordCompleted(profileRecords.Records, dungeon.SoloRecordId);
            var flawlessClear = IsProfileRecordCompleted(profileRecords.Records, dungeon.FlawlessRecordId);
            var soloFlawlessClear = IsProfileRecordCompleted(profileRecords.Records, dungeon.SoloFlawlessRecordId);
            if (!soloClear && !flawlessClear && !soloFlawlessClear)
            {
                continue;
            }

            UpdateActivityCompletionSummary(
                report.DungeonCompletions,
                dungeon.ActivityName,
                summary => summary with
                {
                    SoloClear = summary.SoloClear || soloClear || soloFlawlessClear,
                    FlawlessClear = summary.FlawlessClear || flawlessClear || soloFlawlessClear,
                    SoloFlawlessClear = summary.SoloFlawlessClear || soloFlawlessClear
                });
        }
    }

    private static bool IsProfileRecordCompleted(
        IDictionary<string, DestinyRecordComponent> profileRecords,
        long recordHash)
    {
        return recordHash > 0
            && TryGetProfileRecord(profileRecords, recordHash, out var component)
            && component is not null
            && IsRecordCompleted(component);
    }

    private static void UpdateActivityCompletionSummary(
        IList<ActivityCompletionSummary> completions,
        string activityName,
        Func<ActivityCompletionSummary, ActivityCompletionSummary> update)
    {
        if (string.IsNullOrWhiteSpace(activityName))
        {
            return;
        }

        var normalizedName = ContestModeLookup.NormalizeActivityName(activityName);
        for (var i = 0; i < completions.Count; i++)
        {
            if (!string.Equals(completions[i].ActivityName, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            completions[i] = update(completions[i]);
            return;
        }
    }

    private static IEnumerable<long> GetSealPresentationNodeHashes(JObject presentationNodes)
    {
        return TriumphSealRootPresentationNodeHashes
            .Select(rootHash => GetDefinition(presentationNodes, rootHash))
            .SelectMany(GetChildPresentationNodeHashes);
    }

    private static IEnumerable<long> GetChildPresentationNodeHashes(JObject? presentationNode)
    {
        return presentationNode?["children"]?["presentationNodes"]?
            .OrderBy(node => node["nodeDisplayPriority"]?.Value<int>() ?? int.MaxValue)
            .Select(node => node["presentationNodeHash"]?.Value<long>() ?? 0)
            .Where(hash => hash > 0) ?? [];
    }

    private static bool IsRecordCompleted(DestinyRecordComponent component)
    {
        const int objectiveNotCompleted = 4;
        return component.CompletedCount > 0 || (component.State & objectiveNotCompleted) == 0;
    }

    private static bool TryGetProfileRecord(
        IDictionary<string, DestinyRecordComponent> profileRecords,
        long recordHash,
        out DestinyRecordComponent? component)
    {
        return TryGetHashValue(profileRecords, recordHash, out component);
    }
}
