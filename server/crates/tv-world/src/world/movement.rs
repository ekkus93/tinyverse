//! Client-reported avatar state, server-driven `MoveTo`, and the per-tick
//! batch of changed transforms.

use glam::Vec3;

use super::allowance::Allowance;
use super::validate::{self, MAX_TAG_CHARS};
use super::{MoveTarget, World, ack};
use crate::error::WorldError;
use crate::event::{Audience, AvatarStateUpdate, Event, EventKind};
use crate::types::{AvatarTransform, EntityId, Limits, MoveMode};

/// Closer than this to a `MoveTo` target counts as arrived.
const ARRIVE_EPSILON_M: f32 = 1e-4;
/// How fast a walking or running `MoveTo` brings the avatar to ground level,
/// up or down, in metres per second.
const GROUND_SETTLE_SPEED: f32 = 10.0;
/// Higher than this above ground, a walking or running `MoveTo` shows "fall".
const FALL_ANIMATION_HEIGHT_M: f32 = 0.5;

/// One tick of flight in a straight 3D line: the new position and velocity,
/// or `None` if the avatar arrives at `target` within this tick.
fn fly_step(position: Vec3, target: Vec3, speed: f32, dt: f32) -> Option<(Vec3, Vec3)> {
    let delta = target - position;
    let remaining = delta.length();
    if remaining <= speed * dt || remaining < ARRIVE_EPSILON_M {
        return None;
    }
    let direction = delta / remaining;
    Some((position + direction * (speed * dt), direction * speed))
}

/// One tick of a walking or running `MoveTo`: horizontally towards `target`
/// at `speed` and, at the same time, vertically towards ground level
/// (`target.y`) at [`GROUND_SETTLE_SPEED`]. Returns the new position and
/// velocity, or `None` once both have been reached.
fn ground_step(position: Vec3, target: Vec3, speed: f32, dt: f32) -> Option<(Vec3, Vec3)> {
    let horizontal = Vec3::new(target.x - position.x, 0.0, target.z - position.z);
    let remaining = horizontal.length();
    let (mut next, mut velocity) = (position, Vec3::ZERO);
    if remaining <= speed * dt || remaining < ARRIVE_EPSILON_M {
        next.x = target.x;
        next.z = target.z;
    } else {
        let direction = horizontal / remaining;
        next += direction * (speed * dt);
        velocity = direction * speed;
    }

    let rise = target.y - position.y;
    if rise.abs() <= GROUND_SETTLE_SPEED * dt || rise.abs() < ARRIVE_EPSILON_M {
        next.y = target.y;
    } else {
        next.y = position.y + rise.signum() * GROUND_SETTLE_SPEED * dt;
        velocity.y = rise.signum() * GROUND_SETTLE_SPEED;
    }

    let arrived = next.x == target.x && next.z == target.z && next.y == target.y;
    (!arrived).then_some((next, velocity))
}

pub(super) fn speed(limits: &Limits, mode: MoveMode) -> f32 {
    match mode {
        MoveMode::Walk => limits.walk_speed,
        MoveMode::Run => limits.run_speed,
        MoveMode::Fly => limits.fly_speed,
    }
}

fn moving_animation(mode: MoveMode) -> &'static str {
    match mode {
        MoveMode::Walk => "walk",
        MoveMode::Run => "run",
        MoveMode::Fly => "fly",
    }
}

/// Heading in degrees, 0..360, of a horizontal direction (clockwise seen from
/// above, 0 = +Z, 90 = +X), or `None` if it has no horizontal component.
pub(super) fn heading_of(direction: Vec3) -> Option<f32> {
    if direction.x.hypot(direction.z) < 1e-6 {
        None
    } else {
        Some(direction.x.atan2(direction.z).to_degrees().rem_euclid(360.0))
    }
}

impl World {
    pub(super) fn avatar_state(
        &mut self,
        actor: EntityId,
        transform: AvatarTransform,
        now_ms: i64,
    ) -> Result<Vec<Event>, WorldError> {
        validate::finite_vec(transform.position, "position")?;
        validate::finite(transform.yaw, "yaw")?;
        validate::finite_vec(transform.velocity, "velocity")?;

        let limits = &self.config.limits;
        let in_bounds = validate::in_bounds(&self.config, transform.position);
        let presence = self.avatars.get_mut(&actor).ok_or(WorldError::NotJoined)?;

        let delta = transform.position - presence.avatar.transform.position;
        let violation = if !in_bounds {
            Some("out of bounds")
        } else if !presence.allowance.try_spend(limits, transform.move_mode, delta, now_ms) {
            Some("too fast")
        } else {
            None
        };
        if let Some(reason) = violation {
            presence.allowance.after_correction(
                limits,
                transform.move_mode,
                presence.avatar.transform.move_mode,
                now_ms,
            );
            return Ok(vec![Event::new(
                Audience::Only(actor),
                EventKind::AvatarCorrection {
                    transform: presence.avatar.transform.clone(),
                    reason: reason.to_owned(),
                },
            )]);
        }

        let transform = AvatarTransform {
            animation: validate::truncate_chars(&transform.animation, MAX_TAG_CHARS),
            ..transform
        };
        presence.move_to = None;
        if presence.avatar.transform != transform {
            presence.avatar.transform = transform;
            presence.changed = Some(false);
        }
        Ok(Vec::new())
    }

    pub(super) fn move_to(
        &mut self,
        actor: EntityId,
        target: Vec3,
        mode: MoveMode,
    ) -> Result<Vec<Event>, WorldError> {
        validate::finite_vec(target, "target")?;
        self.actor(actor)?;
        let target = match mode {
            MoveMode::Walk | MoveMode::Run => Vec3::new(target.x, self.ground_y(), target.z),
            MoveMode::Fly => target,
        };
        validate::check_bounds(&self.config, target, "the target")?;
        let presence = self.avatars.get_mut(&actor).ok_or(WorldError::NotJoined)?;
        presence.move_to = Some(MoveTarget { target, mode });
        Ok(vec![ack(actor)])
    }

    pub(super) fn stop(&mut self, actor: EntityId, now_ms: i64) -> Vec<Event> {
        let limits = &self.config.limits;
        if let Some(presence) = self.avatars.get_mut(&actor)
            && presence.move_to.take().is_some()
        {
            let transform = &mut presence.avatar.transform;
            let before = transform.clone();
            transform.velocity = Vec3::ZERO;
            transform.animation = "idle".to_owned();
            presence.allowance = Allowance::fresh(limits, transform.move_mode, now_ms);
            if *transform != before {
                presence.changed = Some(true);
            }
        }
        vec![ack(actor)]
    }

    /// Ground level for server-driven walking: the plane y = 0, clamped into
    /// the world bounds. The server has no collision, so it knows no other
    /// floor.
    pub(super) fn ground_y(&self) -> f32 {
        0.0f32.clamp(self.config.bounds_min.y, self.config.bounds_max.y)
    }

    /// Moves every avatar that follows a `MoveTo` by `dt` seconds.
    pub(super) fn advance_moves(&mut self, dt: f32, now_ms: i64) {
        let dt = if dt.is_finite() { dt.max(0.0) } else { 0.0 };
        let limits = &self.config.limits;
        for presence in self.avatars.values_mut() {
            let Some(MoveTarget { target, mode }) = presence.move_to else {
                continue;
            };
            let transform = &mut presence.avatar.transform;
            let before = transform.clone();
            let delta = target - transform.position;
            if let Some(heading) = heading_of(delta) {
                transform.yaw = heading;
            }
            transform.move_mode = mode;
            let step = match mode {
                MoveMode::Fly => fly_step(transform.position, target, speed(limits, mode), dt),
                MoveMode::Walk | MoveMode::Run => {
                    ground_step(transform.position, target, speed(limits, mode), dt)
                }
            };
            match step {
                None => {
                    transform.position = target;
                    transform.velocity = Vec3::ZERO;
                    transform.animation = "idle".to_owned();
                    presence.move_to = None;
                }
                Some((position, velocity)) => {
                    transform.position = position;
                    transform.velocity = velocity;
                    let falling =
                        mode != MoveMode::Fly && position.y - target.y > FALL_ANIMATION_HEIGHT_M;
                    transform.animation =
                        if falling { "fall" } else { moving_animation(mode) }.to_owned();
                }
            }
            presence.allowance = Allowance::fresh(limits, mode, now_ms);
            if *transform != before {
                presence.changed = Some(true);
            }
        }
    }

    /// Every avatar whose transform changed since the last call, once, in
    /// ascending id order.
    pub(super) fn take_changed_states(&mut self) -> Vec<AvatarStateUpdate> {
        self.avatars
            .values_mut()
            .filter_map(|presence| {
                presence.changed.take().map(|server_driven| AvatarStateUpdate {
                    id: presence.avatar.id,
                    transform: presence.avatar.transform.clone(),
                    server_driven,
                })
            })
            .collect()
    }
}
