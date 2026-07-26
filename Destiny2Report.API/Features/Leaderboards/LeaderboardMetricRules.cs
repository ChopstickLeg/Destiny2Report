namespace Destiny2Report.API.Features.Leaderboards;

public static class LeaderboardMetricRules
{
    private static readonly string[] SpecificModeMetricPrefixes =
    [
        "time.mode",
        "combat.kills.mode",
        "competition.crucible.playlist"
    ];

    private static readonly int[] PrivateMatchModes = [32, 51, 52, 53, 54, 55, 56, 57];

    public static IReadOnlyList<string> ExcludedMetricKeys { get; } = PrivateMatchModes
        .SelectMany(mode => SpecificModeMetricPrefixes.Select(prefix => $"{prefix}.{mode}"))
        .ToArray();

    public static bool IsPrivateMatchMode(int mode) =>
        mode == 32 || mode is >= 51 and <= 57;

    public static bool IsPublishedMetric(string metricKey)
    {
        var prefix = SpecificModeMetricPrefixes.FirstOrDefault(
            value => metricKey.StartsWith($"{value}.", StringComparison.Ordinal));
        return prefix is null
            || !int.TryParse(metricKey.AsSpan(prefix.Length + 1), out var mode)
            || !IsPrivateMatchMode(mode);
    }

    public static bool IsRecognizedWeapon(long weaponHash, string? categoryName)
    {
        return weaponHash > 0
            && !string.IsNullOrWhiteSpace(categoryName)
            && !categoryName.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && !categoryName.Equals("Abilities", StringComparison.OrdinalIgnoreCase);
    }
}
