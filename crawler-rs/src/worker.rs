use std::time::Duration;

use anyhow::Context;
use mongodb::{
    Client,
    bson::{DateTime, Document, doc},
    options::ReturnDocument,
};
use opentelemetry::trace::Status;
use rand::Rng;
use redis::{
    Value,
    aio::ConnectionManager,
    streams::{StreamAutoClaimReply, StreamId, StreamReadReply},
};
use tokio::time::Instant;
use tokio_util::sync::CancellationToken;
use tracing::{Instrument, Span};
use tracing_opentelemetry::OpenTelemetrySpanExt;

use crate::{
    bungie::{BungieClient, BungieError},
    config::Config,
    crawler,
    manifest::ManifestStore,
    models::{CrawlJob, CrawlMessage, PROTOCOL_VERSION, STATE_QUEUED, STATE_RUNNING, player_key},
    storage,
};

const STREAM: &str = "crawler:jobs";
const GROUP: &str = "crawler-workers";
const MANIFEST_REFRESH_INTERVAL: Duration = Duration::from_secs(24 * 60 * 60);
const MANIFEST_REFRESH_RETRY_INTERVAL: Duration = Duration::from_secs(60 * 60);
const REDIS_RECLAIM_GRACE: Duration = Duration::from_secs(5);
const PUBLISH_PROGRESS_SCRIPT: &str = r#"
local currentRun = redis.call('HGET', KEYS[1], 'runId')
local currentFence = tonumber(redis.call('HGET', KEYS[1], 'fence') or '-1')
if currentRun and currentRun ~= ARGV[1] then return 0 end
if currentFence > tonumber(ARGV[2]) then return 0 end
redis.call('HSET', KEYS[1], 'runId', ARGV[1], 'fence', ARGV[2],
  'status', ARGV[3],
  'progressPhase', ARGV[4],
  'progressLabel', ARGV[5],
  'progressCurrent', ARGV[6],
  'progressTotal', ARGV[7],
  'progressStartedAtUtc', ARGV[8],
  'progressUpdatedAtUtc', ARGV[8],
  'updatedAtUtc', ARGV[8],
  'error', ARGV[9],
  'streamEntryId', ARGV[10])
redis.call('EXPIRE', KEYS[1], 86400)
return 1
"#;
const ACK_AND_DELETE_SCRIPT: &str = r#"
local acknowledged = redis.call('XACK', KEYS[1], ARGV[1], ARGV[2])
local deleted = redis.call('XDEL', KEYS[1], ARGV[2])
return { acknowledged, deleted }
"#;

pub struct Worker {
    config: Config,
    mongo: Client,
    redis: ConnectionManager,
    bungie: BungieClient,
    manifest: ManifestStore,
    pending_cursor: String,
}

impl Worker {
    pub fn new(config: Config, mongo: Client, redis: ConnectionManager) -> Self {
        let bungie = BungieClient::new(&config).expect("validated Bungie configuration");
        let manifest = ManifestStore::new(&config.manifest_path);
        Self {
            config,
            mongo,
            redis,
            bungie,
            manifest,
            pending_cursor: "0-0".into(),
        }
    }

    pub async fn run(mut self, cancellation: CancellationToken) -> anyhow::Result<()> {
        let jitter = if self.config.startup_jitter.is_zero() {
            0
        } else {
            rand::rng().random_range(0..=self.config.startup_jitter.as_millis() as u64)
        };
        tokio::time::sleep(Duration::from_millis(jitter)).await;
        self.manifest
            .refresh(&self.bungie)
            .await
            .context("prepare private SQLite manifest")?;
        let _: Result<Value, _> = redis::cmd("XGROUP")
            .arg("CREATE")
            .arg(STREAM)
            .arg(GROUP)
            .arg("0-0")
            .arg("MKSTREAM")
            .query_async(&mut self.redis)
            .await;
        tracing::info!(consumer = %self.config.consumer_name, "Rust crawler worker started");
        let mut next_manifest_refresh = Instant::now() + MANIFEST_REFRESH_INTERVAL;

        while !cancellation.is_cancelled() {
            if Instant::now() >= next_manifest_refresh {
                next_manifest_refresh = match self.manifest.refresh(&self.bungie).await {
                    Ok(()) => Instant::now() + MANIFEST_REFRESH_INTERVAL,
                    Err(error) => {
                        tracing::error!(%error, "periodic Destiny manifest refresh failed");
                        Instant::now() + MANIFEST_REFRESH_RETRY_INTERVAL
                    }
                };
            }
            match self.read_message().await {
                Ok(Some(message)) => self.process_message(message, cancellation.clone()).await,
                Ok(None) => {
                    if let Ok(Some(job)) = self.claim_fallback().await {
                        self.execute_instrumented(job, false, cancellation.clone())
                            .await;
                    }
                }
                Err(error) => {
                    tracing::error!(%error, "Redis read failed; checking Mongo fallback");
                    if let Ok(Some(job)) = self.claim_fallback().await {
                        self.execute_instrumented(job, false, cancellation.clone())
                            .await;
                    }
                    tokio::time::sleep(Duration::from_secs(1)).await;
                }
            }
        }
        Ok(())
    }

    async fn read_message(&mut self) -> anyhow::Result<Option<CrawlMessage>> {
        let entry = match self.reclaim_stale_message().await? {
            Some(entry) => entry,
            None => {
                let reply: StreamReadReply = redis::cmd("XREADGROUP")
                    .arg("GROUP")
                    .arg(GROUP)
                    .arg(&self.config.consumer_name)
                    .arg("COUNT")
                    .arg(1)
                    .arg("BLOCK")
                    .arg(1_000)
                    .arg("STREAMS")
                    .arg(STREAM)
                    .arg(">")
                    .query_async(&mut self.redis)
                    .await?;
                let Some(entry) = reply
                    .keys
                    .into_iter()
                    .next()
                    .and_then(|key| key.ids.into_iter().next())
                else {
                    return Ok(None);
                };
                entry
            }
        };

        match parse_stream_message(&entry) {
            Ok(message) => Ok(Some(message)),
            Err(error) => {
                tracing::error!(
                    %error,
                    stream_entry_id = %entry.id,
                    "discarding malformed crawler stream entry"
                );
                self.release_malformed_dispatch(&entry.id)
                    .await
                    .context("release malformed crawler dispatch")?;
                self.ack(&entry.id)
                    .await
                    .context("discard malformed crawler stream entry")?;
                Ok(None)
            }
        }
    }

    async fn reclaim_stale_message(&mut self) -> anyhow::Result<Option<StreamId>> {
        let reply: StreamAutoClaimReply = redis::cmd("XAUTOCLAIM")
            .arg(STREAM)
            .arg(GROUP)
            .arg(&self.config.consumer_name)
            .arg(redis_reclaim_idle(self.config.lease_duration))
            .arg(&self.pending_cursor)
            .arg("COUNT")
            .arg(1)
            .query_async(&mut self.redis)
            .await?;
        self.pending_cursor = reply.next_stream_id;
        Ok(reply.claimed.into_iter().next())
    }
}

fn parse_stream_message(entry: &StreamId) -> anyhow::Result<CrawlMessage> {
    let text = |name: &str| entry.map.get(name).and_then(redis_text);
    Ok(CrawlMessage {
        protocol_version: text("protocolVersion")
            .context("stream protocolVersion")?
            .parse()?,
        run_id: text("runId").context("stream runId")?,
        membership_type_id: text("membershipTypeId")
            .context("stream membershipTypeId")?
            .parse()?,
        membership_id: text("membershipId")
            .context("stream membershipId")?
            .parse()?,
        stream_entry_id: entry.id.clone(),
    })
}

fn redis_reclaim_idle(lease_duration: Duration) -> u64 {
    lease_duration
        .saturating_add(REDIS_RECLAIM_GRACE)
        .as_millis()
        .min(u64::MAX as u128) as u64
}

impl Worker {
    async fn process_message(&mut self, message: CrawlMessage, cancellation: CancellationToken) {
        if message.protocol_version != PROTOCOL_VERSION {
            tracing::error!(
                received = message.protocol_version,
                supported = PROTOCOL_VERSION,
                run_id = %message.run_id,
                "rejecting unsupported crawler protocol version"
            );
            let _ = self
                .release_malformed_dispatch(&message.stream_entry_id)
                .await;
            let _ = self.ack(&message.stream_entry_id).await;
            return;
        }
        match self.claim(&message).await {
            Ok(Some(job)) => self.execute_instrumented(job, true, cancellation).await,
            Ok(None) => {
                if let Ok(current) = self
                    .find_job(message.membership_type_id, message.membership_id)
                    .await
                {
                    if should_ack_unclaimed_message(current.as_ref(), &message) {
                        let _ = self.ack(&message.stream_entry_id).await;
                    }
                }
            }
            Err(error) => tracing::error!(%error, run_id = %message.run_id, "claim failed"),
        }
    }

    async fn execute_instrumented(
        &mut self,
        job: CrawlJob,
        from_redis: bool,
        cancellation: CancellationToken,
    ) {
        let span = if from_redis {
            tracing::info_span!(
                "crawler.player.process",
                otel.kind = "consumer",
                destiny.membership_type_id = job.membership_type_id,
                destiny.membership_id = job.membership_id,
                messaging.system = "redis",
                messaging.destination.name = STREAM,
                messaging.message.id = %job.stream_entry_id,
            )
        } else {
            tracing::info_span!(
                "crawler.player.background_process",
                otel.kind = "consumer",
                destiny.membership_type_id = job.membership_type_id,
                destiny.membership_id = job.membership_id,
                messaging.system = "mongodb",
                messaging.destination.name = "destiny_reports",
            )
        };
        self.execute(job, cancellation).instrument(span).await;
    }

    async fn claim(&self, message: &CrawlMessage) -> anyhow::Result<Option<CrawlJob>> {
        let database = self.mongo.database(&self.config.mongo_database);
        let now = DateTime::now();
        let expiry = DateTime::from_millis(
            now.timestamp_millis() + self.config.lease_duration.as_millis() as i64,
        );
        Ok(database
            .collection::<CrawlJob>("crawl_jobs")
            .find_one_and_update(
                stream_claim_filter(message, now),
                claim_update(&self.config.consumer_name, expiry, now, Some(message)),
            )
            .return_document(ReturnDocument::After)
            .await?)
    }

    async fn claim_fallback(&self) -> anyhow::Result<Option<CrawlJob>> {
        let database = self.mongo.database(&self.config.mongo_database);
        let now = DateTime::now();
        let expiry = DateTime::from_millis(
            now.timestamp_millis() + self.config.lease_duration.as_millis() as i64,
        );
        Ok(database
            .collection::<CrawlJob>("crawl_jobs")
            .find_one_and_update(
                mongo_fallback_claim_filter(now),
                claim_update(&self.config.consumer_name, expiry, now, None),
            )
            .sort(doc! { "qa": 1 })
            .return_document(ReturnDocument::After)
            .await?)
    }

    async fn execute(&mut self, job: CrawlJob, cancellation: CancellationToken) {
        let process_span = Span::current();
        let ownership = cancellation.child_token();
        let heartbeat = tokio::spawn(renew_loop(
            self.mongo.clone(),
            self.redis.clone(),
            self.config.clone(),
            job.clone(),
            ownership.clone(),
        ));
        let _ = self
            .publish_progress(
                &job,
                "running",
                "profile",
                "Loading profile",
                None,
                (0, None),
            )
            .await;
        let bungie = self.bungie.clone();
        let manifest = self.manifest.clone();
        let (progress_tx, mut progress_rx) = tokio::sync::mpsc::unbounded_channel();
        let crawl_span = tracing::info_span!(
            "crawler.player.crawl",
            otel.kind = "internal",
            destiny.membership_type_id = job.membership_type_id,
            destiny.membership_id = job.membership_id,
        );
        let database = self.mongo.database(&self.config.mongo_database);
        let crawl = async {
            const MAX_CRAWL_ATTEMPTS: u32 = 2;
            for attempt in 1..=MAX_CRAWL_ATTEMPTS {
                let result = crawler::crawl(
                    &bungie,
                    &manifest,
                    &database,
                    &job,
                    &ownership,
                    &progress_tx,
                )
                .await;
                match result {
                    Err(error)
                        if attempt < MAX_CRAWL_ATTEMPTS
                            && !ownership.is_cancelled()
                            && !is_cancelled_crawl_error(&error) =>
                    {
                        tracing::warn!(
                            %error,
                            run_id = %job.run_id,
                            attempt,
                            "crawler attempt failed; retrying once immediately"
                        );
                    }
                    result => return result,
                }
            }
            unreachable!("crawl attempt loop always returns")
        }
        .instrument(crawl_span.clone());
        tokio::pin!(crawl);
        let result = loop {
            tokio::select! {
                result = &mut crawl => break result,
                update = progress_rx.recv() => {
                    if let Some(update) = update {
                        let _ = self
                            .publish_progress(
                                &job,
                                "running",
                                update.phase,
                                update.label,
                                None,
                                (update.current, update.total),
                            )
                            .await;
                    }
                }
            }
        };
        match &result {
            Ok(_) => crawl_span.set_status(Status::Ok),
            Err(error) => record_crawl_error(&crawl_span, error),
        }
        if ownership.is_cancelled() {
            tracing::warn!(run_id = %job.run_id, fence = job.fence, "crawl lost ownership before storage");
        } else {
            match result {
                Ok(
                    crawler::CrawlOutcome::Completed(result)
                    | crawler::CrawlOutcome::Private(result)
                    | crawler::CrawlOutcome::NotFound(result),
                ) => {
                    let mut stage_attempt = 1;
                    let stage_result = loop {
                        let staged = storage::stage(
                            &self.mongo.database(&self.config.mongo_database),
                            &job,
                            &result,
                        )
                        .await;
                        match staged {
                            Err(error) if stage_attempt < 2 && !ownership.is_cancelled() => {
                                tracing::warn!(
                                    %error,
                                    run_id = %job.run_id,
                                    attempt = stage_attempt,
                                    "candidate storage failed; retrying once immediately"
                                );
                                stage_attempt += 1;
                            }
                            result => break result,
                        }
                    };
                    match stage_result {
                        Ok(Some(generation)) => {
                            process_span.set_status(Status::Ok);
                            let _ = self
                                .publish_progress(
                                    &job,
                                    "running",
                                    "finalizing",
                                    "Finalizing report",
                                    None,
                                    (1, Some(1)),
                                )
                                .await;
                            if !job.stream_entry_id.is_empty() {
                                let _ = self.ack(&job.stream_entry_id).await;
                            }
                            tracing::info!(run_id = %job.run_id, fence = job.fence, %generation, "candidate generation committed");
                        }
                        Ok(None) => {
                            process_span.set_status(Status::Ok);
                            tracing::warn!(run_id = %job.run_id, fence = job.fence, "candidate rejected by fence")
                        }
                        Err(error) => {
                            record_crawl_error(&process_span, &error);
                            tracing::error!(%error, run_id = %job.run_id, "candidate storage failed")
                        }
                    }
                }
                Err(error) => {
                    record_crawl_error(&process_span, &error);
                    tracing::error!(%error, run_id = %job.run_id, "crawl failed");
                    let error = error.to_string();
                    if matches!(self.fail(&job, &error).await, Ok(true)) {
                        let _ = self
                            .publish_progress(
                                &job,
                                "failed",
                                "failed",
                                "Crawl failed",
                                Some(&error),
                                (1, Some(1)),
                            )
                            .await;
                        if !job.stream_entry_id.is_empty() {
                            let _ = self.ack(&job.stream_entry_id).await;
                        }
                    }
                }
            }
        }
        if cancellation.is_cancelled() {
            let _ = self.release_lease(&job).await;
        }
        ownership.cancel();
        let _ = heartbeat.await;
    }

    async fn fail(&self, job: &CrawlJob, error: &str) -> anyhow::Result<bool> {
        let database = self.mongo.database(&self.config.mongo_database);
        let mut session = self.mongo.start_session().await?;
        session.start_transaction().await?;
        let result = database
            .collection::<CrawlJob>("crawl_jobs")
            .update_one(
                doc! { "_id": &job.player_key, "r": &job.run_id, "f": job.fence, "lo": &job.lease_owner, "s": STATE_RUNNING },
                doc! { "$set": { "s": "failed", "e": error, "lo": "", "le": null, "ua": DateTime::now(), "fa": DateTime::now() } },
            )
            .session(&mut session)
            .await?;
        if result.modified_count != 1 {
            session.abort_transaction().await?;
            return Ok(false);
        }
        database
            .collection::<Document>("destiny_reports")
            .update_one(failed_report_filter(job), failed_report_update(error))
            .session(&mut session)
            .await?;
        session.commit_transaction().await?;
        Ok(true)
    }

    async fn find_job(
        &self,
        membership_type: i32,
        membership_id: i64,
    ) -> anyhow::Result<Option<CrawlJob>> {
        Ok(self
            .mongo
            .database(&self.config.mongo_database)
            .collection("crawl_jobs")
            .find_one(doc! { "_id": player_key(membership_type, membership_id) })
            .await?)
    }

    async fn release_malformed_dispatch(&self, entry_id: &str) -> anyhow::Result<()> {
        self.mongo
            .database(&self.config.mongo_database)
            .collection::<CrawlJob>("crawl_jobs")
            .update_one(
                malformed_dispatch_filter(entry_id),
                doc! { "$set": { "d": false, "se": "", "ua": DateTime::now() } },
            )
            .await?;
        Ok(())
    }

    async fn ack(&mut self, entry_id: &str) -> anyhow::Result<()> {
        let _: Value = redis::Script::new(ACK_AND_DELETE_SCRIPT)
            .key(STREAM)
            .arg(GROUP)
            .arg(entry_id)
            .invoke_async(&mut self.redis)
            .await?;
        Ok(())
    }

    async fn release_lease(&self, job: &CrawlJob) -> anyhow::Result<()> {
        self.mongo.database(&self.config.mongo_database).collection::<CrawlJob>("crawl_jobs")
            .update_one(
                doc! { "_id": &job.player_key, "r": &job.run_id, "f": job.fence, "lo": &job.lease_owner, "s": STATE_RUNNING },
                doc! { "$set": { "lo": "", "le": DateTime::now(), "ua": DateTime::now() } },
            ).await?;
        Ok(())
    }

    async fn publish_progress(
        &mut self,
        job: &CrawlJob,
        state: &str,
        phase: &str,
        label: &str,
        error: Option<&str>,
        progress: (i64, Option<i64>),
    ) -> anyhow::Result<bool> {
        let (current, total) = progress;
        let key = format!(
            "crawler:job:{}:{}",
            job.membership_type_id, job.membership_id
        );
        let now = chrono::Utc::now().to_rfc3339();
        let total_text = total.map(|value| value.to_string()).unwrap_or_default();
        let accepted: i32 = redis::Script::new(PUBLISH_PROGRESS_SCRIPT)
            .key(&key)
            .arg(&job.run_id)
            .arg(job.fence)
            .arg(state)
            .arg(phase)
            .arg(label)
            .arg(current)
            .arg(&total_text)
            .arg(&now)
            .arg(error.unwrap_or(""))
            .arg(&job.stream_entry_id)
            .invoke_async(&mut self.redis)
            .await?;
        if accepted == 1 {
            let event = serde_json::json!({
                "MembershipTypeId": job.membership_type_id,
                "MembershipId": job.membership_id,
                "Status": state,
                "StreamEntryId": if job.stream_entry_id.is_empty() { None } else { Some(job.stream_entry_id.as_str()) },
                "Error": error,
                "UpdatedAtUtc": now,
                "Progress": {
                    "Phase": phase,
                    "Label": label,
                    "Current": current,
                    "Total": total,
                    "StartedAtUtc": now,
                    "UpdatedAtUtc": now
                }
            });
            let _: i64 = redis::cmd("PUBLISH")
                .arg("crawler:job-events")
                .arg(event.to_string())
                .query_async(&mut self.redis)
                .await?;
        }
        Ok(accepted == 1)
    }
}

fn failed_report_filter(job: &CrawlJob) -> Document {
    doc! {
        "PlatformId": job.membership_type_id,
        "PlayerMembershipId": job.membership_id
    }
}

fn failed_report_update(error: &str) -> Document {
    doc! {
        "$set": {
            "CrawlState": "failed",
            "QueuedInRedis": false,
            "LeaseExpiresAtUtc": null,
            "LeaseOwner": "",
            "CrawlError": error
        }
    }
}

fn is_cancelled_crawl_error(error: &anyhow::Error) -> bool {
    matches!(
        error.downcast_ref::<BungieError>(),
        Some(BungieError::Cancelled)
    )
}

fn record_crawl_error(span: &Span, error: &anyhow::Error) {
    span.set_attribute("error.type", "anyhow::Error");
    if let Some(failure) = error
        .downcast_ref::<BungieError>()
        .and_then(BungieError::failure)
    {
        if let Some(status_code) = failure.status_code() {
            span.set_attribute("http.response.status_code", status_code as i64);
        }
        if let Some(response) = failure.response() {
            span.set_attribute("bungie.error.response", response.to_owned());
        }
        span.set_attribute("bungie.error.message", failure.message().to_owned());
        span.set_attribute("error.message", failure.preferred_message().to_owned());
        span.set_status(Status::error(failure.preferred_message().to_owned()));
    } else {
        span.set_attribute("error.message", error.to_string());
        span.set_status(Status::error(error.to_string()));
    }
}

fn claim_update(
    consumer_name: &str,
    expiry: DateTime,
    now: DateTime,
    message: Option<&CrawlMessage>,
) -> Document {
    let mut set = doc! {
        "s": STATE_RUNNING,
        "lo": consumer_name,
        "le": expiry,
        "sa": now,
        "ua": now,
        "e": ""
    };
    if let Some(message) = message {
        set.insert("d", true);
        set.insert("se", &message.stream_entry_id);
    }
    doc! { "$set": set, "$inc": { "f": 1_i64 } }
}

fn malformed_dispatch_filter(entry_id: &str) -> Document {
    doc! { "s": STATE_QUEUED, "d": true, "se": entry_id }
}

fn stream_claim_filter(message: &CrawlMessage, now: DateTime) -> Document {
    doc! {
        "_id": player_key(message.membership_type_id, message.membership_id),
        "v": PROTOCOL_VERSION,
        "r": &message.run_id,
        "d": true,
        "se": &message.stream_entry_id,
        "$or": [
            { "s": STATE_QUEUED },
            { "s": STATE_RUNNING, "le": { "$lt": now } }
        ]
    }
}

fn should_ack_unclaimed_message(current: Option<&CrawlJob>, message: &CrawlMessage) -> bool {
    let Some(current) = current else { return true };
    current.run_id != message.run_id
        || !matches!(current.state.as_str(), STATE_QUEUED | STATE_RUNNING)
        || (current.dispatched && current.stream_entry_id != message.stream_entry_id)
}

fn mongo_fallback_claim_filter(now: DateTime) -> Document {
    doc! {
        "v": PROTOCOL_VERSION,
        "$or": [
            { "s": STATE_QUEUED, "d": false },
            { "s": STATE_RUNNING, "le": { "$lt": now } }
        ]
    }
}

async fn renew_loop(
    mongo: Client,
    mut redis: ConnectionManager,
    config: Config,
    job: CrawlJob,
    cancellation: CancellationToken,
) {
    let interval = (config.lease_duration / 3).max(Duration::from_secs(1));
    let mut timer = tokio::time::interval(interval);
    timer.tick().await;
    while !cancellation.is_cancelled() {
        tokio::select! {
            _ = cancellation.cancelled() => return,
            _ = timer.tick() => {
                let expiry = DateTime::from_millis(DateTime::now().timestamp_millis() + config.lease_duration.as_millis() as i64);
                let result = mongo.database(&config.mongo_database).collection::<CrawlJob>("crawl_jobs").update_one(
                    doc! { "_id": &job.player_key, "r": &job.run_id, "f": job.fence, "lo": &job.lease_owner, "s": STATE_RUNNING },
                    doc! { "$set": { "le": expiry, "ua": DateTime::now() } },
                ).await;
                if !matches!(result, Ok(value) if value.modified_count == 1) { cancellation.cancel(); return; }
                if !job.stream_entry_id.is_empty() {
                    let refreshed: Result<Value, _> = redis::cmd("XCLAIM")
                        .arg(STREAM).arg(GROUP).arg(&config.consumer_name).arg(0)
                        .arg(&job.stream_entry_id).arg("JUSTID")
                        .query_async(&mut redis).await;
                    if refreshed.is_err() {
                        tracing::warn!(run_id = %job.run_id, "could not refresh Redis stream ownership; Mongo lease remains authoritative");
                    }
                }
            }
        }
    }
}

fn redis_text(value: &Value) -> Option<String> {
    match value {
        Value::BulkString(bytes) => String::from_utf8(bytes.clone()).ok(),
        Value::SimpleString(value) => Some(value.clone()),
        Value::Int(value) => Some(value.to_string()),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;

    use super::*;

    fn stream_entry(fields: &[(&str, &str)]) -> StreamId {
        StreamId {
            id: "123-0".into(),
            map: fields
                .iter()
                .map(|(name, value)| {
                    (
                        (*name).to_owned(),
                        Value::BulkString(value.as_bytes().to_vec()),
                    )
                })
                .collect::<HashMap<_, _>>(),
        }
    }

    #[test]
    fn parses_reclaimed_stream_entries() {
        let entry = stream_entry(&[
            ("protocolVersion", &PROTOCOL_VERSION.to_string()),
            ("runId", "run"),
            ("membershipTypeId", "3"),
            ("membershipId", "42"),
        ]);

        let message = parse_stream_message(&entry).unwrap();

        assert_eq!(message.protocol_version, PROTOCOL_VERSION);
        assert_eq!(message.run_id, "run");
        assert_eq!(message.membership_type_id, 3);
        assert_eq!(message.membership_id, 42);
        assert_eq!(message.stream_entry_id, "123-0");
    }

    #[test]
    fn rejects_malformed_stream_entries_without_losing_the_entry_id() {
        let entry = stream_entry(&[
            ("protocolVersion", &PROTOCOL_VERSION.to_string()),
            ("runId", "run"),
            ("membershipTypeId", "not-an-integer"),
            ("membershipId", "42"),
        ]);

        assert!(parse_stream_message(&entry).is_err());
        assert_eq!(entry.id, "123-0");
    }

    #[test]
    fn reclaim_waits_until_after_the_mongo_lease_can_expire() {
        assert_eq!(
            redis_reclaim_idle(Duration::from_secs(300)),
            Duration::from_secs(305).as_millis() as u64
        );
    }

    #[test]
    fn progress_status_persists_stream_entry_identity() {
        assert!(PUBLISH_PROGRESS_SCRIPT.contains("'streamEntryId', ARGV[10]"));
    }

    #[test]
    fn cancellation_errors_are_never_retried() {
        assert!(is_cancelled_crawl_error(&anyhow::Error::new(
            BungieError::Cancelled
        )));
        assert!(!is_cancelled_crawl_error(&anyhow::anyhow!(
            "transient failure"
        )));
    }

    #[test]
    fn terminal_failure_updates_the_materialized_report() {
        let job = CrawlJob {
            player_key: player_key(3, 42),
            membership_type_id: 3,
            membership_id: 42,
            display_name: String::new(),
            protocol_version: PROTOCOL_VERSION,
            run_id: "run".into(),
            state: STATE_RUNNING.into(),
            dispatched: true,
            stream_entry_id: "123-0".into(),
            fence: 1,
            lease_owner: "worker".into(),
            lease_expires_at: None,
            queued_at: DateTime::now(),
            started_at: None,
            force_full_crawl: false,
            active_generation: String::new(),
        };

        assert_eq!(
            failed_report_filter(&job),
            doc! {
                "PlatformId": 3,
                "PlayerMembershipId": 42_i64
            }
        );
        let update = failed_report_update("upstream failed");
        let set = update.get_document("$set").unwrap();
        assert_eq!(set.get_str("CrawlState").unwrap(), "failed");
        assert!(!set.get_bool("QueuedInRedis").unwrap());
        assert_eq!(set.get_str("CrawlError").unwrap(), "upstream failed");
    }

    #[test]
    fn malformed_dispatch_is_released_by_stream_entry_id() {
        let filter = malformed_dispatch_filter("123-0");

        assert_eq!(filter.get_str("s").unwrap(), STATE_QUEUED);
        assert!(filter.get_bool("d").unwrap());
        assert_eq!(filter.get_str("se").unwrap(), "123-0");
    }

    #[test]
    fn stream_claim_persists_dispatch_metadata() {
        let now = DateTime::now();
        let message = CrawlMessage {
            protocol_version: PROTOCOL_VERSION,
            run_id: "run".into(),
            membership_type_id: 3,
            membership_id: 42,
            stream_entry_id: "123-0".into(),
        };

        let update = claim_update("worker", now, now, Some(&message));
        let set = update.get_document("$set").unwrap();

        assert!(set.get_bool("d").unwrap());
        assert_eq!(set.get_str("se").unwrap(), "123-0");
    }

    #[test]
    fn mongo_fallback_does_not_invent_stream_metadata() {
        let now = DateTime::now();
        let update = claim_update("worker", now, now, None);
        let set = update.get_document("$set").unwrap();

        assert!(!set.contains_key("d"));
        assert!(!set.contains_key("se"));
    }

    #[test]
    fn stream_claim_requires_the_supported_protocol_version() {
        let message = CrawlMessage {
            protocol_version: PROTOCOL_VERSION,
            run_id: "run".into(),
            membership_type_id: 3,
            membership_id: 42,
            stream_entry_id: "123-0".into(),
        };

        let filter = stream_claim_filter(&message, DateTime::now());

        assert_eq!(filter.get_i32("v").unwrap(), PROTOCOL_VERSION);
        assert_eq!(filter.get_str("r").unwrap(), "run");
        assert!(filter.get_bool("d").unwrap());
        assert_eq!(filter.get_str("se").unwrap(), "123-0");
    }

    #[test]
    fn uncommitted_same_run_dispatch_is_left_pending_for_mongo_commit() {
        let job = queued_job(false, "");
        let message = crawl_message("123-0");

        assert!(!should_ack_unclaimed_message(Some(&job), &message));
    }

    #[test]
    fn committed_same_run_orphan_is_acknowledged() {
        let job = queued_job(true, "456-0");
        let message = crawl_message("123-0");

        assert!(should_ack_unclaimed_message(Some(&job), &message));
    }

    fn crawl_message(stream_entry_id: &str) -> CrawlMessage {
        CrawlMessage {
            protocol_version: PROTOCOL_VERSION,
            run_id: "run".into(),
            membership_type_id: 3,
            membership_id: 42,
            stream_entry_id: stream_entry_id.into(),
        }
    }

    fn queued_job(dispatched: bool, stream_entry_id: &str) -> CrawlJob {
        CrawlJob {
            player_key: player_key(3, 42),
            membership_type_id: 3,
            membership_id: 42,
            display_name: String::new(),
            protocol_version: PROTOCOL_VERSION,
            run_id: "run".into(),
            state: STATE_QUEUED.into(),
            dispatched,
            stream_entry_id: stream_entry_id.into(),
            fence: 0,
            lease_owner: String::new(),
            lease_expires_at: None,
            queued_at: DateTime::now(),
            started_at: None,
            force_full_crawl: false,
            active_generation: String::new(),
        }
    }

    #[test]
    fn mongo_fallback_requires_the_supported_protocol_version() {
        let filter = mongo_fallback_claim_filter(DateTime::now());

        assert_eq!(filter.get_i32("v").unwrap(), PROTOCOL_VERSION);
    }
}
