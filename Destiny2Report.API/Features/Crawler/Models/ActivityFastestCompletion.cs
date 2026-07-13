namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityFastestCompletion
{
    public TimeSpan Duration { get; init; }
    public DateTime CompletedAt { get; init; }
    public long InstanceId { get; init; }
}
