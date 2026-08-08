namespace Destiny2Report.API.Features.Leaderboards;

public static class LeaderboardStandingRules
{
    public static long Cutoff(IReadOnlyList<long> descendingScores, double fraction)
    {
        if (descendingScores.Count == 0) throw new ArgumentException("At least one score is required.", nameof(descendingScores));
        var index = Math.Min(descendingScores.Count - 1, Math.Max(0, (int)Math.Ceiling(descendingScores.Count * fraction) - 1));
        return descendingScores[index];
    }

    public static string? PercentileTier(long score, LeaderboardPercentileThresholds thresholds) =>
        score >= thresholds.TopPointOnePercent ? "top-0.1"
        : score >= thresholds.TopOnePercent ? "top-1"
        : score >= thresholds.TopFivePercent ? "top-5"
        : null;
}
