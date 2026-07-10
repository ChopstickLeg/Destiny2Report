using System.Diagnostics;

namespace Destiny2Report.API.Observability;

public static class AppTelemetry
{
    public const string ActivitySourceName = "Destiny2Report.API";
    public const string MeterName = "Destiny2Report.API";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
