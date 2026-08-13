use std::collections::{BTreeMap, BTreeSet};

use futures::{StreamExt, stream};
use serde_json::{Value, json};
use tokio::sync::mpsc::UnboundedSender;
use tokio_util::sync::CancellationToken;

use crate::{
    bungie::{BungieClient, BungieError},
    manifest::ManifestStore,
    models::CrawlJob,
    storage,
};

const INCREMENTAL_CRAWL_OVERLAP_HOURS: i64 = 8;
// Two full Bungie history pages comfortably cover the overlap without carrying a large ID blob.
const RECENT_ACTIVITY_INSTANCE_ID_LIMIT: usize = 500;
const BUNGIE_NEXT_MEMBERSHIP_TYPE: i32 = 254;

pub enum CrawlOutcome {
    Completed(CrawlResult),
    Private(CrawlResult),
    NotFound(CrawlResult),
}

pub struct CrawlResult {
    pub report: Value,
    pub state: Value,
    pub weapons: Vec<Value>,
    pub deaths: Vec<Value>,
    pub emblems: Vec<Value>,
    pub encounters: Vec<Value>,
}

#[derive(Clone, Debug)]
pub struct CrawlProgress {
    pub phase: &'static str,
    pub label: &'static str,
    pub current: i64,
    pub total: Option<i64>,
}

#[derive(Default)]
struct ModeTotals {
    kills: f64,
    deaths: f64,
    kd_values: Vec<f64>,
    kda_values: Vec<f64>,
    entered: i32,
    wins: i32,
}

#[derive(Default)]
pub(crate) struct CompletionAggregate {
    pub activity_count: i32,
    pub completion_count: i32,
    pub first_completion: Option<(String, i64)>,
    pub last_completion: Option<(String, i64)>,
    pub fastest_completion: Option<(i64, String, i64)>,
    pub contest_clear: bool,
    pub flawless_clear: bool,
    pub solo_clear: bool,
    pub solo_flawless_clear: bool,
}

#[derive(Clone)]
pub(crate) struct CompletedRaid {
    pub name: String,
    pub period: String,
    pub instance_id: i64,
}

#[derive(Clone)]
struct SherpaCheck {
    raid_name: String,
    period: String,
    instance_id: i64,
    candidates: Vec<(i32, i64)>,
}

#[derive(Default)]
pub(crate) struct CrawlSeed {
    pub newest_period: Option<String>,
    pub earliest_period: Option<String>,
    pub recent_activity_ids: Vec<i64>,
    pub total_kills: i64,
    pub patrol_seconds: BTreeMap<String, i64>,
    pub raid_completions: BTreeMap<String, CompletionAggregate>,
    pub dungeon_completions: BTreeMap<String, CompletionAggregate>,
    pub conquest_completions: BTreeMap<String, CompletionAggregate>,
    pub encounter_counts: BTreeMap<(i32, i64), i32>,
    pub encountered_players: BTreeSet<(i32, i64)>,
    pub weapon_kills: BTreeMap<(String, i32, i64, String, i64), i32>,
    pub deaths_by_mode: BTreeMap<(String, i32), i64>,
    pub emblem_seconds: BTreeMap<u32, i64>,
    pub mode_seconds: BTreeMap<i32, BTreeMap<i32, i64>>,
    pub pvp_playlists: BTreeMap<i32, (i32, i32)>,
    pub crucible_kills_by_mode: BTreeMap<i32, i64>,
    pub gambit_mote_matches: i32,
    pub gambit_banked: BTreeMap<i32, i32>,
    pub gambit_lost: BTreeMap<i32, i32>,
    pub gambit_denied: BTreeMap<i32, i32>,
    pub players_sherpaed: BTreeMap<String, i32>,
    pub play_dates: BTreeSet<String>,
    pub zero_kill_activities: i32,
    pub total_activity_seconds: i64,
    pub deleted_character_identity: BTreeMap<i64, (String, String)>,
}

pub async fn crawl(
    client: &BungieClient,
    manifest: &ManifestStore,
    database: &mongodb::Database,
    job: &CrawlJob,
    cancellation: &CancellationToken,
    progress: &UnboundedSender<CrawlProgress>,
) -> anyhow::Result<CrawlOutcome> {
    let profile = match cancellable(
        cancellation,
        client.profile(job.membership_type_id, job.membership_id),
    )
    .await
    {
        Ok(value) => value,
        Err(error) if is_private_error(&error) => {
            return Ok(CrawlOutcome::Private(empty_result(
                job,
                "private",
                "Destiny profile is not public.",
            )));
        }
        Err(BungieError::NotFound(_)) => {
            return Ok(CrawlOutcome::NotFound(empty_result(
                job,
                "failed",
                "Destiny account not found.",
            )));
        }
        Err(error) => return Err(error.into()),
    };
    let user = profile
        .pointer("/profile/data/userInfo")
        .unwrap_or(&Value::Null);
    let (existing_display_name, existing_display_code) =
        storage::load_existing_identity(database, job).await?;
    let display_name = user
        .get("bungieGlobalDisplayName")
        .and_then(Value::as_str)
        .or_else(|| user.get("displayName").and_then(Value::as_str))
        .filter(|value| !value.trim().is_empty())
        .unwrap_or(&existing_display_name);
    storage::store_active_display_name(database, job, display_name).await?;
    let display_code = user
        .get("bungieGlobalDisplayNameCode")
        .and_then(number_i64)
        .and_then(|value| i32::try_from(value).ok())
        .unwrap_or(existing_display_code);

    let account = match cancellable(
        cancellation,
        client.account_stats(job.membership_type_id, job.membership_id),
    )
    .await
    {
        Ok(value) => value,
        Err(error) if is_private_error(&error) => {
            return Ok(private_outcome(job));
        }
        Err(error) => return Err(error.into()),
    };
    report_progress(progress, "profile", "Profile loaded", 1, Some(1));

    let profile_characters = profile
        .pointer("/characters/data")
        .and_then(Value::as_object);
    let total_playtime_minutes = profile_characters
        .into_iter()
        .flat_map(|characters| characters.values())
        .map(|character| {
            character
                .get("minutesPlayedTotal")
                .and_then(number_i64)
                .unwrap_or(0)
        })
        .sum::<i64>();
    let mut character_playtime = profile_characters.into_iter().flat_map(|characters| characters.values()).map(|character| {
        json!({
            "class": class_name(character.get("classType").and_then(Value::as_i64).unwrap_or(-1) as i32),
            "race": race_name(character.get("raceType").and_then(Value::as_i64).unwrap_or(-1) as i32),
            "isDeleted": false,
            "playtime": timespan(character.get("minutesPlayedTotal").and_then(number_i64).unwrap_or(0) * 60)
        })
    }).collect::<Vec<_>>();

    let activity_definitions = manifest
        .table("DestinyActivityDefinition")?
        .into_iter()
        .collect::<BTreeMap<_, _>>();
    let destination_definitions = manifest
        .table("DestinyDestinationDefinition")?
        .into_iter()
        .collect::<BTreeMap<_, _>>();
    let metric_definitions = manifest
        .table("DestinyMetricDefinition")?
        .into_iter()
        .collect::<BTreeMap<_, _>>();
    let activity_mode_names = manifest
        .table("DestinyActivityModeDefinition")?
        .into_iter()
        .filter_map(|(_, definition)| {
            let mode = definition.get("modeType")?.as_i64()? as i32;
            let name = definition
                .pointer("/displayProperties/name")?
                .as_str()?
                .trim();
            (!name.is_empty()).then_some((mode, name.to_owned()))
        })
        .collect::<BTreeMap<_, _>>();
    let good_boy_protocol = metric_progress(&profile, &metric_definitions, "Good Boy Protocol");
    let fish_caught = metric_progress(&profile, &metric_definitions, "Total Fish Caught");
    let triumph_seals = completed_seals(&profile, manifest)?;
    let misadventures = sum_account_stat(&account, "suicides") as i32;

    let character_ids = account
        .get("characters")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|character| character.get("characterId").and_then(id_i64))
        .collect::<Vec<_>>();
    let incremental_seed = storage::load_incremental_seed(database, job).await?;
    let mut seed = incremental_seed.unwrap_or_default();
    let mut character_class_by_id = historical_character_classes(&account);
    let mut deleted_character_identity = std::mem::take(&mut seed.deleted_character_identity);
    let crawl_after = seed
        .newest_period
        .as_deref()
        .and_then(|value| chrono::DateTime::parse_from_rfc3339(value).ok())
        .map(|value| value - chrono::Duration::hours(INCREMENTAL_CRAWL_OVERLAP_HOURS));
    let recent_activity_ids = seed
        .recent_activity_ids
        .iter()
        .copied()
        .collect::<BTreeSet<_>>();
    let mut mode_totals = std::collections::BTreeMap::<i32, ModeTotals>::new();
    for character_id in &character_ids {
        for mode in [5, 63, 75] {
            let stats = match cancellable(
                cancellation,
                client.historical_stats(
                    job.membership_type_id,
                    job.membership_id,
                    *character_id,
                    mode,
                ),
            )
            .await
            {
                Ok(value) => value,
                Err(error) if is_private_error(&error) => {
                    return Ok(private_outcome(job));
                }
                Err(error) => return Err(error.into()),
            };
            let totals = mode_totals.entry(mode).or_default();
            totals.kills += historical_stat(&stats, mode, "kills");
            totals.deaths += historical_stat(&stats, mode, "deaths");
            let kd = historical_stat(&stats, mode, "killsDeathsRatio");
            if kd > 0.0 {
                totals.kd_values.push(kd);
            }
            let kda = historical_stat(&stats, mode, "killsDeathsAssists");
            if kda > 0.0 {
                totals.kda_values.push(kda);
            }
            totals.entered += historical_stat(&stats, mode, "activitiesEntered") as i32;
            totals.wins += historical_stat(&stats, mode, "activitiesWon") as i32;
        }
    }
    let mut activity_ids = BTreeSet::new();
    let mut fetched_recent_activities = Vec::<(String, i64)>::new();
    let mut newest_period = seed.newest_period.take();
    let mut earliest_period = seed.earliest_period.take();
    let mut patrol_seconds = std::mem::take(&mut seed.patrol_seconds);
    for (character_index, character_id) in character_ids.iter().enumerate() {
        for page in 0..10_000u32 {
            let response = match cancellable(
                cancellation,
                client.activity_history(
                    job.membership_type_id,
                    job.membership_id,
                    *character_id,
                    page,
                ),
            )
            .await
            {
                Ok(value) => value,
                Err(error) if is_private_error(&error) => {
                    return Ok(private_outcome(job));
                }
                Err(error) => return Err(error.into()),
            };
            let activities = response
                .get("activities")
                .and_then(Value::as_array)
                .cloned()
                .unwrap_or_default();
            if page == 0
                && !profile_characters
                    .is_some_and(|characters| characters.contains_key(&character_id.to_string()))
                && deleted_character_identity
                    .get(character_id)
                    .is_none_or(|(_, race)| race == "Unknown")
                && let Some(instance_id) = activities.iter().find_map(|activity| {
                    activity
                        .pointer("/activityDetails/instanceId")
                        .and_then(id_i64)
                        .filter(|instance_id| *instance_id > 0)
                })
            {
                match cancellable(cancellation, client.pgcr(instance_id)).await {
                    Ok(pgcr) => {
                        recover_deleted_character_identity(
                            &pgcr,
                            manifest,
                            job,
                            *character_id,
                            &mut character_class_by_id,
                            &mut deleted_character_identity,
                        );
                    }
                    Err(BungieError::Cancelled) => return Err(BungieError::Cancelled.into()),
                    Err(error) => {
                        tracing::debug!(
                            %error,
                            character_id,
                            "could not recover deleted character identity"
                        );
                    }
                }
            }
            let mut reached_crawl_boundary = false;
            for activity in &activities {
                let Some(instance_id) = activity
                    .pointer("/activityDetails/instanceId")
                    .and_then(id_i64)
                else {
                    continue;
                };
                let period = activity.get("period").and_then(Value::as_str).unwrap_or("");
                if crawl_after.is_some_and(|boundary| {
                    chrono::DateTime::parse_from_rfc3339(period)
                        .is_ok_and(|value| value <= boundary)
                }) {
                    reached_crawl_boundary = true;
                    continue;
                }
                fetched_recent_activities.push((period.to_owned(), instance_id));
                if !period.is_empty() {
                    if newest_period.as_deref().is_none_or(|value| period > value) {
                        newest_period = Some(period.into());
                    }
                    if earliest_period
                        .as_deref()
                        .is_none_or(|value| period < value)
                    {
                        earliest_period = Some(period.into());
                    }
                }
                if recent_activity_ids.contains(&instance_id) {
                    continue;
                }
                let newly_discovered = activity_ids.insert(instance_id);
                if newly_discovered
                    && includes_mode(
                        activity
                            .pointer("/activityDetails/mode")
                            .and_then(Value::as_i64)
                            .unwrap_or(0) as i32,
                        activity
                            .pointer("/activityDetails/modes")
                            .and_then(Value::as_array),
                        6,
                    )
                {
                    if let Some(destination) = activity_destination(
                        activity,
                        &activity_definitions,
                        &destination_definitions,
                    ) {
                        let seconds = preferred_playtime(activity);
                        if seconds > 0 {
                            *patrol_seconds.entry(destination).or_default() += seconds;
                        }
                    }
                }
            }
            report_progress(
                progress,
                "history",
                "Discovering activity history",
                activity_ids.len() as i64,
                None,
            );
            if activities.len() < 250 || reached_crawl_boundary {
                break;
            }
        }
        report_progress(
            progress,
            "history",
            "Discovering activity history",
            (character_index + 1) as i64,
            Some(character_ids.len() as i64),
        );
    }

    let parallelism = client.pgcr_parallelism();
    let discovered_pgcrs = activity_ids.len() as i64;
    report_progress(
        progress,
        "activities",
        "Analyzing activities",
        0,
        Some(discovered_pgcrs),
    );
    let mut pending = stream::iter(activity_ids.iter().copied())
        .map(|activity_id| {
            let client = client.clone();
            async move { (activity_id, client.pgcr(activity_id).await) }
        })
        .buffer_unordered(parallelism);

    let mut total_kills = seed.total_kills;
    let mut total_activity_seconds = seed.total_activity_seconds;
    let mut zero_kill_activities = seed.zero_kill_activities;
    let mut play_dates = std::mem::take(&mut seed.play_dates);
    let mut encounter_counts = std::mem::take(&mut seed.encounter_counts);
    let mut encountered = std::mem::take(&mut seed.encountered_players);
    encountered.extend(encounter_counts.keys().copied());
    let mut weapon_kills = std::mem::take(&mut seed.weapon_kills);
    let mut deaths_by_mode = std::mem::take(&mut seed.deaths_by_mode);
    let mut emblem_seconds = std::mem::take(&mut seed.emblem_seconds);
    let mut mode_seconds = std::mem::take(&mut seed.mode_seconds);
    let mut pvp_playlists = std::mem::take(&mut seed.pvp_playlists);
    let mut crucible_kills_by_mode = std::mem::take(&mut seed.crucible_kills_by_mode);
    let mut gambit_mote_matches = seed.gambit_mote_matches;
    let mut gambit_banked = std::mem::take(&mut seed.gambit_banked);
    let mut gambit_lost = std::mem::take(&mut seed.gambit_lost);
    let mut gambit_denied = std::mem::take(&mut seed.gambit_denied);
    let mut raid_completions = normalize_completion_map(std::mem::take(&mut seed.raid_completions));
    let mut dungeon_completions =
        normalize_completion_map(std::mem::take(&mut seed.dungeon_completions));
    let mut conquest_completions =
        normalize_completion_map(std::mem::take(&mut seed.conquest_completions));
    let mut players_sherpaed =
        normalize_case_insensitive_counts(std::mem::take(&mut seed.players_sherpaed));
    let mut completed_raids = Vec::<CompletedRaid>::new();
    let mut sherpa_checks = Vec::<SherpaCheck>::new();
    let mut processed_pgcrs = 0i64;
    loop {
        let next = tokio::select! {
            _ = cancellation.cancelled() => return Err(BungieError::Cancelled.into()),
            value = pending.next() => value,
        };
        let Some((_activity_id, pgcr)) = next else {
            break;
        };
        processed_pgcrs += 1;
        if processed_pgcrs == discovered_pgcrs || processed_pgcrs % 25 == 0 {
            report_progress(
                progress,
                "activities",
                "Analyzing activities",
                processed_pgcrs,
                Some(discovered_pgcrs),
            );
        }
        let pgcr = match pgcr {
            Ok(value) => value,
            Err(error) => return Err(error.into()),
        };
        let entries = pgcr
            .get("entries")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        let owners = owner_entries(&entries, job.membership_type_id, job.membership_id);
        if owners.is_empty() {
            continue;
        }
        let kills = owners
            .iter()
            .map(|entry| stat_i64(entry, "kills"))
            .sum::<i64>();
        let deaths = owners
            .iter()
            .map(|entry| stat_i64(entry, "deaths"))
            .sum::<i64>();
        let seconds = owners
            .iter()
            .map(|entry| preferred_playtime(entry))
            .sum::<i64>();
        total_kills += kills;
        if kills == 0 {
            zero_kill_activities += 1;
        }
        total_activity_seconds += seconds;
        let mode = pgcr
            .pointer("/activityDetails/mode")
            .and_then(Value::as_i64)
            .unwrap_or(0) as i32;
        let modes = pgcr
            .pointer("/activityDetails/modes")
            .and_then(Value::as_array);
        let is_pvp = is_pvp_activity(mode, modes);
        let is_gambit = includes_mode(mode, modes, 63) || includes_mode(mode, modes, 75);
        let is_raid = includes_mode(mode, modes, 4);
        let is_dungeon = includes_mode(mode, modes, 82);
        let private_competitive = is_private_match_activity(mode, modes)
            || is_private_competitive_activity(&pgcr, &activity_definitions, is_pvp, is_gambit);
        if !private_competitive {
            add_deaths_by_mode(
                &mut deaths_by_mode,
                mode_group_from_flags(is_pvp, is_gambit),
                mode,
                deaths,
            );
        }
        if let Some(period) = pgcr.get("period").and_then(Value::as_str) {
            play_dates.insert(period.get(..10).unwrap_or(period).to_owned());
        }
        for broad in [5, 7, 64]
            .into_iter()
            .filter(|broad| (*broad == 5 && is_pvp) || includes_mode(mode, modes, *broad))
        {
            *mode_seconds
                .entry(broad)
                .or_default()
                .entry(mode)
                .or_default() += seconds;
        }
        if is_pvp {
            *crucible_kills_by_mode.entry(mode).or_default() += kills;
            add_pvp_playlist_result(&mut pvp_playlists, mode, &owners, private_competitive);
        }
        if is_gambit && includes_mode(mode, modes, 64) {
            gambit_mote_matches += 1;
            for owner in &owners {
                let mote_mode = gambit_mote_mode(mode, modes);
                *gambit_banked.entry(mote_mode).or_default() +=
                    mote_stat(owner, "motesDeposited") + mote_stat(owner, "bankOverage");
                *gambit_lost.entry(mote_mode).or_default() += mote_stat(owner, "motesLost");
                *gambit_denied.entry(mote_mode).or_default() += mote_stat(owner, "motesDenied");
            }
        }
        let activity_name = activity_name(&pgcr, &activity_definitions);
        let normalized_activity_name = normalize_activity_name(&activity_name);
        let completion_reason = entries
            .first()
            .map(|entry| stat_i64(entry, "completionReason"))
            .unwrap_or_default();
        let completed =
            completion_reason == 0 && owners.iter().any(|entry| stat_i64(entry, "completed") > 0);
        let period = pgcr
            .get("period")
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_owned();
        let completed_at = activity_completed_at(&period, &owners);
        let instance_id = pgcr
            .pointer("/activityDetails/instanceId")
            .and_then(id_i64)
            .unwrap_or(0);
        let fireteam = entries
            .iter()
            .filter(|entry| {
                entry
                    .pointer("/player/destinyUserInfo/membershipId")
                    .and_then(id_i64)
                    .is_some_and(|id| id > 0)
            })
            .collect::<Vec<_>>();
        let started_from_beginning = activity_started_from_beginning(&pgcr, &fireteam);
        let flawless = completed
            && started_from_beginning
            && !fireteam.is_empty()
            && fireteam.iter().all(|entry| stat_i64(entry, "deaths") == 0);
        let solo = completed
            && started_from_beginning
            && fireteam
                .iter()
                .filter_map(|entry| {
                    entry
                        .pointer("/player/destinyUserInfo/membershipId")
                        .and_then(id_i64)
                })
                .collect::<BTreeSet<_>>()
                .len()
                == 1;
        if is_raid {
            add_completion(
                &mut raid_completions,
                normalized_activity_name.clone(),
                completed,
                &completed_at,
                instance_id,
                seconds,
                is_contest_clear(&pgcr, true, false, &completed_at),
                flawless,
                solo,
            );
            if completed {
                completed_raids.push(CompletedRaid {
                    name: normalized_activity_name.clone(),
                    period: completed_at.clone(),
                    instance_id,
                });
                let candidates = fireteam
                    .iter()
                    .filter(|entry| stat_i64(entry, "completed") > 0)
                    .filter_map(|entry| {
                        let membership_id = entry
                            .pointer("/player/destinyUserInfo/membershipId")
                            .and_then(id_i64)?;
                        let membership_type = entry
                            .pointer("/player/destinyUserInfo/membershipType")
                            .and_then(Value::as_i64)
                            .unwrap_or(0) as i32;
                        (membership_id != job.membership_id)
                            .then_some((membership_type, membership_id))
                    })
                    .collect::<BTreeSet<_>>()
                    .into_iter()
                    .collect();
                sherpa_checks.push(SherpaCheck {
                    raid_name: normalized_activity_name.clone(),
                    period: completed_at.clone(),
                    instance_id,
                    candidates,
                });
            }
        }
        if is_dungeon {
            add_completion(
                &mut dungeon_completions,
                normalized_activity_name,
                completed,
                &completed_at,
                instance_id,
                seconds,
                is_contest_clear(&pgcr, false, true, &completed_at),
                flawless,
                solo,
            );
        }
        if let Some(conquest) = conquest_name(&pgcr, &activity_name, &completed_at) {
            add_completion(
                &mut conquest_completions,
                conquest,
                completed,
                &completed_at,
                instance_id,
                seconds,
                false,
                flawless,
                solo,
            );
        }
        for owner in owners {
            let reported_class = pgcr_character_class(owner, manifest);
            let character_id = owner.get("characterId").and_then(id_i64).unwrap_or(0);
            if character_id > 0 && reported_class != "Unknown" {
                match character_class_by_id.entry(character_id) {
                    std::collections::btree_map::Entry::Occupied(mut entry)
                        if entry.get() == "Unknown" =>
                    {
                        entry.insert(reported_class.clone());
                    }
                    std::collections::btree_map::Entry::Vacant(entry) => {
                        entry.insert(reported_class.clone());
                    }
                    _ => {}
                }
            }
            if character_id > 0
                && !profile_characters
                    .is_some_and(|characters| characters.contains_key(&character_id.to_string()))
            {
                let race = owner
                    .pointer("/player/raceHash")
                    .and_then(id_u32)
                    .and_then(|hash| {
                        manifest_display_name(manifest, "DestinyRaceDefinition", hash)
                            .ok()
                            .flatten()
                    })
                    .unwrap_or_else(|| "Unknown".into());
                merge_character_identity(
                    &mut deleted_character_identity,
                    character_id,
                    &reported_class,
                    &race,
                );
            }
            if let Some(emblem) = owner.pointer("/player/emblemHash").and_then(id_u32) {
                *emblem_seconds.entry(emblem).or_default() += preferred_playtime(owner);
            }
            if !private_competitive {
                for (hash, kills) in weapon_kill_deltas(owner) {
                    *weapon_kills
                        .entry((
                            mode_group_from_flags(is_pvp, is_gambit).into(),
                            mode,
                            character_id,
                            reported_class.clone(),
                            hash,
                        ))
                        .or_default() += kills;
                }
            }
        }
        let mut activity_encounters = BTreeSet::new();
        for entry in entries {
            let Some(id) = entry
                .pointer("/player/destinyUserInfo/membershipId")
                .and_then(id_i64)
            else {
                continue;
            };
            if id == job.membership_id {
                continue;
            }
            let membership_type = entry
                .pointer("/player/destinyUserInfo/membershipType")
                .and_then(Value::as_i64)
                .unwrap_or(0) as i32;
            if is_countable_encounter(membership_type, id) {
                activity_encounters.insert((membership_type, id));
            }
        }
        for key in activity_encounters {
            encountered.insert(key);
            *encounter_counts.entry(key).or_default() += 1;
        }
    }

    append_deleted_character_playtime(
        &mut character_playtime,
        &account,
        profile_characters,
        &character_class_by_id,
        &deleted_character_identity,
    );
    character_playtime.sort_by(|left, right| {
        timespan_seconds(right.get("playtime").and_then(Value::as_str).unwrap_or("")).cmp(
            &timespan_seconds(left.get("playtime").and_then(Value::as_str).unwrap_or("")),
        )
    });

    let mut owner_raid_history = completed_raids.clone();
    for (name, completion) in &raid_completions {
        if let Some((period, instance_id)) = &completion.first_completion
            && !owner_raid_history
                .iter()
                .any(|item| item.instance_id == *instance_id)
        {
            owner_raid_history.push(CompletedRaid {
                name: normalize_activity_name(name),
                period: period.clone(),
                instance_id: *instance_id,
            });
        }
    }
    let sherpa_deltas = resolve_sherpas(
        client,
        database,
        &activity_definitions,
        &owner_raid_history,
        sherpa_checks,
        cancellation,
        progress,
    )
    .await;
    for (raid_name, count) in sherpa_deltas {
        add_case_insensitive_count(&mut players_sherpaed, raid_name, count);
    }
    let players_sherpaed_report = players_sherpaed
        .iter()
        .filter(|(_, count)| **count > 0)
        .map(|(raid_name, player_count)| {
            json!({ "raidName": raid_name, "playerCount": player_count })
        })
        .collect::<Vec<_>>();
    let most_played_with =
        resolve_most_played_with(client, &encounter_counts, cancellation).await?;
    let most_used_emblems = resolve_most_used_emblems(manifest, &emblem_seconds);
    let pvp_playlist_reports = build_pvp_playlist_reports(&pvp_playlists, &activity_mode_names);
    let crucible_kills_total = crucible_kills_by_mode.values().sum::<i64>();
    let mut crucible_kill_modes = BTreeMap::<String, i64>::new();
    for (mode, kills) in &crucible_kills_by_mode {
        let name = activity_mode_name(*mode, &activity_mode_names);
        *crucible_kill_modes.entry(name).or_default() += *kills;
    }
    let banked_total = gambit_banked.values().sum::<i32>();
    let lost_total = gambit_lost.values().sum::<i32>();
    let denied_total = gambit_denied.values().sum::<i32>();
    let mote_modes =
        |values: &BTreeMap<i32, i32>| named_mode_totals_i32(values, &activity_mode_names);
    let patrol_report = patrol_seconds
        .iter()
        .map(|(name, seconds)| (name.clone(), timespan(*seconds)))
        .collect::<BTreeMap<_, _>>();
    apply_activity_triumph_records(&profile, &mut raid_completions, &mut dungeon_completions);
    let raid_reports = completion_reports(&raid_completions);
    let dungeon_reports = completion_reports(&dungeon_completions);
    let conquest_reports = completion_reports(&conquest_completions);

    let mut resolved_weapon_kills = BTreeMap::<(String, i32, String, i64), i32>::new();
    for ((activity_mode, specific, character_id, reported_class, hash), kills) in weapon_kills {
        let class_name = character_class_by_id
            .get(&character_id)
            .map(String::as_str)
            .filter(|class| *class != "Unknown")
            .or_else(|| {
                deleted_character_identity
                    .get(&character_id)
                    .map(|identity| identity.0.as_str())
                    .filter(|class| *class != "Unknown")
            })
            .unwrap_or(&reported_class)
            .to_owned();
        *resolved_weapon_kills
            .entry((activity_mode, specific, class_name, hash))
            .or_default() += kills;
    }
    let weapons = resolved_weapon_kills.into_iter().map(|((activity_mode, specific, class_name, hash), kills)| json!({
        "ownerMembershipType": job.membership_type_id, "ownerMembershipId": job.membership_id,
        "activityMode": activity_mode, "className": class_name, "specificActivityMode": specific,
        "weaponHash": hash, "kills": kills
    })).collect();
    let encounters = encounter_counts.into_iter().map(|((membership_type, membership_id), count)| json!({
        "ownerMembershipType": job.membership_type_id, "ownerMembershipId": job.membership_id,
        "encounteredMembershipType": membership_type, "encounteredMembershipId": membership_id, "count": count
    })).collect();
    let deaths = deaths_by_mode.into_iter().map(|((activity_mode, specific), deaths)| json!({
        "ownerMembershipType": job.membership_type_id, "ownerMembershipId": job.membership_id,
        "activityMode": activity_mode, "specificActivityMode": specific, "deaths": deaths
    })).collect();
    let emblems = emblem_seconds.into_iter().map(|(hash, seconds)| json!({
        "ownerMembershipType": job.membership_type_id, "ownerMembershipId": job.membership_id,
        "emblemHash": hash, "totalSeconds": seconds
    })).collect();
    let playtime_by_mode = mode_seconds
        .into_iter()
        .map(|(mode, specifics)| {
            let total = specifics.values().sum::<i64>();
            let specific = specifics
                .into_iter()
                .map(|(key, value)| (key.to_string(), value))
                .collect::<std::collections::BTreeMap<_, _>>();
            (
                mode.to_string(),
                json!({ "totalSeconds": total, "mostSpecificModeSeconds": specific }),
            )
        })
        .collect::<std::collections::BTreeMap<_, _>>();
    let encountered_bytes = encode_encounters(&encountered);
    let crucible = mode_totals.get(&5);
    let gambit_entered = [63, 75]
        .into_iter()
        .filter_map(|mode| mode_totals.get(&mode))
        .map(|value| value.entered)
        .sum::<i32>();
    let gambit_wins = [63, 75]
        .into_iter()
        .filter_map(|mode| mode_totals.get(&mode))
        .map(|value| value.wins)
        .sum::<i32>();
    let gambit_kdas = [63, 75]
        .into_iter()
        .filter_map(|mode| mode_totals.get(&mode))
        .flat_map(|value| value.kda_values.iter().copied())
        .collect::<Vec<_>>();
    let gambit_kds = [63, 75]
        .into_iter()
        .filter_map(|mode| mode_totals.get(&mode))
        .flat_map(|value| value.kd_values.iter().copied())
        .collect::<Vec<_>>();
    let now = chrono::Utc::now().to_rfc3339();
    let queued_at = bson_datetime_string(job.queued_at);
    let started_at = job.started_at.map(bson_datetime_string);
    let report = json!({
        "platformId": job.membership_type_id,
        "playerMembershipId": job.membership_id,
        "displayName": display_name,
        "displayCode": display_code,
        "crawledAt": now.clone(),
        "firstActivityAtUtc": earliest_period,
        "crawlState": "completed",
        "queuedInRedis": false,
        "queuedAtUtc": queued_at,
        "startedAtUtc": started_at,
        "lastCrawledAtUtc": now.clone(),
        "hasCompletedCrawl": true,
        "leaseExpiresAtUtc": null,
        "leaseOwner": "",
        "crawlError": "",
        "needsFullRecrawl": false,
        "fullRecrawlReason": "",
        "totalPlaytime": timespan(total_playtime_minutes * 60),
        "characterPlaytime": character_playtime,
        "patrolTimeByPlanet": patrol_report,
        "goodBoyProtocol": good_boy_protocol,
        "fishCaught": fish_caught,
        "totalKills": total_kills,
        "crucibleKd": mode_kd(crucible),
        "crucibleKda": average_mode_kda(crucible),
        "gambitKd": average_values(&gambit_kds),
        "gambitKda": average_values(&gambit_kdas),
        "crucibleMatchesPlayed": crucible.map(|value| value.entered).unwrap_or(0),
        "gambitMatchesPlayed": gambit_entered,
        "crucibleWins": crucible.map(|value| value.wins).unwrap_or(0),
        "gambitWins": gambit_wins,
        "gambitPlaylists": [playlist(&mode_totals, 63, "Gambit"), playlist(&mode_totals, 75, "GambitPrime")],
        "crucibleKills": { "total": crucible_kills_total, "byMode": crucible_kill_modes },
        "gambitMotes": {
            "matches": gambit_mote_matches,
            "motesBanked": { "total": banked_total, "byMode": mote_modes(&gambit_banked) },
            "motesLost": { "total": lost_total, "byMode": mote_modes(&gambit_lost) },
            "motesDenied": { "total": denied_total, "byMode": mote_modes(&gambit_denied) },
            "averageMotesBanked": ratio(f64::from(banked_total), f64::from(gambit_mote_matches)),
            "averageMotesLost": ratio(f64::from(lost_total), f64::from(gambit_mote_matches))
        },
        "triumphSeals": triumph_seals,
        "misadventures": misadventures,
        "zeroKillActivities": zero_kill_activities,
        "totalActivityTime": timespan(total_activity_seconds),
        "longestPlaytimeStreak": playtime_streak(&play_dates, false),
        "currentPlaytimeStreak": playtime_streak(&play_dates, true),
        "pvpPlaylists": pvp_playlist_reports,
        "raidCompletions": raid_reports,
        "dungeonCompletions": dungeon_reports,
        "conquestCompletions": conquest_reports,
        "mostPlayedWith": most_played_with,
        "uniquePlayersPlayedWith": encountered.len(),
        "playersSherpaed": players_sherpaed_report,
        "mostUsedEmblems": most_used_emblems
    });
    fetched_recent_activities.sort_by(|left, right| right.0.cmp(&left.0));
    let mut recent_seen = BTreeSet::new();
    let recent_ids = fetched_recent_activities
        .into_iter()
        .map(|(_, id)| id)
        .chain(seed.recent_activity_ids)
        .filter(|id| *id > 0 && recent_seen.insert(*id))
        .take(RECENT_ACTIVITY_INSTANCE_ID_LIMIT)
        .collect::<Vec<_>>();
    let state = json!({
        "platformId": job.membership_type_id,
        "playerMembershipId": job.membership_id,
        "lastSuccessfulCrawlAt": now.clone(),
        "newestActivityPeriod": newest_period.unwrap_or_else(|| now.clone()),
        "firstActivityAtUtc": earliest_period,
        "firstActivityDiscoveryCompleted": true,
        "recentActivityInstanceIds": recent_ids,
        "totalKills": total_kills,
        "patrolSecondsByPlanet": patrol_seconds,
        "raidCompletions": completion_state(&raid_completions),
        "dungeonCompletions": completion_state(&dungeon_completions),
        "conquestCompletions": completion_state(&conquest_completions),
        "encounteredPlayerKeys": base64_encode(&encountered_bytes),
        "uniquePlayersPlayedWith": encountered.len(),
        "zeroKillActivities": zero_kill_activities,
        "totalActivitySeconds": total_activity_seconds,
        "playDates": play_dates,
        "playtimeByActivityMode": playtime_by_mode,
        "gambitMotesBanked": banked_total,
        "gambitMotesLost": lost_total,
        "gambitMotesDenied": denied_total,
        "gambitMoteMatches": gambit_mote_matches,
        "gambitMotesBankedByMode": numeric_mode_map(&gambit_banked),
        "gambitMotesLostByMode": numeric_mode_map(&gambit_lost),
        "gambitMotesDeniedByMode": numeric_mode_map(&gambit_denied),
        "pvpPlaylists": pvp_playlist_state(&pvp_playlists),
        "crucibleKills": crucible_kills_total,
        "crucibleKillsByMode": numeric_mode_map_i64(&crucible_kills_by_mode),
        "playersSherpaed": players_sherpaed,
        "deletedCharacterIdentity": deleted_character_identity.iter().map(|(id, (class, race))| (
            id.to_string(),
            json!({ "class": class, "race": race })
        )).collect::<BTreeMap<_, _>>()
    });
    Ok(CrawlOutcome::Completed(CrawlResult {
        report,
        state,
        weapons,
        deaths,
        emblems,
        encounters,
    }))
}

async fn cancellable<T>(
    cancellation: &CancellationToken,
    request: impl Future<Output = Result<T, BungieError>>,
) -> Result<T, BungieError> {
    tokio::select! {
        _ = cancellation.cancelled() => Err(BungieError::Cancelled),
        result = request => result,
    }
}

fn empty_result(job: &CrawlJob, state: &str, error: &str) -> CrawlResult {
    let now = chrono::Utc::now().to_rfc3339();
    CrawlResult {
        report: json!({ "platformId": job.membership_type_id, "playerMembershipId": job.membership_id, "crawlState": state, "crawlError": error, "lastCrawledAtUtc": now }),
        state: json!({ "platformId": job.membership_type_id, "playerMembershipId": job.membership_id }),
        weapons: vec![],
        deaths: vec![],
        emblems: vec![],
        encounters: vec![],
    }
}

fn private_outcome(job: &CrawlJob) -> CrawlOutcome {
    CrawlOutcome::Private(empty_result(
        job,
        "private",
        "Destiny profile is not public.",
    ))
}

fn is_private_error(error: &BungieError) -> bool {
    matches!(error, BungieError::Private(_))
}

fn is_owner_entry(entry: &Value, membership_type: i32, membership_id: i64) -> bool {
    if entry
        .pointer("/player/destinyUserInfo/membershipId")
        .and_then(id_i64)
        != Some(membership_id)
    {
        return false;
    }
    let entry_membership_type = entry
        .pointer("/player/destinyUserInfo/membershipType")
        .and_then(Value::as_i64)
        .unwrap_or(0) as i32;
    membership_type <= 0 || entry_membership_type <= 0 || entry_membership_type == membership_type
}

fn owner_entries(entries: &[Value], membership_type: i32, membership_id: i64) -> Vec<&Value> {
    entries
        .iter()
        .filter(|entry| is_owner_entry(entry, membership_type, membership_id))
        .collect()
}

fn metric_progress(profile: &Value, definitions: &BTreeMap<u32, Value>, metric_name: &str) -> i64 {
    let metric_hash = definitions
        .iter()
        .find(|(_, definition)| {
            definition
                .pointer("/displayProperties/name")
                .and_then(Value::as_str)
                .is_some_and(|name| name.eq_ignore_ascii_case(metric_name))
        })
        .map(|(hash, _)| *hash);
    let Some(hash) = metric_hash else {
        return 0;
    };
    profile
        .pointer(&format!(
            "/metrics/data/metrics/{hash}/objectiveProgress/progress"
        ))
        .and_then(number_i64)
        .unwrap_or(0)
}

fn completed_seals(profile: &Value, manifest: &ManifestStore) -> anyhow::Result<Vec<Value>> {
    let records = profile
        .pointer("/profileRecords/data/records")
        .and_then(Value::as_object);
    let Some(records) = records else {
        return Ok(Vec::new());
    };
    let mut seals = Vec::new();
    let mut seen = BTreeSet::new();
    for root_hash in [616_318_467_u32, 1_881_970_629_u32] {
        let Some(root) = manifest.definition("DestinyPresentationNodeDefinition", root_hash)?
        else {
            continue;
        };
        let mut children = root
            .pointer("/children/presentationNodes")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .collect::<Vec<_>>();
        children.sort_by_key(|child| {
            child
                .get("nodeDisplayPriority")
                .and_then(number_i64)
                .unwrap_or(i64::MAX)
        });
        for child in children {
            let Some(node_hash) = child.get("presentationNodeHash").and_then(id_u32) else {
                continue;
            };
            let Some(node) = manifest.definition("DestinyPresentationNodeDefinition", node_hash)?
            else {
                continue;
            };
            let Some(record_hash) = node.get("completionRecordHash").and_then(id_u32) else {
                continue;
            };
            if !seen.insert(record_hash) {
                continue;
            }
            let component = records
                .get(&record_hash.to_string())
                .or_else(|| records.get(&(record_hash as i32).to_string()));
            let completed = component.is_some_and(|component| {
                component
                    .get("completedCount")
                    .and_then(number_i64)
                    .unwrap_or(0)
                    > 0
                    || component
                        .get("state")
                        .and_then(number_i64)
                        .is_some_and(|state| state & 4 == 0)
            });
            if !completed {
                continue;
            }
            let definition = manifest.definition("DestinyRecordDefinition", record_hash)?;
            let preferred_name = definition
                .as_ref()
                .and_then(|value| value.pointer("/displayProperties/name"))
                .and_then(Value::as_str);
            let name = first_non_blank(
                preferred_name,
                node.pointer("/displayProperties/name")
                    .and_then(Value::as_str),
            );
            let preferred_description = definition
                .as_ref()
                .and_then(|value| value.pointer("/displayProperties/description"))
                .and_then(Value::as_str);
            let description = first_non_blank(
                preferred_description,
                node.pointer("/displayProperties/description")
                    .and_then(Value::as_str),
            );
            seals.push(json!({
                "name": name,
                "description": description,
                "iconUrl": bungie_url(node.pointer("/displayProperties/icon").and_then(Value::as_str)),
                "isCompleted": true
            }));
        }
    }
    Ok(seals)
}

fn sum_account_stat(account: &Value, stat_name: &str) -> i64 {
    account
        .get("characters")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .flat_map(|character| {
            character
                .get("results")
                .and_then(Value::as_object)
                .into_iter()
                .flat_map(|results| results.values())
        })
        .map(|result| {
            result
                .pointer(&format!("/allTime/{stat_name}/basic/value"))
                .and_then(Value::as_f64)
                .unwrap_or(0.0) as i64
        })
        .sum()
}

fn activity_destination(
    activity: &Value,
    activities: &BTreeMap<u32, Value>,
    destinations: &BTreeMap<u32, Value>,
) -> Option<String> {
    let details = activity.get("activityDetails")?;
    let reference = details
        .get("referenceId")
        .and_then(id_u32)
        .or_else(|| details.get("directorActivityHash").and_then(id_u32))?;
    let definition = activities.get(&reference).or_else(|| {
        details
            .get("directorActivityHash")
            .and_then(id_u32)
            .and_then(|hash| activities.get(&hash))
    })?;
    let destination_hash = definition.get("destinationHash").and_then(id_u32)?;
    let name = destinations
        .get(&destination_hash)?
        .pointer("/displayProperties/name")
        .and_then(Value::as_str)?;
    canonical_patrol_destination(name).map(str::to_owned)
}

fn is_private_competitive_activity(
    pgcr: &Value,
    definitions: &BTreeMap<u32, Value>,
    is_pvp: bool,
    is_gambit: bool,
) -> bool {
    let details = pgcr.get("activityDetails").unwrap_or(&Value::Null);
    let definition = ["referenceId", "directorActivityHash"]
        .into_iter()
        .find_map(|field| {
            details
                .get(field)
                .and_then(id_u32)
                .and_then(|hash| definitions.get(&hash))
        });
    let activity_type = definition
        .and_then(|value| value.get("activityTypeHash"))
        .and_then(id_u32);
    (is_pvp && activity_type == Some(4_260_058_063))
        || (is_gambit && matches!(activity_type, Some(146_907_730) | Some(2_516_284_680)))
}

fn canonical_patrol_destination(name: &str) -> Option<&'static str> {
    Some(match name.to_ascii_lowercase().as_str() {
        "arcadian valley" | "nessus" => "Nessus",
        "echo mesa" | "io" => "IO",
        "hellas basin" | "mars" => "Mars",
        "new pacific arcology" | "titan" => "Titan",
        "the pale heart" => "The Pale Heart",
        "european dead zone" => "European Dead Zone",
        "the moon" => "The Moon",
        "europa" => "Europa",
        "neomuna" => "Neomuna",
        "kepler" => "Kepler",
        "the dreaming city" => "The Dreaming City",
        "the tangled shore" => "The Tangled Shore",
        "savathûn's throne world" => "Savathûn's Throne World",
        "cosmodrome" => "Cosmodrome",
        "mercury" => "Mercury",
        "tharsis expanse" => "Tharsis Expanse",
        "eternity" => "Eternity",
        _ => return None,
    })
}

fn activity_name(pgcr: &Value, definitions: &BTreeMap<u32, Value>) -> String {
    let details = pgcr.get("activityDetails").unwrap_or(&Value::Null);
    for field in ["referenceId", "directorActivityHash"] {
        if let Some(definition) = details
            .get(field)
            .and_then(id_u32)
            .and_then(|hash| definitions.get(&hash))
        {
            if let Some(name) = definition
                .pointer("/displayProperties/name")
                .and_then(Value::as_str)
                .filter(|name| !name.is_empty())
            {
                return name.to_owned();
            }
        }
    }
    details
        .get("referenceId")
        .and_then(id_u32)
        .map(|hash| hash.to_string())
        .unwrap_or_else(|| "Unknown".into())
}

fn report_progress(
    progress: &UnboundedSender<CrawlProgress>,
    phase: &'static str,
    label: &'static str,
    current: i64,
    total: Option<i64>,
) {
    let _ = progress.send(CrawlProgress {
        phase,
        label,
        current,
        total,
    });
}

#[allow(clippy::too_many_arguments)]
fn add_completion(
    values: &mut BTreeMap<String, CompletionAggregate>,
    name: String,
    completed: bool,
    period: &str,
    instance_id: i64,
    duration: i64,
    contest: bool,
    flawless: bool,
    solo: bool,
) {
    if name.is_empty() {
        return;
    }
    let key = values
        .keys()
        .find(|key| key.eq_ignore_ascii_case(&name))
        .cloned()
        .unwrap_or(name);
    let value = values.entry(key).or_default();
    value.activity_count += 1;
    if !completed {
        return;
    }
    value.completion_count += 1;
    value.contest_clear |= contest;
    if value
        .first_completion
        .as_ref()
        .is_none_or(|(date, _)| period < date.as_str())
    {
        value.first_completion = Some((period.to_owned(), instance_id));
    }
    if value
        .last_completion
        .as_ref()
        .is_none_or(|(date, _)| period > date.as_str())
    {
        value.last_completion = Some((period.to_owned(), instance_id));
    }
    if duration > 0
        && value
            .fastest_completion
            .as_ref()
            .is_none_or(|(seconds, _, _)| duration < *seconds)
    {
        value.fastest_completion = Some((duration, period.to_owned(), instance_id));
    }
    value.flawless_clear |= flawless;
    value.solo_clear |= solo;
    value.solo_flawless_clear |= solo && flawless;
}

fn normalize_completion_map(
    values: BTreeMap<String, CompletionAggregate>,
) -> BTreeMap<String, CompletionAggregate> {
    let mut normalized = BTreeMap::new();
    for (name, value) in values {
        let key = normalized
            .keys()
            .find(|key: &&String| key.eq_ignore_ascii_case(&name))
            .cloned()
            .unwrap_or(name);
        let target = normalized
            .entry(key)
            .or_insert_with(CompletionAggregate::default);
        merge_completion(target, value);
    }
    normalized
}

fn merge_completion(target: &mut CompletionAggregate, source: CompletionAggregate) {
    target.activity_count += source.activity_count;
    target.completion_count += source.completion_count;
    if source.first_completion.as_ref().is_some_and(|(date, _)| {
        target
            .first_completion
            .as_ref()
            .is_none_or(|(current, _)| date < current)
    }) {
        target.first_completion = source.first_completion;
    }
    if source.last_completion.as_ref().is_some_and(|(date, _)| {
        target
            .last_completion
            .as_ref()
            .is_none_or(|(current, _)| date > current)
    }) {
        target.last_completion = source.last_completion;
    }
    if source
        .fastest_completion
        .as_ref()
        .is_some_and(|(seconds, _, _)| {
            target
                .fastest_completion
                .as_ref()
                .is_none_or(|(current, _, _)| seconds < current)
        })
    {
        target.fastest_completion = source.fastest_completion;
    }
    target.contest_clear |= source.contest_clear;
    target.flawless_clear |= source.flawless_clear;
    target.solo_clear |= source.solo_clear;
    target.solo_flawless_clear |= source.solo_flawless_clear;
}

fn normalize_case_insensitive_counts(values: BTreeMap<String, i32>) -> BTreeMap<String, i32> {
    let mut normalized = BTreeMap::new();
    for (name, count) in values {
        add_case_insensitive_count(&mut normalized, name, count);
    }
    normalized
}

fn add_case_insensitive_count(values: &mut BTreeMap<String, i32>, name: String, count: i32) {
    let key = values
        .keys()
        .find(|key| key.eq_ignore_ascii_case(&name))
        .cloned()
        .unwrap_or(name);
    *values.entry(key).or_default() += count;
}

fn add_pvp_playlist_result(
    values: &mut BTreeMap<i32, (i32, i32)>,
    mode: i32,
    owners: &[&Value],
    private_competitive: bool,
) {
    if private_competitive {
        return;
    }
    let playlist = values.entry(mode).or_default();
    if owners
        .iter()
        .any(|entry| entry.get("standing").and_then(Value::as_i64).unwrap_or(1) == 0)
    {
        playlist.0 += 1;
    } else {
        playlist.1 += 1;
    }
}

fn build_pvp_playlist_reports(
    values: &BTreeMap<i32, (i32, i32)>,
    activity_mode_names: &BTreeMap<i32, String>,
) -> Vec<Value> {
    let mut playlists = values
        .iter()
        .map(|(mode, (wins, losses))| (*mode, *wins, *losses))
        .collect::<Vec<_>>();
    playlists.sort_by(|left, right| {
        (right.1 + right.2)
            .cmp(&(left.1 + left.2))
            .then_with(|| left.0.cmp(&right.0))
    });
    playlists
        .into_iter()
        .map(|(mode, wins, losses)| {
            json!({
                "mode": mode,
                "modeName": activity_mode_name(mode, activity_mode_names),
                "wins": wins,
                "losses": losses,
                "matches": wins + losses,
                "winRate": ratio(f64::from(wins), f64::from(wins + losses))
            })
        })
        .collect()
}

fn add_deaths_by_mode(
    values: &mut BTreeMap<(String, i32), i64>,
    activity_mode: &str,
    specific_mode: i32,
    deaths: i64,
) {
    if deaths <= 0 {
        return;
    }
    *values
        .entry((activity_mode.to_owned(), specific_mode))
        .or_default() += deaths;
}

fn activity_completed_at(period: &str, owners: &[&Value]) -> String {
    let duration = owners
        .iter()
        .map(|entry| stat_i64(entry, "activityDurationSeconds"))
        .max()
        .unwrap_or(0);
    if duration <= 0 {
        return period.to_owned();
    }
    chrono::DateTime::parse_from_rfc3339(period)
        .ok()
        .map(|value| {
            (value + chrono::Duration::seconds(duration))
                .to_rfc3339_opts(chrono::SecondsFormat::Secs, true)
        })
        .unwrap_or_else(|| period.to_owned())
}

fn apply_activity_triumph_records(
    profile: &Value,
    raids: &mut BTreeMap<String, CompletionAggregate>,
    dungeons: &mut BTreeMap<String, CompletionAggregate>,
) {
    const RAID_FLAWLESS: [(&str, u32); 7] = [
        ("Last Wish", 380_332_968),
        ("Scourge of the Past", 2_925_485_370),
        ("Crown of Sorrow", 3_292_013_042),
        ("Garden of Salvation", 1_522_774_125),
        ("Deep Stone Crypt", 3_560_923_614),
        ("Vault of Glass", 2_750_088_202),
        ("Vow of the Disciple", 4_019_717_242),
    ];
    const DUNGEON_RECORDS: [(&str, u32, u32, u32); 4] = [
        (
            "Shattered Throne",
            3_899_996_566,
            1_178_448_425,
            3_205_009_787,
        ),
        ("Pit of Heresy", 3_841_336_511, 245_952_203, 3_950_599_483),
        ("Prophecy", 3_002_642_730, 2_010_041_484, 3_191_784_400),
        (
            "Grasp of Avarice",
            678_858_776,
            2_693_589_427,
            3_718_971_745,
        ),
    ];

    for (name, record) in RAID_FLAWLESS {
        if profile_record_completed(profile, record)
            && let Some(completion) = completion_by_name_mut(raids, name)
        {
            completion.flawless_clear = true;
        }
    }
    for (name, solo_record, flawless_record, solo_flawless_record) in DUNGEON_RECORDS {
        let solo = profile_record_completed(profile, solo_record);
        let flawless = profile_record_completed(profile, flawless_record);
        let solo_flawless = profile_record_completed(profile, solo_flawless_record);
        if (solo || flawless || solo_flawless)
            && let Some(completion) = completion_by_name_mut(dungeons, name)
        {
            completion.solo_clear |= solo || solo_flawless;
            completion.flawless_clear |= flawless || solo_flawless;
            completion.solo_flawless_clear |= solo_flawless;
        }
    }
}

fn completion_by_name_mut<'a>(
    values: &'a mut BTreeMap<String, CompletionAggregate>,
    name: &str,
) -> Option<&'a mut CompletionAggregate> {
    let normalized = normalize_activity_name(name);
    let key = values
        .keys()
        .find(|key| key.eq_ignore_ascii_case(&normalized))
        .cloned()?;
    values.get_mut(&key)
}

fn profile_record_completed(profile: &Value, hash: u32) -> bool {
    let Some(records) = profile
        .pointer("/profileRecords/data/records")
        .and_then(Value::as_object)
    else {
        return false;
    };
    records
        .get(&hash.to_string())
        .or_else(|| records.get(&(hash as i32).to_string()))
        .is_some_and(|record| {
            record
                .get("completedCount")
                .and_then(number_i64)
                .unwrap_or(0)
                > 0
                || record
                    .get("state")
                    .and_then(number_i64)
                    .is_some_and(|state| state & 4 == 0)
        })
}

fn completion_reports(values: &BTreeMap<String, CompletionAggregate>) -> Vec<Value> {
    values
        .iter()
        .map(|(name, value)| {
            json!({
                "activityName": name,
                "activityCount": value.activity_count,
                "completionCount": value.completion_count,
                "clearRate": if value.activity_count == 0 { 0.0 } else {
                    ((f64::from(value.completion_count) / f64::from(value.activity_count)) * 10_000.0).round() / 10_000.0
                },
                "firstCompletion": value.first_completion.as_ref().map(|(date, id)| json!({"completedAt": date, "instanceId": id})),
                "lastCompletion": value.last_completion.as_ref().map(|(date, id)| json!({"completedAt": date, "instanceId": id})),
                "fastestCompletion": value.fastest_completion.as_ref().map(|(seconds, date, id)| json!({
                    "duration": timespan(*seconds), "completedAt": date, "instanceId": id
                })),
                "contestClear": value.contest_clear,
                "flawlessClear": value.flawless_clear,
                "soloClear": value.solo_clear,
                "soloFlawlessClear": value.solo_flawless_clear
            })
        })
        .collect()
}

fn completion_state(values: &BTreeMap<String, CompletionAggregate>) -> BTreeMap<String, Value> {
    values
        .iter()
        .map(|(name, value)| {
            (
                name.clone(),
                json!({
                    "activityCount": value.activity_count,
                    "completionCount": value.completion_count,
                    "firstCompletion": value.first_completion.as_ref().map(|(date, id)| json!({"completedAt": date, "instanceId": id})),
                    "lastCompletion": value.last_completion.as_ref().map(|(date, id)| json!({"completedAt": date, "instanceId": id})),
                    "fastestCompletion": value.fastest_completion.as_ref().map(|(seconds, date, id)| json!({
                        "duration": timespan(*seconds), "completedAt": date, "instanceId": id
                    })),
                    "contestClear": value.contest_clear,
                    "flawlessClear": value.flawless_clear,
                    "soloClear": value.solo_clear,
                    "soloFlawlessClear": value.solo_flawless_clear
                }),
            )
        })
        .collect()
}

fn activity_started_from_beginning(pgcr: &Value, entries: &[&Value]) -> bool {
    let period = pgcr.get("period").and_then(Value::as_str).unwrap_or("");
    let reported = pgcr
        .get("activityWasStartedFromBeginning")
        .and_then(Value::as_bool);
    if period >= "2022-05-24T17:00:00" {
        return reported == Some(true);
    }
    if period < "2020-11-10T17:00:00" {
        let Some(phase) = pgcr.get("startingPhaseIndex").and_then(Value::as_i64) else {
            return false;
        };
        let hash = pgcr
            .pointer("/activityDetails/directorActivityHash")
            .and_then(id_u32)
            .unwrap_or(0);
        if matches!(hash, 548_750_096 | 2_812_525_063) {
            return phase <= 1;
        }
        if matches!(
            hash,
            2_693_136_600
                | 2_693_136_601
                | 2_693_136_602
                | 2_693_136_603
                | 2_693_136_604
                | 2_693_136_605
                | 89_727_599
                | 287_649_202
                | 1_699_948_563
                | 1_875_726_950
                | 3_916_343_513
                | 4_039_317_196
                | 417_231_112
                | 508_802_457
                | 757_116_822
                | 771_164_842
                | 1_685_065_161
                | 1_800_508_819
                | 2_449_714_930
                | 3_446_541_099
                | 4_206_123_728
                | 3_912_437_239
                | 3_879_860_661
                | 3_857_338_478
        ) {
            return matches!(phase, 0 | 2);
        }
        return phase == 0;
    }
    if period >= "2022-02-22T17:00:00" {
        let deathless =
            !entries.is_empty() && entries.iter().all(|entry| stat_i64(entry, "deaths") <= 0);
        if reported == Some(true) || deathless {
            return reported == Some(true);
        }
    }
    false
}

fn is_contest_clear(pgcr: &Value, is_raid: bool, is_dungeon: bool, completed_at: &str) -> bool {
    let completed_at = chrono::DateTime::parse_from_rfc3339(completed_at).ok();
    let Some(completed_at) = completed_at else {
        return false;
    };
    for hash in [
        pgcr.pointer("/activityDetails/referenceId")
            .and_then(id_u32),
        pgcr.pointer("/activityDetails/directorActivityHash")
            .and_then(id_u32),
    ]
    .into_iter()
    .flatten()
    {
        let windows: &[(&str, &str)] = match (is_raid, is_dungeon, hash) {
            (true, _, 2_693_136_601) => &[("2017-09-13T17:00:00Z", "2017-09-14T17:00:00Z")],
            (true, _, 3_089_205_900) => &[("2017-12-08T18:00:00Z", "2017-12-09T18:00:00Z")],
            (true, _, 119_944_200) => &[("2018-05-11T17:00:00Z", "2018-05-12T17:00:00Z")],
            (true, _, 2_122_313_384) => &[("2018-09-14T17:00:00Z", "2018-09-15T17:00:00Z")],
            (true, _, 548_750_096) => &[("2018-12-07T17:00:00Z", "2018-12-08T17:00:00Z")],
            (true, _, 3_333_172_150) => &[("2019-06-04T23:00:00Z", "2019-06-05T23:00:00Z")],
            (true, _, 2_659_723_068) => &[("2019-10-05T17:00:00Z", "2019-10-06T17:00:00Z")],
            (true, _, 910_380_154) => &[("2020-11-21T18:00:00Z", "2020-11-22T18:00:00Z")],
            (true, _, 1_485_585_878) => &[("2021-05-22T18:00:00Z", "2021-05-23T18:00:00Z")],
            (true, _, 1_441_982_566) => &[("2022-03-05T18:00:00Z", "2022-03-06T18:00:00Z")],
            (true, _, 1_063_970_578) => &[("2022-08-26T18:00:00Z", "2022-08-27T18:00:00Z")],
            (true, _, 2_381_413_764) => &[("2023-03-10T17:00:00Z", "2023-03-12T17:00:00Z")],
            (true, _, 156_253_568) => &[("2023-09-01T17:00:00Z", "2023-09-03T17:00:00Z")],
            (true, _, 2_192_826_039) => &[("2024-06-07T17:00:00Z", "2024-06-09T17:00:00Z")],
            (true, _, 3_896_382_790) => &[("2025-07-19T17:00:00Z", "2025-07-21T17:00:00Z")],
            (true, _, 2_586_252_122) => &[("2025-09-27T17:00:00Z", "2025-09-29T17:00:00Z")],
            (_, true, 1_915_770_060) => &[("2024-10-11T17:00:00Z", "2024-10-13T17:00:00Z")],
            (_, true, 247_869_137) => &[
                ("2025-02-07T17:00:00Z", "2025-02-09T17:00:00Z"),
                ("2025-02-22T17:00:00Z", "2025-02-23T17:00:00Z"),
            ],
            (_, true, 1_754_635_208) => &[("2025-12-13T17:00:00Z", "2025-12-15T17:00:00Z")],
            _ => &[],
        };
        if windows.iter().any(|(start, end)| {
            let start = chrono::DateTime::parse_from_rfc3339(start).ok();
            let end = chrono::DateTime::parse_from_rfc3339(end).ok();
            start.is_some_and(|start| completed_at >= start)
                && end.is_some_and(|end| completed_at < end)
        }) {
            return true;
        }
    }
    false
}

fn conquest_name(pgcr: &Value, _activity_name: &str, period: &str) -> Option<String> {
    let reference = pgcr
        .pointer("/activityDetails/referenceId")
        .and_then(id_u32);
    let director = pgcr
        .pointer("/activityDetails/directorActivityHash")
        .and_then(id_u32);
    let configured = reference
        .into_iter()
        .chain(director)
        .find_map(|hash| match hash {
            123_652_462 => Some((
                "Ultimate Conquest: Hypernet",
                "Ultimate Conquest: Lightblade",
            )),
            1_025_079_976 => Some((
                "Grandmaster Conquest: Glassway",
                "Grandmaster Conquest: Heist Mars",
            )),
            1_298_573_781 => Some((
                "Grandmaster Conquest: Fikrul's Castle",
                "Grandmaster Conquest: Scarlet Keep",
            )),
            1_561_490_698 => Some((
                "Grandmaster Conquest: Delve",
                "Grandmaster Conquest: Arms Dealer",
            )),
            1_645_244_833 => Some((
                "Grandmaster Conquest: Whisper",
                "Grandmaster Conquest: Defiant EDZ",
            )),
            2_384_839_795 => Some((
                "Grandmaster Conquest: Savathûn's Spire",
                "Grandmaster Conquest: Heliostat",
            )),
            2_404_075_359 => Some((
                "Grandmaster Conquest: Fallen S.A.B.E.R.",
                "Grandmaster Conquest: Disgraced",
            )),
            2_500_578_747 => Some((
                "Master Conquest: Conduit",
                "Master Conquest: Conductor's Keep",
            )),
            2_883_193_556 => Some((
                "Master Conquest: Inverted Spire",
                "Master Conquest: Derealize",
            )),
            3_645_820_853 => Some((
                "Ultimate Conquest: //node.ovrd.AVALON//",
                "Ultimate Conquest: Operation: Seraph's Shield",
            )),
            3_656_747_069 => Some((
                "Expert Conquest: Dark Priestess",
                "Expert Conquest: Sunless Cell",
            )),
            4_089_129_430 => Some(("Expert Conquest: Devils' Lair", "Expert Conquest: Moon")),
            _ => None,
        });
    if let Some((edge, renegades)) = configured {
        return Some(
            if period >= "2025-12-02T17:00:00" {
                renegades
            } else {
                edge
            }
            .to_owned(),
        );
    }
    None
}

fn normalize_activity_name(activity_name: &str) -> String {
    const SUFFIXES: [&str; 10] = [
        ": Master",
        ": Normal",
        ": Standard",
        ": Prestige",
        ": Contest",
        ": Customize",
        ": Guided Games",
        ": Legend",
        ": Expert",
        ": Challenge Mode",
    ];
    let mut normalized = activity_name.trim();
    loop {
        let Some(suffix) = SUFFIXES.iter().find(|suffix| {
            normalized
                .to_ascii_lowercase()
                .ends_with(&suffix.to_ascii_lowercase())
        }) else {
            break;
        };
        normalized = normalized[..normalized.len() - suffix.len()].trim();
    }
    normalized.to_owned()
}

fn append_deleted_character_playtime(
    output: &mut Vec<Value>,
    account: &Value,
    current: Option<&serde_json::Map<String, Value>>,
    historical_classes: &BTreeMap<i64, String>,
    recovered: &BTreeMap<i64, (String, String)>,
) {
    for character in account
        .get("characters")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
    {
        let Some(character_id) = character.get("characterId").and_then(id_i64) else {
            continue;
        };
        if current.is_some_and(|characters| characters.contains_key(&character_id.to_string())) {
            continue;
        }
        let seconds = character
            .pointer("/merged/allTime/secondsPlayed/basic/value")
            .and_then(Value::as_f64)
            .unwrap_or(0.0) as i64;
        let recovered_identity = recovered.get(&character_id);
        let class = recovered_identity
            .map(|value| value.0.as_str())
            .filter(|value| *value != "Unknown")
            .or_else(|| historical_classes.get(&character_id).map(String::as_str))
            .or_else(|| character.get("characterClass").and_then(Value::as_str))
            .map(normalize_class_name)
            .unwrap_or("Unknown");
        let race = recovered_identity
            .map(|value| value.1.as_str())
            .map(normalize_race_name)
            .unwrap_or("Unknown");
        output.push(json!({
            "class": class,
            "race": race,
            "isDeleted": true,
            "playtime": timespan(seconds)
        }));
    }
}

fn recover_deleted_character_identity(
    pgcr: &Value,
    manifest: &ManifestStore,
    job: &CrawlJob,
    character_id: i64,
    historical_classes: &mut BTreeMap<i64, String>,
    identities: &mut BTreeMap<i64, (String, String)>,
) {
    let Some(entry) = pgcr
        .get("entries")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .find(|entry| {
            entry.get("characterId").and_then(id_i64) == Some(character_id)
                && is_owner_entry(entry, job.membership_type_id, job.membership_id)
        })
    else {
        return;
    };
    let class = pgcr_character_class(entry, manifest);
    if class != "Unknown" {
        historical_classes.insert(character_id, class.clone());
    }
    let race = entry
        .pointer("/player/raceHash")
        .and_then(id_u32)
        .and_then(|hash| {
            manifest_display_name(manifest, "DestinyRaceDefinition", hash)
                .ok()
                .flatten()
        })
        .map(|value| normalize_race_name(&value))
        .unwrap_or("Unknown");
    merge_character_identity(identities, character_id, &class, race);
}

fn merge_character_identity(
    identities: &mut BTreeMap<i64, (String, String)>,
    character_id: i64,
    class: &str,
    race: &str,
) {
    let identity = identities
        .entry(character_id)
        .or_insert_with(|| ("Unknown".into(), "Unknown".into()));
    if identity.0 == "Unknown" && class != "Unknown" {
        identity.0 = class.to_owned();
    }
    if identity.1 == "Unknown" && race != "Unknown" {
        identity.1 = race.to_owned();
    }
}

async fn resolve_sherpas(
    client: &BungieClient,
    database: &mongodb::Database,
    activity_definitions: &BTreeMap<u32, Value>,
    completed_raids: &[CompletedRaid],
    checks: Vec<SherpaCheck>,
    cancellation: &CancellationToken,
    progress: &UnboundedSender<CrawlProgress>,
) -> BTreeMap<String, i32> {
    let unresolved_membership_ids = checks
        .iter()
        .flat_map(|check| check.candidates.iter())
        .filter(|(membership_type, membership_id)| *membership_type <= 0 && *membership_id > 0)
        .map(|(_, membership_id)| *membership_id)
        .collect::<BTreeSet<_>>();
    let mut resolved_membership_types = BTreeMap::new();
    let mut pending_membership_types = stream::iter(unresolved_membership_ids)
        .map(|membership_id| {
            let client = client.clone();
            async move {
                let linked = client
                    .linked_profiles(BUNGIE_NEXT_MEMBERSHIP_TYPE, membership_id)
                    .await
                    .ok();
                (
                    membership_id,
                    linked
                        .as_ref()
                        .and_then(|value| select_linked_membership_type(value, membership_id)),
                )
            }
        })
        .buffer_unordered(8);
    while let Some((membership_id, membership_type)) = pending_membership_types.next().await {
        resolved_membership_types.insert(membership_id, membership_type);
    }

    let mut checks_by_player = BTreeMap::<(i32, i64), Vec<SherpaCheck>>::new();
    for check in checks {
        let owner_had_prior_clear = completed_raids.iter().any(|completed| {
            completed.name.eq_ignore_ascii_case(&check.raid_name)
                && completed.instance_id != check.instance_id
                && completed.period <= check.period
        });
        if !owner_had_prior_clear {
            continue;
        }
        for candidate in &check.candidates {
            let membership_type = if candidate.0 > 0 {
                Some(candidate.0)
            } else {
                resolved_membership_types
                    .get(&candidate.1)
                    .copied()
                    .flatten()
            };
            let Some(membership_type) = membership_type else {
                continue;
            };
            checks_by_player
                .entry((membership_type, candidate.1))
                .or_default()
                .push(check.clone());
        }
    }

    let total = checks_by_player.len() as i64;
    if total == 0 {
        return BTreeMap::new();
    }
    report_progress(
        progress,
        "sherpas",
        "Checking sherpa raid histories",
        0,
        Some(total),
    );
    let mut pending = stream::iter(checks_by_player.into_iter())
        .map(|((membership_type, membership_id), checks)| {
            let client = client.clone();
            let database = database.clone();
            async move {
                let required_raid_names = checks
                    .iter()
                    .map(|check| check.raid_name.clone())
                    .collect::<BTreeSet<_>>();
                let cached = storage::cached_raid_history(
                    &database,
                    membership_type,
                    membership_id,
                    &required_raid_names,
                )
                .await
                .ok()
                .flatten();
                let history = if cached.is_some() {
                    cached
                } else {
                    let fetched = fetch_completed_raid_history(
                        &client,
                        activity_definitions,
                        membership_type,
                        membership_id,
                        cancellation,
                    )
                    .await;
                    if let Some(history) = fetched.as_ref()
                        && let Err(error) = storage::persist_inferred_raid_history(
                            &database,
                            membership_type,
                            membership_id,
                            history,
                        )
                        .await
                    {
                        tracing::warn!(
                            %error,
                            membership_type,
                            membership_id,
                            "could not persist inferred sherpa raid history"
                        );
                    }
                    fetched
                };
                (checks, history)
            }
        })
        .buffer_unordered(8);
    let mut counts = BTreeMap::<String, i32>::new();
    let mut processed = 0i64;
    loop {
        let next = tokio::select! {
            _ = cancellation.cancelled() => None,
            value = pending.next() => value,
        };
        let Some((checks, history)) = next else {
            break;
        };
        if let Some(history) = history {
            for check in checks {
                let candidate_had_prior_clear = history.iter().any(|completed| {
                    completed.name.eq_ignore_ascii_case(&check.raid_name)
                        && completed.instance_id != check.instance_id
                        && completed.period <= check.period
                });
                if !candidate_had_prior_clear {
                    *counts.entry(check.raid_name).or_default() += 1;
                }
            }
        }
        processed += 1;
        report_progress(
            progress,
            "sherpas",
            "Checking sherpa raid histories",
            processed,
            Some(total),
        );
    }
    counts.retain(|_, count| *count > 0);
    counts
}

fn select_linked_membership_type(linked: &Value, membership_id: i64) -> Option<i32> {
    linked
        .get("profiles")
        .and_then(Value::as_array)?
        .iter()
        .filter(|profile| profile.get("membershipId").and_then(id_i64) == Some(membership_id))
        .filter_map(|profile| {
            let membership_type = profile.get("membershipType")?.as_i64()? as i32;
            (membership_type > 0).then_some((
                membership_type,
                profile
                    .get("isCrossSavePrimary")
                    .and_then(Value::as_bool)
                    .unwrap_or(false),
                !profile
                    .get("isOverridden")
                    .and_then(Value::as_bool)
                    .unwrap_or(false),
                profile
                    .get("dateLastPlayed")
                    .and_then(Value::as_str)
                    .unwrap_or(""),
            ))
        })
        .max_by_key(|(_, primary, not_overridden, last_played)| {
            (*primary, *not_overridden, *last_played)
        })
        .map(|(membership_type, _, _, _)| membership_type)
}

async fn fetch_completed_raid_history(
    client: &BungieClient,
    activity_definitions: &BTreeMap<u32, Value>,
    membership_type: i32,
    membership_id: i64,
    cancellation: &CancellationToken,
) -> Option<Vec<CompletedRaid>> {
    let account = cancellable(
        cancellation,
        client.sherpa_account_stats(membership_type, membership_id),
    )
    .await
    .ok()?;
    let character_ids = account
        .get("characters")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|character| character.get("characterId").and_then(id_i64))
        .collect::<BTreeSet<_>>();
    let mut history = BTreeMap::<i64, CompletedRaid>::new();
    for character_id in character_ids {
        for page in 0..10_000u32 {
            let response = cancellable(
                cancellation,
                client.raid_history(membership_type, membership_id, character_id, page),
            )
            .await
            .ok()?;
            let activities = response
                .get("activities")
                .and_then(Value::as_array)
                .cloned()
                .unwrap_or_default();
            for activity in &activities {
                if stat_i64(activity, "completed") <= 0
                    || stat_i64(activity, "completionReason") != 0
                    || !includes_mode(
                        activity
                            .pointer("/activityDetails/mode")
                            .and_then(Value::as_i64)
                            .unwrap_or(0) as i32,
                        activity
                            .pointer("/activityDetails/modes")
                            .and_then(Value::as_array),
                        4,
                    )
                {
                    continue;
                }
                let Some(instance_id) = activity
                    .pointer("/activityDetails/instanceId")
                    .and_then(id_i64)
                else {
                    continue;
                };
                history.entry(instance_id).or_insert_with(|| CompletedRaid {
                    name: normalize_activity_name(&activity_name(activity, activity_definitions)),
                    period: activity_completed_at(
                        activity.get("period").and_then(Value::as_str).unwrap_or(""),
                        &[activity],
                    ),
                    instance_id,
                });
            }
            if activities.len() < 250 {
                break;
            }
        }
    }
    Some(history.into_values().collect())
}

async fn resolve_most_played_with(
    client: &BungieClient,
    counts: &BTreeMap<(i32, i64), i32>,
    cancellation: &CancellationToken,
) -> anyhow::Result<Vec<Value>> {
    let mut top = counts
        .iter()
        .filter(|((membership_type, membership_id), count)| {
            *membership_type > 0 && *membership_id > 0 && **count >= 2
        })
        .map(|(key, count)| (*key, *count))
        .collect::<Vec<_>>();
    top.sort_by_key(|(_, count)| std::cmp::Reverse(*count));
    top.truncate(10);
    let mut pending = stream::iter(top.into_iter().enumerate())
        .map(|(index, ((membership_type, membership_id), count))| {
            let client = client.clone();
            async move {
                let profile = client.profile_summary(membership_type, membership_id).await;
                (index, membership_type, membership_id, count, profile)
            }
        })
        .buffer_unordered(8);
    let mut resolved = Vec::new();
    loop {
        let next = tokio::select! {
            _ = cancellation.cancelled() => return Err(BungieError::Cancelled.into()),
            value = pending.next() => value,
        };
        let Some((index, membership_type, membership_id, count, profile)) = next else {
            break;
        };
        let profile = match profile {
            Ok(profile) => Some(profile),
            Err(BungieError::NotFound(_) | BungieError::Private(_)) => None,
            Err(error) => return Err(error.into()),
        };
        let user = profile
            .as_ref()
            .and_then(|value| value.pointer("/profile/data/userInfo"));
        let display_name = user
            .and_then(|value| value.get("bungieGlobalDisplayName"))
            .and_then(Value::as_str)
            .or_else(|| {
                user.and_then(|value| value.get("displayName"))
                    .and_then(Value::as_str)
            })
            .unwrap_or("");
        let emblem_path = profile
            .as_ref()
            .and_then(|value| value.pointer("/characters/data"))
            .and_then(Value::as_object)
            .into_iter()
            .flat_map(|characters| characters.values())
            .max_by_key(|character| {
                character
                    .get("dateLastPlayed")
                    .and_then(Value::as_str)
                    .unwrap_or("")
            })
            .and_then(|character| character.get("emblemPath"))
            .and_then(Value::as_str);
        resolved.push((
            index,
            json!({
                "player": {
                    "membershipId": membership_id,
                    "membershipType": membership_type,
                    "displayName": display_name,
                    "emblemUrl": bungie_url(emblem_path)
                },
                "encounterCount": count
            }),
        ));
    }
    resolved.sort_by_key(|(index, _)| *index);
    Ok(resolved.into_iter().map(|(_, value)| value).collect())
}

fn resolve_most_used_emblems(manifest: &ManifestStore, seconds: &BTreeMap<u32, i64>) -> Vec<Value> {
    let mut top = seconds.iter().collect::<Vec<_>>();
    top.sort_by_key(|(_, seconds)| std::cmp::Reverse(**seconds));
    top.truncate(10);
    top.into_iter()
        .map(|(hash, seconds)| {
            let definition = manifest
                .definition("DestinyInventoryItemDefinition", *hash)
                .ok()
                .flatten();
            json!({
                "name": definition.as_ref()
                    .and_then(|value| value.pointer("/displayProperties/name"))
                    .and_then(Value::as_str)
                    .map(str::to_owned)
                    .unwrap_or_else(|| hash.to_string()),
                "iconUrl": bungie_url(definition.as_ref()
                    .and_then(|value| value.pointer("/displayProperties/icon"))
                    .and_then(Value::as_str)),
                "backgroundUrl": bungie_url(definition.as_ref()
                    .and_then(|value| value.get("secondaryIcon"))
                    .and_then(Value::as_str)),
                "totalPlaytime": timespan(*seconds)
            })
        })
        .collect()
}

fn manifest_display_name(
    manifest: &ManifestStore,
    table: &str,
    hash: u32,
) -> anyhow::Result<Option<String>> {
    Ok(manifest.definition(table, hash)?.and_then(|value| {
        value
            .pointer("/displayProperties/name")
            .and_then(Value::as_str)
            .map(str::to_owned)
    }))
}

fn bungie_url(path: Option<&str>) -> String {
    match path.filter(|value| !value.is_empty()) {
        Some(value) if value.starts_with("http") => value.to_owned(),
        Some(value) => format!("https://www.bungie.net{value}"),
        None => String::new(),
    }
}

fn bson_datetime_string(value: mongodb::bson::DateTime) -> String {
    chrono::DateTime::from_timestamp_millis(value.timestamp_millis())
        .unwrap_or_default()
        .to_rfc3339()
}

fn includes_mode(mode: i32, modes: Option<&Vec<Value>>, expected: i32) -> bool {
    mode == expected
        || modes.is_some_and(|modes| {
            modes
                .iter()
                .any(|value| value.as_i64() == Some(i64::from(expected)))
        })
}

fn is_pvp_activity(mode: i32, modes: Option<&Vec<Value>>) -> bool {
    includes_mode(mode, modes, 5) || is_private_match_activity(mode, modes)
}

fn is_private_match_activity(mode: i32, modes: Option<&Vec<Value>>) -> bool {
    includes_mode(mode, modes, 32)
}

fn gambit_mote_mode(mode: i32, modes: Option<&Vec<Value>>) -> i32 {
    match mode {
        63 | 64 | 75 => mode,
        _ if includes_mode(mode, modes, 75) => 75,
        _ if includes_mode(mode, modes, 63) => 63,
        _ if includes_mode(mode, modes, 64) => 64,
        _ => mode,
    }
}

fn mode_group_from_flags(is_pvp: bool, is_gambit: bool) -> &'static str {
    if is_pvp {
        "Crucible"
    } else if is_gambit {
        "Gambit"
    } else {
        "PvE"
    }
}

fn extended_stat_i64(value: &Value, name: &str) -> i64 {
    value
        .pointer(&format!("/extended/values/{name}/basic/value"))
        .or_else(|| value.pointer(&format!("/extended/scoreboardValues/{name}/basic/value")))
        .and_then(Value::as_f64)
        .unwrap_or(0.0) as i64
}

fn weapon_kill_deltas(entry: &Value) -> BTreeMap<i64, i32> {
    let mut deltas = BTreeMap::new();
    let mut attributed = 0i64;
    if let Some(weapons) = entry.pointer("/extended/weapons").and_then(Value::as_array) {
        for weapon in weapons {
            let Some(hash) = weapon.get("referenceId").and_then(id_u32) else {
                continue;
            };
            let unique = stat_i64(weapon, "uniqueWeaponKills");
            let fallback = if unique <= 0 {
                stat_i64(weapon, "kills")
            } else {
                0
            };
            let kills = unique + fallback;
            attributed += kills;
            if kills > 0 {
                *deltas.entry(i64::from(hash)).or_default() += kills as i32;
            }
        }
    }
    for (hash, stat) in [
        (-1_i64, "weaponKillsGrenade"),
        (-2_i64, "weaponKillsMelee"),
        (-3_i64, "weaponKillsSuper"),
    ] {
        let kills = extended_stat_i64(entry, stat);
        attributed += kills;
        if kills > 0 {
            *deltas.entry(hash).or_default() += kills as i32;
        }
    }
    let unknown = stat_i64(entry, "kills") - attributed;
    if unknown > 0 {
        deltas.insert(-4, unknown as i32);
    }
    deltas
}

fn historical_character_classes(account: &Value) -> BTreeMap<i64, String> {
    account
        .get("characters")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|character| {
            let id = character.get("characterId").and_then(id_i64)?;
            let class = ["characterClass", "className", "class", "classType"]
                .into_iter()
                .find_map(|key| {
                    let value = character.get(key)?;
                    let class = value
                        .as_i64()
                        .map(|value| class_name(value as i32))
                        .or_else(|| value.as_str().map(normalize_class_name))
                        .unwrap_or("Unknown");
                    (class != "Unknown").then_some(class)
                })
                .unwrap_or("Unknown");
            Some((id, class.to_owned()))
        })
        .collect()
}

fn pgcr_character_class(entry: &Value, manifest: &ManifestStore) -> String {
    let reported = entry
        .pointer("/player/characterClass")
        .and_then(Value::as_str)
        .map(normalize_class_name)
        .unwrap_or("Unknown");
    if reported != "Unknown" {
        return reported.to_owned();
    }
    entry
        .pointer("/player/classHash")
        .and_then(id_u32)
        .and_then(|hash| {
            manifest_display_name(manifest, "DestinyClassDefinition", hash)
                .ok()
                .flatten()
        })
        .map(|name| normalize_class_name(&name).to_owned())
        .filter(|name| name != "Unknown")
        .unwrap_or_else(|| "Unknown".to_owned())
}

fn normalize_class_name(value: &str) -> &'static str {
    if value.eq_ignore_ascii_case("Titan") {
        "Titan"
    } else if value.eq_ignore_ascii_case("Hunter") {
        "Hunter"
    } else if value.eq_ignore_ascii_case("Warlock") {
        "Warlock"
    } else {
        "Unknown"
    }
}

fn normalize_race_name(value: &str) -> &'static str {
    if value.eq_ignore_ascii_case("Human") {
        "Human"
    } else if value.eq_ignore_ascii_case("Awoken") {
        "Awoken"
    } else if value.eq_ignore_ascii_case("Exo") {
        "Exo"
    } else {
        "Unknown"
    }
}

fn first_non_blank<'a>(preferred: Option<&'a str>, fallback: Option<&'a str>) -> &'a str {
    preferred
        .filter(|value| !value.trim().is_empty())
        .or(fallback)
        .unwrap_or("")
}

fn is_countable_encounter(membership_type: i32, membership_id: i64) -> bool {
    (1..=i32::from(u8::MAX)).contains(&membership_type) && membership_id > 0
}

fn mote_stat(value: &Value, name: &str) -> i32 {
    let direct = stat_i64(value, name);
    if direct > 0 {
        direct as i32
    } else {
        extended_stat_i64(value, name) as i32
    }
}

fn mode_name(mode: i32) -> &'static str {
    match mode {
        0 => "None",
        2 => "Story",
        3 => "Strike",
        4 => "Raid",
        5 => "AllPvP",
        6 => "Patrol",
        7 => "AllPvE",
        10 => "Control",
        12 => "Clash",
        15 => "CrimsonDoubles",
        16 => "Nightfall",
        17 => "HeroicNightfall",
        18 => "AllStrikes",
        19 => "IronBanner",
        25 => "AllMayhem",
        31 => "Supremacy",
        32 => "PrivateMatchesAll",
        37 => "Survival",
        38 => "Countdown",
        39 => "TrialsOfTheNine",
        40 => "Social",
        41 => "TrialsCountdown",
        42 => "TrialsSurvival",
        43 => "IronBannerControl",
        44 => "IronBannerClash",
        45 => "IronBannerSupremacy",
        46 => "ScoredNightfall",
        47 => "ScoredHeroicNightfall",
        48 => "Rumble",
        49 => "AllDoubles",
        50 => "Doubles",
        51 => "PrivateMatchesClash",
        52 => "PrivateMatchesControl",
        53 => "PrivateMatchesSupremacy",
        54 => "PrivateMatchesCountdown",
        55 => "PrivateMatchesSurvival",
        56 => "PrivateMatchesMayhem",
        57 => "PrivateMatchesRumble",
        58 => "HeroicAdventure",
        59 => "Showdown",
        60 => "Lockdown",
        61 => "Scorched",
        62 => "ScorchedTeam",
        63 => "Gambit",
        64 => "AllPvECompetitive",
        65 => "Breakthrough",
        66 => "BlackArmoryRun",
        67 => "Salvage",
        68 => "IronBannerSalvage",
        69 => "PvPCompetitive",
        70 => "PvPQuickplay",
        71 => "ClashQuickplay",
        72 => "ClashCompetitive",
        73 => "ControlQuickplay",
        74 => "ControlCompetitive",
        75 => "GambitPrime",
        76 => "Reckoning",
        77 => "Menagerie",
        78 => "VexOffensive",
        79 => "NightmareHunt",
        80 => "Elimination",
        81 => "Momentum",
        82 => "Dungeon",
        83 => "Sundial",
        84 => "TrialsOfOsiris",
        85 => "Dares",
        86 => "Offensive",
        87 => "LostSector",
        88 => "Rift",
        89 => "ZoneControl",
        90 => "IronBannerRift",
        91 => "IronBannerZoneControl",
        92 => "Relic",
        93 => "LawlessFrontier",
        94 => "SparrowRacingLeague",
        _ => "Unknown",
    }
}

fn activity_mode_name(mode: i32, definitions: &BTreeMap<i32, String>) -> String {
    definitions.get(&mode).cloned().unwrap_or_else(|| {
        let legacy = mode_name(mode);
        if legacy == "Unknown" {
            format!("Mode {mode}")
        } else {
            legacy.to_owned()
        }
    })
}

fn named_mode_totals_i32(
    values: &BTreeMap<i32, i32>,
    definitions: &BTreeMap<i32, String>,
) -> BTreeMap<String, i32> {
    let mut totals = BTreeMap::new();
    for (mode, value) in values {
        let name = activity_mode_name(*mode, definitions);
        *totals.entry(name).or_default() += *value;
    }
    totals
}

fn numeric_mode_map(values: &BTreeMap<i32, i32>) -> BTreeMap<String, i32> {
    values
        .iter()
        .map(|(key, value)| (key.to_string(), *value))
        .collect()
}

fn numeric_mode_map_i64(values: &BTreeMap<i32, i64>) -> BTreeMap<String, i64> {
    values
        .iter()
        .map(|(key, value)| (key.to_string(), *value))
        .collect()
}

fn pvp_playlist_state(values: &BTreeMap<i32, (i32, i32)>) -> BTreeMap<String, Value> {
    values
        .iter()
        .map(|(mode, (wins, losses))| (mode.to_string(), json!({ "wins": wins, "losses": losses })))
        .collect()
}

fn timespan_seconds(value: &str) -> i64 {
    let (days, clock) = value
        .split_once('.')
        .map(|(days, clock)| (days.parse::<i64>().unwrap_or(0), clock))
        .unwrap_or((0, value));
    let mut parts = clock
        .split(':')
        .map(|part| part.parse::<i64>().unwrap_or(0));
    days * 86_400
        + parts.next().unwrap_or(0) * 3_600
        + parts.next().unwrap_or(0) * 60
        + parts.next().unwrap_or(0)
}

fn stat_i64(value: &Value, name: &str) -> i64 {
    value
        .pointer(&format!("/values/{name}/basic/value"))
        .and_then(Value::as_f64)
        .unwrap_or(0.0) as i64
}

fn preferred_playtime(value: &Value) -> i64 {
    let played = stat_i64(value, "timePlayedSeconds");
    if played > 0 {
        played
    } else {
        stat_i64(value, "activityDurationSeconds")
    }
}

fn historical_stat(response: &Value, mode: i32, name: &str) -> f64 {
    let preferred_keys: &[&str] = match mode {
        5 => &["allPvP", "allTime"],
        7 => &["allPvE", "allTime"],
        63 => &["gambit", "allTime"],
        75 => &["gambitPrime", "allTime"],
        _ => &["allTime"],
    };
    let value = |bucket: &Value| {
        bucket
            .pointer(&format!("/allTime/{name}/basic/value"))
            .and_then(Value::as_f64)
    };
    preferred_keys
        .iter()
        .find_map(|key| response.get(*key).and_then(value))
        .or_else(|| {
            response
                .as_object()
                .into_iter()
                .flat_map(|values| values.values())
                .find_map(value)
        })
        .unwrap_or(0.0)
}

fn ratio(numerator: f64, denominator: f64) -> f64 {
    if denominator > 0.0 {
        round3(numerator / denominator)
    } else {
        0.0
    }
}

fn round3(value: f64) -> f64 {
    (value * 1_000.0).round() / 1_000.0
}

fn average_mode_kda(value: Option<&ModeTotals>) -> f64 {
    value
        .filter(|value| !value.kda_values.is_empty())
        .map(|value| average_values(&value.kda_values))
        .unwrap_or(0.0)
}

fn mode_kd(value: Option<&ModeTotals>) -> f64 {
    value
        .map(|value| average_values(&value.kd_values))
        .unwrap_or(0.0)
}

fn average_values(values: &[f64]) -> f64 {
    if values.is_empty() {
        0.0
    } else {
        round3(values.iter().sum::<f64>() / values.len() as f64)
    }
}

fn playlist(values: &std::collections::BTreeMap<i32, ModeTotals>, mode: i32, name: &str) -> Value {
    let value = values.get(&mode);
    let wins = value.map(|value| value.wins).unwrap_or(0);
    let entered = value.map(|value| value.entered).unwrap_or(0);
    json!({ "mode": mode, "modeName": name, "wins": wins, "losses": (entered - wins).max(0) })
}

fn id_i64(value: &Value) -> Option<i64> {
    value.as_i64().or_else(|| value.as_str()?.parse().ok())
}

fn number_i64(value: &Value) -> Option<i64> {
    value.as_i64().or_else(|| value.as_str()?.parse().ok())
}

fn id_u32(value: &Value) -> Option<u32> {
    value
        .as_u64()
        .and_then(|value| u32::try_from(value).ok())
        .or_else(|| value.as_i64().and_then(signed_destiny_hash))
        .or_else(|| {
            let value = value.as_str()?;
            value
                .parse::<u32>()
                .ok()
                .or_else(|| value.parse::<i64>().ok().and_then(signed_destiny_hash))
        })
}

fn signed_destiny_hash(value: i64) -> Option<u32> {
    i32::try_from(value).ok().map(|value| value as u32)
}

fn class_name(value: i32) -> &'static str {
    match value {
        0 => "Titan",
        1 => "Hunter",
        2 => "Warlock",
        _ => "Unknown",
    }
}

fn race_name(value: i32) -> &'static str {
    match value {
        0 => "Human",
        1 => "Awoken",
        2 => "Exo",
        _ => "Unknown",
    }
}

fn timespan(seconds: i64) -> String {
    let days = seconds / 86_400;
    let rest = seconds % 86_400;
    let value = format!(
        "{:02}:{:02}:{:02}",
        rest / 3600,
        (rest % 3600) / 60,
        rest % 60
    );
    if days > 0 {
        format!("{days}.{value}")
    } else {
        value
    }
}

fn playtime_streak(play_dates: &BTreeSet<String>, current_only: bool) -> Value {
    let dates = play_dates
        .iter()
        .filter_map(|value| chrono::NaiveDate::parse_from_str(value, "%Y-%m-%d").ok())
        .collect::<Vec<_>>();
    let Some(&first) = dates.first() else {
        return Value::Null;
    };

    if current_only {
        let today = chrono::Utc::now().date_naive();
        let Some(&last) = dates.last() else {
            return Value::Null;
        };
        if last < today - chrono::Days::new(1) {
            return Value::Null;
        }
        let mut start = last;
        for &date in dates.iter().rev().skip(1) {
            if date == start - chrono::Days::new(1) {
                start = date;
            } else {
                break;
            }
        }
        return json!({
            "startDate": format!("{}T00:00:00Z", start.format("%Y-%m-%d")),
            "endDate": format!("{}T00:00:00Z", last.format("%Y-%m-%d"))
        });
    }

    let mut longest_start = first;
    let mut longest_end = first;
    let mut run_start = first;
    for pair in dates.windows(2) {
        if pair[1] != pair[0] + chrono::Days::new(1) {
            run_start = pair[1];
        }
        if (pair[1] - run_start).num_days() > (longest_end - longest_start).num_days() {
            longest_start = run_start;
            longest_end = pair[1];
        }
    }
    json!({
        "startDate": format!("{}T00:00:00Z", longest_start.format("%Y-%m-%d")),
        "endDate": format!("{}T00:00:00Z", longest_end.format("%Y-%m-%d"))
    })
}

fn encode_encounters(values: &BTreeSet<(i32, i64)>) -> Vec<u8> {
    // This is the existing C# accumulator contract. Compact generation storage
    // compresses the payload; materialization must still recreate these 9-byte keys.
    let mut output = Vec::with_capacity(values.len() * 9);
    for (membership_type, membership_id) in values {
        if !is_countable_encounter(*membership_type, *membership_id) {
            continue;
        }
        output.push(*membership_type as u8);
        output.extend_from_slice(&membership_id.to_le_bytes());
    }
    output
}

fn base64_encode(bytes: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut output = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let n = ((chunk[0] as u32) << 16)
            | ((chunk.get(1).copied().unwrap_or(0) as u32) << 8)
            | chunk.get(2).copied().unwrap_or(0) as u32;
        output.push(TABLE[((n >> 18) & 63) as usize] as char);
        output.push(TABLE[((n >> 12) & 63) as usize] as char);
        output.push(if chunk.len() > 1 {
            TABLE[((n >> 6) & 63) as usize] as char
        } else {
            '='
        });
        output.push(if chunk.len() > 2 {
            TABLE[(n & 63) as usize] as char
        } else {
            '='
        });
    }
    output
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_accept_lossless_strings_and_numeric_boundaries() {
        assert_eq!(id_i64(&json!(i64::MAX)), Some(i64::MAX));
        assert_eq!(id_i64(&json!(i64::MAX.to_string())), Some(i64::MAX));
        assert_eq!(id_u32(&json!(u32::MAX)), Some(u32::MAX));
        assert_eq!(id_u32(&json!(u32::MAX.to_string())), Some(u32::MAX));
        assert_eq!(id_u32(&json!(-1)), Some(u32::MAX));
        assert_eq!(id_u32(&json!("-1")), Some(u32::MAX));
        assert_eq!(
            id_u32(&json!(i32::MIN)),
            Some(i32::MIN as u32),
            "signed int32 Destiny hashes retain their bit pattern"
        );
        assert_eq!(id_u32(&json!(u64::from(u32::MAX) + 1)), None);
    }

    #[test]
    fn encountered_keys_use_compact_nine_byte_contract() {
        let values = BTreeSet::from([(3, 0x0102_0304_0506_0708)]);
        assert_eq!(encode_encounters(&values), [3, 8, 7, 6, 5, 4, 3, 2, 1]);
    }

    #[test]
    fn historical_stats_use_documented_dictionary_bucket() {
        let response =
            json!({ "allPvP": { "allTime": { "kills": { "basic": { "value": 42.0 } } } } });
        assert_eq!(historical_stat(&response, 5, "kills"), 42.0);
    }

    #[test]
    fn historical_stats_prefer_the_mode_specific_bucket() {
        let response = json!({
            "allTime": { "allTime": { "kills": { "basic": { "value": 999.0 } } } },
            "gambit": { "allTime": { "kills": { "basic": { "value": 42.0 } } } }
        });
        assert_eq!(historical_stat(&response, 63, "kills"), 42.0);
    }

    #[test]
    fn owner_entries_require_a_compatible_membership_type() {
        let matching = json!({ "player": { "destinyUserInfo": {
            "membershipId": "42", "membershipType": 3
        }}});
        let unknown_type = json!({ "player": { "destinyUserInfo": {
            "membershipId": "42", "membershipType": 0
        }}});
        let other_type = json!({ "player": { "destinyUserInfo": {
            "membershipId": "42", "membershipType": 2
        }}});
        assert!(is_owner_entry(&matching, 3, 42));
        assert!(is_owner_entry(&unknown_type, 3, 42));
        assert!(!is_owner_entry(&other_type, 3, 42));
    }

    #[test]
    fn gambit_mote_mode_prefers_the_primary_mode() {
        let modes = vec![json!(7), json!(64), json!(63), json!(75)];
        assert_eq!(gambit_mote_mode(63, Some(&modes)), 63);
        assert_eq!(gambit_mote_mode(75, Some(&modes)), 75);
        assert_eq!(gambit_mote_mode(0, Some(&modes)), 75);
    }

    #[test]
    fn preferred_playtime_uses_activity_duration_fallback() {
        assert_eq!(
            preferred_playtime(&json!({ "values": {
                "timePlayedSeconds": { "basic": { "value": 10.0 } },
                "activityDurationSeconds": { "basic": { "value": 99.0 } }
            }})),
            10
        );
        assert_eq!(
            preferred_playtime(&json!({ "values": {
                "activityDurationSeconds": { "basic": { "value": 99.0 } }
            }})),
            99
        );
    }

    #[test]
    fn weapon_breakdown_preserves_fallback_and_unattributed_kills() {
        let entry = json!({
            "values": { "kills": { "basic": { "value": 25.0 } } },
            "extended": {
                "weapons": [
                    {
                        "referenceId": 100,
                        "values": {
                            "uniqueWeaponKills": { "basic": { "value": 10.0 } },
                            "kills": { "basic": { "value": 99.0 } }
                        }
                    },
                    {
                        "referenceId": 200,
                        "values": {
                            "uniqueWeaponKills": { "basic": { "value": 0.0 } },
                            "kills": { "basic": { "value": 4.0 } }
                        }
                    }
                ],
                "values": {
                    "weaponKillsGrenade": { "basic": { "value": 3.0 } },
                    "weaponKillsMelee": { "basic": { "value": 2.0 } },
                    "weaponKillsSuper": { "basic": { "value": 1.0 } }
                }
            }
        });

        let deltas = weapon_kill_deltas(&entry);
        assert_eq!(deltas.get(&100), Some(&10));
        assert_eq!(deltas.get(&200), Some(&4));
        assert_eq!(deltas.get(&-1), Some(&3));
        assert_eq!(deltas.get(&-2), Some(&2));
        assert_eq!(deltas.get(&-3), Some(&1));
        assert_eq!(deltas.get(&-4), Some(&5));
    }

    #[test]
    fn historical_character_class_accepts_names_and_numeric_types() {
        let account = json!({ "characters": [
            { "characterId": "10", "characterClass": "Hunter" },
            { "characterId": "20", "classType": 2 }
        ] });

        let classes = historical_character_classes(&account);
        assert_eq!(classes.get(&10).map(String::as_str), Some("Hunter"));
        assert_eq!(classes.get(&20).map(String::as_str), Some("Warlock"));
    }

    #[test]
    fn historical_character_class_skips_unrecognized_earlier_fields() {
        let account = json!({ "characters": [{
            "characterId": "20",
            "characterClass": "",
            "classType": 2
        }] });

        let classes = historical_character_classes(&account);

        assert_eq!(classes.get(&20).map(String::as_str), Some("Warlock"));
    }

    #[test]
    fn pgcr_character_class_falls_back_to_the_class_manifest() {
        let path = std::env::temp_dir().join(format!(
            "destiny2report-class-manifest-{}.sqlite",
            uuid::Uuid::new_v4().simple()
        ));
        let connection = rusqlite::Connection::open(&path).unwrap();
        connection
            .execute(
                "CREATE TABLE DestinyClassDefinition (id INTEGER PRIMARY KEY, json TEXT NOT NULL)",
                [],
            )
            .unwrap();
        connection
            .execute(
                "INSERT INTO DestinyClassDefinition (id, json) VALUES (?1, ?2)",
                rusqlite::params![123_i32, r#"{"displayProperties":{"name":"Warlock"}}"#],
            )
            .unwrap();
        drop(connection);
        let manifest = ManifestStore::new(&path);
        let entry = json!({ "player": { "characterClass": "", "classHash": 123 } });

        assert_eq!(pgcr_character_class(&entry, &manifest), "Warlock");

        std::fs::remove_file(path).unwrap();
    }

    #[test]
    fn completion_and_sherpa_state_merge_names_case_insensitively() {
        let completions = BTreeMap::from([
            (
                "King's Fall".to_owned(),
                CompletionAggregate {
                    activity_count: 2,
                    completion_count: 1,
                    first_completion: Some(("2024-01-02T00:00:00Z".into(), 2)),
                    ..CompletionAggregate::default()
                },
            ),
            (
                "KING'S FALL".to_owned(),
                CompletionAggregate {
                    activity_count: 3,
                    completion_count: 2,
                    first_completion: Some(("2024-01-01T00:00:00Z".into(), 1)),
                    flawless_clear: true,
                    ..CompletionAggregate::default()
                },
            ),
        ]);

        let completions = normalize_completion_map(completions);
        let completion = completions.values().next().unwrap();
        assert_eq!(completions.len(), 1);
        assert_eq!(completion.activity_count, 5);
        assert_eq!(completion.completion_count, 3);
        assert_eq!(completion.first_completion.as_ref().unwrap().1, 1);
        assert!(completion.flawless_clear);

        let sherpas = normalize_case_insensitive_counts(BTreeMap::from([
            ("Root of Nightmares".to_owned(), 2),
            ("ROOT OF NIGHTMARES".to_owned(), 3),
        ]));
        assert_eq!(sherpas.len(), 1);
        assert_eq!(sherpas.values().next(), Some(&5));
    }

    #[test]
    fn activity_mode_names_use_manifest_and_lossless_fallbacks() {
        let definitions = BTreeMap::from([(95, "New Crucible Mode".to_owned())]);
        assert_eq!(activity_mode_name(95, &definitions), "New Crucible Mode");
        assert_eq!(activity_mode_name(96, &definitions), "Mode 96");
        assert_eq!(
            named_mode_totals_i32(&BTreeMap::from([(95, 2), (96, 3)]), &definitions),
            BTreeMap::from([
                ("Mode 96".to_owned(), 3),
                ("New Crucible Mode".to_owned(), 2)
            ])
        );
    }

    #[test]
    fn incremental_overlap_is_eight_hours() {
        assert_eq!(INCREMENTAL_CRAWL_OVERLAP_HOURS, 8);
        assert_eq!(RECENT_ACTIVITY_INSTANCE_ID_LIMIT, 500);
    }

    #[test]
    fn linked_profile_resolution_prefers_cross_save_primary() {
        let linked = json!({ "profiles": [
            {
                "membershipId": "42",
                "membershipType": 1,
                "isCrossSavePrimary": false,
                "isOverridden": false,
                "dateLastPlayed": "2026-01-01T00:00:00Z"
            },
            {
                "membershipId": "42",
                "membershipType": 3,
                "isCrossSavePrimary": true,
                "isOverridden": false,
                "dateLastPlayed": "2025-01-01T00:00:00Z"
            }
        ] });

        assert_eq!(select_linked_membership_type(&linked, 42), Some(3));
    }

    #[test]
    fn activity_names_use_legacy_suffix_normalization() {
        assert_eq!(
            normalize_activity_name("Crota's End: Master"),
            "Crota's End"
        );
        assert_eq!(
            normalize_activity_name("Pantheon: Calus Resplendent: Customize"),
            "Pantheon: Calus Resplendent"
        );
        assert_eq!(
            normalize_activity_name("Prophecy: Eternity"),
            "Prophecy: Eternity"
        );
    }

    #[test]
    fn conquest_names_switch_at_the_renegades_release() {
        let pgcr = json!({ "activityDetails": { "directorActivityHash": 123_652_462 } });
        assert_eq!(
            conquest_name(&pgcr, "ignored", "2025-12-01T00:00:00Z").as_deref(),
            Some("Ultimate Conquest: Hypernet")
        );
        assert_eq!(
            conquest_name(&pgcr, "ignored", "2025-12-03T00:00:00Z").as_deref(),
            Some("Ultimate Conquest: Lightblade")
        );
    }

    #[test]
    fn post_haunted_activity_requires_reported_start() {
        let started = json!({
            "period": "2026-01-01T00:00:00Z",
            "startingPhaseIndex": 2,
            "activityWasStartedFromBeginning": true
        });
        let joined_late = json!({
            "period": "2026-01-01T00:00:00Z",
            "startingPhaseIndex": 2,
            "activityWasStartedFromBeginning": false
        });
        let entries = vec![&started];

        assert!(activity_started_from_beginning(&started, &entries));
        assert!(!activity_started_from_beginning(&joined_late, &entries));
    }

    #[test]
    fn contest_clear_is_limited_to_the_activity_window() {
        let root = json!({ "activityDetails": { "referenceId": 2_381_413_764u64 } });

        assert!(is_contest_clear(&root, true, false, "2023-03-11T13:00:00Z",));
        assert!(!is_contest_clear(
            &root,
            true,
            false,
            "2023-03-13T12:00:00Z",
        ));
    }

    #[test]
    fn completion_timestamp_uses_longest_owner_duration() {
        let first = json!({ "values": {
            "activityDurationSeconds": { "basic": { "value": 600.0 } }
        }});
        let second = json!({ "values": {
            "activityDurationSeconds": { "basic": { "value": 900.0 } }
        }});
        assert_eq!(
            activity_completed_at("2024-01-01T00:00:00Z", &[&first, &second]),
            "2024-01-01T00:15:00Z"
        );
    }

    #[test]
    fn completion_aggregates_compare_completed_at_not_start_time() {
        let mut values = BTreeMap::new();
        add_completion(
            &mut values,
            "Raid".into(),
            true,
            "2024-01-01T00:20:00Z",
            2,
            600,
            false,
            false,
            false,
        );
        add_completion(
            &mut values,
            "Raid".into(),
            true,
            "2024-01-01T00:10:00Z",
            1,
            900,
            false,
            false,
            false,
        );
        assert_eq!(values["Raid"].first_completion.as_ref().unwrap().1, 1);
        assert_eq!(values["Raid"].last_completion.as_ref().unwrap().1, 2);
    }

    #[test]
    fn triumph_records_restore_historical_activity_flags() {
        let profile = json!({ "profileRecords": { "data": { "records": {
            (380_332_968u32.to_string()): { "completedCount": 1 },
            ((3_899_996_566u32 as i32).to_string()): { "state": 0 },
            ((3_205_009_787u32 as i32).to_string()): { "completedCount": 1 }
        }}}});
        let mut raids = BTreeMap::from([("Last Wish".into(), CompletionAggregate::default())]);
        let mut dungeons =
            BTreeMap::from([("Shattered Throne".into(), CompletionAggregate::default())]);

        apply_activity_triumph_records(&profile, &mut raids, &mut dungeons);

        assert!(raids["Last Wish"].flawless_clear);
        let dungeon = &dungeons["Shattered Throne"];
        assert!(dungeon.solo_clear);
        assert!(dungeon.flawless_clear);
        assert!(dungeon.solo_flawless_clear);
    }

    #[test]
    fn private_crucible_does_not_change_playlist_results() {
        let winner = json!({ "standing": 0 });
        let mut playlists = BTreeMap::new();
        add_pvp_playlist_result(&mut playlists, 31, &[&winner], true);
        assert!(playlists.is_empty());
        add_pvp_playlist_result(&mut playlists, 31, &[&winner], false);
        assert_eq!(playlists[&31], (1, 0));
    }

    #[test]
    fn private_matches_all_is_both_pvp_and_private_without_all_pvp_marker() {
        assert!(is_pvp_activity(32, None));
        assert!(is_private_match_activity(32, None));

        let modes = vec![json!(31), json!(32)];
        assert!(is_pvp_activity(31, Some(&modes)));
        assert!(is_private_match_activity(31, Some(&modes)));
    }

    #[test]
    fn pvp_playlists_are_ordered_by_matches_then_mode() {
        let playlists = BTreeMap::from([(70, (2, 1)), (12, (1, 4)), (10, (3, 2))]);
        let reports = build_pvp_playlist_reports(&playlists, &BTreeMap::new());
        let modes = reports
            .iter()
            .map(|report| report["mode"].as_i64().unwrap())
            .collect::<Vec<_>>();

        assert_eq!(modes, vec![10, 12, 70]);
    }

    #[test]
    fn zero_deaths_do_not_create_aggregate_rows() {
        let mut deaths = BTreeMap::new();
        add_deaths_by_mode(&mut deaths, "PvE", 3, 0);
        assert!(deaths.is_empty());

        add_deaths_by_mode(&mut deaths, "PvE", 3, 2);
        add_deaths_by_mode(&mut deaths, "PvE", 3, 3);
        assert_eq!(deaths[&("PvE".to_owned(), 3)], 5);
    }

    #[test]
    fn kd_averages_bungie_ratios_like_kda() {
        assert_eq!(average_values(&[7.25, 8.75]), 8.0);
        assert_eq!(average_values(&[1.0, 3.0]), 2.0);
    }

    #[test]
    fn privacy_is_terminal_at_every_profile_data_endpoint() {
        assert!(is_private_error(&BungieError::Private(None)));
        assert!(!is_private_error(&BungieError::NotFound(None)));
    }

    #[test]
    fn sherpa_membership_resolution_uses_bungie_next_for_known_membership_ids() {
        assert_eq!(BUNGIE_NEXT_MEMBERSHIP_TYPE, 254);
    }

    #[test]
    fn persisted_deleted_character_identity_is_reused_and_enriched() {
        let mut identities = BTreeMap::from([(42, ("Hunter".to_owned(), "Unknown".to_owned()))]);
        merge_character_identity(&mut identities, 42, "Unknown", "Awoken");
        assert_eq!(identities[&42], ("Hunter".to_owned(), "Awoken".to_owned()));

        let account = json!({ "characters": [{
            "characterId": "42",
            "merged": { "allTime": {
                "secondsPlayed": { "basic": { "value": 3600.0 } }
            }}
        }]});
        let mut output = Vec::new();
        let historical_classes = BTreeMap::from([(42, "Hunter".to_owned())]);
        append_deleted_character_playtime(
            &mut output,
            &account,
            None,
            &historical_classes,
            &identities,
        );
        assert_eq!(output[0]["class"], "Hunter");
        assert_eq!(output[0]["race"], "Awoken");
    }

    #[test]
    fn pgcr_without_an_owner_entry_is_not_processed() {
        let entries = vec![json!({ "player": { "destinyUserInfo": {
            "membershipType": 3,
            "membershipId": "99"
        }}})];

        assert!(owner_entries(&entries, 3, 42).is_empty());
    }

    #[test]
    fn encounter_membership_type_must_fit_the_persisted_byte_contract() {
        assert!(is_countable_encounter(1, 42));
        assert!(is_countable_encounter(255, 42));
        assert!(!is_countable_encounter(0, 42));
        assert!(!is_countable_encounter(256, 42));
        assert!(!is_countable_encounter(3, 0));
    }

    #[test]
    fn unconfigured_conquest_names_are_not_counted() {
        let pgcr = json!({ "activityDetails": {
            "referenceId": 999,
            "directorActivityHash": 998
        }});

        assert_eq!(
            conquest_name(&pgcr, "An Unconfigured Conquest", "2026-01-01T00:00:00Z"),
            None
        );
    }

    #[test]
    fn seal_text_falls_back_when_the_record_value_is_blank() {
        assert_eq!(
            first_non_blank(Some("   "), Some("Presentation node description")),
            "Presentation node description"
        );
        assert_eq!(
            first_non_blank(Some("Record description"), Some("Fallback")),
            "Record description"
        );
    }
}
