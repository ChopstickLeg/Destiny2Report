namespace Destiny2Report.API.Features.Crawler;

public interface ICrawlProgress
{
    ValueTask StartPhaseAsync(string phase, string label, long? total = null, CancellationToken cancellationToken = default);

    ValueTask ReportAsync(long current, long? total = null, CancellationToken cancellationToken = default);

    ValueTask CompletePhaseAsync(long? current = null, long? total = null, CancellationToken cancellationToken = default);
}
