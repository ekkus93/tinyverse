//! The speed check for client-reported avatar state: a per-avatar allowance
//! of distance that accrues over time (a token bucket).
//!
//! Judging each report alone against the time since the previous one
//! punishes honest clients whose reports arrive unevenly. Instead each avatar
//! holds three budgets, each refilled at its limit rate and capped at what
//! [`CAP_S`] seconds would bring:
//!
//! * `main`: horizontal distance in Walk and Run mode, 3D distance in Fly
//!   mode, at `speed(mode) * speed_tolerance`;
//! * `down`: downward distance in Walk and Run mode, at [`MAX_FALL_SPEED`];
//! * `up`: upward distance in Walk and Run mode, at [`MAX_RISE_SPEED`].
//!
//! An accepted report spends what it moved; a refused one spends nothing,
//! and the correction it causes caps every budget at [`START_S`] seconds'
//! worth.
//! Sustained travel above the limit drains the budget within [`CAP_S`]
//! seconds, and no single report can move further than a full budget.

use glam::Vec3;

use super::movement::speed;
use crate::types::{Limits, MoveMode};

/// The most any budget can hold, in seconds of travel at its rate. A long
/// idle gap authorises no more than this.
const CAP_S: f32 = 2.0;
/// What a fresh budget holds, in seconds of travel: after joining and after a
/// server-driven move, so the first reports are not punished. A correction
/// caps the budgets at this much (see [`Allowance::after_correction`]).
const START_S: f32 = 0.25;
/// Fastest fall accepted in Walk and Run mode, in metres per second. Fixed,
/// with no tolerance factor: the client applies gravity.
const MAX_FALL_SPEED: f32 = 60.0;
/// Fastest rise accepted in Walk and Run mode (jumps, steps, being pushed up
/// onto an object), in metres per second. Fixed, with no tolerance factor.
const MAX_RISE_SPEED: f32 = 10.0;
/// Absorbs float rounding at exactly the limit.
const EPSILON_M: f32 = 1e-3;

fn main_rate(limits: &Limits, mode: MoveMode) -> f32 {
    speed(limits, mode) * limits.speed_tolerance
}

/// How far an avatar may still move, per kind of movement.
#[derive(Clone, Debug, PartialEq)]
pub(super) struct Allowance {
    /// The mode `main` was accrued for.
    mode: MoveMode,
    main: f32,
    down: f32,
    up: f32,
    /// When the budgets were last refilled.
    refilled_ms: i64,
}

impl Allowance {
    /// A fresh allowance of [`START_S`] seconds' travel in `mode`.
    pub(super) fn fresh(limits: &Limits, mode: MoveMode, now_ms: i64) -> Self {
        Allowance {
            mode,
            main: main_rate(limits, mode) * START_S,
            down: MAX_FALL_SPEED * START_S,
            up: MAX_RISE_SPEED * START_S,
            refilled_ms: now_ms,
        }
    }

    /// Refills the budgets for the time since the last refill, judged in
    /// `mode`, which is the mode of the incoming report. Time that does not
    /// move forward adds nothing.
    ///
    /// On a change of mode the carried `main` budget is first cut to what the
    /// new mode may hold, and the interval accrues at the slower of the two
    /// modes' rates, so switching to a faster mode gains nothing
    /// retroactively and switching to a slower one carries nothing over.
    fn refill(&mut self, limits: &Limits, mode: MoveMode, now_ms: i64) {
        let elapsed_s = if now_ms > self.refilled_ms {
            let elapsed = (now_ms - self.refilled_ms) as f32 / 1000.0;
            self.refilled_ms = now_ms;
            elapsed.min(CAP_S)
        } else {
            0.0
        };
        let new_rate = main_rate(limits, mode);
        let rate =
            if mode == self.mode { new_rate } else { new_rate.min(main_rate(limits, self.mode)) };
        self.mode = mode;
        let main_cap = new_rate * CAP_S;
        self.main = (self.main.min(main_cap) + rate * elapsed_s).min(main_cap);
        self.down = (self.down + MAX_FALL_SPEED * elapsed_s).min(MAX_FALL_SPEED * CAP_S);
        self.up = (self.up + MAX_RISE_SPEED * elapsed_s).min(MAX_RISE_SPEED * CAP_S);
    }

    /// Refills for a report in `mode` arriving at `now_ms` that moves the
    /// avatar by `delta`, then spends the distance and returns true if the
    /// budgets cover it. If they do not, nothing is spent and it returns
    /// false.
    pub(super) fn try_spend(
        &mut self,
        limits: &Limits,
        mode: MoveMode,
        delta: Vec3,
        now_ms: i64,
    ) -> bool {
        self.refill(limits, mode, now_ms);
        let (main, down, up) = match mode {
            MoveMode::Fly => (delta.length(), 0.0, 0.0),
            MoveMode::Walk | MoveMode::Run => {
                (delta.x.hypot(delta.z), (-delta.y).max(0.0), delta.y.max(0.0))
            }
        };
        let covered = main <= self.main + EPSILON_M
            && down <= self.down + EPSILON_M
            && up <= self.up + EPSILON_M;
        if covered {
            self.main = (self.main - main).max(0.0);
            self.down = (self.down - down).max(0.0);
            self.up = (self.up - up).max(0.0);
        }
        covered
    }

    /// Settles the budgets after a report in `report_mode` was refused at
    /// `now_ms` and the avatar was corrected back to its last accepted state
    /// in `accepted_mode`. The time still counts, then every budget is capped
    /// at [`START_S`] seconds' worth: enough for a client that obeys the
    /// correction to carry on, but never more than it had. Raising the
    /// budgets to that level instead would hand a client that ignores
    /// corrections fresh distance with each one, enough to sustain 8 m/s
    /// against a 6 m/s walking limit.
    pub(super) fn after_correction(
        &mut self,
        limits: &Limits,
        report_mode: MoveMode,
        accepted_mode: MoveMode,
        now_ms: i64,
    ) {
        self.refill(limits, report_mode, now_ms);
        let fresh = Allowance::fresh(limits, accepted_mode, now_ms);
        self.mode = accepted_mode;
        self.main = self.main.min(fresh.main);
        self.down = self.down.min(fresh.down);
        self.up = self.up.min(fresh.up);
    }
}
