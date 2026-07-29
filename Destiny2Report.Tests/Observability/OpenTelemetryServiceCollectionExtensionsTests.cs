using Destiny2Report.API.Observability;
using OpenTelemetry.Exporter;

namespace Destiny2Report.Tests.Observability;

public sealed class OpenTelemetryServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("http", "traces", "https://openobserve.example/api/default/v1/traces")]
    [InlineData("http/protobuf", "metrics", "https://openobserve.example/api/default/v1/metrics")]
    [InlineData("HTTP/PROTOBUF", "logs", "https://openobserve.example/api/default/v1/logs")]
    public void ConfigureOtlpExporter_AppendsSignalPathForHttpProtobuf(
        string protocol,
        string signal,
        string expectedEndpoint)
    {
        var exporterOptions = new OtlpExporterOptions();
        var telemetryOptions = new TelemetryOptions
        {
            Endpoint = "https://openobserve.example/api/default/",
            Protocol = protocol
        };

        OpenTelemetryServiceCollectionExtensions.ConfigureOtlpExporter(
            exporterOptions,
            telemetryOptions,
            signal);

        Assert.Equal(OtlpExportProtocol.HttpProtobuf, exporterOptions.Protocol);
        Assert.Equal(new Uri(expectedEndpoint), exporterOptions.Endpoint);
    }

    [Fact]
    public void ConfigureOtlpExporter_LeavesGrpcEndpointUnchanged()
    {
        var exporterOptions = new OtlpExporterOptions();
        var telemetryOptions = new TelemetryOptions
        {
            Endpoint = "http://openobserve:5081",
            Protocol = "grpc"
        };

        OpenTelemetryServiceCollectionExtensions.ConfigureOtlpExporter(
            exporterOptions,
            telemetryOptions,
            "traces");

        Assert.Equal(OtlpExportProtocol.Grpc, exporterOptions.Protocol);
        Assert.Equal(new Uri("http://openobserve:5081"), exporterOptions.Endpoint);
    }

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
    public void ConfigureOtlpExporter_AddsEncodedAuthorizationHeader()
    {
        var exporterOptions = new OtlpExporterOptions();
        var telemetryOptions = new TelemetryOptions
        {
            AuthorizationHeader = " Basic dXNlcjpwYXNzd29yZA== "
        };

        OpenTelemetryServiceCollectionExtensions.ConfigureOtlpExporter(
            exporterOptions,
            telemetryOptions);

        Assert.Equal(
            "authorization=Basic%20dXNlcjpwYXNzd29yZA%3D%3D",
            exporterOptions.Headers);
    }

    [Fact]
    public void ConfigureOtlpExporter_AuthorizationHeaderTakesPrecedenceOverBearerToken()
    {
        var exporterOptions = new OtlpExporterOptions();
        var telemetryOptions = new TelemetryOptions
        {
            AuthorizationHeader = "Basic dXNlcjpwYXNzd29yZA==",
            AuthorizationBearerToken = "ignored-token"
        };

        OpenTelemetryServiceCollectionExtensions.ConfigureOtlpExporter(
            exporterOptions,
            telemetryOptions);

        Assert.Equal(
            "authorization=Basic%20dXNlcjpwYXNzd29yZA%3D%3D",
            exporterOptions.Headers);
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

    [Fact]
    public void ConfigureOtlpExporter_ComposesConfiguredHeadersAndAuthorization()
    {
        var exporterOptions = new OtlpExporterOptions();
        var telemetryOptions = new TelemetryOptions
        {
            Headers = " organization=default,stream-name=default ",
            AuthorizationHeader = "Basic dXNlcjpwYXNzd29yZA=="
        };

        OpenTelemetryServiceCollectionExtensions.ConfigureOtlpExporter(
            exporterOptions,
            telemetryOptions);

        Assert.Equal(
            "organization=default,stream-name=default,authorization=Basic%20dXNlcjpwYXNzd29yZA%3D%3D",
            exporterOptions.Headers);
    }
}
