namespace Destiny2Report.API.Features.Crawler.Models;

public record GambitMotesReport
{
    public GambitMoteStatReport MotesBanked { get; init; } = new();
    public GambitMoteStatReport MotesLost { get; init; } = new();
    public GambitMoteStatReport MotesDenied { get; init; } = new();
}
