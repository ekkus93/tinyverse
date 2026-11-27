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

#[test]
fn legal_walk_run_and_fly_are_accepted() {
    // walk 4, run 8, fly 16 m/s with tolerance 1.5, over one second
    for (mode, ok, too_far) in
        [(MoveMode::Walk, 5.9, 6.1), (MoveMode::Run, 11.9, 12.1), (MoveMode::Fly, 23.9, 24.1)]
    {
        let mut w = world();
        let id = join(&mut w, "a", Role::Visitor);
        assert!(!report(&mut w, id, Vec3::new(too_far, 0.0, 0.0), mode, T0 + 1000));
        assert!(report(&mut w, id, Vec3::new(ok, 0.0, 0.0), mode, T0 + 1000));
        assert_eq!(w.avatar(id).unwrap().transform.position.x, ok);
        assert_eq!(w.avatar(id).unwrap().transform.move_mode, mode);
    }
}

#[test]
fn speed_is_checked_in_three_dimensions() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    // 4 m along each axis is 6.93 m in total: too far for a 1 s walk.
    assert!(!report(&mut w, id, Vec3::splat(4.0), MoveMode::Walk, T0 + 1000));
    assert!(report(&mut w, id, Vec3::splat(3.0), MoveMode::Walk, T0 + 1000));
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
fn a_burst_of_reports_is_judged_over_at_least_50_ms() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    // 50 ms of walking allows 4 * 1.5 * 0.05 = 0.3 m per report.
    let mut x = 0.0;
    for i in 1..=5 {
        x += 0.29;
        assert!(report(&mut w, id, Vec3::new(x, 0.0, 0.0), MoveMode::Walk, T0 + i));
    }
    assert!(!report(&mut w, id, Vec3::new(x + 0.35, 0.0, 0.0), MoveMode::Walk, T0 + 6));
    // Even zero or negative elapsed time gets the 50 ms floor.
    assert!(report(&mut w, id, Vec3::new(x + 0.29, 0.0, 0.0), MoveMode::Walk, T0 - 100));
}

#[test]
fn a_long_idle_gap_allows_at_most_two_seconds_of_travel() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    let later = T0 + 60_000;
    // 2 s * 6 m/s = 12 m, however long the avatar stood still.
    assert!(!report(&mut w, id, Vec3::new(12.5, 0.0, 0.0), MoveMode::Walk, later));
    assert!(report(&mut w, id, Vec3::new(11.9, 0.0, 0.0), MoveMode::Walk, later));
}

#[test]
fn rejected_reports_do_not_reset_the_clock() {
    let mut w = world();
    let id = join(&mut w, "a", Role::Visitor);
    assert!(!report(&mut w, id, Vec3::new(9.0, 0.0, 0.0), MoveMode::Walk, T0 + 1000));
    // Two seconds since the last *accepted* state now allow 12 m.
    assert!(report(&mut w, id, Vec3::new(9.0, 0.0, 0.0), MoveMode::Walk, T0 + 2000));
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

    // Walking from up there stays at the current height.
    moving(&mut w, id, Vec3::new(-3.0, 0.0, -8.0), MoveMode::Walk);
    let t = states(&w.tick(1.0, T0 + 3000))[0].transform.clone();
    assert_eq!(t.position, Vec3::new(-3.0, 16.0, -8.0));
    assert!(approx(t.yaw, 270.0), "facing -X is yaw 270");
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
    // At x = 4 now, last updated at T0 + 1000: 0.1 s later 0.6 m is allowed.
    assert!(!report(&mut w, id, Vec3::new(5.0, 0.0, 0.0), MoveMode::Walk, T0 + 1100));
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
