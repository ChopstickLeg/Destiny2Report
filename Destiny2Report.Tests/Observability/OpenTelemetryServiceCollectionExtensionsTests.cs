using Destiny2Report.API.Observability;
using OpenTelemetry.Exporter;

namespace Destiny2Report.Tests.Observability;

public sealed class OpenTelemetryServiceCollectionExtensionsTests
{
    [Fact]
    public void ConfigureOtlpExporter_DoesNotAddAuthorizationWithoutToken()
    {
        var exporterOptions = new OtlpExporterOptions();
        var telemetryOptions = new TelemetryOptions
        {
            AuthorizationBearerToken = "   "
        };

        OpenTelemetryServiceCollectionExtensions.ConfigureOtlpExporter(
            exporterOptions,
            telemetryOptions);

        Assert.Null(exporterOptions.Headers);
    }

    [Fact]
    public void ConfigureOtlpExporter_AddsEncodedBearerAuthorization()
    {
        var exporterOptions = new OtlpExporterOptions();
        var telemetryOptions = new TelemetryOptions
        {
            AuthorizationBearerToken = " token-value "
        };

        OpenTelemetryServiceCollectionExtensions.ConfigureOtlpExporter(
            exporterOptions,
            telemetryOptions);

        Assert.Equal("authorization=Bearer%20token-value", exporterOptions.Headers);
    }

    [Fact]
    public void ConfigureOtlpExporter_PreservesExistingHeaders()
    {
        var exporterOptions = new OtlpExporterOptions
        {
            Headers = "organization=default,stream-name=default"
        };
        var telemetryOptions = new TelemetryOptions
        {
            AuthorizationBearerToken = "token-value"
        };

        OpenTelemetryServiceCollectionExtensions.ConfigureOtlpExporter(
            exporterOptions,
            telemetryOptions);

        Assert.Equal(
            "organization=default,stream-name=default,authorization=Bearer%20token-value",
            exporterOptions.Headers);
    }
}
