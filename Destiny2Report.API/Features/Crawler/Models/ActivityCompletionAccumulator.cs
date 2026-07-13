using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityCompletionAccumulator
{
    public int ActivityCount { get; set; }
    public int CompletionCount { get; set; }
    [BsonIgnoreIfNull]
    public RaidFirstCompletion? FirstCompletion { get; set; }
    [BsonIgnoreIfNull]
    public RaidFirstCompletion? LastCompletion { get; set; }
    [BsonIgnoreIfNull]
    public ActivityFastestCompletion? FastestCompletion { get; set; }
    [BsonIgnoreIfDefault]
    public bool ContestClear { get; set; }
    [BsonIgnoreIfDefault]
    public bool FlawlessClear { get; set; }
    [BsonIgnoreIfDefault]
    public bool SoloClear { get; set; }
    [BsonIgnoreIfDefault]
    public bool SoloFlawlessClear { get; set; }
}
