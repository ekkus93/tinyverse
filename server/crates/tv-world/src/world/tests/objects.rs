//! Object creation, validation, editing, deletion and permissions.

use super::*;
use crate::error::WorldError;
use crate::types::{Color, Material, Object, ObjectPatch, Shape};

/// Recognises the expected kind of error.
type ErrorCheck = fn(&WorldError) -> bool;

fn update(
    w: &mut World,
    actor: EntityId,
    id: EntityId,
    patch: ObjectPatch,
) -> Result<Object, WorldError> {
    let events = w.apply(actor, Command::ObjectUpdate { id, patch }, T0)?;
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].audience, Audience::All);
    match &events[0].kind {
        EventKind::ObjectUpdated { object, by } => {
            assert_eq!(*by, actor);
            Ok(object.clone())
        }
        other => panic!("expected ObjectUpdated, got {other:?}"),
    }
}

fn create_err(w: &mut World, actor: EntityId, spec: ObjectSpec) -> WorldError {
    let count = w.object_count();
    let next = w.next_entity_id();
    let err = w.apply(actor, Command::ObjectCreate(spec), T0).unwrap_err();
    assert_eq!(w.object_count(), count);
    assert_eq!(w.next_entity_id(), next);
    err
}

#[test]
fn builders_and_owners_create_visitors_cannot() {
    let mut w = world();
    let visitor = join(&mut w, "v", Role::Visitor);
    let builder = join(&mut w, "b", Role::Builder);
    let owner = join(&mut w, "o", Role::Owner);

    assert!(matches!(
        create_err(&mut w, visitor, ObjectSpec::default()),
        WorldError::PermissionDenied(_)
    ));

    for (actor, user) in [(builder, "b"), (owner, "o")] {
        let spec = ObjectSpec {
            name: "Table".into(),
            shape: Shape::Cylinder,
            position: Vec3::new(1.0, 2.0, 3.0),
            scale: Vec3::new(2.0, 1.0, 0.5),
            color: Color::new(1.0, 0.0, 0.0, 1.0),
            material: Material::Metal,
            solid: false,
            locked: true,
            ..ObjectSpec::default()
        };
        let events = w.apply(actor, Command::ObjectCreate(spec.clone()), T0).unwrap();
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].audience, Audience::All);
        let EventKind::ObjectCreated { object, by } = &events[0].kind else {
            panic!("expected ObjectCreated");
        };
        assert_eq!(*by, actor);
        assert_eq!(object.owner, user.into());
        assert_eq!(object.spec, spec);
        assert_eq!(w.object(object.id), Some(object));
    }
}

#[test]
fn creation_stops_at_the_object_limit() {
    let mut w = world_with(|c| c.limits.max_objects = 2);
    let b = join(&mut w, "b", Role::Builder);
    let first = create(&mut w, b, ObjectSpec::default());
    create(&mut w, b, ObjectSpec::default());
    assert!(matches!(create_err(&mut w, b, ObjectSpec::default()), WorldError::LimitExceeded(_)));
    w.apply(b, Command::ObjectDelete { id: first }, T0).unwrap();
    create(&mut w, b, ObjectSpec::default());
}

#[test]
fn spec_validation_errors() {
    let mut w = world();
    let b = join(&mut w, "b", Role::Builder);
    let limits = w.config().limits.clone();
    let cases: Vec<(ObjectSpec, ErrorCheck)> = vec![
        (ObjectSpec { name: "n".repeat(limits.max_name_chars + 1), ..Default::default() }, |e| {
            matches!(e, WorldError::InvalidArgument(_))
        }),
        (
            ObjectSpec { scale: Vec3::new(limits.min_scale / 2.0, 1.0, 1.0), ..Default::default() },
            |e| matches!(e, WorldError::InvalidArgument(_)),
        ),
        (
            ObjectSpec { scale: Vec3::new(1.0, limits.max_scale + 1.0, 1.0), ..Default::default() },
            |e| matches!(e, WorldError::InvalidArgument(_)),
        ),
        (ObjectSpec { scale: Vec3::new(1.0, 1.0, -1.0), ..Default::default() }, |e| {
            matches!(e, WorldError::InvalidArgument(_))
        }),
        (
            ObjectSpec {
                shape: Shape::Plane,
                scale: Vec3::new(100.0, 1.0, 1.0),
                ..Default::default()
            },
            |e| matches!(e, WorldError::InvalidArgument(_)),
        ),
        (spec_at(Vec3::new(0.0, 300.0, 0.0)), |e| matches!(e, WorldError::OutOfBounds(_))),
        (spec_at(Vec3::new(-129.0, 0.0, 0.0)), |e| matches!(e, WorldError::OutOfBounds(_))),
        (spec_at(Vec3::new(f32::NAN, 0.0, 0.0)), |e| matches!(e, WorldError::InvalidArgument(_))),
        (
            ObjectSpec {
                rotation: Quat::from_xyzw(0.0, f32::INFINITY, 0.0, 1.0),
                ..Default::default()
            },
            |e| matches!(e, WorldError::InvalidArgument(_)),
        ),
        (
            ObjectSpec {
                scale: Vec3::new(1.0, f32::NAN, 1.0),
                shape: Shape::Plane,
                ..Default::default()
            },
            |e| matches!(e, WorldError::InvalidArgument(_)),
        ),
        (ObjectSpec { color: Color::new(0.0, 0.0, f32::NAN, 1.0), ..Default::default() }, |e| {
            matches!(e, WorldError::InvalidArgument(_))
        }),
    ];
    for (spec, expected) in cases {
        let err = create_err(&mut w, b, spec.clone());
        assert!(expected(&err), "{spec:?} gave {err:?}");
    }
}

#[test]
fn spec_is_normalised() {
    let mut w = world();
    let b = join(&mut w, "b", Role::Builder);
    let limits = w.config().limits.clone();

    let id = create(
        &mut w,
        b,
        ObjectSpec {
            name: format!("  {}  ", "n".repeat(limits.max_name_chars)),
            rotation: Quat::from_xyzw(0.0, 0.0, 0.0, 0.0),
            color: Color::new(1.5, -0.5, 0.25, 2.0),
            scale: Vec3::new(limits.min_scale, limits.max_scale, 1.0),
            ..Default::default()
        },
    );
    let s = &w.object(id).unwrap().spec;
    assert_eq!(s.name, "n".repeat(limits.max_name_chars));
    assert_eq!(s.rotation, Quat::IDENTITY);
    assert_eq!(s.color, Color::new(1.0, 0.0, 0.25, 1.0));

    let id = create(
        &mut w,
        b,
        ObjectSpec { rotation: Quat::from_xyzw(0.0, 2.0, 0.0, 2.0), ..Default::default() },
    );
    let r = w.object(id).unwrap().spec.rotation;
    assert!(r.is_normalized());
    assert!(approx_quat(r, Quat::from_rotation_y(std::f32::consts::FRAC_PI_2)));

    // A plane's y scale is ignored: kept as given when in range, clamped otherwise.
    for (y, stored) in [(0.0, limits.min_scale), (5.0, 5.0), (1000.0, limits.max_scale)] {
        let id = create(
            &mut w,
            b,
            ObjectSpec { shape: Shape::Plane, scale: Vec3::new(4.0, y, 4.0), ..Default::default() },
        );
        assert_eq!(w.object(id).unwrap().spec.scale, Vec3::new(4.0, stored, 4.0));
    }
}

#[test]
fn update_applies_only_present_fields() {
    let mut w = world();
    let b = join(&mut w, "b", Role::Builder);
    let id = create(&mut w, b, spec_at(Vec3::new(1.0, 1.0, 1.0)));
    let before = w.object(id).unwrap().clone();

    let unchanged = update(&mut w, b, id, ObjectPatch::default()).unwrap();
    assert_eq!(unchanged, before, "an empty patch still answers with the object");

    let after = update(
        &mut w,
        b,
        id,
        ObjectPatch {
            name: Some("Lamp".into()),
            shape: Some(Shape::Sphere),
            position: Some(Vec3::new(5.0, 6.0, 7.0)),
            rotation: Some(Quat::from_xyzw(0.0, 0.0, 0.0, 0.0)),
            scale: Some(Vec3::splat(3.0)),
            color: Some(Color::new(0.0, 2.0, 0.0, 1.0)),
            material: Some(Material::Emissive),
            solid: Some(false),
            locked: Some(true),
        },
    )
    .unwrap();
    assert_eq!(after.id, id);
    assert_eq!(after.owner, before.owner);
    assert_eq!(
        after.spec,
        ObjectSpec {
            name: "Lamp".into(),
            shape: Shape::Sphere,
            position: Vec3::new(5.0, 6.0, 7.0),
            rotation: Quat::IDENTITY,
            scale: Vec3::splat(3.0),
            color: Color::new(0.0, 1.0, 0.0, 1.0),
            material: Material::Emissive,
            solid: false,
            locked: true,
        }
    );
    assert_eq!(w.object(id), Some(&after));

    let moved =
        update(&mut w, b, id, ObjectPatch { position: Some(Vec3::ZERO), ..Default::default() })
            .unwrap();
    assert_eq!(moved.spec, ObjectSpec { position: Vec3::ZERO, ..after.spec });
}

#[test]
fn update_validates_the_result_and_changes_nothing_on_error() {
    let mut w = world();
    let b = join(&mut w, "b", Role::Builder);
    let id = create(&mut w, b, ObjectSpec::default());
    let before = w.object(id).unwrap().clone();
    let bad = [
        (ObjectPatch { scale: Some(Vec3::splat(100.0)), ..Default::default() }, "invalid"),
        (
            ObjectPatch { position: Some(Vec3::new(0.0, -20.0, 0.0)), ..Default::default() },
            "bounds",
        ),
        (
            ObjectPatch { name: Some("x".repeat(65)), solid: Some(false), ..Default::default() },
            "invalid",
        ),
        (
            ObjectPatch { color: Some(Color::new(f32::NAN, 0.0, 0.0, 0.0)), ..Default::default() },
            "invalid",
        ),
    ];
    for (patch, kind) in bad {
        let err = w.apply(b, Command::ObjectUpdate { id, patch }, T0).unwrap_err();
        match kind {
            "bounds" => assert!(matches!(err, WorldError::OutOfBounds(_)), "{err:?}"),
            _ => assert!(matches!(err, WorldError::InvalidArgument(_)), "{err:?}"),
        }
        assert_eq!(w.object(id), Some(&before));
    }
    // Turning a thin box into a plane does not fail on its y scale.
    let thin = update(
        &mut w,
        b,
        id,
        ObjectPatch {
            shape: Some(Shape::Plane),
            scale: Some(Vec3::new(2.0, 0.0, 2.0)),
            ..Default::default()
        },
    )
    .unwrap();
    assert_eq!(thin.spec.scale.y, w.config().limits.min_scale);
}

#[test]
fn unknown_objects_are_not_found() {
    let mut w = world();
    let o = join(&mut w, "o", Role::Owner);
    let v = join(&mut w, "v", Role::Visitor);
    for actor in [o, v] {
        assert!(matches!(
            w.apply(actor, Command::ObjectUpdate { id: 99, patch: Default::default() }, T0),
            Err(WorldError::NotFound(_))
        ));
        assert!(matches!(
            w.apply(actor, Command::ObjectDelete { id: 99 }, T0),
            Err(WorldError::NotFound(_))
        ));
    }
    // An avatar id is not an object id.
    assert!(matches!(
        w.apply(o, Command::ObjectDelete { id: v }, T0),
        Err(WorldError::NotFound(_))
    ));
}

#[test]
fn delete_removes_and_announces() {
    let mut w = world();
    let b = join(&mut w, "b", Role::Builder);
    let id = create(&mut w, b, ObjectSpec::default());
    let events = w.apply(b, Command::ObjectDelete { id }, T0).unwrap();
    assert_eq!(events, vec![Event::new(Audience::All, EventKind::ObjectDeleted { id, by: b })]);
    assert!(w.object(id).is_none());
    assert!(matches!(w.apply(b, Command::ObjectDelete { id }, T0), Err(WorldError::NotFound(_))));
    assert!(create(&mut w, b, ObjectSpec::default()) > id);
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum Op {
    Update,
    Delete,
    ToggleLock,
    /// Sends `locked` with its current value, which is not a lock change.
    SameLock,
}

/// Whether `role`, acting on an object it does (`own`) or does not own that
/// is `locked` or not, may perform `op`.
fn allowed(role: Role, own: bool, locked: bool, op: Op) -> bool {
    match role {
        Role::Visitor => false,
        Role::Owner => true,
        Role::Builder => match op {
            Op::ToggleLock => own,
            _ => own || !locked,
        },
    }
}

#[test]
fn permission_matrix() {
    for role in [Role::Visitor, Role::Builder, Role::Owner] {
        for own in [true, false] {
            for locked in [false, true] {
                for op in [Op::Update, Op::Delete, Op::ToggleLock, Op::SameLock] {
                    let owner_user = if own { "actor" } else { "someone-else" };
                    let object = Object {
                        id: 1,
                        owner: owner_user.into(),
                        spec: ObjectSpec { locked, ..Default::default() },
                    };
                    let mut w = World::new(WorldConfig::default(), vec![object.clone()], 10);
                    let actor = join(&mut w, "actor", role);
                    let command = match op {
                        Op::Update => Command::ObjectUpdate {
                            id: 1,
                            patch: ObjectPatch {
                                name: Some("renamed".into()),
                                ..Default::default()
                            },
                        },
                        Op::Delete => Command::ObjectDelete { id: 1 },
                        Op::ToggleLock => Command::ObjectUpdate {
                            id: 1,
                            patch: ObjectPatch { locked: Some(!locked), ..Default::default() },
                        },
                        Op::SameLock => Command::ObjectUpdate {
                            id: 1,
                            patch: ObjectPatch { locked: Some(locked), ..Default::default() },
                        },
                    };
                    let result = w.apply(actor, command, T0);
                    let case = format!("{role:?} own={own} locked={locked} {op:?}");
                    if allowed(role, own, locked, op) {
                        let events = result.unwrap_or_else(|e| panic!("{case}: {e:?}"));
                        assert_eq!(audiences(&events), vec![Audience::All], "{case}");
                        match op {
                            Op::Delete => assert!(w.object(1).is_none(), "{case}"),
                            Op::Update => assert_eq!(w.object(1).unwrap().spec.name, "renamed"),
                            Op::ToggleLock => assert_eq!(w.object(1).unwrap().spec.locked, !locked),
                            Op::SameLock => assert_eq!(w.object(1), Some(&object)),
                        }
                    } else {
                        assert!(
                            matches!(result, Err(WorldError::PermissionDenied(_))),
                            "{case}: {result:?}"
                        );
                        assert_eq!(w.object(1), Some(&object), "{case}");
                    }
                }
            }
        }
    }
}

#[test]
fn a_builder_cannot_sneak_a_lock_change_into_an_edit() {
    let object = Object { id: 1, owner: "other".into(), spec: ObjectSpec::default() };
    let mut w = World::new(WorldConfig::default(), vec![object.clone()], 10);
    let b = join(&mut w, "b", Role::Builder);
    let patch =
        ObjectPatch { name: Some("mine now".into()), locked: Some(true), ..Default::default() };
    assert!(matches!(
        w.apply(b, Command::ObjectUpdate { id: 1, patch }, T0),
        Err(WorldError::PermissionDenied(_))
    ));
    assert_eq!(w.object(1), Some(&object));
}
