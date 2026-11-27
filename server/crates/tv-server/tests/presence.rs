//! Joining, moving, leaving, and chat.

mod common;

use std::time::Duration;

use common::*;
use tv_proto::Encoding;
use tv_proto::v1::{self, client_message::Body as C, server_message::Body as S};

#[tokio::test]
async fn two_clients_see_each_other_join_move_and_leave() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut a = join(&server, "Ada").await;
    let mut b = join_with(&server, Encoding::Binary, hello("Bob")).await;

    // Bob's snapshot holds Ada; Ada hears that Bob joined.
    let others = b.snapshot_avatars();
    assert_eq!(others.len(), 1);
    assert_eq!(others[0].id, a.avatar_id());
    assert_eq!(others[0].name, "Ada");
    let msg = a.recv_where("AvatarJoined", |m| matches!(m.body, Some(S::AvatarJoined(_)))).await;
    let Some(S::AvatarJoined(j)) = msg.body else { unreachable!() };
    assert_eq!(j.avatar.unwrap().id, b.avatar_id());

    // Ada walks for a second at 25 reports per second; Bob receives the
    // batches at about the tick rate, and Ada never gets her own entry.
    let (ada, bob) = (a.avatar_id(), b.avatar_id());
    let mover = async {
        for i in 1..=25 {
            a.send(state_at(i as f32 * 0.1, 0.0, 0.0)).await;
            tokio::time::sleep(Duration::from_millis(40)).await;
        }
        a.collect(Duration::from_millis(200)).await
    };
    let watcher = b.collect(Duration::from_millis(1200));
    let (ada_saw, bob_saw) = tokio::join!(mover, watcher);
    assert!(
        ada_saw.iter().all(|m| state_of(m, ada).is_none()),
        "own entry must be left out: {ada_saw:?}"
    );
    let batches = bob_saw.iter().filter(|m| state_of(m, ada).is_some()).count();
    assert!((10..=26).contains(&batches), "{batches} AvatarStates batches in ~1 s");
    assert!(bob_saw.iter().all(|m| state_of(m, bob).is_none()));
    let last = bob_saw.iter().rev().find_map(|m| state_of(m, ada)).unwrap();
    assert!((last.x - 2.5).abs() < 1e-4, "last position {last:?}");
    for m in bob_saw.iter().filter(|m| is_avatar_states(m)) {
        assert_eq!(m.reply_to, 0);
        let Some(S::AvatarStates(s)) = &m.body else { unreachable!() };
        assert!(s.server_time_ms > 0);
    }

    // Ada leaves; Bob hears it.
    a.client.close().await;
    let msg = b.recv_where("AvatarLeft", |m| matches!(m.body, Some(S::AvatarLeft(_)))).await;
    let Some(S::AvatarLeft(l)) = msg.body else { unreachable!() };
    assert_eq!(l.avatar_id, ada);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn move_to_is_server_driven_and_reported_to_the_mover() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut agent = join(&server, "agent").await;
    let id = agent.avatar_id();
    agent.send(C::MoveTo(v1::MoveTo { target: Some(v(2.0, 0.0, 0.0)), mode: 0 })).await;
    let msg = agent.recv_where("own AvatarStates", |m| state_of(m, id).is_some()).await;
    let p = state_of(&msg, id).unwrap();
    assert!(p.x > 0.0 && p.x <= 2.0);
    agent
        .recv_where("arrival", |m| state_of(m, id).is_some_and(|p| (p.x - 2.0).abs() < 1e-3))
        .await;
    server.shutdown().await.unwrap();
}

/// The next chat line a client receives, with its reply_to.
async fn next_chat(c: &mut Session) -> (u32, v1::ChatEntry) {
    let msg = c.recv_where("ChatMessage", |m| matches!(m.body, Some(S::ChatMessage(_)))).await;
    let Some(S::ChatMessage(chat)) = msg.body else { unreachable!() };
    (msg.reply_to, chat.entry.unwrap())
}

#[tokio::test]
async fn local_chat_carries_20_m_and_world_chat_everywhere() {
    // Fast limits: the listeners jump 10 m and 30 m in one report.
    let world = TestWorld::fast();
    let server = world.start().await;
    let mut a = join(&server, "speaker").await;
    let mut near = join(&server, "near").await;
    let mut far = join(&server, "far").await;
    move_and_wait(&mut near, &mut a, 10.0, 0.0).await;
    move_and_wait(&mut far, &mut a, 30.0, 0.0).await;

    let local = a
        .send(C::Chat(v1::Chat { channel: v1::ChatChannel::Local as i32, text: "psst".into() }))
        .await;
    let world_seq = a
        .send(C::Chat(v1::Chat {
            channel: v1::ChatChannel::World as i32,
            text: "hello all".into(),
        }))
        .await;

    // Local chat reaches everyone within 20 m, the speaker included (its copy
    // answers the seq); world chat reaches everyone.
    let (reply_to, entry) = next_chat(&mut a).await;
    assert_eq!((reply_to, entry.text.as_str()), (local, "psst"));
    let (reply_to, entry) = next_chat(&mut a).await;
    assert_eq!((reply_to, entry.text.as_str()), (world_seq, "hello all"));

    let (reply_to, entry) = next_chat(&mut near).await;
    assert_eq!((reply_to, entry.text.as_str()), (0, "psst"));
    assert_eq!(entry.sender_name, "speaker");
    assert_eq!(entry.sender_avatar_id, a.avatar_id());
    assert_eq!(entry.sender_user_id, a.user_id());
    assert_eq!(entry.channel, v1::ChatChannel::Local as i32);
    let (reply_to, entry) = next_chat(&mut near).await;
    assert_eq!((reply_to, entry.text.as_str()), (0, "hello all"));
    assert_eq!(entry.channel, v1::ChatChannel::World as i32);

    // The far avatar's first chat line is the world one: it never heard "psst".
    let (reply_to, entry) = next_chat(&mut far).await;
    assert_eq!((reply_to, entry.text.as_str()), (0, "hello all"));

    // A latecomer gets the world line (not the local one) in its history.
    let mut late = join(&server, "late").await;
    let texts: Vec<_> = late.history.iter().map(|e| e.text.as_str()).collect();
    assert_eq!(texts, vec!["hello all"]);

    // ChatHistoryRequest answers with the same.
    let seq = late.send(C::ChatHistoryRequest(v1::ChatHistoryRequest::default())).await;
    let msg = late.recv_where("ChatHistory", |m| matches!(m.body, Some(S::ChatHistory(_)))).await;
    assert_eq!(msg.reply_to, seq);
    let Some(S::ChatHistory(h)) = msg.body else { unreachable!() };
    assert_eq!(h.entries.len(), 1);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn observe_and_set_appearance() {
    // Fast limits: the subject jumps 5 m in one report.
    let world = TestWorld::fast();
    let server = world.start().await;
    let mut a = join(&server, "observer").await;
    let mut b = join(&server, "subject").await;
    move_and_wait(&mut b, &mut a, 5.0, 0.0).await;

    let seq = a.send(C::Observe(v1::Observe { radius: 0.0 })).await;
    let msg = a.recv_where("Observation", |m| matches!(m.body, Some(S::Observation(_)))).await;
    assert_eq!(msg.reply_to, seq);
    let Some(S::Observation(o)) = msg.body else { unreachable!() };
    assert_eq!(o.self_.unwrap().id, a.avatar_id());
    let seen = o.avatars.iter().find(|s| s.avatar.as_ref().unwrap().id == b.avatar_id()).unwrap();
    assert!((seen.distance - 5.0).abs() < 1e-3, "{seen:?}");
    assert!(!o.summary.is_empty());

    // AvatarUpdated goes to everyone; the changer's own copy answers its seq.
    let red = v1::Color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 };
    let seq = b
        .send(C::SetAppearance(v1::SetAppearance {
            appearance: Some(v1::Appearance { body_color: Some(red), ..Default::default() }),
        }))
        .await;
    for (client, want) in [(&mut a, 0), (&mut b, seq)] {
        let msg = client
            .recv_where("AvatarUpdated", |m| matches!(m.body, Some(S::AvatarUpdated(_))))
            .await;
        assert_eq!(msg.reply_to, want);
        let Some(S::AvatarUpdated(u)) = msg.body else { unreachable!() };
        let info = u.avatar.unwrap();
        assert_eq!(info.name, "subject");
        assert_eq!(info.appearance.unwrap().body_color, Some(red));
    }
    server.shutdown().await.unwrap();
}
#[tokio::test]
async fn speeding_is_corrected() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut a = join(&server, "racer").await;
    a.send(state_at(100.0, 0.0, 0.0)).await;
    let msg =
        a.recv_where("AvatarCorrection", |m| matches!(m.body, Some(S::AvatarCorrection(_)))).await;
    let Some(S::AvatarCorrection(c)) = msg.body else { unreachable!() };
    assert!(c.transform.unwrap().position.unwrap().x < 100.0);
    server.shutdown().await.unwrap();
}
#[tokio::test]
async fn unset_appearance_colours_reach_others_as_all_zero() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut watcher = join_with(&server, Encoding::Binary, hello("watcher")).await;

    // Hand-written JSON that sets only the body colour.
    let mut client = Client::connect(&server, Encoding::Json).await;
    client
        .send_text(
            r#"{"seq":1,"hello":{"protocolVersion":1,"name":"painter",
                "appearance":{"bodyColor":{"r":0.25,"g":0.5,"b":0.75,"a":1}}}}"#,
        )
        .await;
    let welcome = client.recv().await;
    let Some(S::Welcome(w)) = welcome.body else { panic!("{welcome:?}") };
    let painter = w.avatar_id;

    let body = v1::Color { r: 0.25, g: 0.5, b: 0.75, a: 1.0 };
    let zero = Some(v1::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 });
    let check = |a: &v1::Appearance| {
        assert_eq!(a.body_color, Some(body));
        assert_eq!(
            [a.skin_color, a.hair_color, a.top_color, a.bottom_color],
            [zero, zero, zero, zero],
            "{a:?}"
        );
    };
    check(w.avatar.as_ref().unwrap().appearance.as_ref().unwrap());

    let msg =
        watcher.recv_where("AvatarJoined", |m| matches!(m.body, Some(S::AvatarJoined(_)))).await;
    let Some(S::AvatarJoined(j)) = msg.body else { unreachable!() };
    let info = j.avatar.unwrap();
    assert_eq!(info.id, painter);
    check(info.appearance.as_ref().unwrap());

    // A latecomer's snapshot says the same.
    let late = join(&server, "late").await;
    let seen = late.snapshot_avatars().into_iter().find(|a| a.id == painter).unwrap();
    check(seen.appearance.as_ref().unwrap());
    server.shutdown().await.unwrap();
}
