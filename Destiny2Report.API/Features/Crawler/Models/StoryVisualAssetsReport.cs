namespace Destiny2Report.API.Features.Crawler.Models;

public sealed record StoryVisualAssetsReport(
    string RaidIconUrl,
    string DungeonIconUrl,
    string CrucibleIconUrl,
    string GuidedGamesIconUrl,
    IReadOnlyList<ContestRaidEmblemAsset> ContestRaidEmblems,
    IReadOnlyList<PantheonEmblemAsset> PantheonEmblems,
    string TitanIconUrl,
    string HunterIconUrl,
    string WarlockIconUrl,
    string GoodBoyProtocolIconUrl);

public sealed record ContestRaidEmblemAsset(
    string RaidName,
    string EmblemName,
    string IconUrl);

public sealed record PantheonEmblemAsset(
    string PantheonName,
    string EmblemName,
    string IconUrl);
