//! Observe: radius, contents, ordering, bearing, truncation and summary.

use super::*;
use crate::error::WorldError;
use crate::types::{Color, Observation, Shape};

fn observe(w: &mut World, id: EntityId, radius: f32) -> Observation {
    let events = w.apply(id, Command::Observe { radius }, T0).unwrap();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].audience, Audience::Only(id));
    match &events[0].kind {
        EventKind::Observation(o) => (**o).clone(),
        other => panic!("expected Observation, got {other:?}"),
    }
}

#[test]
fn radius_defaults_and_is_clamped() {
    let mut w = world();
    let a = join(&mut w, "a", Role::Visitor);
    let limits = w.config().limits.clone();
    assert_eq!(observe(&mut w, a, 0.0).radius, limits.default_observe_radius);
    assert_eq!(observe(&mut w, a, 12.5).radius, 12.5);
    assert_eq!(observe(&mut w, a, 1e6).radius, limits.max_observe_radius);
    for bad in [-1.0, f32::NAN, f32::INFINITY] {
        assert!(matches!(
            w.apply(a, Command::Observe { radius: bad }, T0),
            Err(WorldError::InvalidArgument(_))
        ));
    }
}

#[test]
fn lists_nearby_avatars_and_objects_nearest_first() {
    let mut w = world();
    let me = join(&mut w, "me", Role::Builder);
    let far_friend = join(&mut w, "far", Role::Visitor);
    let near_friend = join_agent(&mut w, "near", Role::Visitor);
    let out_of_range = join(&mut w, "gone", Role::Visitor);
    place(&mut w, me, Vec3::new(10.0, 0.0, 10.0), 0.0);
    place(&mut w, far_friend, Vec3::new(10.0, 0.0, 20.0), 0.0);
    place(&mut w, near_friend, Vec3::new(13.0, 4.0, 10.0), 0.0);
    place(&mut w, out_of_range, Vec3::new(10.0, 0.0, 40.1), 0.0);

    let o_far = create(&mut w, me, spec_at(Vec3::new(10.0, 0.0, 39.0)));
    let o_near = create(&mut w, me, spec_at(Vec3::new(9.0, 0.0, 10.0)));
    let o_mid = create(&mut w, me, spec_at(Vec3::new(10.0, 0.0, 5.0)));
    create(&mut w, me, spec_at(Vec3::new(50.0, 0.0, 10.0)));

    let o = observe(&mut w, me, 0.0);
    assert_eq!(o.observer, w.avatar(me).unwrap().clone());
    assert!(!o.truncated);
    let avatar_ids: Vec<_> = o.avatars.iter().map(|s| s.item.id).collect();
    assert_eq!(avatar_ids, vec![near_friend, far_friend], "self is not listed");
    assert!(approx(o.avatars[0].distance, 5.0));
    assert!(approx(o.avatars[1].distance, 10.0));
    let object_ids: Vec<_> = o.objects.iter().map(|s| s.item.id).collect();
    assert_eq!(object_ids, vec![o_near, o_mid, o_far]);
    assert!(approx(o.objects[2].distance, 29.0));
    assert_eq!(o.objects[0].item, w.object(o_near).unwrap().clone());

    // Distance is to the observer's feet, from the object's centre.
    let o = observe(&mut w, me, 4.9);
    assert!(o.avatars.is_empty());
    assert_eq!(o.objects.len(), 1);
}

#[test]
fn equal_distances_are_ordered_by_id() {
    let mut w = world();
    let me = join(&mut w, "me", Role::Builder);
    let ids: Vec<_> = [Vec3::X, -Vec3::X, Vec3::Z, -Vec3::Z]
        .iter()
        .map(|p| create(&mut w, me, spec_at(*p * 3.0)))
        .collect();
    let o = observe(&mut w, me, 0.0);
    assert_eq!(o.objects.iter().map(|s| s.item.id).collect::<Vec<_>>(), ids);
}

#[test]
fn bearing_in_all_four_quadrants() {
    let mut w = world();
    let me = join(&mut w, "me", Role::Builder);
    let cases = [
        (Vec3::new(0.0, 0.0, 5.0), 0.0),
        (Vec3::new(5.0, 0.0, 5.0), 45.0),
        (Vec3::new(5.0, 0.0, 0.0), 90.0),
        (Vec3::new(5.0, 0.0, -5.0), 135.0),
        (Vec3::new(0.0, 0.0, -5.0), 180.0),
        (Vec3::new(-5.0, 0.0, -5.0), -135.0),
        (Vec3::new(-5.0, 0.0, 0.0), -90.0),
        (Vec3::new(-5.0, 0.0, 5.0), -45.0),
        (Vec3::new(0.0, 5.0, 0.0), 0.0),
    ];
    let ids: Vec<_> = cases.iter().map(|(p, _)| create(&mut w, me, spec_at(*p))).collect();
    let bearing_of =
        |o: &Observation, id| o.objects.iter().find(|s| s.item.id == id).unwrap().bearing;

    let o = observe(&mut w, me, 0.0);
    for ((p, expected), id) in cases.iter().zip(&ids) {
        assert!(approx(bearing_of(&o, *id), *expected), "{p} facing north");
    }

    // Facing east (yaw 90): +X is ahead, +Z is to the left, -Z to the right.
    place(&mut w, me, Vec3::ZERO, 90.0);
    let o = observe(&mut w, me, 0.0);
    assert!(approx(bearing_of(&o, ids[2]), 0.0));
    assert!(approx(bearing_of(&o, ids[0]), -90.0));
    assert!(approx(bearing_of(&o, ids[4]), 90.0));
    assert!(approx(bearing_of(&o, ids[6]).abs(), 180.0));

    // Facing south-west (yaw 225, or equivalently -135).
    for yaw in [225.0, -135.0, 585.0] {
        place(&mut w, me, Vec3::ZERO, yaw);
        let o = observe(&mut w, me, 0.0);
        assert!(approx(bearing_of(&o, ids[5]), 0.0), "yaw {yaw}");
        assert!(approx(bearing_of(&o, ids[1]).abs(), 180.0), "yaw {yaw}");
        assert!(approx(bearing_of(&o, ids[6]), 45.0), "yaw {yaw}");
        assert!(approx(bearing_of(&o, ids[4]), -45.0), "yaw {yaw}");
    }
    for s in &o.objects {
        assert!((-180.0..=180.0).contains(&s.bearing));
    }
}

#[test]
fn lists_are_truncated_to_the_nearest() {
    let mut w = world_with(|c| c.limits.max_observed = 3);
    let me = join(&mut w, "me", Role::Builder);
    let ids: Vec<_> =
        (1..=5).map(|i| create(&mut w, me, spec_at(Vec3::new(i as f32, 0.0, 0.0)))).collect();
    let o = observe(&mut w, me, 0.0);
    assert!(o.truncated);
    assert_eq!(o.objects.iter().map(|s| s.item.id).collect::<Vec<_>>(), ids[..3]);
    assert!(o.summary.contains("More is nearby than listed."));

    // Exactly the limit is not truncated.
    let mut w = world_with(|c| c.limits.max_observed = 3);
    let me = join(&mut w, "me", Role::Builder);
    for i in 1..=3 {
        create(&mut w, me, spec_at(Vec3::new(i as f32, 0.0, 0.0)));
    }
    assert!(!observe(&mut w, me, 0.0).truncated);

    // Avatars are cut the same way.
    let mut w = world_with(|c| c.limits.max_observed = 2);
    let me = join(&mut w, "me", Role::Builder);
    let others: Vec<_> = (1..=3)
        .map(|i| {
            let id = join(&mut w, &format!("u{i}"), Role::Visitor);
            place(&mut w, id, Vec3::new(0.0, 0.0, i as f32), 0.0);
            id
        })
        .collect();
    let o = observe(&mut w, me, 0.0);
    assert!(o.truncated);
    assert_eq!(o.avatars.iter().map(|s| s.item.id).collect::<Vec<_>>(), others[..2]);
}

#[test]
fn summary_describes_the_scene() {
    let mut w = world();
    let me = join_agent(&mut w, "Scout", Role::Builder);
    place(&mut w, me, Vec3::new(1.0, 0.0, -2.5), 90.0);
    let bob = join(&mut w, "Bob", Role::Visitor);
    place(&mut w, bob, Vec3::new(5.0, 0.0, -6.5), 0.0);
    let bot = join_agent(&mut w, "Helper", Role::Visitor);
    place(&mut w, bot, Vec3::new(-9.0, 0.0, -2.5), 0.0);
    let table = create(
        &mut w,
        me,
        ObjectSpec {
            name: "Table".into(),
            position: Vec3::new(4.0, 0.5, -2.5),
            scale: Vec3::new(2.0, 1.0, 0.5),
            color: Color::new(0.9, 0.1, 0.1, 1.0),
            ..Default::default()
        },
    );
    let ball = create(
        &mut w,
        me,
        ObjectSpec {
            shape: Shape::Sphere,
            position: Vec3::new(1.0, 3.0, -2.5),
            scale: Vec3::splat(0.25),
            color: Color::new(0.1, 0.2, 0.9, 0.3),
            ..Default::default()
        },
    );

    let summary = observe(&mut w, me, 20.0).summary;
    let expected = format!(
        "You are Scout at (1.0, 0.0, -2.5), facing east.\n\
         People within 20 m: Bob (human), 5.7 m ahead-right; Helper (agent), 10.0 m behind.\n\
         Objects within 20 m: translucent blue sphere 0.25x0.25x0.25 m, 3.0 m above, id {ball}; \
         red box \"Table\" 2x1x0.5 m, 3.0 m ahead, id {table}."
    );
    assert_eq!(summary, expected);
    assert_eq!(observe(&mut w, me, 20.0).summary, summary, "deterministic");
}

#[test]
fn summary_of_an_empty_neighbourhood() {
    let mut w = world();
    let me = join(&mut w, "Ann\nLee", Role::Visitor);
    place(&mut w, me, Vec3::new(-0.01, 2.0, 0.0), 182.0);
    let summary = observe(&mut w, me, 0.0).summary;
    assert_eq!(
        summary,
        "You are Ann Lee at (0.0, 2.0, 0.0), facing south.\n\
         No one else within 30 m.\n\
         No objects within 30 m."
    );
}

#[test]
fn summary_directions_and_colours() {
    let mut w = world();
    let me = join(&mut w, "me", Role::Builder);
    let colored = |c: Color, p: Vec3| ObjectSpec { color: c, position: p, ..Default::default() };
    let specs = [
        (
            colored(Color::new(1.0, 1.0, 1.0, 1.0), Vec3::new(-5.0, 0.0, 5.0)),
            "white box 1x1x1 m, 7.1 m ahead-left",
        ),
        (
            colored(Color::new(0.0, 0.0, 0.0, 1.0), Vec3::new(-10.0, 0.0, 0.0)),
            "black box 1x1x1 m, 10.0 m left",
        ),
        (
            colored(Color::new(0.5, 0.5, 0.5, 1.0), Vec3::new(-11.0, 0.0, -11.0)),
            "grey box 1x1x1 m, 15.6 m behind-left",
        ),
        (
            colored(Color::new(0.1, 0.9, 0.1, 1.0), Vec3::new(12.0, 0.0, -12.0)),
            "green box 1x1x1 m, 17.0 m behind-right",
        ),
        (
            colored(Color::new(1.0, 0.9, 0.1, 1.0), Vec3::new(0.0, -15.0, 0.0)),
            "yellow box 1x1x1 m, 15.0 m below",
        ),
        (
            colored(Color::new(1.0, 0.5, 0.0, 1.0), Vec3::new(0.0, 0.0, 19.0)),
            "orange box 1x1x1 m, 19.0 m ahead",
        ),
        (
            colored(Color::new(0.4, 0.2, 0.05, 1.0), Vec3::new(20.0, 0.0, 0.0)),
            "brown box 1x1x1 m, 20.0 m right",
        ),
        (
            colored(Color::new(0.1, 0.9, 0.9, 1.0), Vec3::new(0.0, 0.0, -21.0)),
            "cyan box 1x1x1 m, 21.0 m behind",
        ),
        (
            colored(Color::new(0.5, 0.1, 0.9, 1.0), Vec3::new(0.0, 0.0, 22.0)),
            "purple box 1x1x1 m, 22.0 m ahead",
        ),
        (
            colored(Color::new(1.0, 0.4, 0.7, 1.0), Vec3::new(0.0, 0.0, 23.0)),
            "pink box 1x1x1 m, 23.0 m ahead",
        ),
    ];
    for (spec, _) in &specs {
        create(&mut w, me, spec.clone());
    }
    let summary = observe(&mut w, me, 0.0).summary;
    for (_, text) in &specs {
        assert!(summary.contains(text), "missing {text:?} in\n{summary}");
    }
}
