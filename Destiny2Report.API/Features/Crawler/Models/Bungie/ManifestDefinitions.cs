using Newtonsoft.Json;

namespace Destiny2Report.API.Features.Crawler.Models.Bungie;

// These are deliberately limited to the manifest fields consumed by the crawler.
// Avoiding the full manifest schema keeps the local cache substantially smaller.
public sealed class ManifestDisplayProperties
{
    [JsonProperty("name")]
    public string? Name { get; init; }

    [JsonProperty("description")]
    public string? Description { get; init; }

    [JsonProperty("icon")]
    public string? Icon { get; init; }
}

public sealed class ManifestActivityDefinition
{
    [JsonProperty("displayProperties")]
    public ManifestDisplayProperties? DisplayProperties { get; init; }

    [JsonProperty("destinationHash")]
    public long DestinationHash { get; init; }

    [JsonProperty("activityTypeHash")]
    public long ActivityTypeHash { get; init; }
}

public sealed class ManifestActivityModeDefinition
{
    [JsonProperty("displayProperties")]
    public ManifestDisplayProperties? DisplayProperties { get; init; }

    [JsonProperty("modeType")]
    public int ModeType { get; init; }
}

public sealed class ManifestDestinationDefinition
{
    [JsonProperty("displayProperties")]
    public ManifestDisplayProperties? DisplayProperties { get; init; }
}

public sealed class ManifestCharacterIdentityDefinition
{
    [JsonProperty("displayProperties")]
    public ManifestDisplayProperties? DisplayProperties { get; init; }
}

public sealed class ManifestPresentationNodeDefinition
{
    [JsonProperty("displayProperties")]
    public ManifestDisplayProperties? DisplayProperties { get; init; }

    [JsonProperty("completionRecordHash")]
    public long CompletionRecordHash { get; init; }

    [JsonProperty("children")]
    public ManifestPresentationNodeChildren? Children { get; init; }
}

public sealed class ManifestPresentationNodeChildren
{
    [JsonProperty("presentationNodes")]
    public IReadOnlyCollection<ManifestPresentationNodeChild>? PresentationNodes { get; init; }
}

public sealed class ManifestPresentationNodeChild
{
    [JsonProperty("nodeDisplayPriority")]
    public int NodeDisplayPriority { get; init; }

    [JsonProperty("presentationNodeHash")]
    public long PresentationNodeHash { get; init; }
}

public sealed class ManifestRecordDefinition
{
    [JsonProperty("displayProperties")]
    public ManifestDisplayProperties? DisplayProperties { get; init; }
}

public sealed class ManifestMetricDefinition
{
    [JsonProperty("displayProperties")]
    public ManifestDisplayProperties? DisplayProperties { get; init; }
}

public sealed class ManifestInventoryItemDefinition
{
    [JsonProperty("itemType")]
    public int ItemType { get; init; }

    [JsonProperty("displayProperties")]
    public ManifestDisplayProperties? DisplayProperties { get; init; }

    [JsonProperty("itemTypeDisplayName")]
    public string? ItemTypeDisplayName { get; init; }

    [JsonProperty("itemSubType")]
    public int ItemSubType { get; init; }

    [JsonProperty("defaultDamageType")]
    public int DefaultDamageType { get; init; }

    [JsonProperty("inventory")]
    public ManifestInventoryBlock? Inventory { get; init; }
}

public sealed class ManifestInventoryBlock
{
    [JsonProperty("tierType")]
    public int TierType { get; init; }
}
