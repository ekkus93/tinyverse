//! Join, leave, rejoin, ids and accessors.

use super::*;
use crate::error::WorldError;
use crate::types::{Color, Object};

#[test]
fn join_places_avatar_at_spawn_and_announces_it_to_others() {
    let mut w = world_with(|c| c.spawn_point = Vec3::new(1.0, 2.0, 3.0));
    let (id, events) = w.join(request("u1", "  Alice  ", Role::Builder), T0).unwrap();
    let avatar = w.avatar(id).unwrap().clone();
    assert_eq!(avatar.name, "Alice");
    assert_eq!(avatar.role, Role::Builder);
    assert_eq!(avatar.kind, ClientKind::Human);
    assert_eq!(avatar.transform.position, Vec3::new(1.0, 2.0, 3.0));
    assert_eq!(avatar.transform.yaw, 0.0);
    assert_eq!(avatar.transform.velocity, Vec3::ZERO);
    assert_eq!(avatar.transform.move_mode, MoveMode::Walk);
    assert_eq!(avatar.transform.animation, "idle");
    assert_eq!(events, vec![Event::new(Audience::AllExcept(id), EventKind::AvatarJoined(avatar))]);
}

#[test]
fn join_rejects_bad_names_without_changing_state() {
    let mut w = world();
    for name in ["", "   ", "\t\n"] {
        assert!(matches!(
            w.join(request("u", name, Role::Builder), T0),
            Err(WorldError::InvalidArgument(_))
        ));
    }
    let long: String = "é".repeat(65);
    assert!(matches!(
        w.join(request("u", &long, Role::Builder), T0),
        Err(WorldError::InvalidArgument(_))
    ));
    assert!(w.avatars().is_empty());
    assert_eq!(w.next_entity_id(), 1);

    // Exactly the limit, counted in characters rather than bytes, is fine.
    let ok: String = "é".repeat(64);
    let (id, _) = w.join(request("u", &ok, Role::Builder), T0).unwrap();
    assert_eq!(w.avatar(id).unwrap().name, ok);
}

#[test]
fn join_sanitises_appearance() {
    let mut w = world();
    let mut req = request("u", "A", Role::Visitor);
    req.appearance.body_color = Color::new(2.0, -1.0, 0.5, 1.5);
    req.appearance.body_preset = "p".repeat(40);
    req.appearance.hair_style = "h".repeat(33);
    let (id, _) = w.join(req, T0).unwrap();
    let a = &w.avatar(id).unwrap().appearance;
    assert_eq!(a.body_color, Color::new(1.0, 0.0, 0.5, 1.0));
    assert_eq!(a.body_preset, "p".repeat(32));
    assert_eq!(a.hair_style, "h".repeat(32));

    let mut bad = request("v", "B", Role::Visitor);
    bad.appearance.skin_color.g = f32::NAN;
    assert!(matches!(w.join(bad, T0), Err(WorldError::InvalidArgument(_))));
    assert_eq!(w.avatars().len(), 1);
}

#[test]
fn join_refuses_when_full_but_a_rejoin_does_not_count() {
    let mut w = world_with(|c| c.limits.max_users = 2);
    let a = join(&mut w, "a", Role::Visitor);
    join(&mut w, "b", Role::Visitor);
    let next = w.next_entity_id();
    assert_eq!(w.join(request("c", "c", Role::Visitor), T0), Err(WorldError::WorldFull));
    assert_eq!(w.next_entity_id(), next);
    assert_eq!(w.avatars().len(), 2);

    let (a2, _) = w.join(request("a", "a", Role::Visitor), T0).unwrap();
    assert_ne!(a, a2);
    assert_eq!(w.avatars().len(), 2);
}

#[test]
fn rejoin_replaces_the_old_avatar() {
    let mut w = world();
    let old = join(&mut w, "u", Role::Builder);
    let bystander = join(&mut w, "b", Role::Visitor);
    let (new, events) = w.join(request("u", "U2", Role::Builder), T0).unwrap();
    assert!(new > old);
    assert_eq!(events.len(), 3);
    assert_eq!(events[0].audience, Audience::Only(old));
    assert!(matches!(events[0].kind, EventKind::Kicked { .. }));
    assert_eq!(events[1], Event::new(Audience::AllExcept(old), EventKind::AvatarLeft { id: old }));
    assert_eq!(events[2].audience, Audience::AllExcept(new));
    assert!(matches!(&events[2].kind, EventKind::AvatarJoined(a) if a.id == new));

    assert!(w.avatar(old).is_none());
    assert_eq!(w.avatar_of_user(&"u".into()).unwrap().id, new);
    assert_eq!(w.avatars().len(), 2);
    assert_eq!(w.apply(old, Command::Stop, T0), Err(WorldError::NotJoined));
    assert!(w.leave(old, T0).is_empty());
    assert!(w.avatar(bystander).is_some());
}

#[test]
fn leave_announces_departure_once() {
    let mut w = world();
    let a = join(&mut w, "a", Role::Visitor);
    let b = join(&mut w, "b", Role::Visitor);
    assert_eq!(
        w.leave(a, T0),
        vec![Event::new(Audience::AllExcept(a), EventKind::AvatarLeft { id: a })]
    );
    assert!(w.leave(a, T0).is_empty());
    assert!(w.leave(999, T0).is_empty());
    assert_eq!(w.apply(a, Command::Stop, T0), Err(WorldError::NotJoined));
    assert_eq!(w.avatars().iter().map(|x| x.id).collect::<Vec<_>>(), vec![b]);
}

#[test]
fn every_command_from_an_unknown_actor_is_not_joined() {
    let mut w = world();
    let commands = vec![
        Command::AvatarState(transform(Vec3::ZERO, MoveMode::Walk)),
        Command::MoveTo { target: Vec3::ONE, mode: MoveMode::Walk },
        Command::Stop,
        Command::Chat { channel: crate::ChatChannel::World, text: "hi".into() },
        Command::ChatHistory { since_ms: 0, limit: 0 },
        Command::ObjectCreate(ObjectSpec::default()),
        Command::ObjectUpdate { id: 1, patch: Default::default() },
        Command::ObjectDelete { id: 1 },
        Command::Observe { radius: 0.0 },
        Command::SetAppearance(Appearance::default()),
        Command::Kick { user_id: "x".into(), reason: String::new() },
        Command::Ban { user_id: "x".into(), reason: String::new() },
        Command::Unban { user_id: "x".into() },
        Command::SetRole { user_id: "x".into(), role: Role::Owner },
    ];
    for command in commands {
        assert_eq!(w.apply(42, command, T0), Err(WorldError::NotJoined));
    }
}

#[test]
fn ids_are_shared_monotonic_and_never_reused() {
    let mut w = world();
    let a = join(&mut w, "a", Role::Builder);
    assert_eq!(a, 1);
    let o1 = create(&mut w, a, spec_at(Vec3::ZERO));
    assert_eq!(o1, 2);
    let b = join(&mut w, "b", Role::Builder);
    assert_eq!(b, 3);
    w.apply(a, Command::ObjectDelete { id: o1 }, T0).unwrap();
    w.leave(b, T0);
    let o2 = create(&mut w, a, spec_at(Vec3::ZERO));
    let c = join(&mut w, "c", Role::Builder);
    assert_eq!((o2, c), (4, 5));
    assert_eq!(w.next_entity_id(), 6);
}

#[test]
fn new_starts_at_given_id_and_never_below_loaded_objects() {
    let w = World::new(WorldConfig::default(), Vec::new(), 100);
    assert_eq!(w.next_entity_id(), 100);

    let object = |id| Object { id, owner: "o".into(), spec: ObjectSpec::default() };
    let mut w = World::new(WorldConfig::default(), vec![object(7), object(3)], 50);
    assert_eq!(w.next_entity_id(), 50);
    assert_eq!(w.object_count(), 2);
    assert_eq!(w.objects().iter().map(|o| o.id).collect::<Vec<_>>(), vec![3, 7]);
    assert_eq!(w.object(7).unwrap().id, 7);
    assert!(w.object(5).is_none());
    assert_eq!(join(&mut w, "a", Role::Visitor), 50);

    // A caller that breaks the precondition still never gets a clash.
    let w = World::new(WorldConfig::default(), vec![object(7)], 2);
    assert_eq!(w.next_entity_id(), 8);
    let w = World::new(WorldConfig::default(), Vec::new(), 0);
    assert_eq!(w.next_entity_id(), 1);
}

#[test]
fn accessors_list_in_id_order() {
    let mut w = world();
    let ids: Vec<_> = ["c", "a", "b"].iter().map(|u| join(&mut w, u, Role::Builder)).collect();
    assert_eq!(w.avatars().iter().map(|a| a.id).collect::<Vec<_>>(), ids);
    assert_eq!(w.avatar_of_user(&"a".into()).unwrap().id, ids[1]);
    assert!(w.avatar_of_user(&"zz".into()).is_none());
    assert_eq!(w.config(), &WorldConfig::default());
    let o = create(&mut w, ids[0], spec_at(Vec3::ZERO));
    assert_eq!(w.object_count(), 1);
    assert_eq!(w.objects()[0].id, o);
}
