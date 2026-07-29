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
const OTEL_EXPORTER_OTLP_AUTHORIZATION_HEADER: &str = "OTEL_EXPORTER_OTLP_AUTHORIZATION_HEADER";
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
    let authorization_header = env::var(OTEL_EXPORTER_OTLP_AUTHORIZATION_HEADER).ok();
    let bearer_token = env::var(OTEL_EXPORTER_OTLP_BEARER_TOKEN).ok();
    if let Some(authorization) =
        authorization_value(authorization_header.as_deref(), bearer_token.as_deref())
    {
        let mut metadata = MetadataMap::new();
        metadata.insert(
            "authorization",
            authorization.parse().map_err(|error| {
                anyhow::anyhow!(
                    "the configured OTLP authorization header is not a valid gRPC metadata value: {error}"
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

fn authorization_value(header: Option<&str>, bearer_token: Option<&str>) -> Option<String> {
    header
        .map(str::trim)
        .filter(|header| !header.is_empty())
        .map(str::to_owned)
        .or_else(|| {
            bearer_token
                .map(str::trim)
                .filter(|token| !token.is_empty())
                .map(|token| format!("Bearer {token}"))
        })
}

#[cfg(test)]
mod tests {
    use super::authorization_value;

    #[test]
    fn bearer_authorization_is_absent_without_a_token() {
        assert_eq!(authorization_value(None, None), None);
        assert_eq!(authorization_value(None, Some("   ")), None);
    }

    #[test]
    fn bearer_authorization_uses_a_trimmed_token() {
        assert_eq!(
            authorization_value(None, Some("  token-value  ")),
            Some("Bearer token-value".to_owned())
        );
    }

    #[test]
    fn explicit_authorization_header_is_used_as_is() {
        assert_eq!(
            authorization_value(Some("  Basic dXNlcjpwYXNzd29yZA==  "), None),
            Some("Basic dXNlcjpwYXNzd29yZA==".to_owned())
        );
    }

    #[test]
    fn explicit_authorization_header_takes_precedence() {
        assert_eq!(
            authorization_value(Some("Basic dXNlcjpwYXNzd29yZA=="), Some("ignored")),
            Some("Basic dXNlcjpwYXNzd29yZA==".to_owned())
        );
    }
}
