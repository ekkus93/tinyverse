//! Keepalive, malformed input, size and rate limits, timeouts.

mod common;

use std::time::Duration;

use common::*;
use tv_proto::v1::{self, ErrorCode, client_message::Body as C, server_message::Body as S};
use tv_proto::{Encoding, Frame};
use tv_server::Tuning;
use tv_server::session::SessionTiming;

#[tokio::test]
async fn ping_pong_in_both_encodings() {
    let world = TestWorld::new();
    let server = world.start().await;
    for encoding in [Encoding::Json, Encoding::Binary] {
        let mut s = join_with(&server, encoding, hello("pinger")).await;
        let seq = s.send(C::Ping(v1::Ping { client_time_ms: 1234 })).await;
        let msg = s.recv_where("Pong", |m| matches!(m.body, Some(S::Pong(_)))).await;
        assert_eq!(msg.reply_to, seq);
        let Some(S::Pong(p)) = msg.body else { unreachable!() };
        assert_eq!(p.client_time_ms, 1234);
        assert!(p.server_time_ms > 1_700_000_000_000);
    }
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn malformed_json_gets_a_helpful_error_and_the_session_survives() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut s = join(&server, "typist").await;

    s.send_text(r#"{"seq":7,"chat":{"channel":"LOUD","text":"hi"}}"#).await;
    let (reply_to, e) = s.error().await;
    assert_eq!(reply_to, 7, "the seq is recovered from broken JSON");
    assert_eq!(code(&e), ErrorCode::BadRequest);
    assert!(e.message.contains("invalid JSON"), "{}", e.message);
    assert!(e.message.contains("LOUD") || e.message.contains("channel"), "{}", e.message);

    s.send_text(r#"{"seq":8,"chatt":{"text":"hi"}}"#).await;
    let (_, e) = s.error().await;
    assert!(e.message.contains("chatt"), "names the unknown field: {}", e.message);

    s.send_text(r#"{"seq":9}"#).await;
    let (reply_to, e) = s.error().await;
    assert_eq!((reply_to, code(&e)), (9, ErrorCode::BadRequest));
    assert!(e.message.contains("no body"));

    // A second Hello is a bad request too, but not fatal.
    let seq = s.send(C::Hello(hello("again"))).await;
    let (reply_to, e) = s.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::BadRequest));

    // Still alive.
    let seq = s.send(C::Ping(v1::Ping { client_time_ms: 1 })).await;
    let msg = s.recv_where("Pong", |m| matches!(m.body, Some(S::Pong(_)))).await;
    assert_eq!(msg.reply_to, seq);

    // Hand-written JSON in snake_case with numbers for enums works.
    s.send_text(r#"{"seq":50,"chat":{"channel":2,"text":"typed by hand"}}"#).await;
    s.recv_where("chat", |m| match &m.body {
        Some(S::ChatMessage(c)) => c.entry.as_ref().unwrap().text == "typed by hand",
        _ => false,
    })
    .await;
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn five_bad_frames_in_a_row_close_the_connection() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut s = join(&server, "noisy").await;
    for _ in 0..5 {
        s.send_text("this is not JSON").await;
    }
    let before = s.expect_closed().await;
    let errors = before.iter().filter(|m| matches!(m.body, Some(S::Error(_)))).count();
    assert_eq!(errors, 5);

    // Binary garbage counts the same way.
    let mut s = join_with(&server, Encoding::Binary, hello("noisy2")).await;
    for _ in 0..5 {
        s.send_frame(Frame::Binary(vec![0xff, 0xff, 0xff, 0xff])).await;
    }
    s.expect_closed().await;
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn oversized_frames_are_refused() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut s = join(&server, "big").await;
    let text = format!(
        r#"{{"seq":1,"chat":{{"channel":"CHAT_CHANNEL_WORLD","text":"{}"}}}}"#,
        "x".repeat(70_000)
    );
    s.send_text(&text).await;
    let before = s.expect_closed().await;
    let error = before.iter().find_map(|m| match &m.body {
        Some(S::Error(e)) => Some(e.clone()),
        _ => None,
    });
    let error = error.expect("an Error before the close");
    assert_eq!(code(&error), ErrorCode::BadRequest);
    assert!(error.message.contains("65536"), "{}", error.message);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn rate_limit() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut s = join(&server, "chatty").await;
    let mut seqs = Vec::new();
    for i in 0..20 {
        seqs.push(s.send(C::Chat(v1::Chat { channel: 2, text: format!("line {i}") })).await);
    }
    let got = s.collect(Duration::from_millis(500)).await;
    let limited: Vec<u32> = got
        .iter()
        .filter_map(|m| match &m.body {
            Some(S::Error(e)) if code(e) == ErrorCode::RateLimited => Some(m.reply_to),
            _ => None,
        })
        .collect();
    assert!(limited.len() >= 8, "burst is 10, so most of the last 10 are refused: {limited:?}");
    assert!(limited.iter().all(|r| seqs.contains(r)));
    // Other message types are not affected.
    let seq = s.send(C::Ping(v1::Ping { client_time_ms: 0 })).await;
    let msg = s.recv_where("Pong", |m| matches!(m.body, Some(S::Pong(_)))).await;
    assert_eq!(msg.reply_to, seq);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn idle_connections_are_closed() {
    let world = TestWorld::new();
    let tuning = Tuning {
        session: SessionTiming {
            idle_timeout: Duration::from_millis(500),
            ..SessionTiming::default()
        },
        ..Tuning::default()
    };
    let server = world.start_with(tuning).await;
    let mut watcher = join(&server, "watcher").await;
    let mut quiet = join(&server, "quiet").await;
    // The watcher keeps itself alive with pings; the quiet one says nothing.
    let keepalive = async {
        for _ in 0..6 {
            watcher.send(C::Ping(v1::Ping { client_time_ms: 0 })).await;
            tokio::time::sleep(Duration::from_millis(200)).await;
        }
    };
    let (_, closed) = tokio::join!(keepalive, quiet.expect_closed());
    drop(closed);
    let msg = watcher.recv_where("AvatarLeft", |m| matches!(m.body, Some(S::AvatarLeft(_)))).await;
    let Some(S::AvatarLeft(l)) = msg.body else { unreachable!() };
    assert_eq!(l.avatar_id, quiet.avatar_id());
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn shutdown_closes_sessions() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut s = join(&server, "last one").await;
    server.shutdown().await.unwrap();
    s.expect_closed().await;
}
