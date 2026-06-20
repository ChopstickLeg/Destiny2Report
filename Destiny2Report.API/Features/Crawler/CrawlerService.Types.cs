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
    private sealed record RivalAggregate(ReportPlayer Player)
    {
        public int Matches { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public double Kills { get; set; }
        public double Deaths { get; set; }
    }

    private sealed record ActivityCompletionAggregate(string ActivityName)
    {
        public int CompletionCount { get; set; }
        public bool ContestClear { get; set; }
        public bool FlawlessClear { get; set; }
        public bool SoloClear { get; set; }
        public bool SoloFlawlessClear { get; set; }
    }

    private sealed record CompletedRaidActivity(string RaidName, DateTimeOffset CompletedAt, long InstanceId);

    private sealed record SherpaCheck(DestinyPostGameCarnageReportData Pgcr, string NormalizedRaidName, DateTimeOffset CompletedAt);

    private sealed record SherpaCandidateCheck(
        DestinyPostGameCarnageReportData Pgcr,
        string NormalizedRaidName,
        DateTimeOffset CompletedAt,
        int MembershipType,
        long MembershipId);

    private sealed class PrivateProfileUnavailableException(string operation, BungieResponse response)
        : InvalidOperationException($"{operation} failed because the Destiny profile is not public. Bungie error code {response.ErrorCode}: {response.Message}");

    private sealed record WeaponDefinitionSummary(string Name, string IconUrl);

    private static class ActivityModes
    {
        public const int Raid = 4;
        public const int AllPvP = 5;
        public const int Patrol = 6;
        public const int AllPvE = 7;
        public const int Gambit = 63;
        public const int GambitPrime = 75;
        public const int Dungeon = 82;
    }

    private sealed class ManifestContext(DestinyManifest manifest, CrawlerService service)
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<JObject>>> _tables = new(StringComparer.Ordinal);

        public DestinyManifest Manifest => manifest;

        public JObject PresentationNodes => GetTable("DestinyPresentationNodeDefinition");
        public JObject Records => GetTable("DestinyRecordDefinition");

        public async Task<JObject> GetTableAsync(string tableName, CancellationToken cancellationToken)
        {
            return await _tables.GetOrAdd(tableName, name => new Lazy<Task<JObject>>(() => service.GetManifestTableAsync(manifest, name, cancellationToken)))
                .Value
                .ConfigureAwait(false);
        }

        public uint? FindMetricHash(params string[] terms)
        {
            var metrics = GetTable("DestinyMetricDefinition");
            foreach (var property in metrics.Properties())
            {
                var name = property.Value["displayProperties"]?["name"]?.Value<string>() ?? "";
                var description = property.Value["displayProperties"]?["description"]?.Value<string>() ?? "";
                if (terms.All(term =>
                        name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || description.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    return uint.Parse(property.Name);
                }
            }

            return null;
        }

        private JObject GetTable(string tableName)
        {
            return GetTableAsync(tableName, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
