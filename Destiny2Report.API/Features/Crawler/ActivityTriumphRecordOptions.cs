namespace Destiny2Report.API.Features.Crawler;

public sealed class ActivityTriumphRecordOptions
{
    public const string SectionName = "ActivityTriumphRecords";

    public List<RaidTriumphRecordOptions> Raids { get; init; } = [];

    public List<DungeonTriumphRecordOptions> Dungeons { get; init; } = [];
}

public sealed class RaidTriumphRecordOptions
{
    public string ActivityName { get; init; } = "";

    public long RecordId { get; init; }
}

public sealed class DungeonTriumphRecordOptions
{
    public string ActivityName { get; init; } = "";

    public long SoloRecordId { get; init; }

    public long FlawlessRecordId { get; init; }

    public long SoloFlawlessRecordId { get; init; }
}
