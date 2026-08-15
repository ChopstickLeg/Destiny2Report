using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Destiny2Report.API.Observability;

public static class AppTelemetry
{
    public const string ActivitySourceName = "Destiny2Report.API";
    public const string MeterName = "Destiny2Report.API";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
}
