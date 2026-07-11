using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityCompletionSummary
{
    public string ActivityName { get; init; } = "";
    public int CompletionCount { get; init; }
    [BsonIgnoreIfNull]
    public RaidFirstCompletion? FirstCompletion { get; init; }
    [BsonIgnoreIfDefault]
    public bool ContestClear { get; init; }
    [BsonIgnoreIfDefault]
    public bool FlawlessClear { get; init; }
    [BsonIgnoreIfDefault]
    public bool SoloClear { get; init; }
    [BsonIgnoreIfDefault]
    public bool SoloFlawlessClear { get; init; }
}
