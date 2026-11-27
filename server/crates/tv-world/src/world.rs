//! The world itself. Signatures are frozen; bodies arrive in milestone M1.

use crate::command::{Command, JoinRequest};
use crate::error::WorldError;
use crate::event::Event;
use crate::types::{Avatar, ChatEntry, EntityId, Object, UserId, WorldConfig};

/// The complete state of one world: who is present, what has been built, and
/// recent chat.
pub struct World {
    config: WorldConfig,
}

impl World {
    /// Creates a world holding the given objects and no avatars.
    ///
    /// `next_entity_id` is the first id the world will hand out. It must be
    /// greater than every id in `objects`; a fresh world passes 1.
    pub fn new(config: WorldConfig, objects: Vec<Object>, next_entity_id: EntityId) -> Self {
        let _ = (objects, next_entity_id);
        World { config }
    }

    /// The world's static configuration.
    pub fn config(&self) -> &WorldConfig {
        &self.config
    }

    /// Puts a new avatar at the spawn point and returns its id.
    ///
    /// The events announce the arrival to everyone else
    /// ([`crate::EventKind::AvatarJoined`], audience `AllExcept(new id)`).
    /// The server builds `Welcome`, the snapshot and the chat history for the
    /// newcomer itself from the accessors below.
    ///
    /// If the same user is already present, that older avatar is removed
    /// first: the events then also contain `Kicked` for it and `AvatarLeft`
    /// for the others.
    ///
    /// Errors: [`WorldError::WorldFull`]; [`WorldError::InvalidArgument`] for
    /// an empty or overlong name.
    pub fn join(
        &mut self,
        request: JoinRequest,
        now_ms: i64,
    ) -> Result<(EntityId, Vec<Event>), WorldError> {
        let _ = (request, now_ms);
        todo!("M1.A")
    }

    /// Removes an avatar. Returns `AvatarLeft` for everyone else, or nothing
    /// if the avatar is not present (for example because it was kicked).
    pub fn leave(&mut self, avatar: EntityId, now_ms: i64) -> Vec<Event> {
        let _ = (avatar, now_ms);
        todo!("M1.A")
    }

    /// Applies one command from an avatar.
    ///
    /// On success, returns the events to deliver. Every command that the
    /// protocol answers produces at least one event addressed to the actor
    /// (alone or as part of a wider audience), so the server can set
    /// `reply_to` on it. On failure nothing has changed.
    ///
    /// Errors: [`WorldError::NotJoined`] if `actor` is not present; otherwise
    /// as documented on each [`Command`] variant.
    pub fn apply(
        &mut self,
        actor: EntityId,
        command: Command,
        now_ms: i64,
    ) -> Result<Vec<Event>, WorldError> {
        let _ = (actor, command, now_ms);
        todo!("M1.A")
    }

    /// Advances the world by `dt` seconds: moves avatars that are following a
    /// `MoveTo`, drops expired chat, and returns one
    /// [`crate::EventKind::AvatarStates`] event (audience `All`) listing the
    /// avatars whose transform changed since the previous tick, or no event
    /// if none did.
    pub fn tick(&mut self, dt: f32, now_ms: i64) -> Vec<Event> {
        let _ = (dt, now_ms);
        todo!("M1.A")
    }

    /// The avatar with this id, if present.
    pub fn avatar(&self, id: EntityId) -> Option<&Avatar> {
        let _ = id;
        todo!("M1.A")
    }

    /// The avatar of this user, if present.
    pub fn avatar_of_user(&self, user: &UserId) -> Option<&Avatar> {
        let _ = user;
        todo!("M1.A")
    }

    /// All avatars present, in ascending id order.
    pub fn avatars(&self) -> Vec<&Avatar> {
        todo!("M1.A")
    }

    /// The object with this id, if it exists.
    pub fn object(&self, id: EntityId) -> Option<&Object> {
        let _ = id;
        todo!("M2.A")
    }

    /// All objects, in ascending id order.
    pub fn objects(&self) -> Vec<&Object> {
        todo!("M1.A")
    }

    /// The number of objects in the world.
    pub fn object_count(&self) -> usize {
        todo!("M1.A")
    }

    /// The id the next avatar or object will receive. The store persists this
    /// so ids are never reused across restarts.
    pub fn next_entity_id(&self) -> EntityId {
        todo!("M1.A")
    }

    /// Retained world chat newer than `since_ms` (0 = all), oldest first,
    /// keeping at most the newest `limit` entries (0 = no limit). Entries
    /// older than the retention period at `now_ms` are never returned.
    pub fn chat_history(&self, since_ms: i64, limit: usize, now_ms: i64) -> Vec<ChatEntry> {
        let _ = (since_ms, limit, now_ms);
        todo!("M1.A")
    }
}
