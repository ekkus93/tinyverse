//! The world itself. Signatures are frozen; the rules live in the private
//! submodules: `movement`, `chat`, `objects`, `observe` and `admin`.

mod admin;
mod chat;
mod movement;
mod objects;
mod observe;
mod validate;

#[cfg(test)]
mod tests;

use std::collections::{BTreeMap, VecDeque};

use crate::command::{Command, JoinRequest};
use crate::error::WorldError;
use crate::event::{Audience, Event, EventKind};
use crate::types::{
    Avatar, AvatarTransform, ChatEntry, EntityId, MoveMode, Object, UserId, WorldConfig,
};

/// The complete state of one world: who is present, what has been built, and
/// recent chat.
pub struct World {
    config: WorldConfig,
    avatars: BTreeMap<EntityId, Presence>,
    objects: BTreeMap<EntityId, Object>,
    next_id: EntityId,
    /// Retained world chat, oldest first.
    chat: VecDeque<ChatEntry>,
}

/// An avatar plus the bookkeeping the rules need about it.
struct Presence {
    /// `avatar.transform` is always the last accepted transform.
    avatar: Avatar,
    /// When `avatar.transform` was last accepted or set by the world.
    last_accepted_ms: i64,
    /// A `MoveTo` in progress.
    move_to: Option<MoveTarget>,
    /// `Some(server_driven)` if the transform changed since the last tick.
    changed: Option<bool>,
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct MoveTarget {
    target: glam::Vec3,
    mode: MoveMode,
}

const KICK_REASON: &str = "removed by a world owner";
const BAN_REASON: &str = "banned by a world owner";
const REPLACED_REASON: &str = "signed in again from another connection";

impl World {
    /// Creates a world holding the given objects and no avatars.
    ///
    /// `next_entity_id` is the first id the world will hand out. It must be
    /// greater than every id in `objects`; a fresh world passes 1.
    pub fn new(config: WorldConfig, objects: Vec<Object>, next_entity_id: EntityId) -> Self {
        let objects: BTreeMap<EntityId, Object> =
            objects.into_iter().map(|object| (object.id, object)).collect();
        // Defensive: never hand out 0 or an id that is already taken, even if
        // the caller broke the precondition.
        let above_objects = objects.keys().next_back().map_or(1, |id| id + 1);
        World {
            config,
            avatars: BTreeMap::new(),
            objects,
            next_id: next_entity_id.max(above_objects).max(1),
            chat: VecDeque::new(),
        }
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
        let name = validate::display_name(&request.name, self.config.limits.max_name_chars)?;
        let appearance = validate::appearance(request.appearance)?;
        let previous = self.avatar_of_user(&request.user_id).map(|avatar| avatar.id);
        let staying = self.avatars.len() - usize::from(previous.is_some());
        if staying >= self.config.limits.max_users {
            return Err(WorldError::WorldFull);
        }

        let mut events = Vec::new();
        if let Some(previous) = previous {
            events.extend(self.remove_avatar(previous, Some(REPLACED_REASON.to_owned())));
        }

        let id = self.allocate_id();
        let avatar = Avatar {
            id,
            user_id: request.user_id,
            name,
            kind: request.kind,
            role: request.role,
            appearance,
            transform: AvatarTransform {
                position: self.config.spawn_point,
                yaw: 0.0,
                velocity: glam::Vec3::ZERO,
                move_mode: MoveMode::Walk,
                animation: "idle".to_owned(),
            },
        };
        events.push(Event::new(Audience::AllExcept(id), EventKind::AvatarJoined(avatar.clone())));
        self.avatars.insert(
            id,
            Presence { avatar, last_accepted_ms: now_ms, move_to: None, changed: None },
        );
        Ok((id, events))
    }

    /// Removes an avatar. Returns `AvatarLeft` for everyone else, or nothing
    /// if the avatar is not present (for example because it was kicked).
    pub fn leave(&mut self, avatar: EntityId, now_ms: i64) -> Vec<Event> {
        let _ = now_ms;
        self.remove_avatar(avatar, None)
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
        if !self.avatars.contains_key(&actor) {
            return Err(WorldError::NotJoined);
        }
        match command {
            Command::AvatarState(transform) => self.avatar_state(actor, transform, now_ms),
            Command::MoveTo { target, mode } => self.move_to(actor, target, mode),
            Command::Stop => Ok(self.stop(actor)),
            Command::Chat { channel, text } => self.chat(actor, channel, &text, now_ms),
            Command::ChatHistory { since_ms, limit } => Ok(vec![Event::new(
                Audience::Only(actor),
                EventKind::ChatHistory(self.chat_history(since_ms, limit, now_ms)),
            )]),
            Command::ObjectCreate(spec) => self.object_create(actor, spec),
            Command::ObjectUpdate { id, patch } => self.object_update(actor, id, patch),
            Command::ObjectDelete { id } => self.object_delete(actor, id),
            Command::Observe { radius } => self.observe(actor, radius),
            Command::SetAppearance(appearance) => self.set_appearance(actor, appearance),
            Command::Kick { user_id, reason } => self.kick(actor, &user_id, reason),
            Command::Ban { user_id, reason } => self.ban(actor, user_id, reason),
            Command::Unban { user_id } => self.unban(actor, user_id),
            Command::SetRole { user_id, role } => self.set_role(actor, user_id, role),
        }
    }

    /// Advances the world by `dt` seconds: moves avatars that are following a
    /// `MoveTo`, drops expired chat, and returns one
    /// [`crate::EventKind::AvatarStates`] event (audience `All`) listing the
    /// avatars whose transform changed since the previous tick, or no event
    /// if none did.
    pub fn tick(&mut self, dt: f32, now_ms: i64) -> Vec<Event> {
        self.prune_chat(now_ms);
        self.advance_moves(dt, now_ms);
        let updates = self.take_changed_states();
        if updates.is_empty() {
            Vec::new()
        } else {
            vec![Event::new(Audience::All, EventKind::AvatarStates(updates))]
        }
    }

    /// The avatar with this id, if present.
    pub fn avatar(&self, id: EntityId) -> Option<&Avatar> {
        self.avatars.get(&id).map(|presence| &presence.avatar)
    }

    /// The avatar of this user, if present.
    pub fn avatar_of_user(&self, user: &UserId) -> Option<&Avatar> {
        self.avatars
            .values()
            .map(|presence| &presence.avatar)
            .find(|avatar| &avatar.user_id == user)
    }

    /// All avatars present, in ascending id order.
    pub fn avatars(&self) -> Vec<&Avatar> {
        self.avatars.values().map(|presence| &presence.avatar).collect()
    }

    /// The object with this id, if it exists.
    pub fn object(&self, id: EntityId) -> Option<&Object> {
        self.objects.get(&id)
    }

    /// All objects, in ascending id order.
    pub fn objects(&self) -> Vec<&Object> {
        self.objects.values().collect()
    }

    /// The number of objects in the world.
    pub fn object_count(&self) -> usize {
        self.objects.len()
    }

    /// The id the next avatar or object will receive. The store persists this
    /// so ids are never reused across restarts.
    pub fn next_entity_id(&self) -> EntityId {
        self.next_id
    }

    /// Retained world chat newer than `since_ms` (0 = all), oldest first,
    /// keeping at most the newest `limit` entries (0 = no limit). Entries
    /// older than the retention period at `now_ms` are never returned.
    pub fn chat_history(&self, since_ms: i64, limit: usize, now_ms: i64) -> Vec<ChatEntry> {
        self.retained_chat(since_ms, limit, now_ms)
    }

    // --- private helpers shared by the submodules ---

    fn allocate_id(&mut self) -> EntityId {
        let id = self.next_id;
        self.next_id += 1;
        id
    }

    /// The avatar behind `actor`. Callers have checked it is present.
    fn actor(&self, actor: EntityId) -> Result<&Avatar, WorldError> {
        self.avatar(actor).ok_or(WorldError::NotJoined)
    }

    /// Removes an avatar if present. With a reason, the avatar itself is told
    /// it was kicked; everyone else always hears `AvatarLeft`.
    fn remove_avatar(&mut self, id: EntityId, kicked: Option<String>) -> Vec<Event> {
        if self.avatars.remove(&id).is_none() {
            return Vec::new();
        }
        let mut events = Vec::new();
        if let Some(reason) = kicked {
            events.push(Event::new(Audience::Only(id), EventKind::Kicked { reason }));
        }
        events.push(Event::new(Audience::AllExcept(id), EventKind::AvatarLeft { id }));
        events
    }
}

fn ack(actor: EntityId) -> Event {
    Event::new(Audience::Only(actor), EventKind::Ack)
}
