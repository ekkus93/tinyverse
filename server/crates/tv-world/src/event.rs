//! What the world tells the server to deliver.

use crate::types::{
    Avatar, AvatarTransform, ChatEntry, EntityId, Object, Observation, Role, UserId,
};

/// Who an event is for.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Audience {
    /// Every avatar in the world.
    All,
    /// Every avatar except this one.
    AllExcept(EntityId),
    /// This avatar only.
    Only(EntityId),
    /// These avatars.
    Set(Vec<EntityId>),
    /// Nobody in the world: an instruction for the host process itself
    /// (persist a ban, close a connection, ...).
    Host,
}

/// One avatar's new transform inside an [`EventKind::AvatarStates`] batch.
#[derive(Clone, Debug, PartialEq)]
pub struct AvatarStateUpdate {
    pub id: EntityId,
    pub transform: AvatarTransform,
    /// True if the world itself moved the avatar (`MoveTo`). The server omits
    /// an avatar's own entry when sending the batch to that avatar unless
    /// this is set, because a client simulates its own avatar.
    pub server_driven: bool,
}

/// Something that happened, and who should hear about it.
#[derive(Clone, Debug, PartialEq)]
pub struct Event {
    pub audience: Audience,
    pub kind: EventKind,
}

impl Event {
    pub fn new(audience: Audience, kind: EventKind) -> Self {
        Event { audience, kind }
    }
}

/// What happened. Variants up to `Kicked` map one-to-one to server messages
/// of the protocol; the `User*` variants are always addressed to
/// [`Audience::Host`].
#[derive(Clone, Debug, PartialEq)]
pub enum EventKind {
    AvatarJoined(Avatar),
    AvatarLeft {
        id: EntityId,
    },
    /// Avatars whose transform changed since the last tick.
    AvatarStates(Vec<AvatarStateUpdate>),
    /// The actor's reported state was rejected; it must snap to `transform`.
    AvatarCorrection {
        transform: AvatarTransform,
        reason: String,
    },
    /// An avatar's name, role or appearance changed.
    AvatarUpdated(Avatar),
    Chat(ChatEntry),
    /// Retained world chat, oldest first.
    ChatHistory(Vec<ChatEntry>),
    ObjectCreated {
        object: Object,
        by: EntityId,
    },
    /// Carries the full new state of the object.
    ObjectUpdated {
        object: Object,
        by: EntityId,
    },
    ObjectDeleted {
        id: EntityId,
        by: EntityId,
    },
    Observation(Box<Observation>),
    /// The avatar has been removed from the world; the server must tell the
    /// client and close its connection. An `AvatarLeft` for the others is
    /// emitted alongside.
    Kicked {
        reason: String,
    },
    /// The command succeeded and has no more specific result.
    Ack,
    /// Host: refuse this user from now on.
    UserBanned {
        user_id: UserId,
        reason: String,
    },
    /// Host: allow this user again.
    UserUnbanned {
        user_id: UserId,
    },
    /// Host: remember this user's role.
    UserRoleChanged {
        user_id: UserId,
        role: Role,
    },
}
