use std::{
    fs::{self, File},
    io::{self, Cursor, Read},
    path::{Path, PathBuf},
};

use anyhow::{Context, bail};
use rusqlite::Connection;

use crate::bungie::BungieClient;

const SQLITE_HEADER: &[u8; 16] = b"SQLite format 3\0";

pub struct ManifestStore {
    path: PathBuf,
}

impl ManifestStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    pub async fn refresh(&self, client: &BungieClient) -> anyhow::Result<()> {
        let response = client.manifest().await?;
        let path = response
            .pointer("/mobileWorldContentPaths/en")
            .and_then(serde_json::Value::as_str)
            .context("Bungie manifest has no English SQLite path")?;
        let bytes = client.download_public_file(path).await?;
        let parent = self.path.parent().unwrap_or_else(|| Path::new("."));
        fs::create_dir_all(parent)?;
        let temporary = parent.join(format!("manifest-{}.tmp", uuid::Uuid::new_v4().simple()));
        prepare_downloaded_manifest(&temporary, &bytes)?;
        if self.path.exists() {
            fs::remove_file(&self.path)?;
        }
        fs::rename(&temporary, &self.path)?;
        tracing::info!(path = %self.path.display(), "activated private SQLite manifest");
        Ok(())
    }

    #[allow(dead_code)]
    pub fn definition_json(&self, table: &str, hash: u32) -> anyhow::Result<Option<String>> {
        if !table
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
        {
            bail!("invalid manifest table name");
        }
        let connection =
            Connection::open_with_flags(&self.path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)?;
        let mut statement =
            connection.prepare(&format!("SELECT json FROM {table} WHERE id = ?1"))?;
        let signed_hash = hash as i32;
        let mut rows = statement.query([signed_hash])?;
        Ok(rows.next()?.map(|row| row.get(0)).transpose()?)
    }
}

fn prepare_downloaded_manifest(path: &Path, downloaded: &[u8]) -> anyhow::Result<()> {
    let result = materialize_sqlite(path, downloaded).and_then(|()| validate(path));
    if result.is_err() {
        let _ = fs::remove_file(path);
    }
    result
}

fn materialize_sqlite(path: &Path, downloaded: &[u8]) -> anyhow::Result<()> {
    if downloaded.starts_with(SQLITE_HEADER) {
        fs::write(path, downloaded).context("write downloaded SQLite manifest")?;
        return Ok(());
    }

    let mut archive = zip::ZipArchive::new(Cursor::new(downloaded))
        .context("downloaded manifest is neither raw SQLite nor a valid ZIP archive")?;
    let mut database_index = None;

    for index in 0..archive.len() {
        let mut entry = archive
            .by_index(index)
            .context("read downloaded manifest ZIP entry")?;
        if entry.is_dir() {
            continue;
        }

        let mut header = [0; SQLITE_HEADER.len()];
        match entry.read_exact(&mut header) {
            Ok(()) if &header == SQLITE_HEADER => {
                database_index = Some(index);
                break;
            }
            Ok(()) => {}
            Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => {}
            Err(error) => return Err(error).context("inspect downloaded manifest ZIP entry"),
        }
    }

    let index = database_index.context("downloaded manifest ZIP contains no SQLite database")?;
    let mut database = archive
        .by_index(index)
        .context("open SQLite database in downloaded manifest ZIP")?;
    let mut output = File::create(path).context("create extracted SQLite manifest")?;
    io::copy(&mut database, &mut output).context("extract SQLite manifest from ZIP")?;
    Ok(())
}

fn validate(path: &Path) -> anyhow::Result<()> {
    let connection = Connection::open_with_flags(path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)
        .context("open downloaded SQLite manifest")?;
    let tables: i64 = connection.query_row(
        "SELECT count(*) FROM sqlite_master WHERE type = 'table'",
        [],
        |row| row.get(0),
    )?;
    if tables == 0 {
        bail!("downloaded manifest contains no tables");
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::io::{Cursor, Write};

    use super::*;

    fn temporary_path(label: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "destiny2report-{label}-{}",
            uuid::Uuid::new_v4().simple()
        ))
    }

    fn sqlite_fixture() -> Vec<u8> {
        let path = temporary_path("source.sqlite");
        let connection = Connection::open(&path).unwrap();
        connection
            .execute("CREATE TABLE definitions (id INTEGER PRIMARY KEY, json TEXT)", [])
            .unwrap();
        drop(connection);
        let bytes = fs::read(&path).unwrap();
        fs::remove_file(path).unwrap();
        bytes
    }

    #[test]
    fn extracts_sqlite_database_from_zip_download() {
        let sqlite = sqlite_fixture();
        let mut archive = zip::ZipWriter::new(Cursor::new(Vec::new()));
        archive
            .start_file(
                "world_sql_content.content",
                zip::write::SimpleFileOptions::default()
                    .compression_method(zip::CompressionMethod::Deflated),
            )
            .unwrap();
        archive.write_all(&sqlite).unwrap();
        let downloaded = archive.finish().unwrap().into_inner();
        let destination = temporary_path("extracted.sqlite");

        prepare_downloaded_manifest(&destination, &downloaded).unwrap();

        validate(&destination).unwrap();
        assert_eq!(fs::read(&destination).unwrap(), sqlite);
        fs::remove_file(destination).unwrap();
    }

    #[test]
    fn accepts_uncompressed_sqlite_download() {
        let sqlite = sqlite_fixture();
        let destination = temporary_path("raw.sqlite");

        prepare_downloaded_manifest(&destination, &sqlite).unwrap();

        validate(&destination).unwrap();
        fs::remove_file(destination).unwrap();
    }

    #[test]
    fn rejects_non_database_response_without_leaving_a_file() {
        let destination = temporary_path("invalid.sqlite");

        let error = prepare_downloaded_manifest(&destination, b"<html>upstream error</html>")
            .unwrap_err();

        assert!(error.to_string().contains("neither raw SQLite nor a valid ZIP"));
        assert!(!destination.exists());
    }
}
