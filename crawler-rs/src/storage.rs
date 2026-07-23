use anyhow::bail;
use mongodb::{
    Database,
    bson::{DateTime, Document, doc},
};
use serde_json::Value;

use crate::{
    crawler::CrawlResult,
    models::{CrawlJob, STATE_AWAITING_FINALIZATION, STATE_RUNNING},
};

const WEAPON: i32 = 1;
const DEATH: i32 = 2;
const EMBLEM: i32 = 3;
const ENCOUNTER: i32 = 4;

pub async fn stage(
    database: &Database,
    job: &CrawlJob,
    result: &CrawlResult,
) -> anyhow::Result<Option<String>> {
    let generation = uuid::Uuid::new_v4().simple().to_string();
    let now = DateTime::now();
    database
        .collection::<Document>("crawl_state")
        .insert_one(doc! {
            "p": &job.player_key, "g": &generation,
            "d": mongodb::bson::to_document(&result.state)?, "ca": now
        })
        .await?;

    write_artifacts(database, job, &generation, WEAPON, &result.weapons, now).await?;
    write_artifacts(database, job, &generation, DEATH, &result.deaths, now).await?;
    write_artifacts(database, job, &generation, EMBLEM, &result.emblems, now).await?;
    write_artifacts(
        database,
        job,
        &generation,
        ENCOUNTER,
        &result.encounters,
        now,
    )
    .await?;

    database
        .collection::<Document>("reports")
        .insert_one(doc! {
            "p": &job.player_key, "g": &generation,
            "d": mongodb::bson::to_document(&result.report)?, "ca": now
        })
        .await?;

    let update = database.collection::<CrawlJob>("crawl_jobs").update_one(
        doc! { "_id": &job.player_key, "r": &job.run_id, "f": job.fence, "lo": &job.lease_owner, "s": STATE_RUNNING },
        doc! { "$set": { "cg": &generation, "s": STATE_AWAITING_FINALIZATION, "lo": "", "le": null, "ua": DateTime::now() } },
    ).await?;
    Ok((update.modified_count == 1).then_some(generation))
}

async fn write_artifacts(
    database: &Database,
    job: &CrawlJob,
    generation: &str,
    kind: i32,
    values: &[Value],
    created_at: DateTime,
) -> anyhow::Result<()> {
    if values.is_empty() {
        return Ok(());
    }
    let documents = values
        .iter()
        .map(|value| artifact_document(job, generation, kind, value, created_at))
        .collect::<anyhow::Result<Vec<_>>>()?;
    database
        .collection::<Document>("crawl_artifacts")
        .insert_many(documents)
        .await?;
    Ok(())
}

fn artifact_document(
    job: &CrawlJob,
    generation: &str,
    kind: i32,
    value: &Value,
    created_at: DateTime,
) -> anyhow::Result<Document> {
    let mut document = doc! { "p": &job.player_key, "g": generation, "k": kind, "ca": created_at };
    match kind {
        WEAPON => {
            insert_nonzero(
                &mut document,
                "m",
                i64::from(stored_mode(text(value, "activityMode"))),
            );
            insert_nonzero(&mut document, "s", integer(value, "specificActivityMode")?);
            insert_nonzero(
                &mut document,
                "c",
                i64::from(stored_class(text(value, "className"))),
            );
            insert_nonzero(&mut document, "h", integer(value, "weaponHash")?);
            document.insert("n", integer(value, "kills")?);
        }
        DEATH => {
            insert_nonzero(
                &mut document,
                "m",
                i64::from(stored_mode(text(value, "activityMode"))),
            );
            insert_nonzero(&mut document, "s", integer(value, "specificActivityMode")?);
            document.insert("n", integer(value, "deaths")?);
        }
        EMBLEM => {
            insert_nonzero(&mut document, "h", integer(value, "emblemHash")?);
            document.insert("n", integer(value, "totalSeconds")?);
        }
        ENCOUNTER => {
            insert_nonzero(
                &mut document,
                "t",
                integer(value, "encounteredMembershipType")?,
            );
            insert_nonzero(
                &mut document,
                "i",
                integer(value, "encounteredMembershipId")?,
            );
            document.insert("n", integer(value, "count")?);
        }
        _ => bail!("unsupported crawler artifact kind {kind}"),
    }
    Ok(document)
}

fn insert_nonzero(document: &mut Document, name: &str, value: i64) {
    if value != 0 {
        if let Ok(value) = i32::try_from(value) {
            document.insert(name, value);
        } else {
            document.insert(name, value);
        }
    }
}

fn text<'a>(value: &'a Value, name: &str) -> &'a str {
    value.get(name).and_then(Value::as_str).unwrap_or("")
}

fn integer(value: &Value, name: &str) -> anyhow::Result<i64> {
    value
        .get(name)
        .and_then(|item| {
            item.as_i64()
                .or_else(|| item.as_u64().and_then(|item| i64::try_from(item).ok()))
        })
        .ok_or_else(|| anyhow::anyhow!("artifact field {name} is missing or outside BSON int64"))
}

fn stored_mode(value: &str) -> i32 {
    match value {
        "PvE" => 1,
        "Crucible" => 2,
        "Gambit" => 3,
        _ => 0,
    }
}

fn stored_class(value: &str) -> i32 {
    match value {
        "Titan" => 1,
        "Hunter" => 2,
        "Warlock" => 3,
        _ => 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::player_key;

    fn job() -> CrawlJob {
        CrawlJob {
            player_key: player_key(3, 42),
            membership_type_id: 3,
            membership_id: 42,
            protocol_version: 1,
            run_id: "run".into(),
            state: "running".into(),
            dispatched: true,
            stream_entry_id: "1-0".into(),
            fence: 1,
            lease_owner: "worker".into(),
            lease_expires_at: None,
            queued_at: DateTime::now(),
            force_full_crawl: false,
        }
    }

    #[test]
    fn weapon_artifact_is_queryable_and_uses_numeric_dimensions() {
        let document = artifact_document(
            &job(),
            "generation",
            WEAPON,
            &serde_json::json!({
                "activityMode": "Crucible", "specificActivityMode": 70, "className": "Warlock",
                "weaponHash": u32::MAX, "kills": 99
            }),
            DateTime::now(),
        )
        .unwrap();
        assert_eq!(document.get_i32("m").unwrap(), 2);
        assert_eq!(document.get_i32("c").unwrap(), 3);
        assert_eq!(document.get_i64("h").unwrap(), i64::from(u32::MAX));
        assert_eq!(document.get_i64("n").unwrap(), 99);
    }

    #[test]
    fn report_fields_remain_bson_queryable() {
        let document = mongodb::bson::to_document(&serde_json::json!({"totalKills": 42})).unwrap();
        assert_eq!(document.get_i64("totalKills").unwrap(), 42);
    }
}
