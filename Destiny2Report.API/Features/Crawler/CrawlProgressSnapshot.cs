namespace Destiny2Report.API.Features.Crawler;

public sealed record CrawlProgressSnapshot(
    string Phase,
    string Label,
    long? Current,
    long? Total,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    internal static CrawlProgressSnapshot? FromFields(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("progressPhase", out var phase) || string.IsNullOrWhiteSpace(phase))
        {
            return null;
        }

        fields.TryGetValue("progressLabel", out var label);
        fields.TryGetValue("progressCurrent", out var currentValue);
        fields.TryGetValue("progressTotal", out var totalValue);
        fields.TryGetValue("progressStartedAtUtc", out var startedAtUtcValue);
        fields.TryGetValue("progressUpdatedAtUtc", out var progressUpdatedAtUtcValue);

        var current = long.TryParse(currentValue, out var parsedCurrent) ? parsedCurrent : (long?)null;
        var total = long.TryParse(totalValue, out var parsedTotal) ? parsedTotal : (long?)null;
        var startedAtUtc = DateTimeOffset.TryParse(startedAtUtcValue, out var parsedStartedAtUtc)
            ? parsedStartedAtUtc
            : DateTimeOffset.UtcNow;
        var updatedAtUtc = DateTimeOffset.TryParse(progressUpdatedAtUtcValue, out var parsedProgressUpdatedAtUtc)
            ? parsedProgressUpdatedAtUtc
            : startedAtUtc;

        return new CrawlProgressSnapshot(phase, label ?? phase, current, total, startedAtUtc, updatedAtUtc);
    }
}
