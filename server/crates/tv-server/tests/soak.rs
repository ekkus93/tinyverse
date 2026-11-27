//! Eight clients in mixed encodings walk, chat and build against one server
//! for a few seconds. Nobody may be dropped, refused or corrected, and every
//! client must end up with exactly the objects that reach `world.db`.

mod common;

use std::collections::BTreeMap;
use std::time::Duration;

use common::*;
use tv_proto::Encoding;
use tv_proto::v1::{self, client_message::Body as C, server_message::Body as S};

const CLIENTS: usize = 8;
const RUN: Duration = Duration::from_secs(3);

#[derive(Default)]
struct Seen {
    objects: BTreeMap<u64, v1::ObjectInfo>,
    /// Ids of objects this client created, in order.
    mine: Vec<u64>,
    chats: usize,
    sent_chats: usize,
    problems: Vec<String>,
}

impl Seen {
    fn absorb(&mut self, msg: &v1::ServerMessage, create_seqs: &[u32]) {
        match &msg.body {
            Some(S::ObjectCreated(c)) => {
                let o = c.object.clone().unwrap();
                if create_seqs.contains(&msg.reply_to) {
                    self.mine.push(o.id);
                }
                self.objects.insert(o.id, o);
            }
            Some(S::ObjectUpdated(u)) => {
                let o = u.object.clone().unwrap();
                self.objects.insert(o.id, o);
            }
            Some(S::ObjectDeleted(d)) => {
                self.objects.remove(&d.id);
            }
            Some(S::ChatMessage(_)) => self.chats += 1,
            Some(
                body @ (S::Error(_)
                | S::Kicked(_)
                | S::AvatarCorrection(_)
                | S::Welcome(_)
                | S::Snapshot(_)),
            ) => self.problems.push(format!("{body:?}")),
            _ => {}
        }
    }
}

/// Reads until the reply to `seq`, absorbing everything before it.
async fn fence(s: &mut Session, seen: &mut Seen, create_seqs: &[u32]) {
    let seq = s.send(C::ChatHistoryRequest(v1::ChatHistoryRequest { since_ms: 0, limit: 1 })).await;
    loop {
        match s.next(WAIT).await {
            Got::Msg(m) => {
                if m.reply_to == seq && matches!(m.body, Some(S::ChatHistory(_))) {
                    return;
                }
                seen.absorb(&m, create_seqs);
            }
            other => panic!("client lost while fencing: {other:?}"),
        }
    }
}

/// One client's few seconds: walk a circle at 15 Hz, a world chat line every
/// half second, a new box every 300 ms, recolour every second own box, delete
/// every third.
async fn activity(mut s: Session, index: usize) -> (Session, Seen) {
    let mut seen = Seen::default();
    for o in s.snapshot_objects() {
        seen.objects.insert(o.id, o);
    }
    // Where this client builds: a row of its own near the spawn point.
    let row = index as f32 * 2.0 - 8.0;
    let phase = index as f32 * std::f32::consts::TAU / CLIENTS as f32;
    let mut create_seqs = Vec::new();
    let start = tokio::time::Instant::now();
    let mut walk = tokio::time::interval(Duration::from_millis(66));
    let mut chat = tokio::time::interval(Duration::from_millis(500));
    let mut build = tokio::time::interval(Duration::from_millis(300));
    let mut step = 0u32;
    let mut builds = 0usize;
    while start.elapsed() < RUN {
        tokio::select! {
            _ = walk.tick() => {
                // A spiral out of the spawn point: every hop is under 0.2 m
                // (about 3 m/s at 15 Hz), inside the walk limit even when two
                // reports arrive together.
                step += 1;
                let radius = (step as f32 * 0.02).min(1.5);
                let angle = phase + step as f32 * 0.1;
                s.send(state_at(radius * angle.cos(), 0.0, radius * angle.sin())).await;
            }
            _ = chat.tick() => {
                let text = format!("client {index} line {}", seen.sent_chats);
                s.send(C::Chat(v1::Chat { channel: v1::ChatChannel::World as i32, text })).await;
                seen.sent_chats += 1;
            }
            _ = build.tick() => {
                builds += 1;
                let spec = box_at(row, 0.5, 3.0 + builds as f32 * 0.5);
                create_seqs.push(s.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(spec) })).await);
                if let Some(&id) = seen.mine.last() {
                    if builds.is_multiple_of(2) {
                        let color = v1::Color { r: 0.0, g: index as f32 / 8.0, b: 1.0, a: 1.0 };
                        s.send(C::ObjectUpdate(v1::ObjectUpdate { id, color: Some(color), ..Default::default() })).await;
                    }
                    if builds.is_multiple_of(3) {
                        s.send(C::ObjectDelete(v1::ObjectDelete { id })).await;
                        seen.mine.pop();
                    }
                }
            }
            got = s.next(Duration::from_secs(10)) => match got {
                Got::Msg(m) => seen.absorb(&m, &create_seqs),
                other => panic!("client {index} lost: {other:?}"),
            },
        }
    }
    // Everything this client sent has been applied once this answers.
    fence(&mut s, &mut seen, &create_seqs).await;
    (s, seen)
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn eight_clients_walk_chat_and_build() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut sessions = Vec::new();
    for i in 0..CLIENTS {
        let encoding = if i % 2 == 0 { Encoding::Binary } else { Encoding::Json };
        sessions.push(join_with(&server, encoding, hello(&format!("soak{i}"))).await);
    }
    // Each client only hears AvatarJoined for those after it; drain them in
    // the activity loop (they are not problems).

    let tasks: Vec<_> =
        sessions.into_iter().enumerate().map(|(i, s)| tokio::spawn(activity(s, i))).collect();
    let mut done = Vec::new();
    for t in tasks {
        done.push(t.await.unwrap());
    }

    // Every client has passed its own fence, so every command of every client
    // has been applied; a second fence now delivers all their events.
    for (s, seen) in &mut done {
        fence(s, seen, &[]).await;
    }

    let total_chats: usize = done.iter().map(|(_, seen)| seen.sent_chats).sum();
    assert!(total_chats >= CLIENTS * 5, "{total_chats} chat lines sent");
    let reference = done[0].1.objects.clone();
    assert!(reference.len() >= CLIENTS * 4, "{} objects left", reference.len());
    for (i, (_, seen)) in done.iter().enumerate() {
        assert!(seen.problems.is_empty(), "client {i}: {:?}", seen.problems);
        assert_eq!(seen.chats, total_chats, "client {i} heard every world chat line");
        assert_eq!(seen.objects, reference, "client {i} disagrees about the objects");
    }
    let (status, body) = http_get(&server, "/info").await;
    assert_eq!(status, 200);
    let info: tv_server::Info = serde_json::from_str(&body).unwrap();
    assert_eq!(info.users, CLIENTS, "nobody was dropped");

    server.shutdown().await.unwrap();
    for (mut s, _) in done {
        s.expect_closed().await;
    }

    let stored: BTreeMap<u64, v1::ObjectInfo> = world
        .store()
        .load_world()
        .unwrap()
        .objects
        .iter()
        .map(|o| (o.id, tv_server::convert::object_to(o)))
        .collect();
    assert_eq!(stored, reference, "world.db holds what the clients saw");
}
