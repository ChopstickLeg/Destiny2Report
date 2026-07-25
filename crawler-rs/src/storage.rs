use anyhow::bail;
use futures::TryStreamExt;
use mongodb::{
    Database,
    bson::{Bson, DateTime, Document, doc},
    options::UpdateOneModel,
};
use serde_json::Value;

use crate::{
    crawler::{CompletedRaid, CompletionAggregate, CrawlResult, CrawlSeed},
    models::{CrawlJob, STATE_AWAITING_FINALIZATION, STATE_RUNNING},
};

const WEAPON: i32 = 1;
const DEATH: i32 = 2;
const EMBLEM: i32 = 3;
const ENCOUNTER: i32 = 4;

pub async fn store_active_display_name(
    database: &Database,
    job: &CrawlJob,
    display_name: &str,
) -> anyhow::Result<()> {
    if display_name.trim().is_empty() {
        return Ok(());
    }

    database
        .collection::<CrawlJob>("crawl_jobs")
        .update_one(
            doc! {
                "_id": &job.player_key,
                "r": &job.run_id,
                "f": job.fence,
                "lo": &job.lease_owner,
                "s": STATE_RUNNING
            },
            doc! { "$set": { "dn": display_name } },
        )
        .await?;
    Ok(())
}

pub async fn load_incremental_seed(
    database: &Database,
    job: &CrawlJob,
) -> anyhow::Result<Option<CrawlSeed>> {
    if job.force_full_crawl || job.active_generation.is_empty() {
        return Ok(None);
    }

    let legacy_state = database
        .collection::<Document>("crawl_accumulators")
        .find_one(doc! {
            "PlatformId": job.membership_type_id,
            "PlayerMembershipId": job.membership_id
        })
        .await?;
    if legacy_state
        .as_ref()
        .is_some_and(|state| boolean(state, "NeedsFullRecrawl"))
    {
        return Ok(None);
    }

    let Some(state_row) = database
        .collection::<Document>("crawl_state")
        .find_one(doc! { "p": &job.player_key, "g": &job.active_generation })
        .await?
    else {
        return Ok(None);
    };
    let state = state_row.get_document("d")?;
    if !boolean(state, "firstActivityDiscoveryCompleted") {
        return Ok(None);
    }

    let mut seed = CrawlSeed {
        newest_period: string(state, "newestActivityPeriod"),
        earliest_period: string(state, "firstActivityAtUtc"),
        recent_activity_ids: array_i64(state, "recentActivityInstanceIds"),
        total_kills: bson_i64(state.get("totalKills")),
        patrol_seconds: string_i64_map(state, "patrolSecondsByPlanet"),
        raid_completions: completion_map(state, "raidCompletions"),
        dungeon_completions: completion_map(state, "dungeonCompletions"),
        conquest_completions: completion_map(state, "conquestCompletions"),
        mode_seconds: playtime_map(state),
        pvp_playlists: playlist_map(state),
        crucible_kills_by_mode: i32_i64_map(state, "crucibleKillsByMode"),
        gambit_mote_matches: bson_i32(state.get("gambitMoteMatches")),
        gambit_banked: i32_i32_map(state, "gambitMotesBankedByMode"),
        gambit_lost: i32_i32_map(state, "gambitMotesLostByMode"),
        gambit_denied: i32_i32_map(state, "gambitMotesDeniedByMode"),
        players_sherpaed: string_i32_map(state, "playersSherpaed"),
        play_dates: string_set(state, "playDates"),
        zero_kill_activities: bson_i32(state.get("zeroKillActivities")),
        total_activity_seconds: bson_i64(state.get("totalActivitySeconds")),
        deleted_character_identity: character_identity_map(state),
        ..CrawlSeed::default()
    };

    let mut artifacts = database
        .collection::<Document>("crawl_artifacts")
        .find(doc! { "g": &job.active_generation })
        .await?;
    while let Some(artifact) = artifacts.try_next().await? {
        let kind = bson_i32(artifact.get("k"));
        let value = bson_i64(artifact.get("n"));
        match kind {
            WEAPON => {
                let mode = loaded_mode(bson_i32(artifact.get("m")));
                let specific = bson_i32(artifact.get("s"));
                let class_name = loaded_class(bson_i32(artifact.get("c")));
                let hash = bson_i64(artifact.get("h"));
                seed.weapon_kills
                    .insert((mode, specific, 0, class_name, hash), value as i32);
            }
            DEATH => {
                seed.deaths_by_mode.insert(
                    (
                        loaded_mode(bson_i32(artifact.get("m"))),
                        bson_i32(artifact.get("s")),
                    ),
                    value,
                );
            }
            EMBLEM => {
                if let Ok(hash) = u32::try_from(bson_i64(artifact.get("h"))) {
                    seed.emblem_seconds.insert(hash, value);
                }
            }
            ENCOUNTER => {
                let key = (bson_i32(artifact.get("t")), bson_i64(artifact.get("i")));
                if key.0 > 0 && key.1 > 0 {
                    seed.encounter_counts.insert(key, value as i32);
                }
            }
            _ => {}
        }
    }
    Ok(Some(seed))
}

pub async fn cached_raid_history(
    database: &Database,
    membership_type: i32,
    membership_id: i64,
    required_raid_names: &std::collections::BTreeSet<String>,
) -> anyhow::Result<Option<Vec<CompletedRaid>>> {
    let Some(state) = database
        .collection::<Document>("crawl_accumulators")
        .find_one(doc! {
            "PlatformId": membership_type,
            "PlayerMembershipId": membership_id
        })
        .await?
    else {
        return Ok(None);
    };
    let Some(completions) = state
        .get_document("RaidCompletions")
        .ok()
        .or_else(|| state.get_document("raidCompletions").ok())
    else {
        return Ok(None);
    };
    if !required_raid_names.iter().all(|required| {
        completions
            .iter()
            .any(|(name, _)| name.eq_ignore_ascii_case(required))
    }) {
        return Ok(None);
    }
    let history = completions
        .iter()
        .filter_map(|(name, value)| {
            let completion = value.as_document()?;
            let first = completion
                .get_document("FirstCompletion")
                .ok()
                .or_else(|| completion.get_document("firstCompletion").ok())?;
            Some(CompletedRaid {
                name: name.clone(),
                period: bson_date_string(
                    first
                        .get("CompletedAt")
                        .or_else(|| first.get("completedAt")),
                )?,
                instance_id: bson_i64(first.get("InstanceId").or_else(|| first.get("instanceId"))),
            })
        })
        .collect::<Vec<_>>();
    Ok(Some(history))
}

pub async fn persist_inferred_raid_history(
    database: &Database,
    membership_type: i32,
    membership_id: i64,
    history: &[CompletedRaid],
) -> anyhow::Result<()> {
    if history.is_empty() {
        return Ok(());
    }
    let update = inferred_raid_history_update(membership_type, membership_id, history);
    database
        .collection::<Document>("crawl_accumulators")
        .update_one(
            doc! {
                "PlatformId": membership_type,
                "PlayerMembershipId": membership_id
            },
            update,
        )
        .upsert(true)
        .await?;
    Ok(())
}

fn inferred_raid_history_update(
    membership_type: i32,
    membership_id: i64,
    history: &[CompletedRaid],
) -> Document {
    let mut grouped = std::collections::BTreeMap::<String, Vec<&CompletedRaid>>::new();
    for completion in history.iter().filter(|item| !item.name.trim().is_empty()) {
        grouped
            .entry(completion.name.clone())
            .or_default()
            .push(completion);
    }
    let mut set = Document::new();
    for (name, mut completions) in grouped {
        completions.sort_by_key(|item| (&item.period, item.instance_id));
        let first = completions[0];
        set.insert(
            format!("RaidCompletions.{name}.CompletionCount"),
            completions.len() as i64,
        );
        let completed_at = chrono::DateTime::parse_from_rfc3339(&first.period)
            .map(|value| DateTime::from_millis(value.timestamp_millis()))
            .unwrap_or_else(|_| DateTime::now());
        set.insert(
            format!("RaidCompletions.{name}.FirstCompletion"),
            doc! { "CompletedAt": completed_at, "InstanceId": first.instance_id },
        );
    }
    doc! {
        "$setOnInsert": {
            "PlatformId": membership_type,
            "PlayerMembershipId": membership_id,
            "NeedsFullRecrawl": true,
            "FullRecrawlReason": "First raid completions inferred from sherpa history."
        },
        "$set": set
    }
}

pub async fn stage(
    database: &Database,
    job: &CrawlJob,
    result: &CrawlResult,
) -> anyhow::Result<Option<String>> {
    let generation = uuid::Uuid::new_v4().simple().to_string();
    let now = DateTime::now();
    // Insert the cleanup anchor first. Any later partial failure leaves a report
    // generation that the cleanup service can discover and remove.
    database
        .collection::<Document>("reports")
        .insert_one(doc! {
            "p": &job.player_key, "g": &generation,
            "d": mongodb::bson::to_document(&result.report)?, "ca": now
        })
        .await?;
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
    let persisted_encounters = persistable_encounters(&result.encounters);
    write_artifacts(
        database,
        job,
        &generation,
        ENCOUNTER,
        &persisted_encounters,
        now,
    )
    .await?;
    queue_discovered_players(database, job, &result.encounters, now).await?;

    let update = database.collection::<CrawlJob>("crawl_jobs").update_one(
        doc! { "_id": &job.player_key, "r": &job.run_id, "f": job.fence, "lo": &job.lease_owner, "s": STATE_RUNNING },
        doc! { "$set": { "cg": &generation, "s": STATE_AWAITING_FINALIZATION, "lo": "", "le": null, "ua": DateTime::now() } },
    ).await?;
    Ok((update.modified_count == 1).then_some(generation))
}

fn persistable_encounters(encounters: &[Value]) -> Vec<Value> {
    encounters
        .iter()
        .filter(|value| integer(value, "count").is_ok_and(|count| count >= 2))
        .cloned()
        .collect()
}

async fn queue_discovered_players(
    database: &Database,
    job: &CrawlJob,
    encounters: &[Value],
    queued_at: DateTime,
) -> anyhow::Result<()> {
    let reports = database.collection::<Document>("destiny_reports");
    let namespace = reports.namespace();
    for batch in encounters.chunks(500) {
        let models = batch
            .iter()
            .filter_map(|value| {
                let membership_type = integer(value, "encounteredMembershipType").ok()?;
                let membership_id = integer(value, "encounteredMembershipId").ok()?;
                if membership_type <= 0
                    || membership_id <= 0
                    || (membership_type == i64::from(job.membership_type_id)
                        && membership_id == job.membership_id)
                {
                    return None;
                }
                let (filter, update) =
                    discovered_player_upsert(membership_type, membership_id, queued_at);
                Some(
                    UpdateOneModel::builder()
                        .namespace(namespace.clone())
                        .filter(filter)
                        .update(update)
                        .upsert(true)
                        .build(),
                )
            })
            .collect::<Vec<_>>();
        if !models.is_empty() {
            database.client().bulk_write(models).ordered(false).await?;
        }
    }
    Ok(())
}

fn discovered_player_upsert(
    membership_type: i64,
    membership_id: i64,
    queued_at: DateTime,
) -> (Document, Document) {
    (
        doc! {
            "PlatformId": membership_type,
            "PlayerMembershipId": membership_id
        },
        doc! {
            "$setOnInsert": {
                "PlatformId": membership_type,
                "PlayerMembershipId": membership_id,
                "CrawlState": "queued",
                "QueuedInRedis": false,
                "QueuedAtUtc": queued_at,
                "CrawlError": ""
            }
        },
    )
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
    _job: &CrawlJob,
    generation: &str,
    kind: i32,
    value: &Value,
    _created_at: DateTime,
) -> anyhow::Result<Document> {
    // Generation ids are globally unique and immutable. Repeating the 9-byte player
    // key and creation timestamp on every artifact adds substantial BSON/index
    // overhead for large encounter sets without improving queryability.
    let mut document = doc! { "g": generation, "k": kind };
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

fn loaded_mode(value: i32) -> String {
    match value {
        1 => "PvE",
        2 => "Crucible",
        3 => "Gambit",
        _ => "",
    }
    .to_owned()
}

fn loaded_class(value: i32) -> String {
    match value {
        1 => "Titan",
        2 => "Hunter",
        3 => "Warlock",
        _ => "Unknown",
    }
    .to_owned()
}

fn bson_i64(value: Option<&Bson>) -> i64 {
    match value {
        Some(Bson::Int32(value)) => i64::from(*value),
        Some(Bson::Int64(value)) => *value,
        Some(Bson::Double(value)) => *value as i64,
        _ => 0,
    }
}

fn bson_i32(value: Option<&Bson>) -> i32 {
    i32::try_from(bson_i64(value)).unwrap_or_default()
}

fn boolean(document: &Document, name: &str) -> bool {
    document.get_bool(name).unwrap_or(false)
}

fn string(document: &Document, name: &str) -> Option<String> {
    match document.get(name) {
        Some(Bson::String(value)) if !value.is_empty() => Some(value.clone()),
        Some(Bson::DateTime(value)) => Some(bson_datetime_string(*value)),
        _ => None,
    }
}

fn bson_date_string(value: Option<&Bson>) -> Option<String> {
    match value {
        Some(Bson::String(value)) if !value.is_empty() => Some(value.clone()),
        Some(Bson::DateTime(value)) => Some(bson_datetime_string(*value)),
        _ => None,
    }
}

fn bson_datetime_string(value: DateTime) -> String {
    chrono::DateTime::from_timestamp_millis(value.timestamp_millis())
        .unwrap_or_default()
        .to_rfc3339()
}

fn array_i64(document: &Document, name: &str) -> Vec<i64> {
    document
        .get_array(name)
        .into_iter()
        .flatten()
        .map(|value| bson_i64(Some(value)))
        .filter(|value| *value > 0)
        .collect()
}

fn string_set(document: &Document, name: &str) -> std::collections::BTreeSet<String> {
    document
        .get_array(name)
        .into_iter()
        .flatten()
        .filter_map(|value| match value {
            Bson::String(value) => Some(value.clone()),
            Bson::DateTime(value) => Some(bson_datetime_string(*value)),
            _ => None,
        })
        .collect()
}

fn string_i64_map(document: &Document, name: &str) -> std::collections::BTreeMap<String, i64> {
    document
        .get_document(name)
        .into_iter()
        .flat_map(|values| values.iter())
        .map(|(key, value)| (key.clone(), bson_i64(Some(value))))
        .collect()
}

fn string_i32_map(document: &Document, name: &str) -> std::collections::BTreeMap<String, i32> {
    document
        .get_document(name)
        .into_iter()
        .flat_map(|values| values.iter())
        .map(|(key, value)| (key.clone(), bson_i32(Some(value))))
        .collect()
}

fn i32_i64_map(document: &Document, name: &str) -> std::collections::BTreeMap<i32, i64> {
    document
        .get_document(name)
        .into_iter()
        .flat_map(|values| values.iter())
        .filter_map(|(key, value)| Some((key.parse().ok()?, bson_i64(Some(value)))))
        .collect()
}

fn i32_i32_map(document: &Document, name: &str) -> std::collections::BTreeMap<i32, i32> {
    document
        .get_document(name)
        .into_iter()
        .flat_map(|values| values.iter())
        .filter_map(|(key, value)| Some((key.parse().ok()?, bson_i32(Some(value)))))
        .collect()
}

fn playtime_map(
    document: &Document,
) -> std::collections::BTreeMap<i32, std::collections::BTreeMap<i32, i64>> {
    document
        .get_document("playtimeByActivityMode")
        .into_iter()
        .flat_map(|values| values.iter())
        .filter_map(|(mode, value)| {
            let value = value.as_document()?;
            let specifics = value.get_document("mostSpecificModeSeconds").ok()?;
            Some((
                mode.parse().ok()?,
                specifics
                    .iter()
                    .filter_map(|(key, value)| Some((key.parse().ok()?, bson_i64(Some(value)))))
                    .collect(),
            ))
        })
        .collect()
}

fn playlist_map(document: &Document) -> std::collections::BTreeMap<i32, (i32, i32)> {
    document
        .get_document("pvpPlaylists")
        .into_iter()
        .flat_map(|values| values.iter())
        .filter_map(|(mode, value)| {
            let value = value.as_document()?;
            Some((
                mode.parse().ok()?,
                (bson_i32(value.get("wins")), bson_i32(value.get("losses"))),
            ))
        })
        .collect()
}

fn character_identity_map(
    document: &Document,
) -> std::collections::BTreeMap<i64, (String, String)> {
    document
        .get_document("deletedCharacterIdentity")
        .into_iter()
        .flat_map(|values| values.iter())
        .filter_map(|(character_id, value)| {
            let identity = value.as_document()?;
            let character_id = character_id.parse().ok()?;
            Some((
                character_id,
                (
                    identity.get_str("class").unwrap_or("Unknown").to_owned(),
                    identity.get_str("race").unwrap_or("Unknown").to_owned(),
                ),
            ))
        })
        .collect()
}

fn completion_map(
    document: &Document,
    name: &str,
) -> std::collections::BTreeMap<String, CompletionAggregate> {
    document
        .get_document(name)
        .into_iter()
        .flat_map(|values| values.iter())
        .filter_map(|(name, value)| {
            let value = value.as_document()?;
            Some((
                name.clone(),
                CompletionAggregate {
                    activity_count: bson_i32(value.get("activityCount")),
                    completion_count: bson_i32(value.get("completionCount")),
                    first_completion: completion_point(value, "firstCompletion"),
                    last_completion: completion_point(value, "lastCompletion"),
                    fastest_completion: value.get_document("fastestCompletion").ok().and_then(
                        |point| {
                            Some((
                                bson_timespan_seconds(point.get("duration")),
                                bson_date_string(point.get("completedAt"))?,
                                bson_i64(point.get("instanceId")),
                            ))
                        },
                    ),
                    contest_clear: boolean(value, "contestClear"),
                    flawless_clear: boolean(value, "flawlessClear"),
                    solo_clear: boolean(value, "soloClear"),
                    solo_flawless_clear: boolean(value, "soloFlawlessClear"),
                },
            ))
        })
        .collect()
}

fn completion_point(document: &Document, name: &str) -> Option<(String, i64)> {
    let point = document.get_document(name).ok()?;
    Some((
        bson_date_string(point.get("completedAt"))?,
        bson_i64(point.get("instanceId")),
    ))
}

fn bson_timespan_seconds(value: Option<&Bson>) -> i64 {
    let Some(Bson::String(value)) = value else {
        return 0;
    };
    let (days, clock) = value
        .split_once('.')
        .map(|(days, clock)| (days.parse::<i64>().unwrap_or(0), clock))
        .unwrap_or((0, value.as_str()));
    let mut parts = clock
        .split(':')
        .map(|part| part.parse::<i64>().unwrap_or(0));
    days * 86_400
        + parts.next().unwrap_or(0) * 3_600
        + parts.next().unwrap_or(0) * 60
        + parts.next().unwrap_or(0)
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
            display_name: String::new(),
            protocol_version: 1,
            run_id: "run".into(),
            state: "running".into(),
            dispatched: true,
            stream_entry_id: "1-0".into(),
            fence: 1,
            lease_owner: "worker".into(),
            lease_expires_at: None,
            queued_at: DateTime::now(),
            started_at: None,
            force_full_crawl: false,
            active_generation: String::new(),
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
        assert!(!document.contains_key("p"));
        assert!(!document.contains_key("ca"));
    }

    #[test]
    fn report_fields_remain_bson_queryable() {
        let document = mongodb::bson::to_document(&serde_json::json!({"totalKills": 42})).unwrap();
        assert_eq!(document.get_i64("totalKills").unwrap(), 42);
    }

    #[test]
    fn discovered_players_are_insert_only_background_jobs() {
        let queued_at = DateTime::from_millis(1_234);
        let (filter, update) = discovered_player_upsert(3, 42, queued_at);

        assert_eq!(
            filter,
            doc! { "PlatformId": 3_i64, "PlayerMembershipId": 42_i64 }
        );
        let inserted = update.get_document("$setOnInsert").unwrap();
        assert_eq!(inserted.get_str("CrawlState").unwrap(), "queued");
        assert!(!inserted.get_bool("QueuedInRedis").unwrap());
        assert_eq!(inserted.get_datetime("QueuedAtUtc").unwrap(), &queued_at);
        assert!(!update.contains_key("$set"));
    }

    #[test]
    fn one_off_encounters_are_not_persisted() {
        let encounters = vec![
            serde_json::json!({ "encounteredMembershipType": 3, "encounteredMembershipId": 10, "count": 1 }),
            serde_json::json!({ "encounteredMembershipType": 3, "encounteredMembershipId": 20, "count": 2 }),
        ];

        let persisted = persistable_encounters(&encounters);

        assert_eq!(persisted.len(), 1);
        assert_eq!(persisted[0]["encounteredMembershipId"], 20);
    }

    #[test]
    fn inferred_history_caches_first_completions_and_marks_new_players_for_full_crawl() {
        let history = vec![
            CompletedRaid {
                name: "King's Fall".into(),
                period: "2024-02-02T00:00:00Z".into(),
                instance_id: 20,
            },
            CompletedRaid {
                name: "King's Fall".into(),
                period: "2024-01-01T00:00:00Z".into(),
                instance_id: 10,
            },
        ];
        let update = inferred_raid_history_update(3, 42, &history);
        let inserted = update.get_document("$setOnInsert").unwrap();
        assert!(inserted.get_bool("NeedsFullRecrawl").unwrap());
        assert_eq!(
            inserted.get_str("FullRecrawlReason").unwrap(),
            "First raid completions inferred from sherpa history."
        );
        let set = update.get_document("$set").unwrap();
        assert_eq!(
            set.get_i64("RaidCompletions.King's Fall.CompletionCount")
                .unwrap(),
            2
        );
        let first = set
            .get_document("RaidCompletions.King's Fall.FirstCompletion")
            .unwrap();
        assert_eq!(first.get_i64("InstanceId").unwrap(), 10);
    }

    #[test]
    fn generation_state_parsers_restore_incremental_aggregates() {
        let state = doc! {
            "patrolSecondsByPlanet": { "Nessus": 90_i64 },
            "raidCompletions": {
                "Vault of Glass": {
                    "activityCount": 4,
                    "completionCount": 3,
                    "firstCompletion": {
                        "completedAt": "2023-01-01T00:00:00Z",
                        "instanceId": 11_i64
                    },
                    "fastestCompletion": {
                        "duration": "01:02:03",
                        "completedAt": "2023-02-01T00:00:00Z",
                        "instanceId": 12_i64
                    }
                }
            },
            "playtimeByActivityMode": {
                "4": {
                    "totalSeconds": 600_i64,
                    "mostSpecificModeSeconds": { "82": 600_i64 }
                }
            },
            "playersSherpaed": { "Vault of Glass": 2 }
            ,
            "deletedCharacterIdentity": {
                "123": { "class": "Hunter", "race": "Awoken" }
            }
        };
        assert_eq!(
            string_i64_map(&state, "patrolSecondsByPlanet")["Nessus"],
            90
        );
        let completions = completion_map(&state, "raidCompletions");
        let raid = &completions["Vault of Glass"];
        assert_eq!(raid.activity_count, 4);
        assert_eq!(raid.first_completion.as_ref().unwrap().1, 11);
        assert_eq!(raid.fastest_completion.as_ref().unwrap().0, 3_723);
        assert_eq!(playtime_map(&state)[&4][&82], 600);
        assert_eq!(
            string_i32_map(&state, "playersSherpaed")["Vault of Glass"],
            2
        );
        assert_eq!(
            character_identity_map(&state)[&123],
            ("Hunter".into(), "Awoken".into())
        );
    }
}
