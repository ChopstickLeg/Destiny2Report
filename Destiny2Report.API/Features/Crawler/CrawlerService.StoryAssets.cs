using Destiny2Report.API.Features.Crawler.Models;
using Destiny2Report.API.Features.Crawler.Models.Bungie;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    // Retired Guided Games "Master Guide" emblem: three Guardians moving forward together.
    private const long GuidedGamesMasterGuideEmblemHash = 1949951625;

    private static readonly ContestRaidEmblemDefinition[] ContestRaidEmblemDefinitions =
    [
        new("Leviathan", "Glory to the Emperor", 2107367383),
        new("Leviathan, Eater of Worlds", "Covetous Emperor", 4261480750),
        new("Leviathan, Spire of Stars", "Atop the Spire", 1595521942),
        new("Last Wish", "Wish Ascended", 2419113769),
        new("Scourge of the Past", "Scourge of Nothing", 3931192719),
        new("Crown of Sorrow", "Heavy Is the Crown", 1661191198),
        new("Garden of Salvation", "Dive into Darkness", 298334058),
        new("Deep Stone Crypt", "Long Slow Whisper", 1230660645),
        new("Vault of Glass", "Exotemporal", 2510169795),
        new("Vow of the Disciple", "The Cleaver", 787024999),
        new("King's Fall", "Tyrant", 866034300),
        new("Root of Nightmares", "A Good Night's Sleep", 908153541),
        new("Crota's End", "A Broken Throne", 54004489),
        new("Salvation's Edge", "Hunker Down", 2847579025),
        new("The Desert Perpetual", "Timeline's Blade", 4178714191),
        new("The Desert Perpetual (Epic)", "Fractured Timeline", 2565108500),
    ];

    private static readonly PantheonEmblemDefinition[] PantheonEmblemDefinitions =
    [
        new("Pantheon: Atraks Sovereign", "Atraks Dethroned", 2770607179),
        new("Pantheon: Oryx Exalted", "Exalted Beyond Oryx", 2770607178),
        new("Pantheon: Rhulk Indomitable", "Rhulk Subdued", 2770607177),
        new("Pantheon: Nezarec Sublime", "Elevated Above Nezarec", 2770607176),
        new("Pantheon: Calus Resplendent", "Calus Conquered", 707041059),
        new("Pantheon: Morgeth Surpassing", "Morgeth Mastered", 707041058),
        new("Pantheon: Insurrection Prime Revolutionary", "Insurrection Eradicated", 690263480),
    ];

    public async Task<StoryVisualAssetsReport> GetStoryVisualAssetsAsync(
        CancellationToken cancellationToken)
    {
        var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        var modes = await manifest.GetActivityModeDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var classes = await manifest.GetClassDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        var records = await manifest
            .GetTableAsync<ManifestRecordDefinition>("DestinyRecordDefinition", cancellationToken)
            .ConfigureAwait(false);
        var presentationNodes = await manifest
            .GetTableAsync<ManifestPresentationNodeDefinition>("DestinyPresentationNodeDefinition", cancellationToken)
            .ConfigureAwait(false);
        var emblemDefinitions = await GetEmblemDefinitionSummariesAsync(
                manifest.Manifest,
                ContestRaidEmblemDefinitions.Select(item => item.Hash)
                    .Concat(PantheonEmblemDefinitions.Select(item => item.Hash))
                    .Append(GuidedGamesMasterGuideEmblemHash),
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);

        emblemDefinitions.TryGetValue(GuidedGamesMasterGuideEmblemHash, out var guidedGamesEmblem);
        var contestRaidEmblems = ContestRaidEmblemDefinitions
            .Where(item => emblemDefinitions.ContainsKey(item.Hash))
            .Select(item => new ContestRaidEmblemAsset(
                item.RaidName,
                item.EmblemName,
                emblemDefinitions[item.Hash].IconUrl))
            .ToArray();
        var pantheonEmblems = PantheonEmblemDefinitions
            .Where(item => emblemDefinitions.ContainsKey(item.Hash))
            .Select(item => new PantheonEmblemAsset(
                item.PantheonName,
                item.EmblemName,
                emblemDefinitions[item.Hash].IconUrl))
            .ToArray();

        return new StoryVisualAssetsReport(
            RaidIconUrl: GetActivityModeIcon(modes, ActivityModes.Raid),
            DungeonIconUrl: GetActivityModeIcon(modes, ActivityModes.Dungeon),
            CrucibleIconUrl: GetActivityModeIcon(modes, ActivityModes.AllPvP),
            GuidedGamesIconUrl: guidedGamesEmblem?.IconUrl
                ?? GetActivityModeIcon(modes, ActivityModes.Raid),
            ContestRaidEmblems: contestRaidEmblems,
            PantheonEmblems: pantheonEmblems,
            TitanIconUrl: FindIcon(classes.Values, "Titan")
                ?? FindIcon(presentationNodes.Values, "Titan")
                ?? string.Empty,
            HunterIconUrl: FindIcon(classes.Values, "Hunter")
                ?? FindIcon(presentationNodes.Values, "Hunter")
                ?? string.Empty,
            WarlockIconUrl: FindIcon(classes.Values, "Warlock")
                ?? FindIcon(presentationNodes.Values, "Warlock")
                ?? string.Empty,
            GoodBoyProtocolIconUrl: FindIcon(records.Values, "Good Boy Protocol", "Good Boy", "Pet the Dog")
                ?? FindIcon(presentationNodes.Values, "Good Boy Protocol", "Good Boy", "Pet the Dog")
                ?? string.Empty);
    }

    private static string GetActivityModeIcon(
        IReadOnlyDictionary<string, ManifestActivityModeDefinition> modes,
        int modeType)
    {
        var path = modes.Values
            .FirstOrDefault(mode => mode.ModeType == modeType)
            ?.DisplayProperties
            ?.Icon;

        return BungieUrl(path);
    }

    private static string? FindIcon<TDefinition>(
        IEnumerable<TDefinition> definitions,
        params string[] preferredNames)
    {
        foreach (var preferredName in preferredNames)
        {
            var path = definitions
                .Select(GetDisplayProperties)
                .Where(properties => properties is not null && !string.IsNullOrWhiteSpace(properties.Icon))
                .FirstOrDefault(properties =>
                    properties!.Name?.Contains(preferredName, StringComparison.OrdinalIgnoreCase) == true
                    || properties.Description?.Contains(preferredName, StringComparison.OrdinalIgnoreCase) == true)
                ?.Icon;

            if (!string.IsNullOrWhiteSpace(path))
            {
                return BungieUrl(path);
            }
        }

        return null;
    }

    private static ManifestDisplayProperties? GetDisplayProperties<TDefinition>(TDefinition definition) =>
        definition switch
        {
            ManifestCharacterIdentityDefinition identity => identity.DisplayProperties,
            ManifestActivityDefinition activity => activity.DisplayProperties,
            ManifestPresentationNodeDefinition node => node.DisplayProperties,
            ManifestRecordDefinition record => record.DisplayProperties,
            _ => null,
        };

    private sealed record ContestRaidEmblemDefinition(string RaidName, string EmblemName, long Hash);

    private sealed record PantheonEmblemDefinition(string PantheonName, string EmblemName, long Hash);

}
