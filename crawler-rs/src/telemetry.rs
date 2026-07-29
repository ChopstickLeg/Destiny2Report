use std::{collections::HashMap, env, time::Duration};

use opentelemetry::{KeyValue, global, trace::TracerProvider as _};
use opentelemetry_appender_tracing::layer::OpenTelemetryTracingBridge;
use opentelemetry_otlp::{WithHttpConfig, WithTonicConfig, tonic_types::metadata::MetadataMap};
use opentelemetry_sdk::{Resource, logs::SdkLoggerProvider, trace::SdkTracerProvider};
use tracing_subscriber::{
    EnvFilter, Registry,
    filter::{LevelFilter, filter_fn},
    layer::{Layer, SubscriberExt},
    util::SubscriberInitExt,
};

const DEFAULT_SERVICE_NAME: &str = "Destiny2Report.Crawler";
const OTEL_EXPORTER_OTLP_AUTHORIZATION_HEADER: &str = "OTEL_EXPORTER_OTLP_AUTHORIZATION_HEADER";
const OTEL_EXPORTER_OTLP_BEARER_TOKEN: &str = "OTEL_EXPORTER_OTLP_BEARER_TOKEN";
const OTEL_EXPORTER_OTLP_PROTOCOL: &str = "OTEL_EXPORTER_OTLP_PROTOCOL";
const OTEL_EXPORTER_OTLP_TRACES_PROTOCOL: &str = "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL";
const OTEL_EXPORTER_OTLP_LOGS_PROTOCOL: &str = "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL";
const OTEL_EXPORTER_OTLP_LOGS_ENDPOINT: &str = "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT";

pub struct TelemetryGuard {
    tracer_provider: Option<SdkTracerProvider>,
    logger_provider: Option<SdkLoggerProvider>,
}

impl TelemetryGuard {
    pub fn shutdown(&self) {
        if let Some(provider) = &self.logger_provider
            && let Err(error) = provider.shutdown_with_timeout(Duration::from_secs(5))
        {
            eprintln!("OpenTelemetry logger shutdown failed: {error}");
        }
        if let Some(provider) = &self.tracer_provider
            && let Err(error) = provider.shutdown_with_timeout(Duration::from_secs(5))
        {
            eprintln!("OpenTelemetry tracer shutdown failed: {error}");
        }
    }
}

pub fn init() -> anyhow::Result<TelemetryGuard> {
    let fmt_filter = log_filter();
    let fmt_layer = tracing_subscriber::fmt::layer()
        .json()
        .with_filter(fmt_filter);

    if env::var_os("OTEL_EXPORTER_OTLP_ENDPOINT").is_none()
        && env::var_os("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT").is_none()
        && env::var_os(OTEL_EXPORTER_OTLP_LOGS_ENDPOINT).is_none()
    {
        Registry::default().with(fmt_layer).init();
        return Ok(TelemetryGuard {
            tracer_provider: None,
            logger_provider: None,
        });
    }

    let authorization_header = env::var(OTEL_EXPORTER_OTLP_AUTHORIZATION_HEADER).ok();
    let bearer_token = env::var(OTEL_EXPORTER_OTLP_BEARER_TOKEN).ok();
    let authorization =
        authorization_value(authorization_header.as_deref(), bearer_token.as_deref());
    let span_exporter = build_span_exporter(
        protocol_from_env(OTEL_EXPORTER_OTLP_TRACES_PROTOCOL)?,
        authorization.as_deref(),
    )?;
    let log_exporter = build_log_exporter(
        protocol_from_env(OTEL_EXPORTER_OTLP_LOGS_PROTOCOL)?,
        authorization.as_deref(),
    )?;
    let service_name =
        env::var("OTEL_SERVICE_NAME").unwrap_or_else(|_| DEFAULT_SERVICE_NAME.to_owned());
    let resource = Resource::builder()
        .with_service_name(service_name)
        .with_attribute(KeyValue::new("service.version", env!("CARGO_PKG_VERSION")))
        .build();
    let tracer_provider = SdkTracerProvider::builder()
        .with_resource(resource.clone())
        .with_batch_exporter(span_exporter)
        .build();
    let logger_provider = SdkLoggerProvider::builder()
        .with_resource(resource)
        .with_batch_exporter(log_exporter)
        .build();
    global::set_tracer_provider(tracer_provider.clone());
    let tracer = tracer_provider.tracer(DEFAULT_SERVICE_NAME);
    let otel_trace_layer = tracing_opentelemetry::layer()
        .with_tracer(tracer)
        .with_filter(filter_fn(|metadata| {
            metadata.target().starts_with("destiny2report_crawler")
        }));
    let otel_log_layer = OpenTelemetryTracingBridge::new(&logger_provider)
        .with_filter(log_filter())
        .with_filter(filter_fn(|metadata| {
            metadata.target().starts_with("destiny2report_crawler")
        }));

    Registry::default()
        .with(fmt_layer)
        .with(otel_trace_layer)
        .with(otel_log_layer)
        .try_init()?;

    Ok(TelemetryGuard {
        tracer_provider: Some(tracer_provider),
        logger_provider: Some(logger_provider),
    })
}

fn log_filter() -> EnvFilter {
    EnvFilter::builder()
        .with_default_directive(LevelFilter::INFO.into())
        .from_env_lossy()
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum OtlpProtocol {
    Grpc,
    HttpProtobuf,
}

fn protocol_from_env(signal_protocol_variable: &str) -> anyhow::Result<OtlpProtocol> {
    let protocol = env::var(signal_protocol_variable)
        .ok()
        .or_else(|| env::var(OTEL_EXPORTER_OTLP_PROTOCOL).ok())
        .unwrap_or_else(|| "grpc".to_owned());
    parse_protocol(&protocol)
}

fn parse_protocol(protocol: &str) -> anyhow::Result<OtlpProtocol> {
    match protocol.trim().to_ascii_lowercase().as_str() {
        "grpc" => Ok(OtlpProtocol::Grpc),
        "http" | "http/protobuf" => Ok(OtlpProtocol::HttpProtobuf),
        protocol => {
            anyhow::bail!("unsupported OTLP protocol '{protocol}'; expected grpc or http/protobuf")
        }
    }
}

fn build_span_exporter(
    protocol: OtlpProtocol,
    authorization: Option<&str>,
) -> anyhow::Result<opentelemetry_otlp::SpanExporter> {
    Ok(match protocol {
        OtlpProtocol::Grpc => opentelemetry_otlp::SpanExporter::builder()
            .with_tonic()
            .with_metadata(grpc_metadata(authorization)?)
            .build()?,
        OtlpProtocol::HttpProtobuf => opentelemetry_otlp::SpanExporter::builder()
            .with_http()
            .with_headers(http_headers(authorization))
            .build()?,
    })
}

fn build_log_exporter(
    protocol: OtlpProtocol,
    authorization: Option<&str>,
) -> anyhow::Result<opentelemetry_otlp::LogExporter> {
    Ok(match protocol {
        OtlpProtocol::Grpc => opentelemetry_otlp::LogExporter::builder()
            .with_tonic()
            .with_metadata(grpc_metadata(authorization)?)
            .build()?,
        OtlpProtocol::HttpProtobuf => opentelemetry_otlp::LogExporter::builder()
            .with_http()
            .with_headers(http_headers(authorization))
            .build()?,
    })
}

fn grpc_metadata(authorization: Option<&str>) -> anyhow::Result<MetadataMap> {
    let mut metadata = MetadataMap::new();
    if let Some(authorization) = authorization {
        metadata.insert(
            "authorization",
            authorization.parse().map_err(|error| {
                anyhow::anyhow!(
                    "the configured OTLP authorization header is not a valid gRPC metadata value: {error}"
                )
            })?,
        );
    }
    Ok(metadata)
}

fn http_headers(authorization: Option<&str>) -> HashMap<String, String> {
    authorization
        .map(|authorization| {
            HashMap::from([("authorization".to_owned(), authorization.to_owned())])
        })
        .unwrap_or_default()
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
    use super::{OtlpProtocol, authorization_value, http_headers, parse_protocol};

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

    #[test]
    fn supported_protocols_are_case_insensitive() {
        assert_eq!(parse_protocol("grpc").unwrap(), OtlpProtocol::Grpc);
        assert_eq!(
            parse_protocol(" HTTP/PROTOBUF ").unwrap(),
            OtlpProtocol::HttpProtobuf
        );
        assert_eq!(parse_protocol("http").unwrap(), OtlpProtocol::HttpProtobuf);
    }

    #[test]
    fn unsupported_protocol_is_rejected() {
        assert!(parse_protocol("http/json").is_err());
    }

    #[test]
    fn http_authorization_header_is_optional() {
        assert!(http_headers(None).is_empty());
        assert_eq!(
            http_headers(Some("Basic dXNlcjpwYXNzd29yZA=="))
                .get("authorization")
                .map(String::as_str),
            Some("Basic dXNlcjpwYXNzd29yZA==")
        );
    }
}
