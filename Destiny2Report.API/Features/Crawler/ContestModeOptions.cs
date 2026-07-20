namespace Destiny2Report.API.Features.Crawler;

public sealed class ContestModeOptions
{
    public const string SectionName = "ContestMode";

    public List<ContestModeActivityWindow> Raids { get; init; } = [];

    public List<ContestModeActivityWindow> Dungeons { get; init; } = [];
}

public sealed class ContestModeActivityWindow
{
    public long ActivityId { get; init; }

    public DateTimeOffset Start { get; init; }

    public DateTimeOffset End { get; init; }
}

internal sealed class ContestModeLookup
{
    private static readonly string[] ActivityNameSuffixes =
    [
        ": Master",
        ": Normal",
        ": Standard",
        ": Prestige",
        ": Contest",
        ": Customize",
        ": Guided Games",
        ": Legend",
        ": Expert",
        ": Challenge Mode"
    ];

    private ContestModeLookup(
        IReadOnlyDictionary<long, IReadOnlyCollection<ContestModeActivityWindow>> raids,
        IReadOnlyDictionary<long, IReadOnlyCollection<ContestModeActivityWindow>> dungeons)
    {
        Raids = raids;
        Dungeons = dungeons;
    }

    public IReadOnlyDictionary<long, IReadOnlyCollection<ContestModeActivityWindow>> Raids { get; }

    public IReadOnlyDictionary<long, IReadOnlyCollection<ContestModeActivityWindow>> Dungeons { get; }

    public static ContestModeLookup FromOptions(ContestModeOptions options)
    {
        return new ContestModeLookup(
            BuildLookup(options.Raids),
            BuildLookup(options.Dungeons));
    }

    private static IReadOnlyDictionary<long, IReadOnlyCollection<ContestModeActivityWindow>> BuildLookup(
        IEnumerable<ContestModeActivityWindow> windows)
    {
        var lookup = new Dictionary<long, List<ContestModeActivityWindow>>();
        foreach (var window in windows.Where(window => window.ActivityId != 0 && window.End > window.Start))
        {
            foreach (var hash in GetHashAliases(window.ActivityId).Distinct())
            {
                if (!lookup.TryGetValue(hash, out var activityWindows))
                {
                    activityWindows = [];
                    lookup[hash] = activityWindows;
                }

                activityWindows.Add(window);
            }
        }

        return lookup.ToDictionary(
            item => item.Key,
            item => (IReadOnlyCollection<ContestModeActivityWindow>)item.Value);
    }

    public static string NormalizeActivityName(string activityName)
    {
        var normalized = activityName.Trim();
        var removedSuffix = true;
        while (removedSuffix)
        {
            removedSuffix = false;
            foreach (var suffix in ActivityNameSuffixes)
            {
                if (!normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                normalized = normalized[..^suffix.Length].Trim();
                removedSuffix = true;
                break;
            }
        }

        return normalized;
    }

    private static IEnumerable<long> GetHashAliases(long hash)
    {
        yield return hash;

        if (hash is >= int.MinValue and <= int.MaxValue)
        {
            yield return unchecked((uint)(int)hash);
        }

        if (hash is > int.MaxValue and <= uint.MaxValue)
        {
            yield return unchecked((int)(uint)hash);
        }
    }
}
