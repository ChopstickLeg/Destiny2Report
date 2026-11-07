use crate::bungie::BungieClient;
use anyhow::{Context, bail};
use futures::TryStreamExt;
use mongodb::{
    Database, IndexModel,
    bson::{DateTime, Document, doc},
    options::IndexOptions,
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, value::RawValue};
use std::{collections::BTreeMap, time::Duration};
use tracing::Instrument;

const TABLES: &[&str] = &[
    "DestinyActivityDefinition",
    "DestinyDestinationDefinition",
    "DestinyMetricDefinition",
    "DestinyActivityModeDefinition",
    "DestinyPresentationNodeDefinition",
    "DestinyRecordDefinition",
    "DestinyInventoryItemDefinition",
    "DestinyClassDefinition",
    "DestinyRaceDefinition",
];
const DAY_MS: i64 = 24 * 60 * 60 * 1000;

#[derive(Clone)]
pub struct ManifestStore {
    database: Database,
}

struct ActiveGeneration {
    id: String,
    expires_at: DateTime,
}

#[derive(Serialize, Deserialize)]
struct Definition {
    #[serde(rename = "_id")]
    id: String,
    generation: String,
    hash: i64,
    json: String,
    expires_at: DateTime,
}

impl ManifestStore {
    pub fn new(database: Database) -> Self {
        Self { database }
    }

    pub async fn refresh(&self, client: &BungieClient) -> anyhow::Result<()> {
        let entries = self.database.collection::<Definition>("manifest_entries");
        entries
            .create_index(
                IndexModel::builder()
                    .keys(doc! { "expires_at": 1 })
                    .options(IndexOptions::builder().expire_after(Duration::ZERO).build())
                    .build(),
            )
            .await?;
        entries
            .create_index(
                IndexModel::builder()
                    .keys(doc! { "generation": 1, "hash": 1 })
                    .build(),
            )
            .await?;
        let response = client.manifest().await?;
        for table in TABLES {
            let path = response
                .pointer(&format!("/jsonWorldComponentContentPaths/en/{table}"))
                .and_then(Value::as_str)
                .with_context(|| format!("Bungie manifest has no English {table} path"))?;
            self.refresh_table(client, table, path).await?;
        }
        Ok(())
    }

    async fn refresh_table(
        &self,
        client: &BungieClient,
        table: &str,
        path: &str,
    ) -> anyhow::Result<()> {
        let pointers = self.database.collection::<Document>("manifest_tables");
        let owner = uuid::Uuid::new_v4().simple().to_string();
        pointers
            .update_one(
                doc! { "_id": table },
                doc! { "$setOnInsert": {
                    "lease_until": DateTime::from_millis(0)
                }},
            )
            .upsert(true)
            .await?;
        let deadline = tokio::time::Instant::now() + Duration::from_secs(15 * 60);
        loop {
            let now = DateTime::now();
            let refresh_before = DateTime::from_millis(now.timestamp_millis() + DAY_MS / 2);
            if pointers
                .find_one(doc! { "_id": table, "expires_at": { "$gt": refresh_before } })
                .await?
                .is_some()
            {
                return Ok(());
            }
            let claimed = pointers.find_one_and_update(
                doc! { "_id": table, "lease_until": { "$lte": now } },
                doc! { "$set": { "owner": &owner, "lease_until": DateTime::from_millis(now.timestamp_millis() + 15 * 60 * 1000) } }
            ).await?;
            if claimed.is_some() {
                break;
            }
            if self.active_generation(table).await.is_ok() {
                return Ok(());
            }
            if tokio::time::Instant::now() >= deadline {
                bail!("timed out waiting for manifest table {table}");
            }
            tokio::time::sleep(Duration::from_secs(1)).await;
        }
        let result = self
            .upload_table(client, table, path, &owner)
            .instrument(tracing::info_span!("crawler.manifest.refresh", table))
            .await;
        let release = pointers
            .update_one(
                doc! { "_id": table, "owner": &owner },
                doc! { "$set": { "lease_until": DateTime::from_millis(0) } },
            )
            .await;
        result?;
        release?;
        Ok(())
    }

    async fn upload_table(
        &self,
        client: &BungieClient,
        table: &str,
        path: &str,
        owner: &str,
    ) -> anyhow::Result<()> {
        let _memory = crate::memory::Phase::start("manifest.refresh");
        let bytes = client.download_public_file(path).await?;
        crate::memory::checkpoint("manifest.downloaded");
        self.upload_definitions(table, owner, &bytes).await
    }

    async fn upload_definitions(
        &self,
        table: &str,
        owner: &str,
        bytes: &[u8],
    ) -> anyhow::Result<()> {
        // Keep the payload borrowed until each upload batch owns its JSON. Expanding
        // the complete inventory into Values costs roughly ten times its byte size.
        let definitions: BTreeMap<String, &RawValue> = serde_json::from_slice(bytes)
            .with_context(|| format!("parse {table} manifest table"))?;
        if definitions.is_empty() {
            bail!("empty manifest table {table}");
        }
        tracing::info!(
            table,
            input_bytes = bytes.len(),
            definition_count = definitions.len(),
            "prepared raw manifest definitions"
        );
        crate::memory::checkpoint("manifest.parsed");
        let generation = uuid::Uuid::new_v4().simple().to_string();
        let expires_at = DateTime::from_millis(DateTime::now().timestamp_millis() + DAY_MS);
        let entries = self.database.collection::<Definition>("manifest_entries");
        let mut batch = Vec::with_capacity(500);
        for (hash, value) in definitions {
            let hash = normalize_hash(&hash)?;
            batch.push(Definition {
                id: format!("{generation}:{hash}"),
                generation: generation.clone(),
                hash: i64::from(hash),
                json: value.get().to_owned(),
                expires_at,
            });
            if batch.len() == 500 {
                entries.insert_many(std::mem::take(&mut batch)).await?;
            }
        }
        if !batch.is_empty() {
            entries.insert_many(batch).await?;
        }
        crate::memory::checkpoint("manifest.uploaded");
        // Publish only complete tables; abandoned immutable generations expire by TTL.
        let result = self
            .database
            .collection::<Document>("manifest_tables")
            .update_one(
                doc! { "_id": table, "owner": owner },
                doc! { "$set": { "generation": &generation, "expires_at": expires_at } },
            )
            .await?;
        if result.matched_count != 1 {
            bail!("lost manifest refresh ownership for {table}");
        }
        tracing::info!(table, "published shared MongoDB manifest table");
        Ok(())
    }

    async fn active_generation(&self, table: &str) -> anyhow::Result<ActiveGeneration> {
        let pointer = self
            .database
            .collection::<Document>("manifest_tables")
            .find_one(doc! { "_id": table, "expires_at": { "$gt": DateTime::now() } })
            .await?
            .with_context(|| format!("manifest table {table} is missing or expired"))?;
        Ok(ActiveGeneration {
            id: pointer.get_str("generation")?.to_owned(),
            expires_at: *pointer.get_datetime("expires_at")?,
        })
    }

    pub async fn definition(&self, table: &str, hash: u32) -> anyhow::Result<Option<Value>> {
        let generation = self.active_generation(table).await?;
        let entry = self
            .database
            .collection::<Definition>("manifest_entries")
            .find_one(doc! {
                "_id": format!("{}:{hash}", generation.id), "expires_at": { "$gt": DateTime::now() }
            })
            .await?;
        if generation.expires_at <= DateTime::now() {
            bail!("manifest table {table} expired during lookup");
        }
        entry
            .map(|entry| serde_json::from_str(&entry.json).context("parse manifest definition"))
            .transpose()
    }

    pub async fn table(&self, table: &str) -> anyhow::Result<Vec<(u32, Value)>> {
        let generation = self.active_generation(table).await?;
        let mut cursor = self
            .database
            .collection::<Definition>("manifest_entries")
            .find(doc! {
                "generation": &generation.id, "expires_at": { "$gt": DateTime::now() }
            })
            .await?;
        let mut definitions = Vec::new();
        while let Some(entry) = cursor.try_next().await? {
            definitions.push((
                entry.hash as u32,
                serde_json::from_str(&entry.json).context("parse manifest table definition")?,
            ));
        }
        if definitions.is_empty() || generation.expires_at <= DateTime::now() {
            bail!("manifest table {table} is empty or expired");
        }
        Ok(definitions)
    }
}

fn normalize_hash(hash: &str) -> anyhow::Result<u32> {
    let hash: i64 = hash.parse().context("invalid manifest definition hash")?;
    if hash < i64::from(i32::MIN) || hash > i64::from(u32::MAX) {
        bail!("manifest hash outside 32-bit range");
    }
    Ok(hash as u32)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn raw_manifest_batches_publish_complete_generations_and_preserve_json() {
        let Ok(uri) = std::env::var("MONGODB_CACHE_TEST_URI") else {
            return;
        };
        let client = mongodb::Client::with_uri_str(uri).await.unwrap();
        let database = client.database(&format!(
            "d2r_raw_manifest_test_{}",
            uuid::Uuid::new_v4().simple()
        ));
        let pointers = database.collection::<Document>("manifest_tables");
        pointers
            .insert_one(doc! { "_id": "test", "owner": "owner", "generation": "previous" })
            .await
            .unwrap();
        let json = format!(
            "{{{}}}",
            (0..1_001)
                .map(|hash| format!(r#""{hash}": {{ "nested": [true, null, {{"text":"snowman \u2603"}}], "large": 18446744073709551615 }}"#))
                .collect::<Vec<_>>()
                .join(",")
        );
        let store = ManifestStore::new(database.clone());
        store
            .upload_definitions("test", "owner", json.as_bytes())
            .await
            .unwrap();
        let pointer = pointers
            .find_one(doc! { "_id": "test" })
            .await
            .unwrap()
            .unwrap();
        let generation = pointer.get_str("generation").unwrap().to_owned();
        let entries = database.collection::<Definition>("manifest_entries");
        assert_eq!(
            entries
                .count_documents(doc! { "generation": &generation })
                .await
                .unwrap(),
            1_001
        );
        let entry = entries
            .find_one(doc! { "_id": format!("{generation}:1000") })
            .await
            .unwrap()
            .unwrap();
        let source: BTreeMap<String, &RawValue> = serde_json::from_str(&json).unwrap();
        assert_eq!(entry.json, source["1000"].get());
        let resolved = store.definition("test", 1000).await.unwrap().unwrap();
        assert_eq!(resolved["nested"][2]["text"], "snowman \u{2603}");
        assert_eq!(resolved["large"].as_u64(), Some(u64::MAX));
        assert_eq!(
            entry.expires_at,
            *pointer.get_datetime("expires_at").unwrap()
        );
        // A failure after the first uploaded batch must leave the previous pointer intact.
        let invalid = format!(
            "{{{},\"invalid\": {{}}}}",
            (0..501)
                .map(|hash| format!("\"{hash}\":{{}}"))
                .collect::<Vec<_>>()
                .join(",")
        );
        assert!(
            store
                .upload_definitions("test", "owner", invalid.as_bytes())
                .await
                .is_err()
        );
        let pointer = pointers
            .find_one(doc! { "_id": "test" })
            .await
            .unwrap()
            .unwrap();
        assert_eq!(pointer.get_str("generation").unwrap(), generation);
        assert!(
            store
                .upload_definitions("test", "owner", b"{}")
                .await
                .is_err()
        );
        assert!(
            store
                .upload_definitions("test", "owner", b"{\"1\": {}")
                .await
                .is_err()
        );
        database.drop().await.unwrap();
    }
    #[test]
    fn signed_and_unsigned_bungie_hashes_resolve_to_the_same_definition() {
        assert_eq!(
            normalize_hash("-1").unwrap(),
            normalize_hash("4294967295").unwrap()
        );
        assert!(normalize_hash("4294967296").is_err());
    }
}
