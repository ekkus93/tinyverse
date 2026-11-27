//! AvatarState speed and bounds checks, MoveTo, Stop and the tick batch.

use super::*;
use crate::error::WorldError;
use crate::event::AvatarStateUpdate;

/// Sends an AvatarState at `now` and says whether it was accepted.
fn report(w: &mut World, id: EntityId, position: Vec3, mode: MoveMode, now: i64) -> bool {
    let events = w.apply(id, Command::AvatarState(transform(position, mode)), now).unwrap();
    match events.as_slice() {
        [] => true,
        [Event { audience: Audience::Only(to), kind: EventKind::AvatarCorrection { .. } }]
            if *to == id =>
        {
            false
        }
        other => panic!("unexpected events {other:?}"),
    }
}

fn states(events: &[Event]) -> Vec<AvatarStateUpdate> {
    match events {
        [] => Vec::new(),
        [Event { audience: Audience::All, kind: EventKind::AvatarStates(updates) }] => {
            updates.clone()
        }
        other => panic!("unexpected tick events {other:?}"),
    }
}

fn moving(w: &mut World, id: EntityId, target: Vec3, mode: MoveMode) {
    let events = w.apply(id, Command::MoveTo { target, mode }, T0).unwrap();
    only_ack(&events, id);
}

// Speed-check arithmetic used below, with the default limits:
// * the main budget accrues at speed * 1.5: walk 6, run 12, fly 24 m/s;
// * falling accrues 60 m/s and rising 10 m/s;
// * a new avatar starts with 0.25 s worth (walk 1.5 m, down 15 m, up 2.5 m);
// * every budget is capped at 2 s worth (walk 12 m, down 120 m, up 20 m).

/// A fresh avatar that has just switched to `mode` with a stationary report
/// at T0, so its main budget is 1.5 m and accrues at `mode`'s rate from now on.
fn fresh_in(mode: MoveMode, spawn_y: f32) -> (World, EntityId) {
    let (mut w, id) = high_world(spawn_y);
    assert!(report(&mut w, id, Vec3::new(0.0, spawn_y, 0.0), mode, T0));
    (w, id)
}

/// A world whose avatars spawn at the given height, so there is room to fall.
fn high_world(spawn_y: f32) -> (World, EntityId) {
    let mut w = world_with(|c| c.spawn_point = Vec3::new(0.0, spawn_y, 0.0));
    let id = join(&mut w, "a", Role::Visitor);
    (w, id)
}

/// Reports a path at a steady `speed` along +X every `step_ms`, ignoring
/// corrections, and returns the times (ms after `start`) of the corrections.
fn sustained(
    w: &mut World,
    id: EntityId,
    speed: f32,
    step_ms: i64,
    start: i64,
    ms: i64,
) -> Vec<i64> {
    let mut corrected = Vec::new();
    let origin = w.avatar(id).unwrap().transform.position;
    for t in (step_ms..=ms).step_by(step_ms as usize) {
        let position = origin + Vec3::X * (speed * t as f32 / 1000.0);
        if !report(w, id, position, MoveMode::Walk, start + t) {
            corrected.push(t);
        }
    }
    corrected
}

#[test]
fn legal_walk_run_and_fly_are_accepted() {
    // After one second: 1.5 m to start with plus 6, 12 or 24 m accrued.
    for (mode, ok, too_far) in
        [(MoveMode::Walk, 7.4, 7.6), (MoveMode::Run, 13.4, 13.6), (MoveMode::Fly, 25.4, 25.6)]
    {
        let (mut w, id) = fresh_in(mode, 0.0);
        assert!(report(&mut w, id, Vec3::new(ok, 0.0, 0.0), mode, T0 + 1000), "{mode:?}");
        assert_eq!(w.avatar(id).unwrap().transform.position.x, ok);
        assert_eq!(w.avatar(id).unwrap().transform.move_mode, mode);
        let (mut w, id) = fresh_in(mode, 0.0);
        assert!(!report(&mut w, id, Vec3::new(too_far, 0.0, 0.0), mode, T0 + 1000), "{mode:?}");
    }
}

#[test]
fn uneven_reports_at_walking_speed_are_never_corrected() {
    // The logged case: a ~10 fps client walking at 4.1 m/s, 0.41 m per report,
    // with jitter delivering reports alternately 60 ms and 140 ms apart.
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    let (mut x, mut now) = (0.0, T0);
    for i in 0..100 {
        now += if i % 2 == 0 { 60 } else { 140 };
        x += 0.41;
        assert!(report(&mut w, id, Vec3::new(x, 0.0, 0.0), MoveMode::Walk, now), "report {i}");
    }
    assert!(approx(w.avatar(id).unwrap().transform.position.x, 41.0));
    assert_eq!(now, T0 + 10_000);
}

#[test]
fn a_burst_after_a_stall_is_accepted_when_it_covers_legal_distance() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    assert!(report(&mut w, id, Vec3::new(0.4, 0.0, 0.0), MoveMode::Walk, T0 + 100));
    // 500 ms of silence, then five reports within one millisecond covering
    // the 2 m walked meanwhile.
    let mut x = 0.4;
    for i in 0..5 {
        x += 0.4;
        assert!(report(&mut w, id, Vec3::new(x, 0.0, 0.0), MoveMode::Walk, T0 + 600 + i / 4));
    }
    // 1.5 + 0.6 - 0.4 + 3.0 - 2.0 = 2.7 m are left: 3 m more is too much.
    assert!(!report(&mut w, id, Vec3::new(x + 3.0, 0.0, 0.0), MoveMode::Walk, T0 + 601));
}

#[test]
fn time_that_does_not_move_forward_accrues_nothing() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    // 1.5 m to start with; going back in time adds nothing.
    assert!(report(&mut w, id, Vec3::new(1.0, 0.0, 0.0), MoveMode::Walk, T0 - 5000));
    assert!(!report(&mut w, id, Vec3::new(2.0, 0.0, 0.0), MoveMode::Walk, T0));
    assert!(report(&mut w, id, Vec3::new(1.5, 0.0, 0.0), MoveMode::Walk, T0));
    assert!(!report(&mut w, id, Vec3::new(1.6, 0.0, 0.0), MoveMode::Walk, T0));
    // Time moving on again refills.
    assert!(report(&mut w, id, Vec3::new(2.0, 0.0, 0.0), MoveMode::Walk, T0 + 100));
}

#[test]
fn sustained_speeding_is_corrected_within_two_seconds_and_stays_corrected() {
    // Twice walking speed (8 m/s against 6 m/s tolerated) from a fresh avatar.
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    let corrected = sustained(&mut w, id, 8.0, 100, T0, 10_000);
    assert!(corrected[0] <= 2000, "first correction at {} ms", corrected[0]);
    // A client that ignores the corrections gets nowhere: every later report
    // is corrected and its accepted position stays put.
    assert_eq!(corrected.len(), 100 - corrected[0] as usize / 100 + 1);
    assert!(w.avatar(id).unwrap().transform.position.x < 7.0);

    // From a full allowance after a long idle gap, twice the tolerated speed.
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    let corrected = sustained(&mut w, id, 12.0, 100, T0 + 60_000, 10_000);
    assert!(corrected[0] <= 2000, "first correction at {} ms", corrected[0]);
    assert!(corrected.len() > 80);
}

#[test]
fn a_teleport_is_corrected_at_once_even_after_a_long_idle_gap() {
    for wait in [0, 100, 60_000] {
        let mut w = world();
        let id = join(&mut w, "a", Role::Visitor);
        assert!(!report(&mut w, id, Vec3::new(50.0, 0.0, 0.0), MoveMode::Walk, T0 + wait));
        assert!(!report(&mut w, id, Vec3::new(0.0, 0.0, 50.0), MoveMode::Fly, T0 + wait));
        assert_eq!(w.avatar(id).unwrap().transform.position, Vec3::ZERO);
    }
}

#[test]
fn a_long_idle_gap_allows_at_most_two_seconds_of_travel() {
    // 2 s * 6 m/s = 12 m, however long the avatar stood still.
    let later = T0 + 60_000;
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    assert!(report(&mut w, id, Vec3::new(11.9, 0.0, 0.0), MoveMode::Walk, later));
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    assert!(!report(&mut w, id, Vec3::new(12.5, 0.0, 0.0), MoveMode::Walk, later));
}

#[test]
fn switching_from_fly_to_walk_does_not_carry_the_allowance() {
    // Two idle seconds in Fly mode, confirmed by a stationary Fly report,
    // fill 48 m; walking from there may keep only 12 m.
    for (x, accepted) in [(12.5, false), (11.9, true)] {
        let (mut w, id) = fresh_in(MoveMode::Fly, 0.0);
        assert!(report(&mut w, id, Vec3::ZERO, MoveMode::Fly, T0 + 2000));
        let walked = report(&mut w, id, Vec3::new(x, 0.0, 0.0), MoveMode::Walk, T0 + 2000);
        assert_eq!(walked, accepted, "{x} m");
    }

    // The interval in which the mode changed accrues at the slower rate: one
    // second of Walk then a switch to Fly earns 1.5 + 6 m, not 1.5 + 24 m.
    let (mut w, id) = fresh_in(MoveMode::Walk, 0.0);
    assert!(!report(&mut w, id, Vec3::new(8.0, 0.0, 0.0), MoveMode::Fly, T0 + 1000));
    let (mut w, id) = fresh_in(MoveMode::Walk, 0.0);
    assert!(report(&mut w, id, Vec3::new(7.4, 0.0, 0.0), MoveMode::Fly, T0 + 1000));
    // From then on Fly accrues at its own rate.
    assert!(report(&mut w, id, Vec3::new(7.4 + 23.9, 0.0, 0.0), MoveMode::Fly, T0 + 2000));
}

#[test]
fn a_correction_caps_the_allowance_at_a_quarter_second() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    // 7.5 m available after a second, but 9 m is too far ...
    assert!(!report(&mut w, id, Vec3::new(9.0, 0.0, 0.0), MoveMode::Walk, T0 + 1000));
    // ... and the correction leaves 1.5 m, not the 7.5 m that were saved up.
    assert!(!report(&mut w, id, Vec3::new(1.6, 0.0, 0.0), MoveMode::Walk, T0 + 1000));
    assert!(report(&mut w, id, Vec3::new(1.4, 0.0, 0.0), MoveMode::Walk, T0 + 1000));
}

#[test]
fn walking_falls_are_accepted_up_to_free_fall_speed() {
    // 20 m at 30 m/s, reported every 100 ms (3 m per report).
    let (mut w, id) = high_world(30.0);
    let mut y = 30.0;
    let mut now = T0;
    while y > 10.0 {
        y = (y - 3.0f32).max(10.0);
        now += 100;
        assert!(report(&mut w, id, Vec3::new(0.0, y, 0.0), MoveMode::Walk, now), "y = {y}");
    }
    assert_eq!(w.avatar(id).unwrap().transform.position.y, 10.0);

    // A steady 59 m/s fall for three seconds, in Run mode.
    let (mut w, id) = fresh_in(MoveMode::Run, 250.0);
    for i in 1..=30 {
        let y = 250.0 - 5.9 * i as f32;
        assert!(report(&mut w, id, Vec3::new(0.0, y, 0.0), MoveMode::Run, T0 + i * 100), "{i}");
    }

    // A steady 80 m/s fall is faster than free fall allows (60 m/s, no
    // tolerance) and is caught within two seconds.
    let (mut w, id) = high_world(250.0);
    let caught = (1..=30).find(|i| {
        let y = 250.0 - 8.0 * *i as f32;
        !report(&mut w, id, Vec3::new(0.0, y, 0.0), MoveMode::Walk, T0 + i * 100)
    });
    assert!(caught.is_some_and(|i| i <= 20), "caught at report {caught:?}");
}

#[test]
fn jumps_are_accepted_but_fast_rises_are_not() {
    // A 1.2 m jump at 5 m/s, up then down again.
    let (mut w, id) = high_world(0.0);
    assert!(report(&mut w, id, Vec3::new(0.0, 0.6, 0.0), MoveMode::Walk, T0 + 120));
    assert!(report(&mut w, id, Vec3::new(0.0, 1.2, 0.0), MoveMode::Walk, T0 + 240));
    assert!(report(&mut w, id, Vec3::new(0.0, 0.6, 0.0), MoveMode::Walk, T0 + 360));
    assert!(report(&mut w, id, Vec3::new(0.0, 0.0, 0.0), MoveMode::Walk, T0 + 480));

    for mode in [MoveMode::Walk, MoveMode::Run] {
        // A steady 9.9 m/s rise for three seconds is fine ...
        let (mut w, id) = fresh_in(mode, 0.0);
        for i in 1..=30 {
            let y = 0.99 * i as f32;
            assert!(report(&mut w, id, Vec3::new(0.0, y, 0.0), mode, T0 + i * 100), "{i}");
        }
        // ... but a steady 15 m/s rise exceeds the fixed 10 m/s limit and is
        // caught within a second.
        let (mut w, id) = fresh_in(mode, 0.0);
        let caught = (1..=20).find(|i| {
            let y = 1.5 * *i as f32;
            !report(&mut w, id, Vec3::new(0.0, y, 0.0), mode, T0 + i * 100)
        });
        assert!(caught.is_some_and(|i| i <= 10), "{mode:?} caught at report {caught:?}");
    }
}

#[test]
fn horizontal_limits_are_unchanged_while_falling() {
    for (mode, ok, too_far) in [(MoveMode::Walk, 7.4, 7.6), (MoveMode::Run, 13.4, 13.6)] {
        let (mut w, id) = fresh_in(mode, 100.0);
        // Falling 50 m in the same second does not use up any horizontal budget ...
        assert!(report(&mut w, id, Vec3::new(ok, 50.0, 0.0), mode, T0 + 1000));
        // ... nor does it add any.
        let (mut w, id) = fresh_in(mode, 100.0);
        assert!(!report(&mut w, id, Vec3::new(0.0, 50.0, too_far), mode, T0 + 1000));
    }
}

#[test]
fn a_long_gap_caps_each_component_at_two_seconds() {
    let later = T0 + 60_000;
    let cases = [
        // Falling: 2 s * 60 m/s = 120 m.
        (200.0, Vec3::new(0.0, 81.0, 0.0), Vec3::new(0.0, 79.0, 0.0)),
        // Rising: 2 s * 10 m/s = 20 m.
        (0.0, Vec3::new(0.0, 19.0, 0.0), Vec3::new(0.0, 21.0, 0.0)),
        // Horizontal: 2 s * 6 m/s = 12 m, even combined with a fall.
        (200.0, Vec3::new(11.9, 150.0, 0.0), Vec3::new(12.5, 150.0, 0.0)),
    ];
    for (spawn_y, ok, too_far) in cases {
        let (mut w, id) = high_world(spawn_y);
        assert!(report(&mut w, id, ok, MoveMode::Walk, later), "{ok}");
        let (mut w, id) = high_world(spawn_y);
        assert!(!report(&mut w, id, too_far, MoveMode::Walk, later), "{too_far}");
    }
}

#[test]
fn flying_is_still_judged_in_three_dimensions() {
    // fly 16 m/s * 1.5 = 24 m per second along any direction, plus 1.5 m.
    let diagonal = |d: f32| Vec3::new(d, 100.0 + d, d);
    let (mut w, id) = fresh_in(MoveMode::Fly, 100.0);
    assert!(!report(&mut w, id, diagonal(14.8), MoveMode::Fly, T0 + 1000), "25.6 m");
    let (mut w, id) = fresh_in(MoveMode::Fly, 100.0);
    assert!(report(&mut w, id, diagonal(14.6), MoveMode::Fly, T0 + 1000), "25.3 m");
    // No free-fall allowance: dropping 30 m in a second while flying is too fast ...
    let (mut w, id) = fresh_in(MoveMode::Fly, 100.0);
    assert!(!report(&mut w, id, Vec3::new(0.0, 70.0, 0.0), MoveMode::Fly, T0 + 1000));
    // ... but climbing 25.4 m is fine, beyond the walking rise limit.
    let (mut w, id) = fresh_in(MoveMode::Fly, 100.0);
    assert!(report(&mut w, id, Vec3::new(0.0, 125.4, 0.0), MoveMode::Fly, T0 + 1000));
    // 4 m along each axis is 6.93 m in 3D: more than the 1.5 m a fresh flyer
    // has, but fine for a walker after one second (5.66 m across, 4 m up).
    let (mut w, id) = fresh_in(MoveMode::Fly, 0.0);
    assert!(!report(&mut w, id, Vec3::splat(4.0), MoveMode::Fly, T0));
    let (mut w, id) = fresh_in(MoveMode::Walk, 0.0);
    assert!(report(&mut w, id, Vec3::splat(4.0), MoveMode::Walk, T0 + 1000));
}

#[test]
fn too_fast_is_corrected_to_the_last_accepted_state() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    assert!(report(&mut w, id, Vec3::new(1.0, 0.0, 0.0), MoveMode::Walk, T0 + 500));
    w.tick(0.05, T0 + 500);
    let accepted = w.avatar(id).unwrap().transform.clone();

    let events = w
        .apply(
            id,
            Command::AvatarState(transform(Vec3::new(50.0, 0.0, 0.0), MoveMode::Walk)),
            T0 + 1500,
        )
        .unwrap();
    assert_eq!(
        events,
        vec![Event::new(
            Audience::Only(id),
            EventKind::AvatarCorrection { transform: accepted.clone(), reason: "too fast".into() }
        )]
    );
    assert_eq!(w.avatar(id).unwrap().transform, accepted);
    assert!(w.tick(0.05, T0 + 1500).is_empty());
}

#[test]
fn a_server_driven_move_restarts_the_allowance() {
    // Standing still for a long time fills 12 m; a MoveTo step replaces that
    // with a fresh 1.5 m.
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(10.0, 0.0, 0.0), MoveMode::Walk);
    w.tick(0.05, T0 + 60_000);
    let x = w.avatar(id).unwrap().transform.position.x;
    assert!(!report(&mut w, id, Vec3::new(x + 1.6, 0.0, 0.0), MoveMode::Walk, T0 + 60_000));
    // So does Stop.
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(10.0, 0.0, 0.0), MoveMode::Walk);
    only_ack(&w.apply(id, Command::Stop, T0 + 60_000).unwrap(), id);
    assert!(!report(&mut w, id, Vec3::new(1.6, 0.0, 0.0), MoveMode::Walk, T0 + 60_000));
    assert!(report(&mut w, id, Vec3::new(1.4, 0.0, 0.0), MoveMode::Walk, T0 + 60_000));
}
#[test]
fn out_of_bounds_is_corrected() {
    let mut w = world_with(|c| {
        c.bounds_min = Vec3::new(-10.0, -1.0, -10.0);
        c.bounds_max = Vec3::new(10.0, 10.0, 10.0);
        c.spawn_point = Vec3::new(9.0, 0.0, 0.0);
    });
    let id = join(&mut w, "a", Role::Visitor);
    let events = w
        .apply(
            id,
            Command::AvatarState(transform(Vec3::new(10.5, 0.0, 0.0), MoveMode::Walk)),
            T0 + 1000,
        )
        .unwrap();
    assert!(matches!(
        &events[..],
        [Event { audience: Audience::Only(to), kind: EventKind::AvatarCorrection { reason, transform } }]
            if *to == id && reason == "out of bounds" && transform.position.x == 9.0
    ));
    // The boundary itself is inside.
    assert!(report(&mut w, id, Vec3::new(10.0, 0.0, 0.0), MoveMode::Walk, T0 + 1000));
}

#[test]
fn non_finite_state_is_an_error_and_changes_nothing() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    let before = w.avatar(id).unwrap().clone();
    let mut bad = vec![
        transform(Vec3::new(f32::NAN, 0.0, 0.0), MoveMode::Walk),
        transform(Vec3::ZERO, MoveMode::Walk),
        transform(Vec3::ZERO, MoveMode::Walk),
    ];
    bad[1].yaw = f32::INFINITY;
    bad[2].velocity = Vec3::new(0.0, f32::NEG_INFINITY, 0.0);
    for t in bad {
        assert!(matches!(
            w.apply(id, Command::AvatarState(t), T0 + 1000),
            Err(WorldError::InvalidArgument(_))
        ));
    }
    assert_eq!(w.avatar(id).unwrap(), &before);
}

#[test]
fn accepted_state_is_reported_once_on_the_next_tick_as_client_driven() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    let other = join(&mut w, "b", Role::Visitor);
    assert!(w.tick(0.05, T0).is_empty(), "joining is not a transform change");

    let mut t = transform(Vec3::new(1.0, 0.0, 2.0), MoveMode::Run);
    t.yaw = 45.0;
    t.velocity = Vec3::new(3.0, 0.0, 3.0);
    t.animation = "x".repeat(40);
    assert!(w.apply(id, Command::AvatarState(t.clone()), T0 + 500).unwrap().is_empty());
    t.animation = "x".repeat(32);
    assert_eq!(w.avatar(id).unwrap().transform, t);

    let updates = states(&w.tick(0.05, T0 + 550));
    assert_eq!(updates, vec![AvatarStateUpdate { id, transform: t.clone(), server_driven: false }]);
    assert!(w.tick(0.05, T0 + 600).is_empty());
    // An unchanged heartbeat is not broadcast.
    w.apply(id, Command::AvatarState(t), T0 + 1500).unwrap();
    assert!(w.tick(0.05, T0 + 1550).is_empty());
    let _ = other;
}

#[test]
fn tick_batches_every_changed_avatar_in_id_order() {
    let mut w = world();
    let a = join(&mut w, "a", Role::Visitor);
    let b = join(&mut w, "b", Role::Visitor);
    let c = join(&mut w, "c", Role::Visitor);
    for id in [c, a, b] {
        assert!(report(&mut w, id, Vec3::new(1.0, 0.0, 0.0), MoveMode::Walk, T0 + 1000));
    }
    w.leave(b, T0 + 1000);
    let ids: Vec<_> = states(&w.tick(0.05, T0 + 1000)).iter().map(|u| u.id).collect();
    assert_eq!(ids, vec![a, c]);
}

#[test]
fn move_to_validates_target() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    assert!(matches!(
        w.apply(
            id,
            Command::MoveTo { target: Vec3::new(500.0, 0.0, 0.0), mode: MoveMode::Walk },
            T0
        ),
        Err(WorldError::OutOfBounds(_))
    ));
    assert!(matches!(
        w.apply(
            id,
            Command::MoveTo { target: Vec3::new(0.0, 1000.0, 0.0), mode: MoveMode::Fly },
            T0
        ),
        Err(WorldError::OutOfBounds(_))
    ));
    assert!(matches!(
        w.apply(
            id,
            Command::MoveTo { target: Vec3::new(0.0, f32::NAN, 0.0), mode: MoveMode::Walk },
            T0
        ),
        Err(WorldError::InvalidArgument(_))
    ));
    assert!(w.avatars.get(&id).unwrap().move_to.is_none());
    assert!(w.tick(1.0, T0 + 1000).is_empty());
}

#[test]
fn walking_move_to_advances_at_walk_speed_on_the_ground_and_arrives() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    // target.y is ignored for walking, even far out of bounds.
    moving(&mut w, id, Vec3::new(10.0, 9999.0, 0.0), MoveMode::Walk);

    let updates = states(&w.tick(0.5, T0 + 500));
    assert_eq!(updates.len(), 1);
    let u = &updates[0];
    assert!(u.server_driven);
    assert!(approx_vec(u.transform.position, Vec3::new(2.0, 0.0, 0.0)));
    assert!(approx_vec(u.transform.velocity, Vec3::new(4.0, 0.0, 0.0)));
    assert!(approx(u.transform.yaw, 90.0), "facing +X is yaw 90");
    assert_eq!(u.transform.animation, "walk");
    assert_eq!(u.transform.move_mode, MoveMode::Walk);

    for i in 1..=3 {
        let p = states(&w.tick(0.5, T0 + 500 + i * 500))[0].transform.position;
        assert!(approx(p.x, 2.0 + 2.0 * i as f32));
    }
    // 8 m done, 2 m left; a 1 s tick would overshoot, so it lands exactly.
    let last = states(&w.tick(1.0, T0 + 3000));
    let t = &last[0].transform;
    assert_eq!(t.position, Vec3::new(10.0, 0.0, 0.0));
    assert_eq!(t.velocity, Vec3::ZERO);
    assert_eq!(t.animation, "idle");
    assert!(last[0].server_driven);
    assert!(w.avatars.get(&id).unwrap().move_to.is_none());
    assert!(w.tick(1.0, T0 + 4000).is_empty());
}

#[test]
fn running_and_flying_move_to() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(0.0, 0.0, -20.0), MoveMode::Run);
    let t = states(&w.tick(1.0, T0 + 1000))[0].transform.clone();
    assert!(approx_vec(t.position, Vec3::new(0.0, 0.0, -8.0)));
    assert!(approx(t.yaw, 180.0));
    assert_eq!(t.animation, "run");
    assert_eq!(t.move_mode, MoveMode::Run);

    // Fly in 3D: straight up keeps the heading.
    moving(&mut w, id, Vec3::new(0.0, 40.0, -8.0), MoveMode::Fly);
    let t = states(&w.tick(1.0, T0 + 2000))[0].transform.clone();
    assert!(approx_vec(t.position, Vec3::new(0.0, 16.0, -8.0)));
    assert!(approx_vec(t.velocity, Vec3::new(0.0, 16.0, 0.0)));
    assert!(approx(t.yaw, 180.0));
    assert_eq!(t.animation, "fly");
    assert_eq!(t.move_mode, MoveMode::Fly);

    // Walking from up there comes down to the ground at 10 m/s.
    moving(&mut w, id, Vec3::new(-3.0, 0.0, -8.0), MoveMode::Walk);
    let t = states(&w.tick(1.0, T0 + 3000))[0].transform.clone();
    assert_eq!(t.position, Vec3::new(-3.0, 6.0, -8.0));
    assert!(approx(t.yaw, 270.0), "facing -X is yaw 270");
    let t = states(&w.tick(1.0, T0 + 4000))[0].transform.clone();
    assert_eq!(t.position, Vec3::new(-3.0, 0.0, -8.0));
    assert_eq!(t.animation, "idle");
}

#[test]
fn walking_move_to_from_the_air_reaches_the_ground() {
    let mut w = world_with(|c| c.spawn_point = Vec3::new(0.0, 5.0, 0.0));
    let id = join(&mut w, "a", Role::Visitor);
    // target.y is ignored; ground level is used instead.
    moving(&mut w, id, Vec3::new(8.0, 5.0, 0.0), MoveMode::Walk);

    // 0.1 s: 0.4 m forward, 1 m down; still well above ground, so "fall".
    let u = states(&w.tick(0.1, T0 + 100))[0].clone();
    assert!(u.server_driven);
    assert!(approx_vec(u.transform.position, Vec3::new(0.4, 4.0, 0.0)));
    assert!(approx_vec(u.transform.velocity, Vec3::new(4.0, -10.0, 0.0)));
    assert_eq!(u.transform.animation, "fall");
    assert!(approx(u.transform.yaw, 90.0));

    // After 0.45 s in total it is 0.5 m up: no longer "fall".
    let t = states(&w.tick(0.35, T0 + 450))[0].transform.clone();
    assert!(approx(t.position.y, 0.5));
    assert_eq!(t.animation, "walk");

    // On the ground the vertical velocity is gone; walking continues.
    let t = states(&w.tick(0.1, T0 + 550))[0].transform.clone();
    assert_eq!(t.position.y, 0.0);
    assert!(approx_vec(t.velocity, Vec3::new(4.0, 0.0, 0.0)));
    assert_eq!(t.animation, "walk");

    let t = states(&w.tick(2.0, T0 + 2550))[0].transform.clone();
    assert_eq!(t.position, Vec3::new(8.0, 0.0, 0.0));
    assert_eq!(t.velocity, Vec3::ZERO);
    assert_eq!(t.animation, "idle");
    assert!(w.avatars.get(&id).unwrap().move_to.is_none());
}

#[test]
fn a_short_hop_from_high_up_keeps_descending_before_arriving() {
    let mut w = world_with(|c| c.spawn_point = Vec3::new(0.0, 30.0, 0.0));
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(0.0, 0.0, 1.0), MoveMode::Run);

    // Horizontally there after the first tick, but 20 m still to fall.
    let t = states(&w.tick(1.0, T0 + 1000))[0].transform.clone();
    assert_eq!(t.position, Vec3::new(0.0, 20.0, 1.0));
    assert_eq!(t.velocity, Vec3::new(0.0, -10.0, 0.0));
    assert_eq!(t.animation, "fall");
    assert!(w.avatars.get(&id).unwrap().move_to.is_some());

    let t = states(&w.tick(1.0, T0 + 2000))[0].transform.clone();
    assert_eq!(t.position, Vec3::new(0.0, 10.0, 1.0));
    assert!(w.avatars.get(&id).unwrap().move_to.is_some());

    let t = states(&w.tick(1.0, T0 + 3000))[0].transform.clone();
    assert_eq!(t.position, Vec3::new(0.0, 0.0, 1.0));
    assert_eq!(t.velocity, Vec3::ZERO);
    assert_eq!(t.animation, "idle");
    assert!(w.avatars.get(&id).unwrap().move_to.is_none());
    assert!(w.tick(1.0, T0 + 4000).is_empty());
}

#[test]
fn walking_move_to_from_below_ground_rises_to_it() {
    let mut w = world_with(|c| c.spawn_point = Vec3::new(0.0, -2.0, 0.0));
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(4.0, -2.0, 0.0), MoveMode::Walk);
    let t = states(&w.tick(0.1, T0 + 100))[0].transform.clone();
    assert!(approx_vec(t.position, Vec3::new(0.4, -1.0, 0.0)));
    assert!(approx_vec(t.velocity, Vec3::new(4.0, 10.0, 0.0)));
    assert_eq!(t.animation, "walk", "below ground is not falling");
    let t = states(&w.tick(0.1, T0 + 200))[0].transform.clone();
    assert!(approx_vec(t.position, Vec3::new(0.8, 0.0, 0.0)));
    assert!(approx_vec(t.velocity, Vec3::new(4.0, 0.0, 0.0)));
    let t = states(&w.tick(1.0, T0 + 1200))[0].transform.clone();
    assert_eq!(t.position, Vec3::new(4.0, 0.0, 0.0));
    assert_eq!(t.animation, "idle");
}

#[test]
fn ground_level_is_clamped_into_the_bounds() {
    let mut w = world_with(|c| {
        c.bounds_min = Vec3::new(-50.0, 10.0, -50.0);
        c.bounds_max = Vec3::new(50.0, 100.0, 50.0);
        c.spawn_point = Vec3::new(0.0, 20.0, 0.0);
    });
    let id = join(&mut w, "a", Role::Visitor);
    assert_eq!(w.ground_y(), 10.0);
    moving(&mut w, id, Vec3::new(1.0, 0.0, 0.0), MoveMode::Walk);
    w.tick(1.0, T0 + 1000);
    assert_eq!(w.avatar(id).unwrap().transform.position, Vec3::new(1.0, 10.0, 0.0));
    let w = world_with(|c| c.bounds_max.y = -5.0);
    assert_eq!(w.ground_y(), -5.0);
}

#[test]
fn walking_target_bounds_use_ground_level() {
    // Bounds that exclude the avatar's current height but include y = 0.
    let mut w = world_with(|c| c.bounds_max.y = 10.0);
    let id = join(&mut w, "a", Role::Visitor);
    place(&mut w, id, Vec3::new(0.0, 50.0, 0.0), 0.0);
    moving(&mut w, id, Vec3::new(3.0, 50.0, 0.0), MoveMode::Walk);
    // Flying to the same point is out of bounds.
    assert!(matches!(
        w.apply(id, Command::MoveTo { target: Vec3::new(3.0, 50.0, 0.0), mode: MoveMode::Fly }, T0),
        Err(WorldError::OutOfBounds(_))
    ));
}

#[test]
fn stop_during_the_descent_leaves_the_avatar_in_the_air() {
    let mut w = world_with(|c| c.spawn_point = Vec3::new(0.0, 30.0, 0.0));
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(2.0, 0.0, 0.0), MoveMode::Walk);
    w.tick(1.0, T0 + 1000);
    only_ack(&w.apply(id, Command::Stop, T0 + 1000).unwrap(), id);
    let t = states(&w.tick(1.0, T0 + 2000))[0].transform.clone();
    assert_eq!(t.position, Vec3::new(2.0, 20.0, 0.0));
    assert_eq!(t.velocity, Vec3::ZERO);
    assert_eq!(t.animation, "idle");
    assert!(w.tick(1.0, T0 + 3000).is_empty());
    assert_eq!(w.avatar(id).unwrap().transform.position.y, 20.0);
}

#[test]
fn move_to_the_current_position_arrives_at_once() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::ZERO, MoveMode::Walk);
    assert!(w.tick(0.05, T0 + 50).is_empty(), "nothing changed");
    assert!(w.avatars.get(&id).unwrap().move_to.is_none());
}

#[test]
fn stop_halts_where_the_avatar_is() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(10.0, 0.0, 0.0), MoveMode::Walk);
    w.tick(0.5, T0 + 500);
    only_ack(&w.apply(id, Command::Stop, T0 + 600).unwrap(), id);
    let updates = states(&w.tick(0.5, T0 + 1000));
    let t = &updates[0].transform;
    assert!(updates[0].server_driven);
    assert!(approx_vec(t.position, Vec3::new(2.0, 0.0, 0.0)));
    assert_eq!(t.velocity, Vec3::ZERO);
    assert_eq!(t.animation, "idle");
    assert!(w.tick(0.5, T0 + 1500).is_empty());

    // Stop without a MoveTo is a no-op that is still answered.
    only_ack(&w.apply(id, Command::Stop, T0 + 1600).unwrap(), id);
    assert!(w.tick(0.5, T0 + 2000).is_empty());
}

#[test]
fn a_new_move_to_replaces_the_old_one() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(10.0, 0.0, 0.0), MoveMode::Walk);
    w.tick(0.5, T0 + 500);
    moving(&mut w, id, Vec3::new(2.0, 0.0, 4.0), MoveMode::Walk);
    let t = states(&w.tick(0.5, T0 + 1000))[0].transform.clone();
    assert!(approx_vec(t.position, Vec3::new(2.0, 0.0, 2.0)));
    assert!(approx(t.yaw, 0.0));
}

#[test]
fn avatar_state_cancels_move_to_and_is_checked_from_the_moved_position() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(20.0, 0.0, 0.0), MoveMode::Walk);
    w.tick(1.0, T0 + 1000);
    // At x = 4 now. The server-driven move left a fresh 0.25 s allowance
    // (1.5 m) at T0 + 1000; 0.1 s later 2.1 m is allowed.
    assert!(!report(&mut w, id, Vec3::new(6.5, 0.0, 0.0), MoveMode::Walk, T0 + 1100));
    // A rejected report leaves the MoveTo running.
    assert!(w.avatars.get(&id).unwrap().move_to.is_some());
    assert!(report(&mut w, id, Vec3::new(4.5, 0.0, 0.0), MoveMode::Walk, T0 + 1100));
    assert!(w.avatars.get(&id).unwrap().move_to.is_none());
    let updates = states(&w.tick(1.0, T0 + 2000));
    assert_eq!(updates[0].transform.position, Vec3::new(4.5, 0.0, 0.0));
    assert!(!updates[0].server_driven);
}

#[test]
fn tick_ignores_bad_dt() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    moving(&mut w, id, Vec3::new(10.0, 0.0, 0.0), MoveMode::Walk);
    for dt in [f32::NAN, -1.0, f32::INFINITY] {
        w.tick(dt, T0 + 100);
        assert_eq!(w.avatar(id).unwrap().transform.position, Vec3::ZERO);
    }
}
