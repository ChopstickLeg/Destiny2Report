use std::time::Duration;

use anyhow::Context;
use mongodb::{
    Client,
    bson::{DateTime, Document, doc},
    options::ReturnDocument,
};
use rand::Rng;
use redis::{Value, aio::ConnectionManager, streams::StreamReadReply};
use tokio_util::sync::CancellationToken;

use crate::{
    bungie::BungieClient,
    config::Config,
    crawler,
    manifest::ManifestStore,
    models::{CrawlJob, CrawlMessage, PROTOCOL_VERSION, STATE_QUEUED, STATE_RUNNING, player_key},
    storage,
};

const STREAM: &str = "crawler:jobs";
const GROUP: &str = "crawler-workers";

pub struct Worker {
    config: Config,
    mongo: Client,
    redis: ConnectionManager,
    bungie: BungieClient,
}

impl Worker {
    pub fn new(config: Config, mongo: Client, redis: ConnectionManager) -> Self {
        let bungie = BungieClient::new(&config).expect("validated Bungie configuration");
        Self {
            config,
            mongo,
            redis,
            bungie,
        }
    }

    pub async fn run(mut self, cancellation: CancellationToken) -> anyhow::Result<()> {
        let jitter = if self.config.startup_jitter.is_zero() {
            0
        } else {
            rand::rng().random_range(0..=self.config.startup_jitter.as_millis() as u64)
        };
        tokio::time::sleep(Duration::from_millis(jitter)).await;
        ManifestStore::new(&self.config.manifest_path)
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

        while !cancellation.is_cancelled() {
            match self.read_message().await {
                Ok(Some(message)) => self.process_message(message, cancellation.clone()).await,
                Ok(None) => {
                    if let Ok(Some(job)) = self.claim_fallback().await {
                        self.execute(job, cancellation.clone()).await;
                    }
                }
                Err(error) => {
                    tracing::error!(%error, "Redis read failed; checking Mongo fallback");
                    if let Ok(Some(job)) = self.claim_fallback().await {
                        self.execute(job, cancellation.clone()).await;
                    }
                    tokio::time::sleep(Duration::from_secs(1)).await;
                }
            }
        }
        Ok(())
    }

    async fn read_message(&mut self) -> anyhow::Result<Option<CrawlMessage>> {
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
        let text = |name: &str| entry.map.get(name).and_then(redis_text);
        let message = CrawlMessage {
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
            stream_entry_id: entry.id,
        };
        Ok(Some(message))
    }

    async fn process_message(&mut self, message: CrawlMessage, cancellation: CancellationToken) {
        if message.protocol_version != PROTOCOL_VERSION {
            tracing::error!(
                received = message.protocol_version,
                supported = PROTOCOL_VERSION,
                run_id = %message.run_id,
                "rejecting unsupported crawler protocol version"
            );
            let _ = self.ack(&message.stream_entry_id).await;
            return;
        }
        match self.claim(&message).await {
            Ok(Some(job)) => self.execute(job, cancellation).await,
            Ok(None) => {
                if let Ok(Some(current)) = self
                    .find_job(message.membership_type_id, message.membership_id)
                    .await
                {
                    if current.run_id != message.run_id
                        || !matches!(current.state.as_str(), STATE_QUEUED | STATE_RUNNING)
                    {
                        let _ = self.ack(&message.stream_entry_id).await;
                    }
                }
            }
            Err(error) => tracing::error!(%error, run_id = %message.run_id, "claim failed"),
        }
    }

    async fn claim(&self, message: &CrawlMessage) -> anyhow::Result<Option<CrawlJob>> {
        let database = self.mongo.database(&self.config.mongo_database);
        let now = DateTime::now();
        let expiry = DateTime::from_millis(
            now.timestamp_millis() + self.config.lease_duration.as_millis() as i64,
        );
        Ok(database.collection::<CrawlJob>("crawl_jobs")
            .find_one_and_update(
                doc! { "_id": player_key(message.membership_type_id, message.membership_id), "r": &message.run_id,
                    "$or": [ { "s": STATE_QUEUED }, { "s": STATE_RUNNING, "le": { "$lt": now } } ] },
                claim_update(&self.config.consumer_name, expiry, now, Some(message)),
            ).return_document(ReturnDocument::After).await?)
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
                doc! { "$or": [
                    { "s": STATE_QUEUED, "d": false },
                    { "s": STATE_RUNNING, "le": { "$lt": now } }
                ] },
                claim_update(&self.config.consumer_name, expiry, now, None),
            )
            .sort(doc! { "qa": 1 })
            .return_document(ReturnDocument::After)
            .await?)
    }

    async fn execute(&mut self, job: CrawlJob, cancellation: CancellationToken) {
        let ownership = cancellation.child_token();
        let heartbeat = tokio::spawn(renew_loop(
            self.mongo.clone(),
            self.redis.clone(),
            self.config.clone(),
            job.clone(),
            ownership.clone(),
        ));
        let _ = self.publish_progress(&job, "running", "starting", 0).await;
        let result = crawler::crawl(&self.bungie, &job, &ownership).await;
        if ownership.is_cancelled() {
            tracing::warn!(run_id = %job.run_id, fence = job.fence, "crawl lost ownership before storage");
        } else {
            match result {
                Ok(
                    crawler::CrawlOutcome::Completed(result)
                    | crawler::CrawlOutcome::Private(result)
                    | crawler::CrawlOutcome::NotFound(result),
                ) => {
                    match storage::stage(
                        &self.mongo.database(&self.config.mongo_database),
                        &job,
                        &result,
                    )
                    .await
                    {
                        Ok(Some(generation)) => {
                            let _ = self
                                .publish_progress(&job, "running", "finalizing", 100)
                                .await;
                            if !job.stream_entry_id.is_empty() {
                                let _ = self.ack(&job.stream_entry_id).await;
                            }
                            tracing::info!(run_id = %job.run_id, fence = job.fence, %generation, "candidate generation committed");
                        }
                        Ok(None) => {
                            tracing::warn!(run_id = %job.run_id, fence = job.fence, "candidate rejected by fence")
                        }
                        Err(error) => {
                            tracing::error!(%error, run_id = %job.run_id, "candidate storage failed")
                        }
                    }
                }
                Err(error) => {
                    tracing::error!(%error, run_id = %job.run_id, "crawl failed");
                    if matches!(self.fail(&job, &error.to_string()).await, Ok(true)) {
                        let _ = self.publish_progress(&job, "failed", "failed", 100).await;
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
        let result = self.mongo.database(&self.config.mongo_database).collection::<CrawlJob>("crawl_jobs")
            .update_one(
                doc! { "_id": &job.player_key, "r": &job.run_id, "f": job.fence, "lo": &job.lease_owner, "s": STATE_RUNNING },
                doc! { "$set": { "s": "failed", "e": error, "lo": "", "le": null, "ua": DateTime::now(), "fa": DateTime::now() } },
            ).await?;
        Ok(result.modified_count == 1)
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

    async fn ack(&mut self, entry_id: &str) -> anyhow::Result<()> {
        let _: i64 = redis::cmd("XACK")
            .arg(STREAM)
            .arg(GROUP)
            .arg(entry_id)
            .query_async(&mut self.redis)
            .await?;
        let _: i64 = redis::cmd("XDEL")
            .arg(STREAM)
            .arg(entry_id)
            .query_async(&mut self.redis)
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
        percent: i32,
    ) -> anyhow::Result<bool> {
        const LUA: &str = r#"
local currentRun = redis.call('HGET', KEYS[1], 'runId')
local currentFence = tonumber(redis.call('HGET', KEYS[1], 'fence') or '-1')
if currentRun and currentRun ~= ARGV[1] then return 0 end
if currentFence > tonumber(ARGV[2]) then return 0 end
redis.call('HSET', KEYS[1], 'runId', ARGV[1], 'fence', ARGV[2],
  'status', ARGV[3], 'phase', ARGV[4], 'percent', ARGV[5], 'updatedAtUtc', ARGV[6])
redis.call('EXPIRE', KEYS[1], 86400)
return 1
"#;
        let key = format!(
            "crawler:job:{}:{}",
            job.membership_type_id, job.membership_id
        );
        let accepted: i32 = redis::Script::new(LUA)
            .key(key)
            .arg(&job.run_id)
            .arg(job.fence)
            .arg(state)
            .arg(phase)
            .arg(percent)
            .arg(chrono::Utc::now().to_rfc3339())
            .invoke_async(&mut self.redis)
            .await?;
        Ok(accepted == 1)
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
    use super::*;

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
}
