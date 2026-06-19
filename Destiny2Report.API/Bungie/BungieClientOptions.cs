namespace Destiny2Report.API.Bungie;

public sealed class BungieClientOptions
{
    public const string SectionName = "Bungie";

    public string? ApiKey { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }
}
