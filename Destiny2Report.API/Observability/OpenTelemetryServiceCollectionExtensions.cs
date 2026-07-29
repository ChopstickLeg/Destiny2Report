using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Destiny2Report.API.Observability;

public static class OpenTelemetryServiceCollectionExtensions
{
    public static WebApplicationBuilder AddAppOpenTelemetry(this WebApplicationBuilder builder)
    {
        var telemetryOptions = builder.Configuration
            .GetSection(TelemetryOptions.SectionName)
            .Get<TelemetryOptions>() ?? new TelemetryOptions();

        builder.Services.Configure<TelemetryOptions>(
            builder.Configuration.GetSection(TelemetryOptions.SectionName));

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: telemetryOptions.ServiceName,
                serviceVersion: telemetryOptions.ServiceVersion,
                serviceInstanceId: Environment.MachineName);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: telemetryOptions.ServiceName,
                serviceVersion: telemetryOptions.ServiceVersion,
                serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing => tracing
                .SetSampler(CreateTraceSampler(telemetryOptions))
                .AddSource(AppTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRedisInstrumentation()
                .AddOtlpExporter(options => ConfigureOtlpExporter(options, telemetryOptions)))
            .WithMetrics(metrics => metrics
                .AddMeter(AppTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddProcessInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(options => ConfigureOtlpExporter(options, telemetryOptions)));

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;
            logging.SetResourceBuilder(resourceBuilder);
            logging.AddOtlpExporter(options => ConfigureOtlpExporter(options, telemetryOptions));
        });

        return builder;
    }

    internal static void ConfigureOtlpExporter(OtlpExporterOptions exporterOptions, TelemetryOptions telemetryOptions)
    {
        exporterOptions.Endpoint = new Uri(telemetryOptions.Endpoint);
        exporterOptions.Protocol = telemetryOptions.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase)
            || telemetryOptions.Protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

        var authorizationValue = !string.IsNullOrWhiteSpace(telemetryOptions.AuthorizationHeader)
            ? telemetryOptions.AuthorizationHeader.Trim()
            : !string.IsNullOrWhiteSpace(telemetryOptions.AuthorizationBearerToken)
                ? $"Bearer {telemetryOptions.AuthorizationBearerToken.Trim()}"
                : null;

        var headers = new[]
        {
            exporterOptions.Headers,
            telemetryOptions.Headers?.Trim(),
            authorizationValue is null
                ? null
                : $"authorization={Uri.EscapeDataString(authorizationValue)}"
        };

        exporterOptions.Headers = string.Join(
            ',',
            headers.Where(header => !string.IsNullOrWhiteSpace(header)));

        if (exporterOptions.Headers.Length == 0)
        {
            exporterOptions.Headers = null;
        }
    }

    private static Sampler CreateTraceSampler(TelemetryOptions telemetryOptions)
    {
        if (telemetryOptions.TraceSampleRatio is < 0 or > 1)
        {
            throw new InvalidOperationException($"{TelemetryOptions.SectionName}:{nameof(TelemetryOptions.TraceSampleRatio)} must be between 0 and 1.");
        }

        return new ParentBasedSampler(new TraceIdRatioBasedSampler(telemetryOptions.TraceSampleRatio));
    }
}
