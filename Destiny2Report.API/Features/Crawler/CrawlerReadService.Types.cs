using System.Collections.Concurrent;
using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models.Bungie;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerReadService
{
    private sealed record WeaponDefinitionSummary(string Name, string IconUrl, string CategoryName, string CategoryKey)
    {
        public int TierType { get; init; }
        public int DamageType { get; init; }
    }

    private sealed record EmblemDefinitionSummary(string Name, string IconUrl, string BackgroundUrl);

    private static class ActivityModes
    {
        public const int Raid = 4;
        public const int AllPvP = 5;
        public const int AllPvE = 7;
        public const int Gambit = 63;
        public const int AllPvECompetitive = 64;
        public const int GambitPrime = 75;
        public const int Dungeon = 82;
    }

    private sealed class ManifestContext(DestinyManifest manifest, CrawlerReadService service)
    {
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> tables = new(StringComparer.Ordinal);

        public DestinyManifest Manifest => manifest;

        public Task<IReadOnlyDictionary<string, ManifestActivityModeDefinition>> GetActivityModeDefinitionsAsync(CancellationToken cancellationToken) =>
            GetTableAsync<ManifestActivityModeDefinition>("DestinyActivityModeDefinition", cancellationToken);

        public Task<IReadOnlyDictionary<string, ManifestCharacterIdentityDefinition>> GetClassDefinitionsAsync(CancellationToken cancellationToken) =>
            GetTableAsync<ManifestCharacterIdentityDefinition>("DestinyClassDefinition", cancellationToken);

        public async Task<IReadOnlyDictionary<string, TDefinition>> GetTableAsync<TDefinition>(string tableName, CancellationToken cancellationToken)
        {
            var table = await tables.GetOrAdd(
                    tableName,
                    name => new Lazy<Task<object>>(
                        async () => await service.GetManifestTableAsync<TDefinition>(manifest, name, CancellationToken.None).ConfigureAwait(false)))
                .Value
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return (IReadOnlyDictionary<string, TDefinition>)table;
        }
    }
}
