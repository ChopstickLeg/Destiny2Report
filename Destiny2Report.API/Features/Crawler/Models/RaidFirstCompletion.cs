namespace Destiny2Report.API.Features.Crawler.Models;

public record RaidFirstCompletion
{
    public DateTimeOffset CompletedAt { get; set; }
    public long InstanceId { get; set; }
}
