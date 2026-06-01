namespace Destiny2Report.API.Observability;

public sealed class TelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    public string ServiceName { get; set; } = "Destiny2Report.API";

    public string ServiceVersion { get; set; } = "0.1.0";

    public string Endpoint { get; set; } = "http://localhost:4317";

    public string Protocol { get; set; } = "Grpc";
}
