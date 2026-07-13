using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityCompletionSummary
{
    public string ActivityName { get; init; } = "";
    public int ActivityCount { get; init; }
    public int CompletionCount { get; init; }
    public double ClearRate => ActivityCount == 0 ? 0 : Math.Round((double)CompletionCount / ActivityCount, 4);
    [BsonIgnoreIfNull]
    public RaidFirstCompletion? FirstCompletion { get; init; }
    [BsonIgnoreIfNull]
    public RaidFirstCompletion? LastCompletion { get; init; }
    [BsonIgnoreIfNull]
    public ActivityFastestCompletion? FastestCompletion { get; init; }
    [BsonIgnoreIfDefault]
    public bool ContestClear { get; init; }
    [BsonIgnoreIfDefault]
    public bool FlawlessClear { get; init; }
    [BsonIgnoreIfDefault]
    public bool SoloClear { get; init; }
    [BsonIgnoreIfDefault]
    public bool SoloFlawlessClear { get; init; }
}
