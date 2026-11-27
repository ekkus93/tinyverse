//! The database schema and its migrations.
//!
//! The schema version lives in `PRAGMA user_version`. Version 0 is an empty
//! database; migration `i` (0-based) in [`MIGRATIONS`] takes the database
//! from version `i` to `i + 1`. Migrations are append-only: once released, an
//! entry is never edited, a later version adds a new one.

use rusqlite::{Connection, TransactionBehavior};

use crate::StoreError;

/// Version 1: objects, users and the key/value meta table.
///
/// Every table is `STRICT`, so SQLite itself rejects a value of the wrong
/// type, and every column is `NOT NULL` unless NULL means something (a NaN
/// coordinate, which SQLite would store as NULL, therefore fails the write).
const V1: &str = "
CREATE TABLE meta (
    key   TEXT PRIMARY KEY NOT NULL,
    value INTEGER NOT NULL
) STRICT;

CREATE TABLE objects (
    id       INTEGER PRIMARY KEY NOT NULL CHECK (id > 0),
    owner    TEXT NOT NULL,
    name     TEXT NOT NULL,
    shape    TEXT NOT NULL,
    pos_x    REAL NOT NULL,
    pos_y    REAL NOT NULL,
    pos_z    REAL NOT NULL,
    rot_x    REAL NOT NULL,
    rot_y    REAL NOT NULL,
    rot_z    REAL NOT NULL,
    rot_w    REAL NOT NULL,
    scale_x  REAL NOT NULL,
    scale_y  REAL NOT NULL,
    scale_z  REAL NOT NULL,
    color_r  REAL NOT NULL,
    color_g  REAL NOT NULL,
    color_b  REAL NOT NULL,
    color_a  REAL NOT NULL,
    material TEXT NOT NULL,
    solid    INTEGER NOT NULL CHECK (solid IN (0, 1)),
    locked   INTEGER NOT NULL CHECK (locked IN (0, 1))
) STRICT;

CREATE INDEX objects_by_owner ON objects (owner);

CREATE TABLE users (
    id            TEXT PRIMARY KEY NOT NULL,
    name          TEXT NOT NULL,
    kind          TEXT NOT NULL,
    role          TEXT NOT NULL,
    created_ms    INTEGER NOT NULL,
    last_seen_ms  INTEGER NOT NULL,
    -- NULL: not banned. Otherwise the reason, possibly empty.
    banned        TEXT,
    -- Hex SHA-256 of the login token. NULL once revoked.
    token_sha256  TEXT UNIQUE
) STRICT;
";

/// All migrations, oldest first.
pub(crate) const MIGRATIONS: &[&str] = &[V1];

/// Brings the schema at `conn` up to the newest of `migrations`.
///
/// An unknown (newer or negative) version is [`StoreError::Corrupt`]. The
/// upgrade runs in one `IMMEDIATE` transaction, so two processes opening an
/// old file at once cannot both migrate it, and a failed migration leaves the
/// file at its old version.
pub(crate) fn migrate(conn: &mut Connection, migrations: &[&str]) -> Result<(), StoreError> {
    let latest = i64::try_from(migrations.len()).expect("migration count fits i64");
    let current = user_version(conn)?;
    check_known(current, latest)?;
    if current == latest {
        return Ok(());
    }

    let tx = conn.transaction_with_behavior(TransactionBehavior::Immediate)?;
    // Another process may have migrated between the read above and the lock.
    let current = user_version(&tx)?;
    check_known(current, latest)?;
    for (index, sql) in migrations.iter().enumerate().skip(current as usize) {
        let target = index as i64 + 1;
        tx.execute_batch(sql)?;
        tx.pragma_update(None, "user_version", target)?;
        tracing::info!(version = target, "migrated world database schema");
    }
    tx.commit()?;
    Ok(())
}

fn user_version(conn: &Connection) -> Result<i64, StoreError> {
    Ok(conn.pragma_query_value(None, "user_version", |row| row.get(0))?)
}

fn check_known(version: i64, latest: i64) -> Result<(), StoreError> {
    if (0..=latest).contains(&version) {
        Ok(())
    } else {
        Err(StoreError::Corrupt(format!(
            "schema version {version} is not one this version knows (newest is {latest})"
        )))
    }
}
