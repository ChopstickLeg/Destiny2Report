namespace Destiny2Report.API.Features.Leaderboards;

public sealed class LeaderboardsOptions
{
    public const string SectionName = "Leaderboards";
    public int MinimumCompletedPlayers { get; init; } = 1000;
}
