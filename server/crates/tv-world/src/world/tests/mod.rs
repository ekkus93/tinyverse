//! Unit tests of the world rules, one file per area. Shared fixtures live here.

mod admin;
mod chat;
mod movement;
mod objects;
mod observe;
mod perf;
mod presence;

use glam::{Quat, Vec3};

use super::World;
use crate::command::{Command, JoinRequest};
use crate::event::{Audience, Event, EventKind};
use crate::types::{
    Appearance, AvatarTransform, ClientKind, EntityId, MoveMode, ObjectSpec, Role, WorldConfig,
};

/// A plausible "now" so that time arithmetic is realistic.
const T0: i64 = 1_700_000_000_000;

fn world() -> World {
    World::new(WorldConfig::default(), Vec::new(), 1)
}

fn world_with(configure: impl FnOnce(&mut WorldConfig)) -> World {
    let mut config = WorldConfig::default();
    configure(&mut config);
    World::new(config, Vec::new(), 1)
}

fn request(user: &str, name: &str, role: Role) -> JoinRequest {
    JoinRequest {
        user_id: user.into(),
        name: name.to_owned(),
        kind: ClientKind::Human,
        role,
        appearance: Appearance::default(),
    }
}

/// Joins a human whose user id and name are both `user`.
fn join(world: &mut World, user: &str, role: Role) -> EntityId {
    world.join(request(user, user, role), T0).expect("join").0
}

fn join_agent(world: &mut World, user: &str, role: Role) -> EntityId {
    let mut req = request(user, user, role);
    req.kind = ClientKind::Agent;
    world.join(req, T0).expect("join").0
}

/// Puts an avatar somewhere directly, bypassing the speed check.
fn place(world: &mut World, id: EntityId, position: Vec3, yaw: f32) {
    let presence = world.avatars.get_mut(&id).expect("present");
    presence.avatar.transform.position = position;
    presence.avatar.transform.yaw = yaw;
}

fn spec_at(position: Vec3) -> ObjectSpec {
    ObjectSpec { position, ..ObjectSpec::default() }
}

/// Creates an object and returns its id.
fn create(world: &mut World, actor: EntityId, spec: ObjectSpec) -> EntityId {
    let events = world.apply(actor, Command::ObjectCreate(spec), T0).expect("create");
    match &events[0].kind {
        EventKind::ObjectCreated { object, .. } => object.id,
        other => panic!("expected ObjectCreated, got {other:?}"),
    }
}

fn transform(position: Vec3, mode: MoveMode) -> AvatarTransform {
    AvatarTransform {
        position,
        yaw: 0.0,
        velocity: Vec3::ZERO,
        move_mode: mode,
        animation: "walk".to_owned(),
    }
}

fn audiences(events: &[Event]) -> Vec<Audience> {
    events.iter().map(|e| e.audience.clone()).collect()
}

fn only_ack(events: &[Event], actor: EntityId) {
    assert_eq!(events, &[Event::new(Audience::Only(actor), EventKind::Ack)]);
}

fn approx(a: f32, b: f32) -> bool {
    (a - b).abs() < 1e-3
}

fn approx_vec(a: Vec3, b: Vec3) -> bool {
    a.distance(b) < 1e-3
}

fn approx_quat(a: Quat, b: Quat) -> bool {
    a.dot(b).abs() > 1.0 - 1e-5
}
