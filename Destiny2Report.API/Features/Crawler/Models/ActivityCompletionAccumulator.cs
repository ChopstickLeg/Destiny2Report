using MongoDB.Bson.Serialization.Attributes;

namespace Destiny2Report.API.Features.Crawler.Models;

public record ActivityCompletionAccumulator
{
    public int CompletionCount { get; set; }
    [BsonIgnoreIfNull]
    public RaidFirstCompletion? FirstCompletion { get; set; }
    [BsonIgnoreIfDefault]
    public bool ContestClear { get; set; }
    [BsonIgnoreIfDefault]
    public bool FlawlessClear { get; set; }
    [BsonIgnoreIfDefault]
    public bool SoloClear { get; set; }
    [BsonIgnoreIfDefault]
    public bool SoloFlawlessClear { get; set; }
}
