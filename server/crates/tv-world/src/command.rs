//! What an avatar can ask the world to do.

use glam::Vec3;

use crate::types::{
    Appearance, AvatarTransform, ChatChannel, ClientKind, MoveMode, ObjectPatch, ObjectSpec, Role,
    UserId,
};

/// Everything needed to put a new avatar into the world. The server has
/// already authenticated the user and looked up their role.
#[derive(Clone, Debug, PartialEq)]
pub struct JoinRequest {
    pub user_id: UserId,
    pub name: String,
    pub kind: ClientKind,
    pub role: Role,
    pub appearance: Appearance,
}

/// A request from an avatar that is present in the world. One variant per
/// client message of the protocol, except `Hello` (see [`crate::World::join`])
/// and `Ping` (answered by the server itself).
#[derive(Clone, Debug, PartialEq)]
pub enum Command {
    /// The client's own simulated state. Cancels a `MoveTo` in progress.
    /// Checked against the speed limit of its move mode and the world bounds;
    /// on a violation the avatar keeps its last accepted state and the actor
    /// receives [`crate::EventKind::AvatarCorrection`]. This is not an error.
    AvatarState(AvatarTransform),
    /// Move the avatar in a straight line to `target`, advanced by
    /// [`crate::World::tick`]. `Walk` and `Run` stay on the ground.
    MoveTo {
        target: Vec3,
        mode: MoveMode,
    },
    /// Cancel a `MoveTo` in progress. A no-op otherwise.
    Stop,
    Chat {
        channel: ChatChannel,
        text: String,
    },
    /// Ask for retained world chat newer than `since_ms` (0 = all), keeping
    /// at most the newest `limit` (0 = no limit).
    ChatHistory {
        since_ms: i64,
        limit: usize,
    },
    ObjectCreate(ObjectSpec),
    ObjectUpdate {
        id: u64,
        patch: ObjectPatch,
    },
    ObjectDelete {
        id: u64,
    },
    /// Describe what is within `radius` metres (0 = the default radius).
    Observe {
        radius: f32,
    },
    SetAppearance(Appearance),
    /// Owner only. Removes the user's avatar if present.
    Kick {
        user_id: UserId,
        reason: String,
    },
    /// Owner only. Removes the user's avatar if present and asks the host to
    /// refuse them from now on.
    Ban {
        user_id: UserId,
        reason: String,
    },
    /// Owner only.
    Unban {
        user_id: UserId,
    },
    /// Owner only. Applies at once if the user is present, and asks the host
    /// to remember it.
    SetRole {
        user_id: UserId,
        role: Role,
    },
}
