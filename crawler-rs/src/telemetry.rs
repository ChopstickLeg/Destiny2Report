use std::{env, time::Duration};

use opentelemetry::{KeyValue, global, trace::TracerProvider as _};
use opentelemetry_otlp::{WithTonicConfig, tonic_types::metadata::MetadataMap};
use opentelemetry_sdk::{Resource, trace::SdkTracerProvider};
use tracing_subscriber::{
    EnvFilter, Registry,
    filter::{LevelFilter, filter_fn},
    layer::{Layer, SubscriberExt},
    util::SubscriberInitExt,
};

const DEFAULT_SERVICE_NAME: &str = "Destiny2Report.Crawler";
const OTEL_EXPORTER_OTLP_BEARER_TOKEN: &str = "OTEL_EXPORTER_OTLP_BEARER_TOKEN";

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

    let mut exporter_builder = opentelemetry_otlp::SpanExporter::builder().with_tonic();
    let bearer_token = env::var(OTEL_EXPORTER_OTLP_BEARER_TOKEN).ok();
    if let Some(authorization) = bearer_authorization_value(bearer_token.as_deref()) {
        let mut metadata = MetadataMap::new();
        metadata.insert(
            "authorization",
            authorization.parse().map_err(|error| {
                anyhow::anyhow!(
                    "{OTEL_EXPORTER_OTLP_BEARER_TOKEN} is not a valid gRPC metadata value: {error}"
                )
            })?,
        );
        exporter_builder = exporter_builder.with_metadata(metadata);
    }
    let exporter = exporter_builder.build()?;
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

fn bearer_authorization_value(token: Option<&str>) -> Option<String> {
    token
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .map(|token| format!("Bearer {token}"))
}

#[cfg(test)]
mod tests {
    use super::bearer_authorization_value;

    #[test]
    fn bearer_authorization_is_absent_without_a_token() {
        assert_eq!(bearer_authorization_value(None), None);
        assert_eq!(bearer_authorization_value(Some("   ")), None);
    }

    #[test]
    fn bearer_authorization_uses_a_trimmed_token() {
        assert_eq!(
            bearer_authorization_value(Some("  token-value  ")),
            Some("Bearer token-value".to_owned())
        );
    }
}
