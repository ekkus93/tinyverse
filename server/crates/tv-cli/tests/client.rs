//! The library `Client` against a scripted mock server.

mod common;

use std::time::Duration;

use common::{MockServer, avatar, v, welcome};
use tv_cli::{Client, ClientError, ClientOptions};
use tv_proto::{
    Encoding, Frame, PROTOCOL_VERSION,
    v1::{
        AvatarJoined, ChatChannel, ChatEntry, ChatHistory, ChatMessage, ClientKind, Error,
        ErrorCode, ObjectDelete, ObjectInfo, Observation, Observe, Pong, Snapshot,
        client_message::Body as C, server_message::Body as S,
    },
};

fn opts(url: &str, encoding: Encoding) -> ClientOptions {
    let mut o = ClientOptions::new(url);
    o.name = "ada".into();
    o.password = "pw".into();
    o.token = "tok".into();
    o.kind = ClientKind::Human;
    o.encoding = encoding;
    o
}

fn entry(text: &str) -> ChatEntry {
    ChatEntry {
        sender_avatar_id: 5,
        sender_name: "bob".into(),
        channel: ChatChannel::World as i32,
        text: text.into(),
        ..Default::default()
    }
}

fn object(id: u64) -> ObjectInfo {
    ObjectInfo { id, owner_id: "u".into(), spec: None }
}

async fn join_with_chunks(encoding: Encoding) {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        let frame = c.next_frame().await.unwrap();
        match (&frame, encoding) {
            (Frame::Binary(_), Encoding::Binary) => {}
            (Frame::Text(json), Encoding::Json) => {
                let v: serde_json::Value = serde_json::from_str(json).unwrap();
                assert_eq!(v["seq"], 1);
                assert_eq!(v["hello"]["protocolVersion"], 1);
                assert_eq!(v["hello"]["kind"], "CLIENT_KIND_HUMAN");
                assert_eq!(v["hello"]["name"], "ada");
            }
            other => panic!("wrong frame type for {encoding:?}: {other:?}"),
        }
        c.encoding = frame.encoding();
        let msg: tv_proto::v1::ClientMessage = tv_proto::decode(&frame).unwrap();
        assert_eq!(msg.seq, 1);
        let Some(C::Hello(h)) = msg.body else { panic!("expected Hello, got {msg:?}") };
        assert_eq!(h.protocol_version, PROTOCOL_VERSION);
        assert_eq!(h.kind, ClientKind::Human as i32);
        assert_eq!((h.name.as_str(), h.password.as_str(), h.token.as_str()), ("ada", "pw", "tok"));
        assert!(h.client_name.starts_with("tv-cli/"), "{}", h.client_name);

        c.send(1, S::Welcome(welcome(3, v(0.0, 0.0, 0.0)))).await;
        let s1 = Snapshot {
            avatars: vec![avatar(5, "bob", v(1.0, 0.0, 0.0)), avatar(6, "cy", v(2.0, 0.0, 0.0))],
            objects: vec![object(10)],
            last: false,
        };
        c.send(0, S::Snapshot(s1)).await;
        // An event during the snapshot must be kept for later.
        c.send(
            0,
            S::AvatarJoined(AvatarJoined { avatar: Some(avatar(7, "dee", v(0.0, 0.0, 0.0))) }),
        )
        .await;
        let s2 = Snapshot {
            avatars: vec![avatar(8, "eve", v(3.0, 0.0, 0.0))],
            objects: vec![object(11)],
            last: true,
        };
        c.send(0, S::Snapshot(s2)).await;
        c.send(0, S::ChatHistory(ChatHistory { entries: vec![entry("earlier")] })).await;
        c
    });

    let mut client = Client::connect(opts(&url, encoding)).await.unwrap();
    let mut conn = script.await.unwrap();
    assert_eq!(client.avatar_id(), 3);
    assert_eq!(client.user_id(), "user-3");
    let snap = client.snapshot();
    assert_eq!(snap.avatars.iter().map(|a| a.id).collect::<Vec<_>>(), [5, 6, 8]);
    assert_eq!(snap.objects.iter().map(|o| o.id).collect::<Vec<_>>(), [10, 11]);
    assert_eq!(snap.chunks, 2);
    assert!(snap.complete);
    assert_eq!(client.history().len(), 1);
    assert_eq!(client.history()[0].text, "earlier");
    let m = client.recv().await.unwrap();
    assert!(matches!(m.body, Some(S::AvatarJoined(_))), "{m:?}");

    client.close().await;
    conn.expect_close().await;
}

#[tokio::test]
async fn binary_hello_and_snapshot_chunks() {
    join_with_chunks(Encoding::Binary).await;
}

#[tokio::test]
async fn json_hello_and_snapshot_chunks() {
    join_with_chunks(Encoding::Json).await;
}

#[tokio::test]
async fn refusal_carries_code_and_message() {
    for encoding in [Encoding::Binary, Encoding::Json] {
        let mut server = MockServer::start().await;
        let url = server.url.clone();
        let script = tokio::spawn(async move {
            let mut c = server.accept().await;
            let (_, seq) = c.hello().await;
            let e = Error { code: ErrorCode::AuthFailed as i32, message: "wrong password".into() };
            c.send(seq, S::Error(e)).await;
            c.close().await;
        });
        let err = Client::connect(opts(&url, encoding)).await.err().expect("should be refused");
        script.await.unwrap();
        assert!(matches!(err, ClientError::Refused { .. }), "{err:?}");
        assert_eq!(err.code(), Some(ErrorCode::AuthFailed));
        let text = err.to_string();
        assert!(text.contains("AUTH_FAILED") && text.contains("wrong password"), "{text}");
    }
}

#[tokio::test]
async fn closing_without_welcome_is_an_error() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.hello().await;
        c.close().await;
    });
    let err = Client::connect(opts(&url, Encoding::Binary)).await.err().unwrap();
    script.await.unwrap();
    assert!(matches!(err, ClientError::Closed(_)), "{err:?}");
}

#[tokio::test]
async fn connect_to_nothing_fails() {
    let url = common::dead_url().await;
    let err = Client::connect(opts(&url, Encoding::Binary)).await.err().unwrap();
    assert!(matches!(err, ClientError::Connect { .. }), "{err:?}");
}

#[tokio::test]
async fn request_matches_reply_to_and_queues_events() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let m = c.next().await;
        let Some(C::Observe(o)) = m.body else { panic!("expected Observe, got {m:?}") };
        assert_eq!(o.radius, 7.0);
        // Unrelated traffic first, including a reply to some other seq.
        c.send(0, S::AvatarJoined(AvatarJoined { avatar: Some(avatar(9, "x", v(0.0, 0.0, 0.0))) }))
            .await;
        c.send(0, S::ChatMessage(ChatMessage { entry: Some(entry("between")) })).await;
        c.send(m.seq + 50, S::Pong(Pong::default())).await;
        let obs = Observation { summary: "quiet here".into(), radius: 7.0, ..Default::default() };
        c.send(m.seq, S::Observation(obs)).await;

        let m = c.next().await;
        assert_eq!(m.body, Some(C::ObjectDelete(ObjectDelete { id: 99 })));
        let e = Error { code: ErrorCode::NotFound as i32, message: "no object 99".into() };
        c.send(m.seq, S::Error(e)).await;
        c
    });

    let mut client = Client::connect(opts(&url, Encoding::Json)).await.unwrap();
    let r = client.request(C::Observe(Observe { radius: 7.0 }), Duration::from_secs(5)).await;
    let r = r.unwrap();
    let Some(S::Observation(o)) = r.body else { panic!("{r:?}") };
    assert_eq!(o.summary, "quiet here");
    // The events that arrived first are delivered in order afterwards.
    assert!(matches!(client.recv().await.unwrap().body, Some(S::AvatarJoined(_))));
    assert!(matches!(client.recv().await.unwrap().body, Some(S::ChatMessage(_))));
    assert!(matches!(client.recv().await.unwrap().body, Some(S::Pong(_))));

    let err = client
        .request(C::ObjectDelete(ObjectDelete { id: 99 }), Duration::from_secs(5))
        .await
        .unwrap_err();
    assert_eq!(err.code(), Some(ErrorCode::NotFound));
    assert!(err.to_string().contains("no object 99"));
    let mut conn = script.await.unwrap();
    client.close().await;
    conn.expect_close().await;
}

#[tokio::test]
async fn request_times_out_without_matching_reply() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let m = c.next().await;
        // Answer without reply_to: not a reply.
        c.send(0, S::Observation(Observation::default())).await;
        let _ = m;
        c
    });
    let mut client = Client::connect(opts(&url, Encoding::Binary)).await.unwrap();
    let err = client
        .request(C::Observe(Observe { radius: 1.0 }), Duration::from_millis(300))
        .await
        .unwrap_err();
    assert!(matches!(err, ClientError::Timeout(_)), "{err:?}");
    // The unmatched message is still there.
    assert!(matches!(client.recv().await.unwrap().body, Some(S::Observation(_))));
    drop(script.await.unwrap());
}

#[tokio::test]
async fn keepalive_pings_are_sent_and_their_pongs_hidden() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        for _ in 0..2 {
            let m = c.next_any().await;
            let Some(C::Ping(p)) = m.body else { panic!("expected Ping, got {m:?}") };
            assert!(p.client_time_ms > 0);
            c.send(m.seq, S::Pong(Pong { client_time_ms: p.client_time_ms, server_time_ms: 1 }))
                .await;
        }
        c.send(0, S::ChatMessage(ChatMessage { entry: Some(entry("after pings")) })).await;
        c
    });
    let mut o = opts(&url, Encoding::Binary);
    o.ping_interval = Some(Duration::from_millis(100));
    let mut client = Client::connect(o).await.unwrap();
    let m = client.recv().await.unwrap();
    let Some(S::ChatMessage(c)) = m.body else { panic!("pong leaked through: {m:?}") };
    assert_eq!(c.entry.unwrap().text, "after pings");
    drop(script.await.unwrap());
}

#[tokio::test]
async fn bad_frames_can_be_sent_on_purpose() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        assert_eq!(c.next_frame().await, Some(Frame::Text("{oops".into())));
        c
    });
    let mut client = Client::connect(opts(&url, Encoding::Json)).await.unwrap();
    client.send_frame(Frame::Text("{oops".into())).unwrap();
    drop(script.await.unwrap());
}
