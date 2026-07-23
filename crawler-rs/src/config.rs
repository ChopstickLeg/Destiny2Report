use std::{env, time::Duration};

use anyhow::{Context, bail};

#[derive(Clone, Debug)]
pub struct Config {
    pub mongo_uri: String,
    pub mongo_database: String,
    pub redis_uri: String,
    pub bungie_api_key: String,
    pub bungie_base_url: String,
    pub pgcr_base_url: String,
    pub ordinary_rps: u32,
    pub pgcr_rps: u32,
    pub sherpa_rps: u32,
    pub ordinary_queue_limit: usize,
    pub pgcr_queue_limit: usize,
    pub sherpa_queue_limit: usize,
    pub max_in_flight: usize,
    pub max_in_flight_pgcrs: usize,
    pub max_buffered_pgcrs: usize,
    pub lease_duration: Duration,
    pub startup_jitter: Duration,
    pub manifest_path: String,
    pub consumer_name: String,
}

impl Config {
    pub fn from_env() -> anyhow::Result<Self> {
        let hostname = env::var("HOSTNAME").unwrap_or_else(|_| "crawler".into());
        let pid = std::process::id();
        let suffix = uuid::Uuid::new_v4().simple();
        let ordinary_rps = number("CRAWLER__ORDINARY_REQUESTS_PER_SECOND_PER_INSTANCE", 20)?;
        let pgcr_rps = number("CRAWLER__PGCR_REQUESTS_PER_SECOND_PER_INSTANCE", 45)?;
        let sherpa_rps = number(
            "CRAWLER__SHERPA_HISTORY_REQUESTS_PER_SECOND_PER_INSTANCE",
            8,
        )?;
        if ordinary_rps == 0 || pgcr_rps == 0 || sherpa_rps == 0 {
            bail!("per-instance request limits must be positive");
        }
        Ok(Self {
            mongo_uri: required("MONGODB_URI")?,
            mongo_database: env::var("MONGODB_DATABASE")
                .unwrap_or_else(|_| "Destiny2Report".into()),
            redis_uri: required("REDIS_URI")?,
            bungie_api_key: required("BUNGIE_API_KEY")?,
            bungie_base_url: env::var("BUNGIE_BASE_URL")
                .unwrap_or_else(|_| "https://www.bungie.net/Platform/".into()),
            pgcr_base_url: env::var("BUNGIE_PGCR_BASE_URL")
                .unwrap_or_else(|_| "https://stats.bungie.net/Platform/".into()),
            ordinary_rps,
            pgcr_rps,
            sherpa_rps,
            ordinary_queue_limit: number(
                "CRAWLER__ORDINARY_RATE_LIMIT_QUEUE_LIMIT_PER_INSTANCE",
                1_000,
            )? as usize,
            pgcr_queue_limit: number("CRAWLER__PGCR_RATE_LIMIT_QUEUE_LIMIT_PER_INSTANCE", 1_000)?
                as usize,
            sherpa_queue_limit: number(
                "CRAWLER__SHERPA_HISTORY_RATE_LIMIT_QUEUE_LIMIT_PER_INSTANCE",
                1_000,
            )? as usize,
            max_in_flight: number("CRAWLER__MAX_IN_FLIGHT_REQUESTS_PER_INSTANCE", 32)? as usize,
            max_in_flight_pgcrs: number("CRAWLER__MAX_IN_FLIGHT_PGCRS_PER_INSTANCE", 64)? as usize,
            max_buffered_pgcrs: number("CRAWLER__MAX_BUFFERED_PGCRS", 128)? as usize,
            lease_duration: Duration::from_secs(number("CRAWLER__LEASE_SECONDS", 300)? as u64),
            startup_jitter: Duration::from_secs(
                number("CRAWLER__STARTUP_JITTER_SECONDS", 10)? as u64
            ),
            manifest_path: env::var("CRAWLER__MANIFEST_PATH")
                .unwrap_or_else(|_| "/tmp/destiny-manifest.sqlite".into()),
            consumer_name: format!("{hostname}-{pid}-{suffix}"),
        })
    }
}

fn required(name: &str) -> anyhow::Result<String> {
    env::var(name).with_context(|| format!("{name} is required"))
}

fn number(name: &str, default: u32) -> anyhow::Result<u32> {
    match env::var(name) {
        Ok(value) => value
            .parse()
            .with_context(|| format!("{name} must be an unsigned integer")),
        Err(_) => Ok(default),
    }
}
