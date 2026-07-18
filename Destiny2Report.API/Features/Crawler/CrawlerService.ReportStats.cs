using D2Report.BungieClient;
using Destiny2Report.API.Features.Crawler.Models;
using MongoDB.Driver;

namespace Destiny2Report.API.Features.Crawler;

public partial class CrawlerService
{
    private static void ApplyAccountStats(
        DestinyReport report,
        DestinyHistoricalStatsAccountResult accountStats,
        IEnumerable<DestinyHistoricalStatsPerCharacter> historicalCharacters,
        IReadOnlyDictionary<long, string> characterClassById,
        IReadOnlyDictionary<long, string> recoveredRaceById,
        DestinyProfileResponse profile)
    {
        report.Misadventures = (int)accountStats.Characters.Sum(c => c.Results.Sum(a => a.Value.AllTime?.TryGetValue("suicides", out var stat) ?? false ? stat?.Basic.Value ?? 0 : 0));

        report.CharacterPlaytime = BuildCharacterPlaytime(
            historicalCharacters,
            characterClassById,
            recoveredRaceById,
            profile.Characters?.Data?.Values ?? []);
    }

    private static void ApplyProfileStats(DestinyReport report, DestinyProfileResponse profile, ManifestContext manifest)
    {
        var profileCharacters = profile.Characters?.Data?.Values.ToArray() ?? [];
        report.TotalPlaytime = TimeSpan.FromMinutes(profileCharacters.Sum(character => character.MinutesPlayedTotal));

        var metrics = profile.Metrics?.Data?.Metrics;
        if (metrics is null)
        {
            return;
        }

        report.GoodBoyProtocol = GetMetricProgress(metrics, manifest.FindMetricHash("Good Boy Protocol"));
        report.FishCaught = GetMetricProgress(metrics, manifest.FindMetricHash("Total Fish Caught"));
    }

    private static int GetMetricProgress(IDictionary<string, DestinyMetricComponent> metrics, uint? metricHash)
    {
        return metricHash is not null && metrics.TryGetValue(metricHash.Value.ToString(), out var metric)
            ? metric.ObjectiveProgress?.Progress ?? 0
            : 0;
    }

    private static void ApplyModeStats(
        DestinyReport report,
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats)
    {
        report.CrucibleKd = WeightedRatio(modeStats, ActivityModes.AllPvP, "kills", "deaths", "killsDeathsRatio");
        report.CrucibleKda = AverageModeStat(modeStats, ActivityModes.AllPvP, "killsDeathsAssists");
        report.GambitKd = WeightedRatio(modeStats, [ActivityModes.Gambit, ActivityModes.GambitPrime], "kills", "deaths", "killsDeathsRatio");
        report.GambitKda = AverageModeStat(modeStats, [ActivityModes.Gambit, ActivityModes.GambitPrime], "killsDeathsAssists");
        report.CrucibleMatchesPlayed = (int)SumModeStat(modeStats, ActivityModes.AllPvP, "activitiesEntered");
        report.GambitMatchesPlayed = (int)(SumModeStat(modeStats, ActivityModes.Gambit, "activitiesEntered") + SumModeStat(modeStats, ActivityModes.GambitPrime, "activitiesEntered"));
        report.CrucibleWins = (int)SumModeStat(modeStats, ActivityModes.AllPvP, "activitiesWon");
        report.GambitWins = (int)(SumModeStat(modeStats, ActivityModes.Gambit, "activitiesWon") + SumModeStat(modeStats, ActivityModes.GambitPrime, "activitiesWon"));
    }

    private static double SumModeStat(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        int mode,
        string statId)
    {
        return modeStats
            .Where(item => item.Key.Mode == mode)
            .Sum(item => GetStat(GetPreferredStatsBucket(item.Value, mode)?.AllTime, statId));
    }

    private static double AverageModeStat(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        int mode,
        string statId)
    {
        return AverageModeStat(modeStats, [mode], statId);
    }

    private static double AverageModeStat(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        IReadOnlyCollection<int> modes,
        string statId)
    {
        var values = modeStats
            .Where(item => modes.Contains(item.Key.Mode))
            .Select(item => GetStat(GetPreferredStatsBucket(item.Value, item.Key.Mode)?.AllTime, statId))
            .Where(value => value > 0)
            .ToArray();

        return values.Length == 0 ? 0 : Math.Round(values.Average(), 3);
    }

    private static double WeightedRatio(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        int mode,
        string numeratorStat,
        string denominatorStat,
        string fallbackStat)
    {
        return WeightedRatio(modeStats, [mode], numeratorStat, denominatorStat, fallbackStat);
    }

    private static double WeightedRatio(
        IReadOnlyDictionary<(long CharacterId, int Mode), IDictionary<string, DestinyHistoricalStatsByPeriod>> modeStats,
        IReadOnlyCollection<int> modes,
        string numeratorStat,
        string denominatorStat,
        string fallbackStat)
    {
        var values = modeStats
            .Where(item => modes.Contains(item.Key.Mode))
            .Select(item => GetPreferredStatsBucket(item.Value, item.Key.Mode)?.AllTime)
            .Where(allTime => allTime is not null)
            .ToArray();

        var numerator = values.Sum(allTime => GetStat(allTime, numeratorStat));
        var denominator = values.Sum(allTime => GetStat(allTime, denominatorStat));
        if (denominator > 0)
        {
            return Math.Round(numerator / denominator, 3);
        }

        var fallbackValues = values.Select(allTime => GetStat(allTime, fallbackStat)).Where(value => value > 0).ToArray();
        return fallbackValues.Length == 0 ? 0 : Math.Round(fallbackValues.Average(), 3);
    }

    private static double GetStat(IDictionary<string, DestinyHistoricalStatsValue>? stats, string statId)
    {
        return stats is not null && stats.TryGetValue(statId, out var value)
            ? value.Basic?.Value ?? 0
            : 0;
    }

    private static DestinyHistoricalStatsByPeriod? GetPreferredStatsBucket(
        IDictionary<string, DestinyHistoricalStatsByPeriod> stats,
        int mode)
    {
        foreach (var key in PreferredStatsKeys(mode))
        {
            if (stats.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return stats.Values.FirstOrDefault();
    }

    private static IEnumerable<string> PreferredStatsKeys(int mode)
    {
        return mode switch
        {
            ActivityModes.AllPvE => ["allPvE", "allTime"],
            ActivityModes.AllPvP => ["allPvP", "allTime"],
            ActivityModes.Gambit => ["gambit", "allTime"],
            ActivityModes.GambitPrime => ["gambitPrime", "allTime"],
            _ => ["allTime"]
        };
    }
}
