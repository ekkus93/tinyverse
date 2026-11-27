//! Moderation over the protocol: kick, ban, unban, set role.

mod common;

use common::*;
use tv_proto::Encoding;
use tv_proto::v1::{self, ErrorCode, admin, client_message::Body as C, server_message::Body as S};
use tv_world::{Role, UserId};

fn admin_body(action: admin::Action) -> C {
    C::Admin(v1::Admin { action: Some(action) })
}

async fn owner(world: &TestWorld, server: &tv_server::ServerHandle) -> Session {
    join_with(server, Encoding::Json, with_token("host", &world.owner_token())).await
}

async fn ack(s: &mut Session, seq: u32) {
    let msg = s
        .recv_where("Ack or Error", |m| {
            m.reply_to == seq && matches!(m.body, Some(S::Ack(_)) | Some(S::Error(_)))
        })
        .await;
    assert!(matches!(msg.body, Some(S::Ack(_))), "expected Ack, got {msg:?}");
}

#[tokio::test]
async fn kick() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut host = owner(&world, &server).await;
    let mut guest = join(&server, "guest").await;
    let mut other = join(&server, "other").await;

    let seq = host
        .send(admin_body(admin::Action::Kick(admin::Kick {
            user_id: guest.user_id().into(),
            reason: "time out".into(),
        })))
        .await;
    ack(&mut host, seq).await;
    let before_close = guest.expect_closed().await;
    let kicked = before_close.iter().find_map(|m| match &m.body {
        Some(S::Kicked(k)) => Some(k.reason.clone()),
        _ => None,
    });
    assert_eq!(kicked.as_deref(), Some("time out"));
    let msg = other.recv_where("AvatarLeft", |m| matches!(m.body, Some(S::AvatarLeft(_)))).await;
    let Some(S::AvatarLeft(l)) = msg.body else { unreachable!() };
    assert_eq!(l.avatar_id, guest.avatar_id());

    // A kicked user may come back.
    let token = guest.welcome.session_token.clone();
    let back = join_with(&server, Encoding::Json, with_token("", &token)).await;
    assert_eq!(back.user_id(), guest.user_id());
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn ban_and_unban() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut host = owner(&world, &server).await;
    let mut guest = join(&server, "troll").await;
    let token = guest.welcome.session_token.clone();
    let user = guest.user_id().to_owned();

    let seq = host
        .send(admin_body(admin::Action::Ban(admin::Ban {
            user_id: user.clone(),
            reason: "spam".into(),
        })))
        .await;
    ack(&mut host, seq).await;
    let rest = guest.expect_closed().await;
    assert!(rest.iter().any(|m| matches!(m.body, Some(S::Kicked(_)))), "{rest:?}");

    let e = refused(&server, Encoding::Json, with_token("", &token)).await;
    assert_eq!(code(&e), ErrorCode::Banned);
    assert!(world.store().user(&UserId(user.clone())).unwrap().unwrap().banned.is_some());

    let seq =
        host.send(admin_body(admin::Action::Unban(admin::Unban { user_id: user.clone() }))).await;
    ack(&mut host, seq).await;
    let back = join_with(&server, Encoding::Json, with_token("", &token)).await;
    assert_eq!(back.user_id(), user);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn set_role_applies_now_and_later() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut host = owner(&world, &server).await;
    let mut guest = join(&server, "guest").await;
    let token = guest.welcome.session_token.clone();
    let user = guest.user_id().to_owned();

    let seq = host
        .send(admin_body(admin::Action::SetRole(admin::SetRole {
            user_id: user.clone(),
            role: v1::Role::Visitor as i32,
        })))
        .await;
    ack(&mut host, seq).await;
    let msg =
        guest.recv_where("AvatarUpdated", |m| matches!(m.body, Some(S::AvatarUpdated(_)))).await;
    let Some(S::AvatarUpdated(u)) = msg.body else { unreachable!() };
    assert_eq!(u.avatar.unwrap().role, v1::Role::Visitor as i32);

    let seq =
        guest.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(box_at(0.0, 0.0, 0.0)) })).await;
    let (reply_to, e) = guest.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::PermissionDenied));

    // Remembered for the next visit.
    guest.client.close().await;
    let back = join_with(&server, Encoding::Json, with_token("", &token)).await;
    assert_eq!(back.welcome.role, v1::Role::Visitor as i32);
    assert_eq!(world.store().user(&UserId(user)).unwrap().unwrap().role, Role::Visitor);

    // UNSPECIFIED role is refused by the server before the world sees it.
    let seq = host
        .send(admin_body(admin::Action::SetRole(admin::SetRole {
            user_id: back.user_id().into(),
            role: 0,
        })))
        .await;
    let (reply_to, e) = host.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::InvalidArgument));
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn only_owners_moderate() {
    let world = TestWorld::new();
    let server = world.start().await;
    let host = owner(&world, &server).await;
    let mut guest = join(&server, "guest").await;
    let seq = guest
        .send(admin_body(admin::Action::Kick(admin::Kick {
            user_id: host.user_id().into(),
            reason: String::new(),
        })))
        .await;
    let (reply_to, e) = guest.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::PermissionDenied));
    let seq = guest.send(C::Admin(v1::Admin { action: None })).await;
    let (reply_to, e) = guest.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::InvalidArgument));
    server.shutdown().await.unwrap();
}
