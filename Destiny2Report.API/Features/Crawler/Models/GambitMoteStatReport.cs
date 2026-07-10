namespace Destiny2Report.API.Features.Crawler.Models;

public record GambitMoteStatReport
{
    public int Total { get; init; }
    public Dictionary<string, int> ByMode { get; init; } = new();
}
