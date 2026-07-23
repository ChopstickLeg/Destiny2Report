use std::collections::BTreeSet;

use futures::{StreamExt, stream};
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;

use crate::{
    bungie::{BungieClient, BungieError},
    models::CrawlJob,
};

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

#[derive(Default)]
struct ModeTotals {
    kills: f64,
    deaths: f64,
    kda_sum: f64,
    kda_count: u32,
    entered: i32,
    wins: i32,
}

pub async fn crawl(
    client: &BungieClient,
    job: &CrawlJob,
    cancellation: &CancellationToken,
) -> anyhow::Result<CrawlOutcome> {
    let profile = match cancellable(
        cancellation,
        client.profile(job.membership_type_id, job.membership_id),
    )
    .await
    {
        Ok(value) => value,
        Err(BungieError::Private) => {
            return Ok(CrawlOutcome::Private(empty_result(
                job,
                "private",
                "Destiny profile is not public.",
            )));
        }
        Err(BungieError::NotFound) => {
            return Ok(CrawlOutcome::NotFound(empty_result(
                job,
                "failed",
                "Destiny account not found.",
            )));
        }
        Err(error) => return Err(error.into()),
    };
    let account = cancellable(
        cancellation,
        client.account_stats(job.membership_type_id, job.membership_id),
    )
    .await?;
    let user = profile
        .pointer("/profile/data/userInfo")
        .unwrap_or(&Value::Null);
    let display_name = user
        .get("bungieGlobalDisplayName")
        .and_then(Value::as_str)
        .or_else(|| user.get("displayName").and_then(Value::as_str))
        .unwrap_or("");
    let display_code = user
        .get("bungieGlobalDisplayNameCode")
        .and_then(Value::as_i64)
        .unwrap_or(0);

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
    let character_playtime = profile_characters.into_iter().flat_map(|characters| characters.values()).map(|character| {
        json!({
            "characterId": character.get("characterId").and_then(number_i64).unwrap_or(0),
            "class": class_name(character.get("classType").and_then(Value::as_i64).unwrap_or(-1) as i32),
            "race": race_name(character.get("raceType").and_then(Value::as_i64).unwrap_or(-1) as i32),
            "isDeleted": false,
            "playtime": timespan(character.get("minutesPlayedTotal").and_then(number_i64).unwrap_or(0) * 60)
        })
    }).collect::<Vec<_>>();

    let character_ids = account
        .get("characters")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|character| character.get("characterId").and_then(id_i64))
        .collect::<Vec<_>>();
    let mut mode_totals = std::collections::BTreeMap::<i32, ModeTotals>::new();
    for character_id in &character_ids {
        for mode in [5, 63, 75] {
            let stats = cancellable(
                cancellation,
                client.historical_stats(
                    job.membership_type_id,
                    job.membership_id,
                    *character_id,
                    mode,
                ),
            )
            .await?;
            let totals = mode_totals.entry(mode).or_default();
            totals.kills += historical_stat(&stats, "kills");
            totals.deaths += historical_stat(&stats, "deaths");
            let kda = historical_stat(&stats, "killsDeathsAssists");
            if kda > 0.0 {
                totals.kda_sum += kda;
                totals.kda_count += 1;
            }
            totals.entered += historical_stat(&stats, "activitiesEntered") as i32;
            totals.wins += historical_stat(&stats, "activitiesWon") as i32;
        }
    }
    let mut activity_ids = BTreeSet::new();
    let mut newest_period: Option<String> = None;
    let mut earliest_period: Option<String> = None;
    for character_id in &character_ids {
        for page in 0..10_000u32 {
            let response = cancellable(
                cancellation,
                client.activity_history(
                    job.membership_type_id,
                    job.membership_id,
                    *character_id,
                    page,
                ),
            )
            .await?;
            let activities = response
                .get("activities")
                .and_then(Value::as_array)
                .cloned()
                .unwrap_or_default();
            for activity in &activities {
                if let Some(id) = activity
                    .pointer("/activityDetails/instanceId")
                    .and_then(id_i64)
                {
                    activity_ids.insert(id);
                }
                if let Some(period) = activity.get("period").and_then(Value::as_str) {
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
            }
            if activities.len() < 250 {
                break;
            }
        }
    }

    let parallelism = client.pgcr_parallelism();
    let mut pending = stream::iter(activity_ids.iter().copied())
        .map(|activity_id| {
            let client = client.clone();
            async move { (activity_id, client.pgcr(activity_id).await) }
        })
        .buffer_unordered(parallelism);

    let mut total_kills = 0i64;
    let mut total_activity_seconds = 0i64;
    let mut zero_kill_activities = 0i32;
    let mut play_dates = BTreeSet::new();
    let mut encountered = BTreeSet::new();
    let mut encounter_counts = std::collections::BTreeMap::<(i32, i64), i32>::new();
    let mut weapon_kills = std::collections::BTreeMap::<(String, i32, String, u32), i32>::new();
    let mut deaths_by_mode = std::collections::BTreeMap::<(String, i32), i64>::new();
    let mut emblem_seconds = std::collections::BTreeMap::<u32, i64>::new();
    let mut mode_seconds =
        std::collections::BTreeMap::<i32, std::collections::BTreeMap<i32, i64>>::new();
    loop {
        let next = tokio::select! {
            _ = cancellation.cancelled() => return Err(BungieError::Cancelled.into()),
            value = pending.next() => value,
        };
        let Some((_activity_id, pgcr)) = next else {
            break;
        };
        let pgcr = pgcr?;
        let entries = pgcr
            .get("entries")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        let owners = entries
            .iter()
            .filter(|entry| {
                entry
                    .pointer("/player/destinyUserInfo/membershipId")
                    .and_then(id_i64)
                    == Some(job.membership_id)
            })
            .collect::<Vec<_>>();
        if !owners.is_empty() {
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
            *deaths_by_mode
                .entry((mode_group(mode).into(), mode))
                .or_default() += deaths;
            if let Some(period) = pgcr.get("period").and_then(Value::as_str) {
                play_dates.insert(period.get(..10).unwrap_or(period).to_owned());
            }
            let modes = pgcr
                .pointer("/activityDetails/modes")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(|value| value.as_i64().and_then(|value| i32::try_from(value).ok()))
                .collect::<Vec<_>>();
            let specific = modes.last().copied().unwrap_or(mode);
            for broad in modes.iter().copied().filter(|value| is_broad_mode(*value)) {
                *mode_seconds
                    .entry(broad)
                    .or_default()
                    .entry(specific)
                    .or_default() += seconds;
            }
            for owner in owners {
                let class_name = owner
                    .pointer("/player/characterClass")
                    .and_then(Value::as_str)
                    .unwrap_or("Unknown")
                    .to_owned();
                if let Some(emblem) = owner.pointer("/player/emblemHash").and_then(id_u32) {
                    *emblem_seconds.entry(emblem).or_default() += preferred_playtime(owner);
                }
                if let Some(weapons) = owner.pointer("/extended/weapons").and_then(Value::as_array)
                {
                    for weapon in weapons {
                        let Some(hash) = weapon.get("referenceId").and_then(id_u32) else {
                            continue;
                        };
                        let kills = stat_i64(weapon, "uniqueWeaponKills") as i32;
                        *weapon_kills
                            .entry((mode_group(mode).into(), mode, class_name.clone(), hash))
                            .or_default() += kills;
                    }
                }
            }
        }
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
            if membership_type > 0 {
                encountered.insert((membership_type, id));
                *encounter_counts.entry((membership_type, id)).or_default() += 1;
            }
        }
    }

    let weapons = weapon_kills.into_iter().map(|((activity_mode, specific, class_name, hash), kills)| json!({
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
    let gambit_kills = [63, 75]
        .into_iter()
        .filter_map(|mode| mode_totals.get(&mode))
        .map(|value| value.kills)
        .sum::<f64>();
    let gambit_deaths = [63, 75]
        .into_iter()
        .filter_map(|mode| mode_totals.get(&mode))
        .map(|value| value.deaths)
        .sum::<f64>();
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
        .filter(|value| value.kda_count > 0)
        .map(|value| value.kda_sum / value.kda_count as f64)
        .collect::<Vec<_>>();
    let now = chrono::Utc::now().to_rfc3339();
    let report = json!({
        "platformId": job.membership_type_id,
        "playerMembershipId": job.membership_id,
        "displayName": display_name,
        "displayCode": display_code,
        "crawledAt": now.clone(),
        "firstActivityAtUtc": earliest_period,
        "crawlState": "completed",
        "queuedInRedis": false,
        "lastCrawledAtUtc": now.clone(),
        "hasCompletedCrawl": false,
        "totalPlaytime": timespan(total_playtime_minutes * 60),
        "characterPlaytime": character_playtime,
        "totalKills": total_kills,
        "crucibleKd": ratio(crucible.map(|value| value.kills).unwrap_or(0.0), crucible.map(|value| value.deaths).unwrap_or(0.0)),
        "crucibleKda": average_mode_kda(crucible),
        "gambitKd": ratio(gambit_kills, gambit_deaths),
        "gambitKda": round3(if gambit_kdas.is_empty() { 0.0 } else { gambit_kdas.iter().sum::<f64>() / gambit_kdas.len() as f64 }),
        "crucibleMatchesPlayed": crucible.map(|value| value.entered).unwrap_or(0),
        "gambitMatchesPlayed": gambit_entered,
        "crucibleWins": crucible.map(|value| value.wins).unwrap_or(0),
        "gambitWins": gambit_wins,
        "gambitPlaylists": [playlist(&mode_totals, 63, "Gambit"), playlist(&mode_totals, 75, "Gambit Prime")],
        "zeroKillActivities": zero_kill_activities,
        "totalActivityTime": timespan(total_activity_seconds),
        "uniquePlayersPlayedWith": encountered.len()
    });
    let recent_ids = activity_ids
        .iter()
        .rev()
        .take(5_000)
        .copied()
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
        "encounteredPlayerKeys": base64_encode(&encountered_bytes),
        "uniquePlayersPlayedWith": encountered.len(),
        "zeroKillActivities": zero_kill_activities,
        "totalActivitySeconds": total_activity_seconds,
        "playDates": play_dates,
        "playtimeByActivityMode": playtime_by_mode
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

fn historical_stat(response: &Value, name: &str) -> f64 {
    response
        .as_object()
        .into_iter()
        .flat_map(|values| values.values())
        .find_map(|bucket| {
            bucket
                .pointer(&format!("/allTime/{name}/basic/value"))
                .and_then(Value::as_f64)
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
        .filter(|value| value.kda_count > 0)
        .map(|value| round3(value.kda_sum / value.kda_count as f64))
        .unwrap_or(0.0)
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
        .or_else(|| value.as_str()?.parse().ok())
}

fn mode_group(mode: i32) -> &'static str {
    match mode {
        5 | 10..=62 | 69..=74 | 80..=81 | 84 | 88..=92 => "Crucible",
        63 | 75 => "Gambit",
        _ => "PvE",
    }
}

fn is_broad_mode(mode: i32) -> bool {
    matches!(mode, 5 | 7 | 63 | 64)
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

fn encode_encounters(values: &BTreeSet<(i32, i64)>) -> Vec<u8> {
    // This is the existing C# accumulator contract. Compact generation storage
    // compresses the payload; materialization must still recreate these 9-byte keys.
    let mut output = Vec::with_capacity(values.len() * 9);
    for (membership_type, membership_id) in values {
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
        assert_eq!(historical_stat(&response, "kills"), 42.0);
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
}
