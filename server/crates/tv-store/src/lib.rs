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

use std::path::Path;

use tv_world::{ClientKind, EntityId, Object, Role, UserId};

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

impl Store {
    /// Opens the database at `path`, creating it and bringing its schema up
    /// to date if needed.
    pub fn open(path: &Path) -> Result<Store, StoreError> {
        let _ = path;
        todo!("M2.B")
    }

    /// Opens a private in-memory database, for tests.
    pub fn open_in_memory() -> Result<Store, StoreError> {
        todo!("M2.B")
    }

    /// Reads all objects and the id counter.
    pub fn load_world(&self) -> Result<LoadedWorld, StoreError> {
        let _ = &self.conn;
        todo!("M2.B")
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
        let _ = (upserts, deletes, next_entity_id);
        todo!("M2.B")
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
        let _ = (name, kind, role, now_ms);
        todo!("M2.B")
    }

    /// The user this login token belongs to, if any. A revoked token matches
    /// nobody. Banned users are still returned; the caller checks `banned`.
    pub fn user_by_token(&self, token: &str) -> Result<Option<UserRecord>, StoreError> {
        let _ = token;
        todo!("M2.B")
    }

    /// The user with this id, if known.
    pub fn user(&self, id: &UserId) -> Result<Option<UserRecord>, StoreError> {
        let _ = id;
        todo!("M2.B")
    }

    /// All users, oldest first.
    pub fn users(&self) -> Result<Vec<UserRecord>, StoreError> {
        todo!("M2.B")
    }

    /// Records a visit: updates `last_seen_ms` and, if `name` is given, the
    /// display name. Returns false if the user is unknown.
    pub fn touch_user(
        &mut self,
        id: &UserId,
        name: Option<&str>,
        now_ms: i64,
    ) -> Result<bool, StoreError> {
        let _ = (id, name, now_ms);
        todo!("M2.B")
    }

    /// Changes a user's role. Returns false if the user is unknown.
    pub fn set_role(&mut self, id: &UserId, role: Role) -> Result<bool, StoreError> {
        let _ = (id, role);
        todo!("M2.B")
    }

    /// Bans a user. Returns false if the user is unknown.
    pub fn ban(&mut self, id: &UserId, reason: &str) -> Result<bool, StoreError> {
        let _ = (id, reason);
        todo!("M2.B")
    }

    /// Lifts a ban. Returns false if the user is unknown or was not banned.
    pub fn unban(&mut self, id: &UserId) -> Result<bool, StoreError> {
        let _ = id;
        todo!("M2.B")
    }

    /// Makes the user's login token useless. The user record and their
    /// objects stay. Returns false if the user is unknown or had no token.
    pub fn revoke_token(&mut self, id: &UserId) -> Result<bool, StoreError> {
        let _ = id;
        todo!("M2.B")
    }

    /// Replaces the user's login token with a fresh one and returns it in
    /// clear text. Returns `None` if the user is unknown.
    pub fn reissue_token(&mut self, id: &UserId) -> Result<Option<String>, StoreError> {
        let _ = id;
        todo!("M2.B")
    }
}
