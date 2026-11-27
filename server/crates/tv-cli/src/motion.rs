//! Geometry and the movement planners used by the bots.
//!
//! Conventions (proto `common.proto`): left-handed, Y up; yaw in degrees,
//! clockwise seen from above, 0 = facing +Z, so yaw 90 faces +X and the
//! heading for yaw `a` is `(sin a, 0, cos a)`.

use std::ops::{Add, Mul, Sub};

use tv_proto::v1::{AvatarTransform, Limits, MoveMode, Vec3};

/// A small value-type vector for arithmetic; converts to and from `Vec3`.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct V3 {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

impl V3 {
    pub const ZERO: V3 = V3 { x: 0.0, y: 0.0, z: 0.0 };

    pub fn new(x: f32, y: f32, z: f32) -> Self {
        V3 { x, y, z }
    }

    pub fn len(self) -> f32 {
        (self.x * self.x + self.y * self.y + self.z * self.z).sqrt()
    }

    /// Length in the horizontal (XZ) plane.
    pub fn hlen(self) -> f32 {
        (self.x * self.x + self.z * self.z).sqrt()
    }

    pub fn dist(self, o: V3) -> f32 {
        (self - o).len()
    }

    pub fn hdist(self, o: V3) -> f32 {
        (self - o).hlen()
    }
}

impl Add for V3 {
    type Output = V3;
    fn add(self, o: V3) -> V3 {
        V3::new(self.x + o.x, self.y + o.y, self.z + o.z)
    }
}

impl Sub for V3 {
    type Output = V3;
    fn sub(self, o: V3) -> V3 {
        V3::new(self.x - o.x, self.y - o.y, self.z - o.z)
    }
}

impl Mul<f32> for V3 {
    type Output = V3;
    fn mul(self, k: f32) -> V3 {
        V3::new(self.x * k, self.y * k, self.z * k)
    }
}

impl From<Vec3> for V3 {
    fn from(v: Vec3) -> Self {
        V3::new(v.x, v.y, v.z)
    }
}

impl From<Option<Vec3>> for V3 {
    fn from(v: Option<Vec3>) -> Self {
        v.map(V3::from).unwrap_or_default()
    }
}

impl From<V3> for Vec3 {
    fn from(v: V3) -> Self {
        Vec3 { x: v.x, y: v.y, z: v.z }
    }
}

/// Normalises an angle in degrees to `-180..=180`.
pub fn norm180(a: f32) -> f32 {
    let mut a = a % 360.0;
    if a > 180.0 {
        a -= 360.0;
    } else if a < -180.0 {
        a += 360.0;
    }
    a
}

/// Normalises an angle in degrees to `0..360`.
pub fn norm360(a: f32) -> f32 {
    let a = a % 360.0;
    if a < 0.0 { a + 360.0 } else { a }
}

/// Yaw (degrees, 0..360) of a horizontal direction.
pub fn yaw_of(d: V3) -> f32 {
    norm360(d.x.atan2(d.z).to_degrees())
}

/// Bearing of `target` as seen from `from` facing `yaw`: 0 = straight ahead,
/// positive = to the right, `-180..=180`.
pub fn bearing(from: V3, yaw: f32, target: V3) -> f32 {
    norm180(yaw_of(target - from) - yaw)
}

/// The server's speed limit for a mode, from `Limits`, with the spec's
/// defaults (walk 4, run 8, fly 16 m/s) for missing values.
pub fn speed_limit(limits: Option<&Limits>, mode: MoveMode) -> f32 {
    let (walk, run, fly) = match limits {
        Some(l) => (l.walk_speed, l.run_speed, l.fly_speed),
        None => (0.0, 0.0, 0.0),
    };
    let pick = |v: f32, d: f32| if v > 0.0 { v } else { d };
    match mode {
        MoveMode::Run => pick(run, 8.0),
        MoveMode::Fly => pick(fly, 16.0),
        _ => pick(walk, 4.0),
    }
}

/// Fraction of the speed limit the bots move at, leaving room for jitter.
pub const SPEED_MARGIN: f32 = 0.85;

/// The animation hint for a moving avatar in a mode.
pub fn animation_for(mode: MoveMode) -> &'static str {
    match mode {
        MoveMode::Run => "run",
        MoveMode::Fly => "fly",
        _ => "walk",
    }
}

fn transform(pos: V3, yaw: f32, vel: V3, mode: MoveMode, animation: &str) -> AvatarTransform {
    AvatarTransform {
        position: Some(pos.into()),
        yaw: norm360(yaw),
        velocity: Some(vel.into()),
        move_mode: mode as i32,
        animation: animation.into(),
    }
}

/// Height above the centre at which the circle bot flies.
pub const FLY_HEIGHT: f32 = 3.0;

#[derive(Clone, Copy, Debug, PartialEq)]
enum Phase {
    /// Moving in a straight line to the circle point at angle `theta`.
    Approach { theta: f32 },
    /// On the circle at angle `theta` (radians), moving clockwise.
    Orbit { theta: f32 },
}

/// Moves an avatar around a horizontal circle at constant speed. It first
/// walks in a straight line from wherever it is to the nearest point of the
/// circle, so it never jumps.
#[derive(Clone, Debug)]
pub struct CirclePlan {
    center: V3,
    radius: f32,
    speed: f32,
    mode: MoveMode,
    pos: V3,
    yaw: f32,
    phase: Phase,
}

impl CirclePlan {
    /// `center` is where the circle is centred (the spawn point); flying
    /// circles are [`FLY_HEIGHT`] above it. `start` is the avatar's current
    /// position and `yaw` its current heading.
    pub fn new(center: V3, start: V3, yaw: f32, radius: f32, speed: f32, mode: MoveMode) -> Self {
        let lift = if mode == MoveMode::Fly { FLY_HEIGHT } else { 0.0 };
        let center = center + V3::new(0.0, lift, 0.0);
        let mut plan = CirclePlan {
            center,
            radius,
            speed,
            mode,
            pos: start,
            yaw,
            phase: Phase::Orbit { theta: 0.0 },
        };
        plan.correct(start);
        plan
    }

    pub fn center(&self) -> V3 {
        self.center
    }

    pub fn radius(&self) -> f32 {
        self.radius
    }

    pub fn position(&self) -> V3 {
        self.pos
    }

    /// True once the avatar is on the circle.
    pub fn orbiting(&self) -> bool {
        matches!(self.phase, Phase::Orbit { .. })
    }

    fn point(&self, theta: f32) -> V3 {
        self.center + V3::new(theta.sin(), 0.0, theta.cos()) * self.radius
    }

    /// The server moved the avatar: carry on from `pos`, heading back to the
    /// nearest point of the circle.
    pub fn correct(&mut self, pos: V3) {
        self.pos = pos;
        let d = pos - self.center;
        let theta = if d.hlen() < 1e-3 { 0.0 } else { d.x.atan2(d.z) };
        self.phase = Phase::Approach { theta };
    }

    /// Advances by `dt` seconds and returns the state to report.
    pub fn step(&mut self, dt: f32) -> AvatarTransform {
        let dt = dt.max(0.0);
        let anim = animation_for(self.mode);
        match self.phase {
            Phase::Approach { theta } => {
                let target = self.point(theta);
                let to = target - self.pos;
                let d = to.len();
                let step = self.speed * dt;
                if d <= step || d < 1e-4 {
                    self.pos = target;
                    self.phase = Phase::Orbit { theta };
                    self.yaw = theta.to_degrees() + 90.0;
                    let vel = V3::new(theta.cos(), 0.0, -theta.sin()) * self.speed;
                    transform(self.pos, self.yaw, vel, self.mode, anim)
                } else {
                    let dir = to * (1.0 / d);
                    self.pos = self.pos + dir * step;
                    if dir.hlen() > 1e-3 {
                        self.yaw = yaw_of(dir);
                    }
                    transform(self.pos, self.yaw, dir * self.speed, self.mode, anim)
                }
            }
            Phase::Orbit { theta } => {
                let theta = (theta + self.speed / self.radius * dt) % std::f32::consts::TAU;
                self.phase = Phase::Orbit { theta };
                self.pos = self.point(theta);
                // Tangent of (sin t, cos t) is (cos t, -sin t): yaw t + 90.
                self.yaw = theta.to_degrees() + 90.0;
                let vel = V3::new(theta.cos(), 0.0, -theta.sin()) * self.speed;
                transform(self.pos, self.yaw, vel, self.mode, anim)
            }
        }
    }

    /// A standing-still state at the current position.
    pub fn idle(&self) -> AvatarTransform {
        transform(self.pos, self.yaw, V3::ZERO, self.mode, "idle")
    }
}

/// Moves an avatar in straight lines to targets, for the wander bot.
#[derive(Clone, Debug)]
pub struct Walker {
    pub pos: V3,
    pub yaw: f32,
    pub speed: f32,
    pub mode: MoveMode,
    pub target: Option<V3>,
}

impl Walker {
    pub fn new(pos: V3, yaw: f32, speed: f32, mode: MoveMode) -> Self {
        Walker { pos, yaw, speed, mode, target: None }
    }

    /// Advances by `dt`. Returns the state and whether the target was reached
    /// during this step.
    pub fn step(&mut self, dt: f32) -> (AvatarTransform, bool) {
        let Some(target) = self.target else { return (self.idle(), false) };
        let to = target - self.pos;
        let d = to.len();
        let step = self.speed * dt.max(0.0);
        if d <= step || d < 1e-4 {
            self.pos = target;
            self.target = None;
            return (self.idle(), true);
        }
        let dir = to * (1.0 / d);
        self.pos = self.pos + dir * step;
        if dir.hlen() > 1e-3 {
            self.yaw = yaw_of(dir);
        }
        (
            transform(self.pos, self.yaw, dir * self.speed, self.mode, animation_for(self.mode)),
            false,
        )
    }

    pub fn idle(&self) -> AvatarTransform {
        transform(self.pos, self.yaw, V3::ZERO, self.mode, "idle")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn yaw_follows_the_protocol_convention() {
        assert_eq!(yaw_of(V3::new(0.0, 0.0, 1.0)), 0.0);
        assert!((yaw_of(V3::new(1.0, 0.0, 0.0)) - 90.0).abs() < 1e-4);
        assert!((yaw_of(V3::new(0.0, 0.0, -1.0)) - 180.0).abs() < 1e-4);
        assert!((yaw_of(V3::new(-1.0, 0.0, 0.0)) - 270.0).abs() < 1e-4);
    }

    #[test]
    fn bearing_is_positive_to_the_right() {
        let o = V3::ZERO;
        assert!((bearing(o, 0.0, V3::new(1.0, 0.0, 0.0)) - 90.0).abs() < 1e-4);
        assert!((bearing(o, 0.0, V3::new(-1.0, 0.0, 0.0)) + 90.0).abs() < 1e-4);
        assert!((bearing(o, 90.0, V3::new(1.0, 0.0, 0.0))).abs() < 1e-4);
        assert!((bearing(o, 350.0, V3::new(0.0, 0.0, 1.0)) - 10.0).abs() < 1e-3);
        assert!((bearing(o, 0.0, V3::new(0.0, 0.0, -1.0)).abs() - 180.0).abs() < 1e-4);
    }

    fn check_circle(mode: MoveMode, start: V3) {
        let center = V3::new(10.0, 0.0, -4.0);
        let limit = speed_limit(None, mode);
        let speed = limit * SPEED_MARGIN;
        let mut plan = CirclePlan::new(center, start, 0.0, 5.0, speed, mode);
        let mut prev = start;
        let mut orbit_steps = 0;
        for i in 0..400 {
            let dt = if i % 7 == 0 { 0.13 } else { 0.1 };
            let t = plan.step(dt);
            let p = V3::from(t.position);
            let v = V3::from(t.velocity);
            assert!(p.dist(prev) <= speed * dt + 1e-3, "step {i} jumped {}", p.dist(prev));
            assert!(v.len() <= limit, "velocity {v:?} over limit");
            assert_eq!(t.move_mode, mode as i32);
            assert_eq!(t.animation, animation_for(mode));
            if v.hlen() > 0.1 {
                let heading = yaw_of(v);
                assert!(
                    norm180(heading - t.yaw).abs() < 0.01,
                    "yaw {} vs heading {heading}",
                    t.yaw
                );
            }
            if plan.orbiting() {
                orbit_steps += 1;
                let r = p.hdist(plan.center());
                assert!((r - 5.0).abs() < 1e-3, "off the circle: r = {r}");
                assert!((p.y - plan.center().y).abs() < 1e-4);
            }
            prev = p;
        }
        assert!(orbit_steps > 300, "never reached the circle");
    }

    #[test]
    fn circle_is_legal_from_the_centre_and_from_outside() {
        check_circle(MoveMode::Walk, V3::new(10.0, 0.0, -4.0));
        check_circle(MoveMode::Run, V3::new(30.0, 0.0, 12.0));
        check_circle(MoveMode::Fly, V3::new(10.0, 0.0, -4.0));
    }

    #[test]
    fn correction_restarts_the_approach() {
        let mut plan = CirclePlan::new(V3::ZERO, V3::ZERO, 0.0, 5.0, 3.0, MoveMode::Walk);
        for _ in 0..50 {
            plan.step(0.1);
        }
        assert!(plan.orbiting());
        plan.correct(V3::new(1.0, 0.0, 0.0));
        assert!(!plan.orbiting());
        let t = plan.step(0.1);
        let p = V3::from(t.position);
        assert!(p.dist(V3::new(1.0, 0.0, 0.0)) <= 0.3 + 1e-4);
        // Heads straight outwards, along +X.
        assert!((t.yaw - 90.0).abs() < 1e-3);
    }

    #[test]
    fn walker_arrives_and_stops() {
        let mut w = Walker::new(V3::ZERO, 0.0, 2.0, MoveMode::Walk);
        w.target = Some(V3::new(0.0, 0.0, -1.0));
        let (t, arrived) = w.step(0.25);
        assert!(!arrived);
        assert!((t.yaw - 180.0).abs() < 1e-3);
        assert_eq!(t.animation, "walk");
        let (t, arrived) = w.step(0.5);
        assert!(arrived);
        assert_eq!(V3::from(t.velocity), V3::ZERO);
        assert_eq!(t.animation, "idle");
        assert_eq!(w.pos, V3::new(0.0, 0.0, -1.0));
    }
}
