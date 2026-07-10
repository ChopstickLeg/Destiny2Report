namespace Destiny2Report.API.Features.Crawler.Models;

public record RaidFirstCompletion
{
    public DateTime CompletedAt { get; set; }
    public long InstanceId { get; set; }
}
