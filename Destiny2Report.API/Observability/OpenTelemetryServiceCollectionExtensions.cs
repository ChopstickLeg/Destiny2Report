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

    private static void ConfigureOtlpExporter(OtlpExporterOptions exporterOptions, TelemetryOptions telemetryOptions)
    {
        exporterOptions.Endpoint = new Uri(telemetryOptions.Endpoint);
        exporterOptions.Protocol = telemetryOptions.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase)
            || telemetryOptions.Protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
    }
}
