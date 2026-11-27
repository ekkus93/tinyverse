//! Tinyverse world state and rules.
//!
//! This crate is pure and synchronous: no sockets, no protobuf, no clock, no
//! disk. The server owns one [`World`], feeds it [`Command`]s from connected
//! avatars and delivers the [`Event`]s it returns. Every rule of the world —
//! validation, permissions, limits — lives here, so all of it is unit-tested.
//!
//! Time is always passed in (`now_ms`, milliseconds since the Unix epoch), so
//! tests are deterministic.
//!
//! The public types of this crate are a frozen contract between the server,
//! the store and the tests. See `docs/SPEC.md` section 7.

mod command;
mod error;
mod event;
mod types;
mod world;

pub use command::{Command, JoinRequest};
pub use error::WorldError;
pub use event::{Audience, AvatarStateUpdate, Event, EventKind};
pub use glam::{Quat, Vec3};
pub use types::{
    Appearance, Avatar, AvatarTransform, ChatChannel, ChatEntry, ClientKind, Color, EntityId,
    Limits, Material, MoveMode, Object, ObjectPatch, ObjectSpec, Observation, Role, Seen, Shape,
    UserId, WorldConfig,
};
pub use world::World;
