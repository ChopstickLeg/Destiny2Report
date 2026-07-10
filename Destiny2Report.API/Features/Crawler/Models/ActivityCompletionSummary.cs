namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityCompletionSummary
{
    public string ActivityName { get; init; } = "";
    public int CompletionCount { get; init; }
    public RaidFirstCompletion? FirstCompletion { get; init; }
    public bool ContestClear { get; init; }
    public bool FlawlessClear { get; init; }
    public bool SoloClear { get; init; }
    public bool SoloFlawlessClear { get; init; }
}
