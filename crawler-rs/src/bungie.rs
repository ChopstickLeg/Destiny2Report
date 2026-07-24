use std::{sync::Arc, time::Duration};

use reqwest::{StatusCode, Url, header::RETRY_AFTER};
use serde_json::Value;
use thiserror::Error;
use tokio::sync::Semaphore;
use tracing::{Instrument, Span};
use tracing_opentelemetry::OpenTelemetrySpanExt;

use crate::{config::Config, rate_limit::LocalRateLimiter};
use opentelemetry::trace::Status;

#[derive(Clone, Copy)]
pub enum EndpointClass {
    Ordinary,
    Pgcr,
    Sherpa,
}

#[derive(Clone)]
pub struct BungieClient {
    http: reqwest::Client,
    api_key: String,
    base_url: Url,
    pgcr_base_url: Url,
    ordinary: LocalRateLimiter,
    pgcr: LocalRateLimiter,
    sherpa: LocalRateLimiter,
    ordinary_concurrency: Arc<Semaphore>,
    pgcr_concurrency: Arc<Semaphore>,
    pgcr_parallelism: usize,
}

#[derive(Debug, Error)]
pub enum BungieError {
    #[error("Bungie account or resource was not found")]
    NotFound(Option<BungieFailure>),
    #[error("Bungie profile is private")]
    Private(Option<BungieFailure>),
    #[error("Bungie request failed: {0}")]
    Request(BungieFailure),
    #[error("Bungie resource was temporarily unavailable: {0}")]
    Unavailable(BungieFailure),
    #[error("crawler lease was lost")]
    Cancelled,
}

#[derive(Debug)]
pub struct BungieFailure {
    message: String,
    status_code: Option<u16>,
    response: Option<String>,
}

impl BungieError {
    pub(crate) fn failure(&self) -> Option<&BungieFailure> {
        match self {
            Self::NotFound(details) | Self::Private(details) => details.as_ref(),
            Self::Request(details) | Self::Unavailable(details) => Some(details),
            Self::Cancelled => None,
        }
    }
}

impl BungieFailure {
    fn with_response(message: String, status_code: u16, response: String) -> Self {
        Self {
            message,
            status_code: Some(status_code),
            response: (!response.is_empty()).then_some(response),
        }
    }

    fn span_message(&self) -> &str {
        self.response.as_deref().unwrap_or(&self.message)
    }

    pub(crate) fn message(&self) -> &str {
        &self.message
    }

    pub(crate) fn status_code(&self) -> Option<u16> {
        self.status_code
    }

    pub(crate) fn response(&self) -> Option<&str> {
        self.response.as_deref()
    }

    pub(crate) fn preferred_message(&self) -> &str {
        self.span_message()
    }
}

impl std::fmt::Display for BungieFailure {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl From<String> for BungieFailure {
    fn from(message: String) -> Self {
        Self {
            message,
            status_code: None,
            response: None,
        }
    }
}

impl From<&str> for BungieFailure {
    fn from(message: &str) -> Self {
        message.to_owned().into()
    }
}

impl BungieClient {
    pub fn new(config: &Config) -> anyhow::Result<Self> {
        Ok(Self {
            http: reqwest::Client::builder()
                .timeout(Duration::from_secs(120))
                .pool_max_idle_per_host(config.max_in_flight_pgcrs)
                .build()?,
            api_key: config.bungie_api_key.clone(),
            base_url: Url::parse(&config.bungie_base_url)?,
            pgcr_base_url: Url::parse(&config.pgcr_base_url)?,
            ordinary: LocalRateLimiter::per_second(
                config.ordinary_rps,
                config.ordinary_queue_limit,
            ),
            pgcr: LocalRateLimiter::per_second(config.pgcr_rps, config.pgcr_queue_limit),
            sherpa: LocalRateLimiter::per_second(config.sherpa_rps, config.sherpa_queue_limit),
            ordinary_concurrency: Arc::new(Semaphore::new(config.max_in_flight)),
            pgcr_concurrency: Arc::new(Semaphore::new(config.max_in_flight_pgcrs)),
            pgcr_parallelism: config
                .max_buffered_pgcrs
                .min(config.max_in_flight_pgcrs)
                .max(1),
        })
    }

    pub fn pgcr_parallelism(&self) -> usize {
        self.pgcr_parallelism
    }

    pub async fn profile(
        &self,
        membership_type: i32,
        membership_id: i64,
    ) -> Result<Value, BungieError> {
        self.get(
            &format!(
                "Destiny2/{membership_type}/Profile/{membership_id}/?components=100,200,900,1100"
            ),
            EndpointClass::Ordinary,
        )
        .await
    }

    pub async fn account_stats(
        &self,
        membership_type: i32,
        membership_id: i64,
    ) -> Result<Value, BungieError> {
        self.get(
            &format!("Destiny2/{membership_type}/Account/{membership_id}/Stats/?groups=1"),
            EndpointClass::Ordinary,
        )
        .await
    }

    pub async fn sherpa_account_stats(
        &self,
        membership_type: i32,
        membership_id: i64,
    ) -> Result<Value, BungieError> {
        self.get(
            &format!("Destiny2/{membership_type}/Account/{membership_id}/Stats/?groups=1"),
            EndpointClass::Sherpa,
        )
        .await
    }

    pub async fn profile_summary(
        &self,
        membership_type: i32,
        membership_id: i64,
    ) -> Result<Value, BungieError> {
        self.get(
            &format!("Destiny2/{membership_type}/Profile/{membership_id}/?components=100,200"),
            EndpointClass::Ordinary,
        )
        .await
    }

    pub async fn activity_history(
        &self,
        membership_type: i32,
        membership_id: i64,
        character_id: i64,
        page: u32,
    ) -> Result<Value, BungieError> {
        self.get(
            &format!("Destiny2/{membership_type}/Account/{membership_id}/Character/{character_id}/Stats/Activities/?count=250&page={page}"),
            EndpointClass::Ordinary,
        ).await
    }

    pub async fn raid_history(
        &self,
        membership_type: i32,
        membership_id: i64,
        character_id: i64,
        page: u32,
    ) -> Result<Value, BungieError> {
        self.get(
            &format!(
                "Destiny2/{membership_type}/Account/{membership_id}/Character/{character_id}/Stats/Activities/?count=250&mode=4&page={page}"
            ),
            EndpointClass::Sherpa,
        )
        .await
    }

    pub async fn historical_stats(
        &self,
        membership_type: i32,
        membership_id: i64,
        character_id: i64,
        mode: i32,
    ) -> Result<Value, BungieError> {
        self.get(
            &format!("Destiny2/{membership_type}/Account/{membership_id}/Character/{character_id}/Stats/?groups=1&modes={mode}"),
            EndpointClass::Ordinary,
        ).await
    }

    #[allow(dead_code)] // retained for fixture parity work without generated OpenAPI code
    pub async fn unique_weapons(
        &self,
        membership_type: i32,
        membership_id: i64,
        character_id: i64,
    ) -> Result<Value, BungieError> {
        self.get(
            &format!("Destiny2/{membership_type}/Account/{membership_id}/Character/{character_id}/Stats/UniqueWeapons/"),
            EndpointClass::Ordinary,
        ).await
    }

    pub async fn pgcr(&self, activity_id: i64) -> Result<Value, BungieError> {
        self.get(
            &format!("Destiny2/Stats/PostGameCarnageReport/{activity_id}/"),
            EndpointClass::Pgcr,
        )
        .await
    }

    pub async fn manifest(&self) -> Result<Value, BungieError> {
        self.get("Destiny2/Manifest/", EndpointClass::Ordinary)
            .await
    }

    pub async fn download_public_file(&self, path: &str) -> Result<Vec<u8>, BungieError> {
        let span = tracing::info_span!(
            "bungie.operation",
            otel.kind = "client",
            bungie.operation = %format!("DownloadManifest:{path}"),
        );
        let result = self
            .download_public_file_inner(path)
            .instrument(span.clone())
            .await;
        set_operation_status(&span, &result);
        result
    }

    async fn download_public_file_inner(&self, path: &str) -> Result<Vec<u8>, BungieError> {
        let url = Url::parse("https://www.bungie.net")
            .and_then(|base| base.join(path))
            .map_err(|error| BungieError::Request(error.to_string().into()))?;
        self.ordinary
            .acquire()
            .await
            .map_err(|_| BungieError::Request("ordinary local rate-limit queue is full".into()))?;
        let _permit = self
            .ordinary_concurrency
            .acquire()
            .await
            .map_err(|_| BungieError::Request("request limiter closed".into()))?;
        let response = self.send_get(url).await?;
        if !response.status().is_success() {
            return Err(BungieError::Request(
                http_failure("manifest download", response, 1).await,
            ));
        }
        Ok(response
            .bytes()
            .await
            .map_err(|error| BungieError::Request(error.to_string().into()))?
            .to_vec())
    }

    #[allow(dead_code)] // used by the sherpa-history pass as that parity fixture is enabled
    pub async fn linked_profiles(
        &self,
        membership_type: i32,
        membership_id: i64,
    ) -> Result<Value, BungieError> {
        self.get(
            &format!("Destiny2/{membership_type}/Profile/{membership_id}/LinkedProfiles/?getAllMemberships=true"),
            EndpointClass::Sherpa,
        ).await
    }

    async fn get(&self, path: &str, class: EndpointClass) -> Result<Value, BungieError> {
        let operation = operation_name(path);
        let span = tracing::info_span!(
            "bungie.operation",
            otel.kind = "client",
            bungie.operation = %operation,
        );
        let result = self.get_inner(path, class).instrument(span.clone()).await;
        set_operation_status(&span, &result);
        result
    }

    async fn get_inner(&self, path: &str, class: EndpointClass) -> Result<Value, BungieError> {
        let limiter = match class {
            EndpointClass::Ordinary => &self.ordinary,
            EndpointClass::Pgcr => &self.pgcr,
            EndpointClass::Sherpa => &self.sherpa,
        };
        let semaphore = match class {
            EndpointClass::Pgcr => &self.pgcr_concurrency,
            _ => &self.ordinary_concurrency,
        };
        let _permit = semaphore
            .acquire()
            .await
            .map_err(|_| BungieError::Request("request limiter closed".into()))?;
        let base = if matches!(class, EndpointClass::Pgcr) {
            &self.pgcr_base_url
        } else {
            &self.base_url
        };
        let url = base
            .join(path)
            .map_err(|error| BungieError::Request(error.to_string().into()))?;

        const MAX_ATTEMPTS: u32 = 6;
        for attempt in 0..MAX_ATTEMPTS {
            limiter
                .acquire()
                .await
                .map_err(|_| BungieError::Request("local rate-limit queue is full".into()))?;
            let response = self.send_get(url.clone()).await;
            let response = match response {
                Ok(value) => value,
                Err(error) if attempt + 1 < MAX_ATTEMPTS => {
                    tokio::time::sleep(backoff(attempt)).await;
                    tracing::warn!(attempt = attempt + 1, %path, error = %error, "retrying Bungie transport failure");
                    continue;
                }
                Err(error) => {
                    return Err(error);
                }
            };

            if response.status() == StatusCode::NOT_FOUND {
                return Err(BungieError::NotFound(Some(
                    http_failure(path, response, attempt + 1).await,
                )));
            }
            if response.status() == StatusCode::TOO_MANY_REQUESTS {
                let delay = response
                    .headers()
                    .get(RETRY_AFTER)
                    .and_then(|value| value.to_str().ok())
                    .and_then(|value| value.parse::<u64>().ok())
                    .map(Duration::from_secs)
                    .unwrap_or_else(|| backoff(attempt));
                limiter.pause(delay).await;
                if attempt + 1 < MAX_ATTEMPTS {
                    continue;
                }
            }
            if (response.status().is_server_error() || response.status().as_u16() == 524)
                && attempt + 1 < MAX_ATTEMPTS
            {
                let delay = backoff(attempt);
                if attempt == 0 || attempt + 2 == MAX_ATTEMPTS {
                    tracing::warn!(
                        attempt = attempt + 1,
                        %path,
                        status = %response.status(),
                        "retrying Bungie server failure"
                    );
                }
                limiter.pause(delay).await;
                continue;
            }
            if response.status().is_server_error() || response.status().as_u16() == 524 {
                return Err(BungieError::Unavailable(
                    http_failure(path, response, attempt + 1).await,
                ));
            }
            if !response.status().is_success() {
                return Err(BungieError::Request(
                    http_failure(path, response, attempt + 1).await,
                ));
            }

            let status_code = response.status().as_u16();
            let response_text = response
                .text()
                .await
                .map_err(|error| BungieError::Request(error.to_string().into()))?;
            let body: Value = serde_json::from_str(&response_text).map_err(|error| {
                BungieError::Request(BungieFailure::with_response(
                    format!("{path}: could not parse Bungie response: {error}"),
                    status_code,
                    response_text.clone(),
                ))
            })?;
            let error_code = body.get("ErrorCode").and_then(Value::as_i64).unwrap_or(1);
            if error_code != 1 {
                let error_status = body
                    .get("ErrorStatus")
                    .and_then(Value::as_str)
                    .unwrap_or("Unknown");
                let message = bungie_response_message(&body)
                    .unwrap_or(error_status)
                    .to_owned();
                let failure = BungieFailure::with_response(
                    format!("{path}: {message}"),
                    status_code,
                    response_text,
                );
                if error_status.contains("Privacy") || error_status.contains("Private") {
                    return Err(BungieError::Private(Some(failure)));
                }
                if error_status.contains("NotFound") {
                    return Err(BungieError::NotFound(Some(failure)));
                }
                let throttle = body
                    .get("ThrottleSeconds")
                    .and_then(Value::as_u64)
                    .unwrap_or(0);
                if throttle > 0 && attempt + 1 < MAX_ATTEMPTS {
                    limiter.pause(Duration::from_secs(throttle)).await;
                    continue;
                }
                return Err(BungieError::Request(failure));
            }
            return Ok(body.get("Response").cloned().unwrap_or(Value::Null));
        }
        Err(BungieError::Request("retry budget exhausted".into()))
    }

    async fn send_get(&self, url: Url) -> Result<reqwest::Response, BungieError> {
        let span = tracing::info_span!(
            "HTTP GET",
            otel.kind = "client",
            http.request.method = "GET",
            url.full = %url,
            server.address = url.host_str().unwrap_or_default(),
            server.port = url.port_or_known_default().unwrap_or_default() as i64,
        );
        let response = self
            .http
            .get(url)
            .header("X-API-Key", &self.api_key)
            .send()
            .instrument(span.clone())
            .await
            .map_err(|error| {
                span.set_attribute("error.type", "reqwest::Error");
                span.set_attribute("error.message", error.to_string());
                span.set_status(Status::error(error.to_string()));
                BungieError::Request(error.to_string().into())
            })?;
        let status = response.status();
        span.set_attribute("http.response.status_code", status.as_u16() as i64);
        if status.is_success() {
            span.set_status(Status::Ok);
        } else {
            span.set_status(Status::error(format!("HTTP {status}")));
        }
        Ok(response)
    }
}

fn set_operation_status<T>(span: &Span, result: &Result<T, BungieError>) {
    match result {
        Ok(_) => {
            span.set_status(Status::Ok);
        }
        Err(BungieError::NotFound(details) | BungieError::Private(details)) => {
            if let Some(details) = details {
                attach_failure(span, details);
            }
            span.set_status(Status::Ok);
        }
        Err(BungieError::Cancelled) => {}
        Err(BungieError::Request(details) | BungieError::Unavailable(details)) => {
            span.set_attribute("error.type", std::any::type_name::<BungieError>());
            attach_failure(span, details);
            span.set_status(Status::error(details.span_message().to_owned()));
        }
    }
}

fn attach_failure(span: &Span, failure: &BungieFailure) {
    if let Some(status_code) = failure.status_code {
        span.set_attribute("http.response.status_code", status_code as i64);
    }
    if let Some(response) = &failure.response {
        span.set_attribute("bungie.error.response", response.clone());
    }
    span.set_attribute("bungie.error.message", failure.message.clone());
    span.set_attribute("error.message", failure.span_message().to_owned());
}

async fn http_failure(path: &str, response: reqwest::Response, attempt: u32) -> BungieFailure {
    let status = response.status();
    let response_text = response.text().await.unwrap_or_default();
    let response_body = serde_json::from_str::<Value>(&response_text).ok();
    let bungie_message = response_body.as_ref().and_then(bungie_response_message);
    let message = bungie_message
        .map(|message| format!("{path}: HTTP {status}: {message}"))
        .unwrap_or_else(|| format!("{path}: HTTP {status} after {attempt} attempt(s)"));
    BungieFailure::with_response(message, status.as_u16(), response_text)
}

fn bungie_response_message(body: &Value) -> Option<&str> {
    body.get("Message")
        .and_then(Value::as_str)
        .filter(|message| !message.is_empty())
        .or_else(|| {
            body.get("ErrorStatus")
                .and_then(Value::as_str)
                .filter(|message| !message.is_empty())
        })
}

fn operation_name(path: &str) -> String {
    let path_without_query = path.split('?').next().unwrap_or(path).trim_matches('/');
    let parts = path_without_query.split('/').collect::<Vec<_>>();
    match parts.as_slice() {
        ["Destiny2", "Manifest"] => "GetDestinyManifest".to_owned(),
        [
            "Destiny2",
            membership_type,
            "Profile",
            membership_id,
            "LinkedProfiles",
        ] => {
            format!("GetLinkedProfiles:{membership_type}:{membership_id}")
        }
        ["Destiny2", membership_type, "Profile", membership_id] => {
            format!("GetProfile:{membership_type}:{membership_id}")
        }
        [
            "Destiny2",
            membership_type,
            "Account",
            membership_id,
            "Stats",
        ] => {
            format!("GetHistoricalStatsForAccount:{membership_type}:{membership_id}")
        }
        [
            "Destiny2",
            membership_type,
            "Account",
            membership_id,
            "Character",
            character_id,
            "Stats",
            "Activities",
        ] => format!("GetActivityHistory:{membership_type}:{membership_id}:{character_id}"),
        [
            "Destiny2",
            membership_type,
            "Account",
            membership_id,
            "Character",
            character_id,
            "Stats",
        ] => format!("GetHistoricalStats:{membership_type}:{membership_id}:{character_id}"),
        ["Destiny2", "Stats", "PostGameCarnageReport", activity_id] => {
            format!("GetPostGameCarnageReport:{activity_id}")
        }
        _ => path_without_query.replace('/', ":"),
    }
}

fn backoff(attempt: u32) -> Duration {
    let base = 250u64.saturating_mul(1u64 << attempt.min(5));
    Duration::from_millis(base + rand::random_range(0..=250))
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::{BungieFailure, bungie_response_message, operation_name};

    #[test]
    fn bungie_response_payload_takes_precedence_in_span_message() {
        let response = r#"{"ErrorCode":5,"ErrorStatus":"SystemDisabled","Message":"Maintenance"}"#;
        let failure = BungieFailure::with_response(
            "HTTP 503 Service Unavailable".to_owned(),
            503,
            response.to_owned(),
        );

        assert_eq!(failure.span_message(), response);
        assert_eq!(
            bungie_response_message(&json!({
                "ErrorStatus": "SystemDisabled",
                "Message": "Maintenance"
            })),
            Some("Maintenance")
        );
    }

    #[test]
    fn span_message_falls_back_when_bungie_has_no_response() {
        let failure = BungieFailure::from("request timed out");

        assert_eq!(failure.span_message(), "request timed out");
        assert_eq!(failure.status_code, None);
        assert_eq!(failure.response, None);
    }

    #[test]
    fn operation_names_match_the_previous_crawler_telemetry() {
        assert_eq!(
            operation_name("Destiny2/1/Profile/42/?components=100,200"),
            "GetProfile:1:42"
        );
        assert_eq!(
            operation_name("Destiny2/3/Account/42/Stats/?groups=1"),
            "GetHistoricalStatsForAccount:3:42"
        );
        assert_eq!(
            operation_name("Destiny2/Stats/PostGameCarnageReport/123/"),
            "GetPostGameCarnageReport:123"
        );
    }
}
