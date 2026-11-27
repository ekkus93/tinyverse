//! A full world stays fast. The bounds are generous so the test also passes
//! in unoptimised builds on a slow machine.

use std::time::{Duration, Instant};

use super::*;
use crate::error::WorldError;

#[test]
fn ten_thousand_objects_observe_and_tick_stay_fast() {
    let mut w = world();
    let builder = join(&mut w, "b", Role::Builder);
    let max = w.config().limits.max_objects;
    assert_eq!(max, 10_000);

    let started = Instant::now();
    for i in 0..max {
        // A 100 x 100 grid, 2 m apart, centred on the origin.
        let x = (i % 100) as f32 * 2.0 - 99.0;
        let z = (i / 100) as f32 * 2.0 - 99.0;
        w.apply(builder, Command::ObjectCreate(spec_at(Vec3::new(x, 0.5, z))), T0).unwrap();
    }
    assert!(started.elapsed() < Duration::from_secs(10));
    assert_eq!(w.object_count(), max);
    assert!(matches!(
        w.apply(builder, Command::ObjectCreate(ObjectSpec::default()), T0),
        Err(WorldError::LimitExceeded(_))
    ));

    // A crowd, half of them walking somewhere.
    for i in 0..15 {
        let id = join(&mut w, &format!("u{i}"), Role::Visitor);
        if i % 2 == 0 {
            w.apply(
                id,
                Command::MoveTo { target: Vec3::new(100.0, 0.0, i as f32), mode: MoveMode::Run },
                T0,
            )
            .unwrap();
        }
    }

    let runs = 100;
    let started = Instant::now();
    for _ in 0..runs {
        let events = w.apply(builder, Command::Observe { radius: 100.0 }, T0).unwrap();
        let EventKind::Observation(o) = &events[0].kind else {
            panic!("expected Observation");
        };
        assert!(o.truncated);
        assert_eq!(o.objects.len(), w.config().limits.max_observed);
    }
    let per_observe = started.elapsed() / runs;
    assert!(per_observe < Duration::from_millis(50), "observe took {per_observe:?}");

    let ticks = 1000;
    let started = Instant::now();
    for i in 0..ticks {
        w.tick(0.05, T0 + i * 50);
    }
    let per_tick = started.elapsed() / ticks as u32;
    assert!(per_tick < Duration::from_millis(1), "tick took {per_tick:?}");
}
