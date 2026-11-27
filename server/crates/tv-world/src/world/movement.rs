//! Client-reported avatar state, server-driven `MoveTo`, and the per-tick
//! batch of changed transforms.

use glam::Vec3;

use super::validate::{self, MAX_TAG_CHARS};
use super::{MoveTarget, World, ack};
use crate::error::WorldError;
use crate::event::{Audience, AvatarStateUpdate, Event, EventKind};
use crate::types::{AvatarTransform, EntityId, Limits, MoveMode};

/// Reports closer together than this are judged as if this far apart, so a
/// burst of packets after a network stall is not punished.
const MIN_ELAPSED_S: f32 = 0.05;
/// However long an avatar stood still, it may not cover more than this many
/// seconds' worth of travel in one report.
const MAX_ELAPSED_S: f32 = 2.0;
/// Absorbs float rounding at exactly the speed limit.
const SPEED_EPSILON_M: f32 = 1e-3;
/// Closer than this to a `MoveTo` target counts as arrived.
const ARRIVE_EPSILON_M: f32 = 1e-4;

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

        let violation = if !in_bounds {
            Some("out of bounds")
        } else {
            let elapsed_s = (now_ms.saturating_sub(presence.last_accepted_ms) as f32 / 1000.0)
                .clamp(MIN_ELAPSED_S, MAX_ELAPSED_S);
            let allowed = speed(limits, transform.move_mode) * limits.speed_tolerance * elapsed_s;
            let moved = transform.position.distance(presence.avatar.transform.position);
            (moved > allowed + SPEED_EPSILON_M).then_some("too fast")
        };
        if let Some(reason) = violation {
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
        presence.last_accepted_ms = now_ms;
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
        let current = self.actor(actor)?.transform.position;
        let target = match mode {
            MoveMode::Walk | MoveMode::Run => Vec3::new(target.x, current.y, target.z),
            MoveMode::Fly => target,
        };
        validate::check_bounds(&self.config, target, "the target")?;
        let presence = self.avatars.get_mut(&actor).ok_or(WorldError::NotJoined)?;
        presence.move_to = Some(MoveTarget { target, mode });
        Ok(vec![ack(actor)])
    }

    pub(super) fn stop(&mut self, actor: EntityId) -> Vec<Event> {
        if let Some(presence) = self.avatars.get_mut(&actor)
            && presence.move_to.take().is_some()
        {
            let transform = &mut presence.avatar.transform;
            let before = transform.clone();
            transform.velocity = Vec3::ZERO;
            transform.animation = "idle".to_owned();
            if *transform != before {
                presence.changed = Some(true);
            }
        }
        vec![ack(actor)]
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
            let speed = speed(limits, mode);
            let delta = target - transform.position;
            let remaining = delta.length();
            if let Some(heading) = heading_of(delta) {
                transform.yaw = heading;
            }
            transform.move_mode = mode;
            if remaining <= speed * dt || remaining < ARRIVE_EPSILON_M {
                transform.position = target;
                transform.velocity = Vec3::ZERO;
                transform.animation = "idle".to_owned();
                presence.move_to = None;
            } else {
                let direction = delta / remaining;
                transform.position += direction * (speed * dt);
                transform.velocity = direction * speed;
                transform.animation = moving_animation(mode).to_owned();
            }
            presence.last_accepted_ms = now_ms;
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
