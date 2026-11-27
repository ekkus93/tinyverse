//! Local and world chat, and chat history.

use super::*;
use crate::error::WorldError;
use crate::types::{ChatChannel, ChatEntry};

fn say(w: &mut World, id: EntityId, channel: ChatChannel, text: &str, now: i64) -> Vec<Event> {
    w.apply(id, Command::Chat { channel, text: text.to_owned() }, now).unwrap()
}

fn texts(entries: &[ChatEntry]) -> Vec<&str> {
    entries.iter().map(|e| e.text.as_str()).collect()
}

const MINUTE: i64 = 60_000;
const HOUR: i64 = 60 * MINUTE;

#[test]
fn local_chat_reaches_avatars_within_the_radius_including_the_speaker() {
    let mut w = world();
    let speaker = join(&mut w, "s", Role::Visitor);
    let near = join(&mut w, "n", Role::Visitor);
    let edge = join(&mut w, "e", Role::Visitor);
    let far = join(&mut w, "f", Role::Visitor);
    place(&mut w, speaker, Vec3::new(5.0, 0.0, 5.0), 0.0);
    place(&mut w, near, Vec3::new(10.0, 3.0, 5.0), 0.0);
    place(&mut w, edge, Vec3::new(5.0, 0.0, 25.0), 0.0);
    place(&mut w, far, Vec3::new(5.0, 0.0, 25.1), 0.0);

    let events = say(&mut w, speaker, ChatChannel::Local, "  hello  ", T0);
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].audience, Audience::Set(vec![speaker, near, edge]));
    let EventKind::Chat(entry) = &events[0].kind else {
        panic!("expected Chat");
    };
    assert_eq!(
        entry,
        &ChatEntry {
            sender_avatar: speaker,
            sender_user: "s".into(),
            sender_name: "s".into(),
            sender_kind: ClientKind::Human,
            channel: ChatChannel::Local,
            text: "hello".into(),
            timestamp_ms: T0,
        }
    );
    assert!(w.chat_history(0, 0, T0).is_empty(), "local chat is never retained");
}

#[test]
fn world_chat_reaches_everyone_and_is_retained() {
    let mut w = world();
    let a = join_agent(&mut w, "bot", Role::Visitor);
    let events = say(&mut w, a, ChatChannel::World, "hi all", T0);
    assert_eq!(events[0].audience, Audience::All);
    let EventKind::Chat(entry) = &events[0].kind else {
        panic!("expected Chat");
    };
    assert_eq!(entry.sender_kind, ClientKind::Agent);
    assert_eq!(w.chat_history(0, 0, T0), vec![entry.clone()]);
}

#[test]
fn chat_text_is_validated() {
    let mut w = world();
    let a = join(&mut w, "a", Role::Visitor);
    let max = w.config().limits.max_chat_chars;
    for text in [String::new(), "   ".into(), "ü".repeat(max + 1)] {
        for channel in [ChatChannel::Local, ChatChannel::World] {
            assert!(matches!(
                w.apply(a, Command::Chat { channel, text: text.clone() }, T0),
                Err(WorldError::InvalidArgument(_))
            ));
        }
    }
    assert!(w.chat_history(0, 0, T0).is_empty());
    // Exactly the limit in characters, padded with whitespace, is fine.
    let ok = format!("  {}  ", "ü".repeat(max));
    say(&mut w, a, ChatChannel::World, &ok, T0);
    assert_eq!(w.chat_history(0, 0, T0)[0].text, "ü".repeat(max));
}

#[test]
fn history_drops_entries_older_than_the_retention_period() {
    let mut w = world_with(|c| c.chat_retention_ms = 3 * HOUR);
    let a = join(&mut w, "a", Role::Visitor);
    for (i, t) in [0, HOUR, 2 * HOUR].iter().enumerate() {
        say(&mut w, a, ChatChannel::World, &format!("m{i}"), T0 + t);
    }
    assert_eq!(texts(&w.chat_history(0, 0, T0 + 3 * HOUR)), ["m0", "m1", "m2"]);
    // Read-only queries already hide expired entries ...
    assert_eq!(texts(&w.chat_history(0, 0, T0 + 3 * HOUR + 1)), ["m1", "m2"]);
    assert_eq!(texts(&w.chat_history(0, 0, T0 + 5 * HOUR + 1)), Vec::<&str>::new());
    // ... and tick drops them for good.
    w.tick(0.05, T0 + 4 * HOUR + 1);
    assert_eq!(w.chat.len(), 1);
    assert_eq!(texts(&w.chat_history(0, 0, T0 + 4 * HOUR + 1)), ["m2"]);
}

#[test]
fn history_keeps_only_the_newest_entries() {
    let mut w = world_with(|c| c.chat_history_max = 3);
    let a = join(&mut w, "a", Role::Visitor);
    for i in 0..5 {
        say(&mut w, a, ChatChannel::World, &format!("m{i}"), T0 + i);
    }
    assert_eq!(texts(&w.chat_history(0, 0, T0 + 10)), ["m2", "m3", "m4"]);
}

#[test]
fn zero_retention_disables_history() {
    let mut w = world_with(|c| c.chat_retention_ms = 0);
    let a = join(&mut w, "a", Role::Visitor);
    let events = say(&mut w, a, ChatChannel::World, "gone", T0);
    assert_eq!(events[0].audience, Audience::All, "still delivered live");
    assert!(w.chat_history(0, 0, T0).is_empty());
    assert!(w.chat.is_empty());
}

#[test]
fn history_filters_by_time_and_limit() {
    let mut w = world();
    let a = join(&mut w, "a", Role::Visitor);
    for i in 0..5 {
        say(&mut w, a, ChatChannel::World, &format!("m{i}"), T0 + i * MINUTE);
    }
    let now = T0 + 10 * MINUTE;
    assert_eq!(w.chat_history(0, 0, now).len(), 5);
    assert_eq!(texts(&w.chat_history(T0 + MINUTE, 0, now)), ["m2", "m3", "m4"]);
    assert_eq!(texts(&w.chat_history(0, 2, now)), ["m3", "m4"]);
    assert_eq!(texts(&w.chat_history(T0, 10, now)), ["m1", "m2", "m3", "m4"]);
    assert!(w.chat_history(T0 + 4 * MINUTE, 0, now).is_empty());
}

#[test]
fn chat_history_command_answers_the_actor_only() {
    let mut w = world();
    let a = join(&mut w, "a", Role::Visitor);
    let b = join(&mut w, "b", Role::Visitor);
    say(&mut w, a, ChatChannel::World, "one", T0);
    say(&mut w, a, ChatChannel::World, "two", T0 + 1);
    say(&mut w, a, ChatChannel::Local, "local", T0 + 2);
    let events = w.apply(b, Command::ChatHistory { since_ms: 0, limit: 1 }, T0 + 3).unwrap();
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].audience, Audience::Only(b));
    let EventKind::ChatHistory(entries) = &events[0].kind else {
        panic!("expected ChatHistory");
    };
    assert_eq!(texts(entries), ["two"]);
}

#[test]
fn history_survives_the_sender_leaving() {
    let mut w = world();
    let a = join(&mut w, "a", Role::Visitor);
    say(&mut w, a, ChatChannel::World, "bye", T0);
    w.leave(a, T0);
    assert_eq!(w.chat_history(0, 0, T0)[0].sender_avatar, a);
}
