//! Tinyverse persistence: one SQLite file (`world.db`) per world.
//!
//! The store keeps what must survive a restart: the objects built in the
//! world, the id counter, and the users (role, login token hash, ban). Chat
//! is deliberately never stored.
//!
//! The store is synchronous. The server calls it from a blocking thread and
//! decides when to flush; every method here is one atomic transaction, so a
//! crash between two calls loses at most the changes not yet handed over.
//!
//! The public API of this crate is a frozen contract with `tv-server`.

mod codec;
mod schema;
#[cfg(test)]
mod tests;

use std::path::Path;
use std::time::Duration;

use rand::TryRng;
use rusqlite::{OptionalExtension, TransactionBehavior, params};
use sha2::{Digest, Sha256};
use tv_world::{ClientKind, EntityId, Object, Role, UserId};

use crate::codec::{OBJECT_COLUMNS, USER_COLUMNS};

/// A failure of the store.
#[derive(Debug, thiserror::Error)]
pub enum StoreError {
    #[error("database error: {0}")]
    Sqlite(#[from] rusqlite::Error),
    /// The file is not a world database this version can read: an unknown
    /// schema version, or a row that does not decode.
    #[error("unreadable world database: {0}")]
    Corrupt(String),
}

/// What [`Store::load_world`] returns: everything needed for `World::new`.
#[derive(Clone, Debug, PartialEq)]
pub struct LoadedWorld {
    /// All objects, in ascending id order.
    pub objects: Vec<Object>,
    /// The first id the world may hand out; 1 for a fresh database.
    pub next_entity_id: EntityId,
}

/// A user known to this world.
#[derive(Clone, Debug, PartialEq)]
pub struct UserRecord {
    pub id: UserId,
    /// The display name last used.
    pub name: String,
    pub kind: ClientKind,
    pub role: Role,
    pub created_ms: i64,
    pub last_seen_ms: i64,
    /// `Some(reason)` if the user is banned.
    pub banned: Option<String>,
    /// False once the user's token has been revoked.
    pub has_token: bool,
}

/// An open world database.
pub struct Store {
    conn: rusqlite::Connection,
}

/// How long a statement waits for another connection (the host CLI while the
/// server runs, or the other way round) to release its lock.
const BUSY_TIMEOUT: Duration = Duration::from_secs(5);

/// Room for every statement this crate prepares, so none is ever evicted.
const STATEMENT_CACHE_CAPACITY: usize = 32;

/// Key of the id counter in the `meta` table.
const META_NEXT_ENTITY_ID: &str = "next_entity_id";

/// Bytes of randomness in a login token.
const TOKEN_BYTES: usize = 32;

const UPSERT_OBJECT: &str = "INSERT INTO objects (id, owner, name, shape, \
     pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w, scale_x, scale_y, scale_z, \
     color_r, color_g, color_b, color_a, material, solid, locked) \
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, \
     ?17, ?18, ?19, ?20, ?21) \
     ON CONFLICT (id) DO UPDATE SET owner = excluded.owner, name = excluded.name, \
     shape = excluded.shape, pos_x = excluded.pos_x, pos_y = excluded.pos_y, \
     pos_z = excluded.pos_z, rot_x = excluded.rot_x, rot_y = excluded.rot_y, \
     rot_z = excluded.rot_z, rot_w = excluded.rot_w, scale_x = excluded.scale_x, \
     scale_y = excluded.scale_y, scale_z = excluded.scale_z, color_r = excluded.color_r, \
     color_g = excluded.color_g, color_b = excluded.color_b, color_a = excluded.color_a, \
     material = excluded.material, solid = excluded.solid, locked = excluded.locked";

impl Store {
    /// Opens the database at `path`, creating it and bringing its schema up
    /// to date if needed.
    pub fn open(path: &Path) -> Result<Store, StoreError> {
        let conn = rusqlite::Connection::open(path)?;
        Store::init(conn, true).map_err(not_a_database)
    }

    /// Opens a private in-memory database, for tests.
    pub fn open_in_memory() -> Result<Store, StoreError> {
        Store::init(rusqlite::Connection::open_in_memory()?, false)
    }

    /// Reads all objects and the id counter.
    pub fn load_world(&self) -> Result<LoadedWorld, StoreError> {
        // One read transaction, so objects and counter come from one snapshot.
        let tx = self.conn.unchecked_transaction()?;
        let objects = {
            let mut stmt =
                tx.prepare_cached(&format!("SELECT {OBJECT_COLUMNS} FROM objects ORDER BY id"))?;
            let mut rows = stmt.query([])?;
            let mut objects = Vec::new();
            while let Some(row) = rows.next()? {
                objects.push(codec::object_from_row(row)?);
            }
            objects
        };
        let stored: Option<i64> = tx
            .prepare_cached("SELECT value FROM meta WHERE key = ?1")?
            .query_row([META_NEXT_ENTITY_ID], |row| row.get(0))
            .optional()?;
        tx.finish()?;

        let stored = match stored {
            Some(v) => u64::try_from(v)
                .map_err(|_| StoreError::Corrupt(format!("next_entity_id is negative: {v}")))?,
            None => 1,
        };
        // Objects are in ascending order, so the last one has the largest id.
        let past_objects = objects.last().map_or(1, |o| o.id.saturating_add(1));
        Ok(LoadedWorld { objects, next_entity_id: stored.max(past_objects).max(1) })
    }

    /// Writes a batch of object changes in one transaction: inserts or
    /// replaces `upserts`, removes `deletes` (unknown ids are ignored), and
    /// records `next_entity_id`. Either all of it is stored or none.
    pub fn save_objects(
        &mut self,
        upserts: &[Object],
        deletes: &[EntityId],
        next_entity_id: EntityId,
    ) -> Result<(), StoreError> {
        let next = to_i64(next_entity_id)?;
        let tx = self.conn.transaction_with_behavior(TransactionBehavior::Immediate)?;
        {
            let mut upsert = tx.prepare_cached(UPSERT_OBJECT)?;
            for object in upserts {
                let s = &object.spec;
                upsert.execute(params![
                    to_i64(object.id)?,
                    object.owner.as_str(),
                    s.name,
                    codec::shape_name(s.shape),
                    s.position.x,
                    s.position.y,
                    s.position.z,
                    s.rotation.x,
                    s.rotation.y,
                    s.rotation.z,
                    s.rotation.w,
                    s.scale.x,
                    s.scale.y,
                    s.scale.z,
                    s.color.r,
                    s.color.g,
                    s.color.b,
                    s.color.a,
                    codec::material_name(s.material),
                    s.solid,
                    s.locked,
                ])?;
            }
            // Deletes run after upserts, so an id in both lists ends up absent.
            let mut delete = tx.prepare_cached("DELETE FROM objects WHERE id = ?1")?;
            for &id in deletes {
                // An id beyond i64 cannot have been stored: it is unknown.
                if let Ok(id) = i64::try_from(id) {
                    delete.execute([id])?;
                }
            }
            // The counter never goes backwards.
            tx.prepare_cached(
                "INSERT INTO meta (key, value) VALUES (?1, ?2) \
                 ON CONFLICT (key) DO UPDATE SET value = max(value, excluded.value)",
            )?
            .execute(params![META_NEXT_ENTITY_ID, next])?;
        }
        tx.commit()?;
        Ok(())
    }

    /// Creates a user with a fresh random id and login token, and returns the
    /// record together with the token in clear text. Only a hash of the token
    /// is stored, so this is the only time it can be read.
    pub fn create_user(
        &mut self,
        name: &str,
        kind: ClientKind,
        role: Role,
        now_ms: i64,
    ) -> Result<(UserRecord, String), StoreError> {
        let id = UserId(uuid::Uuid::new_v4().to_string());
        let token = new_token();
        self.conn
            .prepare_cached(
                "INSERT INTO users (id, name, kind, role, created_ms, last_seen_ms, \
                 banned, token_sha256) VALUES (?1, ?2, ?3, ?4, ?5, ?5, NULL, ?6)",
            )?
            .execute(params![
                id.as_str(),
                name,
                codec::kind_name(kind),
                codec::role_name(role),
                now_ms,
                token_digest(&token),
            ])?;
        let record = UserRecord {
            id,
            name: name.to_owned(),
            kind,
            role,
            created_ms: now_ms,
            last_seen_ms: now_ms,
            banned: None,
            has_token: true,
        };
        Ok((record, token))
    }

    /// The user this login token belongs to, if any. A revoked token matches
    /// nobody. Banned users are still returned; the caller checks `banned`.
    pub fn user_by_token(&self, token: &str) -> Result<Option<UserRecord>, StoreError> {
        self.query_user("token_sha256 = ?1", &token_digest(token))
    }

    /// The user with this id, if known.
    pub fn user(&self, id: &UserId) -> Result<Option<UserRecord>, StoreError> {
        self.query_user("id = ?1", id.as_str())
    }

    /// All users, oldest first.
    pub fn users(&self) -> Result<Vec<UserRecord>, StoreError> {
        let mut stmt = self.conn.prepare_cached(&format!(
            "SELECT {USER_COLUMNS} FROM users ORDER BY created_ms, rowid"
        ))?;
        let mut rows = stmt.query([])?;
        let mut users = Vec::new();
        while let Some(row) = rows.next()? {
            users.push(codec::user_from_row(row)?);
        }
        Ok(users)
    }

    /// Records a visit: updates `last_seen_ms` and, if `name` is given, the
    /// display name. Returns false if the user is unknown.
    pub fn touch_user(
        &mut self,
        id: &UserId,
        name: Option<&str>,
        now_ms: i64,
    ) -> Result<bool, StoreError> {
        self.update_user(
            "UPDATE users SET last_seen_ms = ?2, name = coalesce(?3, name) WHERE id = ?1",
            params![id.as_str(), now_ms, name],
        )
    }

    /// Changes a user's role. Returns false if the user is unknown.
    pub fn set_role(&mut self, id: &UserId, role: Role) -> Result<bool, StoreError> {
        self.update_user(
            "UPDATE users SET role = ?2 WHERE id = ?1",
            params![id.as_str(), codec::role_name(role)],
        )
    }

    /// Bans a user. Returns false if the user is unknown.
    pub fn ban(&mut self, id: &UserId, reason: &str) -> Result<bool, StoreError> {
        self.update_user("UPDATE users SET banned = ?2 WHERE id = ?1", params![id.as_str(), reason])
    }

    /// Lifts a ban. Returns false if the user is unknown or was not banned.
    pub fn unban(&mut self, id: &UserId) -> Result<bool, StoreError> {
        self.update_user(
            "UPDATE users SET banned = NULL WHERE id = ?1 AND banned IS NOT NULL",
            params![id.as_str()],
        )
    }

    /// Makes the user's login token useless. The user record and their
    /// objects stay. Returns false if the user is unknown or had no token.
    pub fn revoke_token(&mut self, id: &UserId) -> Result<bool, StoreError> {
        self.update_user(
            "UPDATE users SET token_sha256 = NULL WHERE id = ?1 AND token_sha256 IS NOT NULL",
            params![id.as_str()],
        )
    }

    /// Replaces the user's login token with a fresh one and returns it in
    /// clear text. Returns `None` if the user is unknown.
    pub fn reissue_token(&mut self, id: &UserId) -> Result<Option<String>, StoreError> {
        let token = new_token();
        let changed = self.update_user(
            "UPDATE users SET token_sha256 = ?2 WHERE id = ?1",
            params![id.as_str(), token_digest(&token)],
        )?;
        Ok(changed.then_some(token))
    }
}

impl Store {
    /// Configures a fresh connection and migrates its schema.
    fn init(mut conn: rusqlite::Connection, on_disk: bool) -> Result<Store, StoreError> {
        // The busy timeout comes first: switching to WAL needs a lock too.
        conn.busy_timeout(BUSY_TIMEOUT)?;
        conn.set_prepared_statement_cache_capacity(STATEMENT_CACHE_CAPACITY);
        if on_disk {
            let mode: String =
                conn.pragma_update_and_check(None, "journal_mode", "WAL", |row| row.get(0))?;
            if !mode.eq_ignore_ascii_case("wal") {
                tracing::warn!(mode, "world database is not in WAL mode");
            }
        }
        conn.pragma_update(None, "synchronous", "NORMAL")?;
        conn.pragma_update(None, "foreign_keys", true)?;
        schema::migrate(&mut conn, schema::MIGRATIONS)?;
        Ok(Store { conn })
    }

    /// Runs a `SELECT` of one user by the given condition on `?1`.
    fn query_user(&self, condition: &str, value: &str) -> Result<Option<UserRecord>, StoreError> {
        let mut stmt = self
            .conn
            .prepare_cached(&format!("SELECT {USER_COLUMNS} FROM users WHERE {condition}"))?;
        let mut rows = stmt.query([value])?;
        match rows.next()? {
            Some(row) => Ok(Some(codec::user_from_row(row)?)),
            None => Ok(None),
        }
    }

    /// Runs an `UPDATE` of one user; true if a row changed.
    fn update_user(&self, sql: &str, params: &[&dyn rusqlite::ToSql]) -> Result<bool, StoreError> {
        Ok(self.conn.prepare_cached(sql)?.execute(params)? > 0)
    }
}

/// Reports a file that is not SQLite at all as [`StoreError::Corrupt`].
fn not_a_database(error: StoreError) -> StoreError {
    match error {
        StoreError::Sqlite(rusqlite::Error::SqliteFailure(e, _))
            if e.code == rusqlite::ErrorCode::NotADatabase =>
        {
            StoreError::Corrupt("not a SQLite database".to_owned())
        }
        other => other,
    }
}

/// SQLite integers are signed; ids and the counter never reach 2^63.
fn to_i64(value: u64) -> Result<i64, StoreError> {
    i64::try_from(value)
        .map_err(|e| StoreError::Sqlite(rusqlite::Error::ToSqlConversionFailure(Box::new(e))))
}

/// A fresh login token: 32 bytes from the OS RNG, hex-encoded.
fn new_token() -> String {
    let mut bytes = [0u8; TOKEN_BYTES];
    // The OS RNG does not fail on any supported platform; if it ever does,
    // handing out a guessable token would be worse than stopping.
    rand::rngs::SysRng
        .try_fill_bytes(&mut bytes)
        .expect("the operating system's random number generator failed");
    hex::encode(bytes)
}

/// What is stored for a token: its SHA-256 digest, hex-encoded. Tokens carry
/// 256 bits of entropy, so a plain hash suffices and no KDF is needed.
fn token_digest(token: &str) -> String {
    hex::encode(Sha256::digest(token.as_bytes()))
}
