#![recursion_limit = "256"]

mod bungie;
mod config;
mod crawler;
mod manifest;
mod models;
mod rate_limit;
mod storage;
mod telemetry;
mod worker;

use anyhow::Context;
use mongodb::Client;
use redis::aio::ConnectionManager;
use tokio_util::sync::CancellationToken;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let telemetry = telemetry::init().context("initialize OpenTelemetry")?;

    let config = config::Config::from_env()?;
    let mongo = Client::with_uri_str(&config.mongo_uri)
        .await
        .context("connect to MongoDB")?;
    let redis_client = redis::Client::open(config.redis_uri.as_str())?;
    let redis = ConnectionManager::new(redis_client)
        .await
        .context("connect to Redis")?;
    let cancellation = CancellationToken::new();
    let signal = cancellation.clone();
    tokio::spawn(async move {
        let _ = tokio::signal::ctrl_c().await;
        signal.cancel();
    });

    let result = worker::Worker::new(config, mongo, redis)
        .run(cancellation)
        .await;
    telemetry.shutdown();
    result
}
