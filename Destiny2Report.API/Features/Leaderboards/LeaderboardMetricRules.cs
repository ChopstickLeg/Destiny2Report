namespace Destiny2Report.API.Features.Leaderboards;

public static class LeaderboardMetricRules
{
    public static bool IsRecognizedWeapon(long weaponHash, string? categoryName)
    {
        return weaponHash > 0
            && !string.IsNullOrWhiteSpace(categoryName)
            && !categoryName.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && !categoryName.Equals("Abilities", StringComparison.OrdinalIgnoreCase);
    }
}
