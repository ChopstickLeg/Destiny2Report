namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityCompletionAccumulator
{
    public int CompletionCount { get; set; }
    public bool ContestClear { get; set; }
    public bool FlawlessClear { get; set; }
    public bool SoloClear { get; set; }
    public bool SoloFlawlessClear { get; set; }
}
