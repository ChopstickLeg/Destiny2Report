use std::{env, time::Duration};

use opentelemetry::{KeyValue, global, trace::TracerProvider as _};
use opentelemetry_sdk::{Resource, trace::SdkTracerProvider};
use tracing_subscriber::{
    EnvFilter, Registry,
    filter::{LevelFilter, filter_fn},
    layer::{Layer, SubscriberExt},
    util::SubscriberInitExt,
};

const DEFAULT_SERVICE_NAME: &str = "Destiny2Report.Crawler";

pub struct TelemetryGuard {
    provider: Option<SdkTracerProvider>,
}

impl TelemetryGuard {
    pub fn shutdown(&self) {
        if let Some(provider) = &self.provider
            && let Err(error) = provider.shutdown_with_timeout(Duration::from_secs(5))
        {
            eprintln!("OpenTelemetry shutdown failed: {error}");
        }
    }
}

pub fn init() -> anyhow::Result<TelemetryGuard> {
    let log_filter = EnvFilter::builder()
        .with_default_directive(LevelFilter::INFO.into())
        .from_env_lossy();
    let fmt_layer = tracing_subscriber::fmt::layer()
        .json()
        .with_filter(log_filter);

    if env::var_os("OTEL_EXPORTER_OTLP_ENDPOINT").is_none()
        && env::var_os("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT").is_none()
    {
        Registry::default().with(fmt_layer).init();
        return Ok(TelemetryGuard { provider: None });
    }

    let exporter = opentelemetry_otlp::SpanExporter::builder()
        .with_tonic()
        .build()?;
    let service_name =
        env::var("OTEL_SERVICE_NAME").unwrap_or_else(|_| DEFAULT_SERVICE_NAME.to_owned());
    let provider = SdkTracerProvider::builder()
        .with_resource(
            Resource::builder()
                .with_service_name(service_name)
                .with_attribute(KeyValue::new("service.version", env!("CARGO_PKG_VERSION")))
                .build(),
        )
        .with_batch_exporter(exporter)
        .build();
    global::set_tracer_provider(provider.clone());
    let tracer = provider.tracer(DEFAULT_SERVICE_NAME);
    let otel_layer = tracing_opentelemetry::layer()
        .with_tracer(tracer)
        .with_filter(filter_fn(|metadata| {
            metadata.target().starts_with("destiny2report_crawler")
        }));

    Registry::default()
        .with(fmt_layer)
        .with(otel_layer)
        .try_init()?;

    Ok(TelemetryGuard {
        provider: Some(provider),
    })
}
