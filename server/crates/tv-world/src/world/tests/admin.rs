//! SetAppearance and the owner-only moderation commands.

use super::*;
use crate::error::WorldError;
use crate::types::{Color, UserId};

fn kick(user: &str, reason: &str) -> Command {
    Command::Kick { user_id: user.into(), reason: reason.into() }
}

fn ban(user: &str, reason: &str) -> Command {
    Command::Ban { user_id: user.into(), reason: reason.into() }
}

fn unban(user: &str) -> Command {
    Command::Unban { user_id: user.into() }
}

fn set_role(user: &str, role: Role) -> Command {
    Command::SetRole { user_id: user.into(), role }
}

#[test]
fn set_appearance_is_sanitised_and_announced() {
    let mut w = world();
    let a = join(&mut w, "a", Role::Visitor);
    let appearance = Appearance {
        body_color: Color::new(0.2, 0.4, 0.6, 1.0),
        body_preset: "b".repeat(50),
        skin_color: Color::new(-1.0, 0.5, 9.0, 1.0),
        hair_color: Color::WHITE,
        hair_style: "short".into(),
        top_color: Color::WHITE,
        bottom_color: Color::new(0.0, 0.0, 0.0, 0.0),
    };
    let events = w.apply(a, Command::SetAppearance(appearance), T0).unwrap();
    let stored = w.avatar(a).unwrap().clone();
    assert_eq!(stored.appearance.body_preset, "b".repeat(32));
    assert_eq!(stored.appearance.skin_color, Color::new(0.0, 0.5, 1.0, 1.0));
    assert_eq!(stored.appearance.hair_style, "short");
    assert_eq!(events, vec![Event::new(Audience::All, EventKind::AvatarUpdated(stored.clone()))]);

    let mut bad = Appearance::default();
    bad.top_color.a = f32::NAN;
    assert!(matches!(
        w.apply(a, Command::SetAppearance(bad), T0),
        Err(WorldError::InvalidArgument(_))
    ));
    assert_eq!(w.avatar(a).unwrap(), &stored);
}

#[test]
fn only_owners_may_moderate() {
    let mut w = world();
    let visitor = join(&mut w, "v", Role::Visitor);
    let builder = join(&mut w, "b", Role::Builder);
    join(&mut w, "t", Role::Visitor);
    for actor in [visitor, builder] {
        for command in [kick("t", ""), ban("t", ""), unban("t"), set_role("t", Role::Owner)] {
            assert!(matches!(w.apply(actor, command, T0), Err(WorldError::PermissionDenied(_))));
        }
    }
    assert_eq!(w.avatars().len(), 3);
    assert_eq!(w.avatar_of_user(&"t".into()).unwrap().role, Role::Visitor);
}

#[test]
fn an_owner_may_not_remove_or_demote_themself() {
    let mut w = world();
    let o = join(&mut w, "o", Role::Owner);
    for command in
        [kick("o", ""), ban("o", ""), set_role("o", Role::Builder), set_role("o", Role::Visitor)]
    {
        assert!(matches!(w.apply(o, command, T0), Err(WorldError::InvalidArgument(_))));
    }
    assert_eq!(w.avatar(o).unwrap().role, Role::Owner);
    // Re-asserting their own role is harmless.
    let events = w.apply(o, set_role("o", Role::Owner), T0).unwrap();
    assert_eq!(events.last(), Some(&Event::new(Audience::Only(o), EventKind::Ack)));
}

#[test]
fn an_empty_user_id_is_invalid() {
    let mut w = world();
    let o = join(&mut w, "o", Role::Owner);
    for command in [kick(" ", ""), ban("", ""), unban(""), set_role("", Role::Builder)] {
        assert!(matches!(w.apply(o, command, T0), Err(WorldError::InvalidArgument(_))));
    }
}

#[test]
fn kick_removes_a_present_user() {
    let mut w = world();
    let o = join(&mut w, "o", Role::Owner);
    let t = join(&mut w, "t", Role::Builder);
    assert!(matches!(w.apply(o, kick("nobody", "x"), T0), Err(WorldError::NotFound(_))));

    let events = w.apply(o, kick("t", "  be nice  "), T0).unwrap();
    assert_eq!(
        events,
        vec![
            Event::new(Audience::Only(t), EventKind::Kicked { reason: "be nice".into() }),
            Event::new(Audience::AllExcept(t), EventKind::AvatarLeft { id: t }),
            Event::new(Audience::Only(o), EventKind::Ack),
        ]
    );
    assert!(w.avatar(t).is_none());
    assert_eq!(w.apply(t, Command::Stop, T0), Err(WorldError::NotJoined));
    assert!(w.leave(t, T0).is_empty());
    assert!(matches!(w.apply(o, kick("t", "again"), T0), Err(WorldError::NotFound(_))));

    // A kicked user may come back, with a fresh id.
    let (t2, _) = w.join(request("t", "t", Role::Builder), T0).unwrap();
    assert!(t2 > t);
    // An empty reason gets a default.
    let events = w.apply(o, kick("t", ""), T0).unwrap();
    assert!(matches!(&events[0].kind, EventKind::Kicked { reason } if !reason.is_empty()));
}

#[test]
fn ban_works_whether_or_not_the_user_is_present() {
    let mut w = world();
    let o = join(&mut w, "o", Role::Owner);
    let t = join(&mut w, "t", Role::Visitor);

    let events = w.apply(o, ban("t", "spam"), T0).unwrap();
    assert_eq!(
        events,
        vec![
            Event::new(
                Audience::Host,
                EventKind::UserBanned { user_id: "t".into(), reason: "spam".into() }
            ),
            Event::new(Audience::Only(t), EventKind::Kicked { reason: "spam".into() }),
            Event::new(Audience::AllExcept(t), EventKind::AvatarLeft { id: t }),
            Event::new(Audience::Only(o), EventKind::Ack),
        ]
    );
    assert!(w.avatar(t).is_none());

    let events = w.apply(o, ban("absent", ""), T0).unwrap();
    assert_eq!(events.len(), 2);
    assert_eq!(events[0].audience, Audience::Host);
    assert!(matches!(&events[0].kind, EventKind::UserBanned { user_id, reason }
        if user_id == &UserId::from("absent") && !reason.is_empty()));
    assert_eq!(events[1], Event::new(Audience::Only(o), EventKind::Ack));
}

#[test]
fn unban_tells_the_host() {
    let mut w = world();
    let o = join(&mut w, "o", Role::Owner);
    assert_eq!(
        w.apply(o, unban("t"), T0).unwrap(),
        vec![
            Event::new(Audience::Host, EventKind::UserUnbanned { user_id: "t".into() }),
            Event::new(Audience::Only(o), EventKind::Ack),
        ]
    );
}

#[test]
fn set_role_applies_at_once_and_tells_the_host() {
    let mut w = world();
    let o = join(&mut w, "o", Role::Owner);
    let t = join(&mut w, "t", Role::Visitor);
    assert!(matches!(
        w.apply(t, Command::ObjectCreate(ObjectSpec::default()), T0),
        Err(WorldError::PermissionDenied(_))
    ));

    let events = w.apply(o, set_role("t", Role::Builder), T0).unwrap();
    let updated = w.avatar(t).unwrap().clone();
    assert_eq!(updated.role, Role::Builder);
    assert_eq!(
        events,
        vec![
            Event::new(
                Audience::Host,
                EventKind::UserRoleChanged { user_id: "t".into(), role: Role::Builder }
            ),
            Event::new(Audience::All, EventKind::AvatarUpdated(updated)),
            Event::new(Audience::Only(o), EventKind::Ack),
        ]
    );
    create(&mut w, t, ObjectSpec::default());

    // A second owner can then moderate, including demoting the first.
    w.apply(o, set_role("t", Role::Owner), T0).unwrap();
    w.apply(t, set_role("o", Role::Visitor), T0).unwrap();
    assert_eq!(w.avatar(o).unwrap().role, Role::Visitor);

    // Absent users: only the host hears about it.
    assert_eq!(
        w.apply(t, set_role("absent", Role::Owner), T0).unwrap(),
        vec![
            Event::new(
                Audience::Host,
                EventKind::UserRoleChanged { user_id: "absent".into(), role: Role::Owner }
            ),
            Event::new(Audience::Only(t), EventKind::Ack),
        ]
    );
}
