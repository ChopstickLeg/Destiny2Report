use mongodb::bson::{Binary, DateTime};
use serde::{Deserialize, Serialize};

pub const PROTOCOL_VERSION: i32 = 1;
pub const STATE_QUEUED: &str = "queued";
pub const STATE_RUNNING: &str = "running";
pub const STATE_AWAITING_FINALIZATION: &str = "awaiting_finalization";

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct CrawlJob {
    #[serde(rename = "_id")]
    pub player_key: Binary,
    #[serde(rename = "mt")]
    pub membership_type_id: i32,
    #[serde(rename = "mi")]
    pub membership_id: i64,
    #[serde(rename = "v")]
    pub protocol_version: i32,
    #[serde(rename = "r")]
    pub run_id: String,
    #[serde(rename = "s")]
    pub state: String,
    #[serde(rename = "d")]
    pub dispatched: bool,
    #[serde(rename = "se", default)]
    pub stream_entry_id: String,
    #[serde(rename = "f", default)]
    pub fence: i64,
    #[serde(rename = "lo", default)]
    pub lease_owner: String,
    #[serde(rename = "le", default)]
    pub lease_expires_at: Option<DateTime>,
    #[serde(rename = "qa")]
    pub queued_at: DateTime,
    #[serde(rename = "sa", default)]
    pub started_at: Option<DateTime>,
    #[serde(rename = "ff", default)]
    pub force_full_crawl: bool,
    #[serde(rename = "ag", default)]
    pub active_generation: String,
}

#[derive(Clone, Debug)]
pub struct CrawlMessage {
    pub protocol_version: i32,
    pub run_id: String,
    pub membership_type_id: i32,
    pub membership_id: i64,
    pub stream_entry_id: String,
}

pub fn player_key(membership_type_id: i32, membership_id: i64) -> Binary {
    let mut bytes = Vec::with_capacity(12);
    bytes.extend_from_slice(&membership_type_id.to_be_bytes());
    bytes.extend_from_slice(&membership_id.to_be_bytes());
    Binary {
        subtype: mongodb::bson::spec::BinarySubtype::Generic,
        bytes,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn player_key_is_stable_big_endian() {
        let key = player_key(3, 0x0102_0304_0506_0708);
        assert_eq!(key.bytes, [0, 0, 0, 3, 1, 2, 3, 4, 5, 6, 7, 8]);
    }
}
