namespace Destiny2Report.API.Features.Crawler;

public sealed class ConquestOptions
{
    public const string SectionName = "Conquests";

    public List<ConquestActivityOptions> Activities { get; init; } = [];
}

public sealed class ConquestActivityOptions
{
    public long ActivityId { get; init; }

    public string EdgeOfFateName { get; init; } = "";

    public string RenegadesName { get; init; } = "";
}

internal sealed class ConquestLookup
{
    internal static readonly DateTimeOffset RenegadesRelease =
        new(2025, 12, 2, 17, 0, 0, TimeSpan.Zero);

    private readonly IReadOnlyDictionary<long, ConquestActivityOptions> activities;

    private ConquestLookup(IReadOnlyDictionary<long, ConquestActivityOptions> activities)
    {
        this.activities = activities;
    }

    public static ConquestLookup FromOptions(ConquestOptions options)
    {
        var activities = new Dictionary<long, ConquestActivityOptions>();
        foreach (var activity in options.Activities.Where(IsValid))
        {
            foreach (var hash in GetHashAliases(activity.ActivityId))
            {
                activities[hash] = activity;
            }
        }

        return new ConquestLookup(activities);
    }

    public string? GetName(long referenceId, long directorActivityHash, DateTimeOffset completedAt)
    {
        if (!activities.TryGetValue(referenceId, out var activity)
            && !activities.TryGetValue(directorActivityHash, out activity))
        {
            return null;
        }

        return completedAt < RenegadesRelease
            ? activity.EdgeOfFateName.Trim()
            : activity.RenegadesName.Trim();
    }

    private static bool IsValid(ConquestActivityOptions activity)
    {
        return activity.ActivityId != 0
            && !string.IsNullOrWhiteSpace(activity.EdgeOfFateName)
            && !string.IsNullOrWhiteSpace(activity.RenegadesName);
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
