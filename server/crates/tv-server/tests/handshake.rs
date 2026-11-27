//! Hello, Welcome, the join sequence and every way a Hello is refused.

mod common;

use std::time::Duration;

use common::*;
use tv_proto::Encoding;
use tv_proto::v1::{self, ErrorCode, client_message::Body as C, server_message::Body as S};
use tv_server::session::SessionTiming;
use tv_server::{Info, Tuning};
use tv_world::{ClientKind, Role, UserId};

#[tokio::test]
async fn handshake_in_both_encodings() {
    let world = TestWorld::new();
    let server = world.start().await;
    for encoding in [Encoding::Json, Encoding::Binary] {
        let s = join_with(&server, encoding, hello("Ada")).await;
        let w = &s.welcome;
        assert_eq!(w.protocol_version, tv_proto::PROTOCOL_VERSION);
        assert_eq!(w.tick_rate_hz, 20);
        assert_eq!(w.role, v1::Role::Builder as i32, "default_role is builder");
        assert!(!w.session_token.is_empty(), "a new user gets a session token");
        assert!(!w.user_id.is_empty());
        assert_ne!(w.avatar_id, 0);
        assert_eq!(w.server_version, env!("CARGO_PKG_VERSION"));
        let info = w.world.as_ref().unwrap();
        assert_eq!(info.name, "Test World");
        let limits = w.limits.as_ref().unwrap();
        assert_eq!(limits.max_message_bytes, 65536);
        assert_eq!(limits.walk_speed, 4.0);
        let avatar = w.avatar.as_ref().unwrap();
        assert_eq!(avatar.id, w.avatar_id);
        assert_eq!(avatar.name, "Ada");
        assert_eq!(avatar.kind, v1::ClientKind::Human as i32);
        assert!(s.snapshot.last().unwrap().last);
        assert!(s.snapshot_avatars().iter().all(|a| a.id != w.avatar_id), "self not in snapshot");
        s.client.close().await;
    }
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn empty_world_still_sends_one_snapshot() {
    let world = TestWorld::new();
    let server = world.start().await;
    let s = join(&server, "Solo").await;
    assert_eq!(s.snapshot.len(), 1);
    assert!(s.snapshot[0].last);
    assert!(s.snapshot[0].avatars.is_empty() && s.snapshot[0].objects.is_empty());
    assert!(s.history.is_empty());
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn session_token_logs_in_as_the_same_user() {
    let world = TestWorld::new();
    let server = world.start().await;
    let first = join(&server, "Ada").await;
    let token = first.welcome.session_token.clone();
    let user = first.user_id().to_owned();
    first.client.close().await;

    let again = join_with(&server, Encoding::Json, with_token("", &token)).await;
    assert_eq!(again.user_id(), user);
    assert!(again.welcome.session_token.is_empty(), "token logins get no new token");
    assert_eq!(again.welcome.avatar.as_ref().unwrap().name, "Ada", "stored name when empty");
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn wrong_protocol_version() {
    let world = TestWorld::new();
    let server = world.start().await;
    let e =
        refused(&server, Encoding::Json, v1::Hello { protocol_version: 99, ..hello("x") }).await;
    assert_eq!(code(&e), ErrorCode::UnsupportedVersion);
    // The version is checked before the credential.
    let e = refused(
        &server,
        Encoding::Binary,
        v1::Hello { protocol_version: 0, ..with_token("x", "bogus") },
    )
    .await;
    assert_eq!(code(&e), ErrorCode::UnsupportedVersion);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn password_is_required_without_a_token() {
    let world = TestWorld::with(|f| {
        f.password_hash = Some(tv_server::auth::hash_password("sesame").unwrap());
    });
    let server = world.start().await;

    let e = refused(&server, Encoding::Json, hello("x")).await;
    assert_eq!(code(&e), ErrorCode::AuthFailed);
    let e =
        refused(&server, Encoding::Json, v1::Hello { password: "nope".into(), ..hello("x") }).await;
    assert_eq!(code(&e), ErrorCode::AuthFailed);

    let s =
        join_with(&server, Encoding::Json, v1::Hello { password: "sesame".into(), ..hello("x") })
            .await;
    s.client.close().await;

    // A valid token needs no password.
    let s = join_with(&server, Encoding::Json, with_token("Owner", &world.owner_token())).await;
    assert_eq!(s.welcome.role, v1::Role::Owner as i32);

    let (status, body) = http_get(&server, "/info").await;
    assert_eq!(status, 200);
    let info: Info = serde_json::from_str(&body).unwrap();
    assert!(info.password_required);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn unknown_token() {
    let world = TestWorld::new();
    let server = world.start().await;
    let e = refused(&server, Encoding::Json, with_token("x", "not-a-token")).await;
    assert_eq!(code(&e), ErrorCode::InvalidToken);

    // A revoked token is unknown too.
    let (id, token) = world.create_token("bot", ClientKind::Agent, Role::Builder);
    assert!(world.store().revoke_token(&id).unwrap());
    let e = refused(&server, Encoding::Json, with_token("x", &token)).await;
    assert_eq!(code(&e), ErrorCode::InvalidToken);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn banned_user() {
    let world = TestWorld::new();
    let (id, token) = world.create_token("troll", ClientKind::Human, Role::Builder);
    assert!(world.store().ban(&id, "spam").unwrap());
    let server = world.start().await;
    let e = refused(&server, Encoding::Json, with_token("", &token)).await;
    assert_eq!(code(&e), ErrorCode::Banned);
    assert!(e.message.contains("spam"), "{}", e.message);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn full_world() {
    let world = TestWorld::with(|f| f.max_users = 1);
    let (_, token) = world.create_token("bot", ClientKind::Agent, Role::Builder);
    let server = world.start().await;
    let first = join(&server, "first").await;
    let e = refused(&server, Encoding::Json, hello("second")).await;
    assert_eq!(code(&e), ErrorCode::WorldFull);
    let e = refused(&server, Encoding::Json, with_token("bot", &token)).await;
    assert_eq!(code(&e), ErrorCode::WorldFull);

    // The same user may come back (the older connection is replaced).
    let token = first.welcome.session_token.clone();
    let mut first = first;
    let again = join_with(&server, Encoding::Json, with_token("", &token)).await;
    assert_eq!(again.user_id(), first.user_id());
    let rest = first.expect_closed().await;
    assert!(rest.iter().any(|m| matches!(m.body, Some(S::Kicked(_)))), "{rest:?}");
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn agent_token_login() {
    let world = TestWorld::new();
    let (id, token) = world.create_token("scout", ClientKind::Agent, Role::Visitor);
    let server = world.start().await;
    let mut watcher = join(&server, "watcher").await;

    // Kind and role come from the token, not from Hello.
    let hello = v1::Hello { kind: v1::ClientKind::Human as i32, ..with_token("", &token) };
    let agent = join_with(&server, Encoding::Json, hello).await;
    assert_eq!(agent.user_id(), id.as_str());
    assert!(agent.welcome.session_token.is_empty());
    let me = agent.welcome.avatar.clone().unwrap();
    assert_eq!(me.kind, v1::ClientKind::Agent as i32);
    assert_eq!(me.role, v1::Role::Visitor as i32);
    assert_eq!(me.name, "scout");
    assert_eq!(agent.snapshot_avatars().len(), 1);

    let joined =
        watcher.recv_where("AvatarJoined", |m| matches!(m.body, Some(S::AvatarJoined(_)))).await;
    let Some(S::AvatarJoined(j)) = joined.body else { unreachable!() };
    assert_eq!(j.avatar.unwrap().kind, v1::ClientKind::Agent as i32);
    assert_eq!(joined.reply_to, 0);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn new_users_get_the_default_role_and_their_kind() {
    let world = TestWorld::with(|f| f.default_role = tv_server::config::RoleName::Visitor);
    let server = world.start().await;
    let hello = v1::Hello { kind: v1::ClientKind::Agent as i32, ..hello("bot") };
    let s = join_with(&server, Encoding::Json, hello).await;
    assert_eq!(s.welcome.role, v1::Role::Visitor as i32);
    assert_eq!(s.welcome.avatar.as_ref().unwrap().kind, v1::ClientKind::Agent as i32);
    let user = world.store().user(&UserId(s.user_id().to_owned())).unwrap().unwrap();
    assert_eq!(user.kind, ClientKind::Agent);
    assert_eq!(user.role, Role::Visitor);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn first_message_must_be_hello() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut c = Client::connect(&server, Encoding::Json).await;
    c.send(C::Ping(v1::Ping { client_time_ms: 1 })).await;
    let msg = c.recv().await;
    let Some(S::Error(e)) = msg.body else { panic!("{msg:?}") };
    assert_eq!(code(&e), ErrorCode::BadRequest);
    assert!(e.message.contains("Hello"));
    c.expect_closed().await;

    let mut c = Client::connect(&server, Encoding::Json).await;
    c.send_text("{\"hello\": ").await;
    let msg = c.recv().await;
    let Some(S::Error(e)) = msg.body else { panic!("{msg:?}") };
    assert_eq!(code(&e), ErrorCode::BadRequest);
    c.expect_closed().await;
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn hello_timeout() {
    let world = TestWorld::new();
    let tuning = Tuning {
        session: SessionTiming {
            hello_timeout: Duration::from_millis(300),
            ..SessionTiming::default()
        },
        ..Tuning::default()
    };
    let server = world.start_with(tuning).await;
    let mut c = Client::connect(&server, Encoding::Json).await;
    let rest = c.expect_closed().await;
    assert_eq!(rest.len(), 1);
    assert!(matches!(rest[0].body, Some(S::Error(_))));
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn info_and_health() {
    let world = TestWorld::with(|f| {
        f.description = "for tests".into();
        f.max_users = 5;
    });
    let server = world.start().await;
    let (status, body) = http_get(&server, "/health").await;
    assert_eq!((status, body.as_str()), (200, "ok"));

    let _s = join(&server, "Ada").await;
    let (status, body) = http_get(&server, "/info").await;
    assert_eq!(status, 200);
    let info: Info = serde_json::from_str(&body).unwrap();
    assert_eq!(
        info,
        Info {
            name: "Test World".into(),
            description: "for tests".into(),
            users: 1,
            max_users: 5,
            protocol_version: tv_proto::PROTOCOL_VERSION,
            server_version: env!("CARGO_PKG_VERSION").into(),
            password_required: false,
        }
    );
    server.shutdown().await.unwrap();
}
