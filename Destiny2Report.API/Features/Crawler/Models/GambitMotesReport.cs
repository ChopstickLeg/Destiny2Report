namespace Destiny2Report.API.Features.Crawler.Models;

public record GambitMotesReport
{
    public int Matches { get; init; }
    public GambitMoteStatReport MotesBanked { get; init; } = new();
    public GambitMoteStatReport MotesLost { get; init; } = new();
    public GambitMoteStatReport MotesDenied { get; init; } = new();
    public double AverageMotesBanked => Matches == 0 ? 0 : Math.Round((double)MotesBanked.Total / Matches, 2);
    public double AverageMotesLost => Matches == 0 ? 0 : Math.Round((double)MotesLost.Total / Matches, 2);
}
