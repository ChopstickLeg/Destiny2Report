use std::{sync::Arc, time::Duration};

use reqwest::{StatusCode, Url, header::RETRY_AFTER};
use serde_json::Value;
use thiserror::Error;
use tokio::sync::Semaphore;

use crate::{config::Config, rate_limit::LocalRateLimiter};

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
    NotFound,
    #[error("Bungie profile is private")]
    Private,
    #[error("Bungie request failed: {0}")]
    Request(String),
    #[error("crawler lease was lost")]
    Cancelled,
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
        let url = Url::parse("https://www.bungie.net")
            .and_then(|base| base.join(path))
            .map_err(|error| BungieError::Request(error.to_string()))?;
        self.ordinary
            .acquire()
            .await
            .map_err(|_| BungieError::Request("ordinary local rate-limit queue is full".into()))?;
        let _permit = self
            .ordinary_concurrency
            .acquire()
            .await
            .map_err(|_| BungieError::Request("request limiter closed".into()))?;
        let response = self
            .http
            .get(url)
            .header("X-API-Key", &self.api_key)
            .send()
            .await
            .map_err(|error| BungieError::Request(error.to_string()))?;
        if !response.status().is_success() {
            return Err(BungieError::Request(format!(
                "manifest download returned HTTP {}",
                response.status()
            )));
        }
        Ok(response
            .bytes()
            .await
            .map_err(|error| BungieError::Request(error.to_string()))?
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
            .map_err(|error| BungieError::Request(error.to_string()))?;

        for attempt in 0..4u32 {
            limiter
                .acquire()
                .await
                .map_err(|_| BungieError::Request("local rate-limit queue is full".into()))?;
            let response = self
                .http
                .get(url.clone())
                .header("X-API-Key", &self.api_key)
                .send()
                .await
                .map_err(|error| BungieError::Request(error.to_string()));
            let response = match response {
                Ok(value) => value,
                Err(error) if attempt < 3 => {
                    tokio::time::sleep(backoff(attempt)).await;
                    tracing::warn!(attempt, error = %error, "retrying Bungie transport failure");
                    continue;
                }
                Err(error) => return Err(error),
            };

            if response.status() == StatusCode::NOT_FOUND {
                return Err(BungieError::NotFound);
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
                if attempt < 3 {
                    continue;
                }
            }
            if (response.status().is_server_error() || response.status().as_u16() == 524)
                && attempt < 3
            {
                tokio::time::sleep(backoff(attempt)).await;
                continue;
            }
            if !response.status().is_success() {
                return Err(BungieError::Request(format!("HTTP {}", response.status())));
            }

            let body: Value = response
                .json()
                .await
                .map_err(|error| BungieError::Request(error.to_string()))?;
            let error_code = body.get("ErrorCode").and_then(Value::as_i64).unwrap_or(1);
            if error_code != 1 {
                let status = body
                    .get("ErrorStatus")
                    .and_then(Value::as_str)
                    .unwrap_or("Unknown");
                if status.contains("Privacy") || status.contains("Private") {
                    return Err(BungieError::Private);
                }
                if status.contains("NotFound") {
                    return Err(BungieError::NotFound);
                }
                let throttle = body
                    .get("ThrottleSeconds")
                    .and_then(Value::as_u64)
                    .unwrap_or(0);
                if throttle > 0 && attempt < 3 {
                    limiter.pause(Duration::from_secs(throttle)).await;
                    continue;
                }
                return Err(BungieError::Request(status.into()));
            }
            return Ok(body.get("Response").cloned().unwrap_or(Value::Null));
        }
        Err(BungieError::Request("retry budget exhausted".into()))
    }
}

fn backoff(attempt: u32) -> Duration {
    let base = 250u64.saturating_mul(1u64 << attempt.min(5));
    Duration::from_millis(base + rand::random_range(0..=250))
}
