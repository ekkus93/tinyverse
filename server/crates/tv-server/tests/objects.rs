//! Building: create, update, delete, permissions, and persistence across a
//! restart.

mod common;

use std::time::Duration;

use common::*;
use tv_proto::v1::{self, ErrorCode, client_message::Body as C, server_message::Body as S};
use tv_server::Tuning;
use tv_server::hub::HubTiming;
use tv_world::{ClientKind, Role};

fn created(m: &v1::ServerMessage) -> Option<&v1::ObjectInfo> {
    match &m.body {
        Some(S::ObjectCreated(c)) => c.object.as_ref(),
        _ => None,
    }
}

#[tokio::test]
async fn create_update_delete_seen_by_both_with_reply_to() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut a = join(&server, "builder").await;
    let mut b = join(&server, "watcher").await;

    let seq = a.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(box_at(1.0, 0.5, 2.0)) })).await;
    let mine = a.recv_where("ObjectCreated", |m| created(m).is_some()).await;
    assert_eq!(mine.reply_to, seq);
    let theirs = b.recv_where("ObjectCreated", |m| created(m).is_some()).await;
    assert_eq!(theirs.reply_to, 0);
    let object = created(&mine).unwrap().clone();
    assert_eq!(created(&theirs).unwrap(), &object);
    assert_eq!(object.owner_id, a.user_id());
    assert_eq!(object.spec.as_ref().unwrap().name, "box");
    let Some(S::ObjectCreated(c)) = &mine.body else { unreachable!() };
    assert_eq!(c.by_avatar_id, a.avatar_id());
    let id = object.id;

    // A partial update: only the colour changes; the rest is kept.
    let green = v1::Color { r: 0.0, g: 1.0, b: 0.0, a: 1.0 };
    let seq = b
        .send(C::ObjectUpdate(v1::ObjectUpdate { id, color: Some(green), ..Default::default() }))
        .await;
    for (client, want) in [(&mut b, seq), (&mut a, 0)] {
        let msg = client
            .recv_where("ObjectUpdated", |m| matches!(m.body, Some(S::ObjectUpdated(_))))
            .await;
        assert_eq!(msg.reply_to, want);
        let Some(S::ObjectUpdated(u)) = msg.body else { unreachable!() };
        let spec = u.object.unwrap().spec.unwrap();
        assert_eq!(spec.color, Some(green));
        assert_eq!(spec.position, Some(v(1.0, 0.5, 2.0)));
    }

    let seq = a.send(C::ObjectDelete(v1::ObjectDelete { id })).await;
    let a_avatar = a.avatar_id();
    for (client, want) in [(&mut a, seq), (&mut b, 0)] {
        let msg = client
            .recv_where("ObjectDeleted", |m| matches!(m.body, Some(S::ObjectDeleted(_))))
            .await;
        assert_eq!(msg.reply_to, want);
        let Some(S::ObjectDeleted(d)) = msg.body else { unreachable!() };
        assert_eq!((d.id, d.by_avatar_id), (id, a_avatar));
    }

    // Deleting it again: NOT_FOUND, answering the seq.
    let seq = a.send(C::ObjectDelete(v1::ObjectDelete { id })).await;
    let (reply_to, e) = a.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::NotFound));
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn bad_objects_are_refused() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut a = join(&server, "builder").await;

    let cases = [
        (v1::ObjectSpec { position: None, ..box_at(0.0, 0.0, 0.0) }, ErrorCode::InvalidArgument),
        (v1::ObjectSpec { shape: 0, ..box_at(0.0, 0.0, 0.0) }, ErrorCode::InvalidArgument),
        (box_at(10_000.0, 0.0, 0.0), ErrorCode::OutOfBounds),
        (
            v1::ObjectSpec { scale: Some(v(1000.0, 1.0, 1.0)), ..box_at(0.0, 0.0, 0.0) },
            ErrorCode::InvalidArgument,
        ),
    ];
    for (spec, want) in cases {
        let seq = a.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(spec.clone()) })).await;
        let (reply_to, e) = a.error().await;
        assert_eq!((reply_to, code(&e)), (seq, want), "{spec:?}: {}", e.message);
    }
    let seq = a.send(C::ObjectCreate(v1::ObjectCreate { spec: None })).await;
    let (reply_to, e) = a.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::InvalidArgument));
    assert!(e.message.contains("object_create.spec"), "{}", e.message);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn permissions() {
    let world = TestWorld::with(|f| f.limits.max_objects = Some(2));
    let (_, visitor_token) = world.create_token("visitor", ClientKind::Human, Role::Visitor);
    let server = world.start().await;
    let mut owner =
        join_with(&server, tv_proto::Encoding::Json, with_token("o", &world.owner_token())).await;
    let mut builder = join(&server, "builder").await;
    let mut visitor =
        join_with(&server, tv_proto::Encoding::Json, with_token("", &visitor_token)).await;

    // Visitors cannot build.
    let seq =
        visitor.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(box_at(0.0, 0.0, 0.0)) })).await;
    let (reply_to, e) = visitor.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::PermissionDenied));

    // The owner builds a locked box; a builder may not touch it, the owner may.
    let locked = v1::ObjectSpec { locked: true, ..box_at(0.0, 0.0, 0.0) };
    owner.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(locked) })).await;
    let id = created(&owner.recv_where("created", |m| created(m).is_some()).await).unwrap().id;
    let seq = builder
        .send(C::ObjectUpdate(v1::ObjectUpdate { id, solid: Some(false), ..Default::default() }))
        .await;
    let (reply_to, e) = builder.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::PermissionDenied));
    let seq = builder.send(C::ObjectDelete(v1::ObjectDelete { id })).await;
    let (reply_to, e) = builder.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::PermissionDenied));

    // A builder's unlocked box may be edited by another builder... and the
    // owner; the object limit (2) stops the third.
    builder.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(box_at(1.0, 0.0, 0.0)) })).await;
    let second =
        created(&builder.recv_where("created", |m| created(m).is_some()).await).unwrap().id;
    let seq = owner
        .send(C::ObjectUpdate(v1::ObjectUpdate {
            id: second,
            name: Some("mine now".into()),
            ..Default::default()
        }))
        .await;
    let msg = owner.recv_where("updated", |m| matches!(m.body, Some(S::ObjectUpdated(_)))).await;
    assert_eq!(msg.reply_to, seq);
    let seq =
        builder.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(box_at(2.0, 0.0, 0.0)) })).await;
    let (reply_to, e) = builder.error().await;
    assert_eq!((reply_to, code(&e)), (seq, ErrorCode::LimitExceeded));
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn restart_keeps_objects_and_never_reuses_ids() {
    let world = TestWorld::new();
    let server = world.start().await;
    let mut a = join(&server, "builder").await;
    let mut seen_ids = vec![a.avatar_id()];
    let mut kept = Vec::new();
    for i in 0..3 {
        a.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(box_at(i as f32, 0.5, 0.0)) })).await;
        let o = created(&a.recv_where("created", |m| created(m).is_some()).await).unwrap().clone();
        seen_ids.push(o.id);
        kept.push(o);
    }
    let gone = kept.remove(1);
    a.send(C::ObjectDelete(v1::ObjectDelete { id: gone.id })).await;
    a.recv_where("deleted", |m| matches!(m.body, Some(S::ObjectDeleted(_)))).await;
    let renamed = kept[0].id;
    a.send(C::ObjectUpdate(v1::ObjectUpdate {
        id: renamed,
        name: Some("renamed".into()),
        ..Default::default()
    }))
    .await;
    let msg = a.recv_where("updated", |m| matches!(m.body, Some(S::ObjectUpdated(_)))).await;
    let Some(S::ObjectUpdated(u)) = msg.body else { unreachable!() };
    kept[0] = u.object.unwrap();
    // Shutdown flushes even though the 5 s timer has not fired.
    server.shutdown().await.unwrap();

    let server = world.start().await;
    let mut b = join(&server, "builder again").await;
    assert_eq!(b.snapshot_objects(), kept);
    assert!(b.avatar_id() > *seen_ids.iter().max().unwrap(), "avatar ids are not reused");
    seen_ids.push(b.avatar_id());
    b.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(box_at(5.0, 0.5, 0.0)) })).await;
    let new = created(&b.recv_where("created", |m| created(m).is_some()).await).unwrap().id;
    assert!(new > *seen_ids.iter().max().unwrap(), "object id {new} reuses an old id");
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn periodic_flush_survives_a_crash() {
    let world = TestWorld::new();
    let tuning = Tuning {
        hub: HubTiming { flush: Duration::from_millis(200), ..HubTiming::default() },
        ..Tuning::default()
    };
    let server = world.start_with(tuning).await;
    let mut a = join(&server, "builder").await;
    a.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(box_at(0.0, 0.5, 0.0)) })).await;
    let o = created(&a.recv_where("created", |m| created(m).is_some()).await).unwrap().clone();
    tokio::time::sleep(Duration::from_millis(800)).await;

    // Read the database behind the running server's back.
    let loaded = world.store().load_world().unwrap();
    assert_eq!(loaded.objects.len(), 1);
    assert_eq!(loaded.objects[0].id, o.id);
    assert!(loaded.next_entity_id > o.id);
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn large_worlds_arrive_in_chunks() {
    let world = TestWorld::with(|f| f.limits.max_objects = Some(1000));
    // Seed 250 objects straight into the store.
    {
        let objects: Vec<_> = (1..=250)
            .map(|id| tv_world::Object {
                id,
                owner: tv_world::UserId("seed".into()),
                spec: tv_world::ObjectSpec {
                    position: tv_world::Vec3::new(id as f32 * 0.1, 0.5, 0.0),
                    ..Default::default()
                },
            })
            .collect();
        world.store().save_objects(&objects, &[], 251).unwrap();
    }
    let server = world.start().await;
    let first = join(&server, "first").await;
    let s = join(&server, "second").await;
    assert_eq!(s.snapshot.len(), 3);
    assert_eq!(s.snapshot[0].avatars.len(), 1);
    assert_eq!(s.snapshot[0].avatars[0].id, first.avatar_id());
    assert!(s.snapshot[1].avatars.is_empty());
    assert_eq!(s.snapshot.iter().map(|c| c.objects.len()).collect::<Vec<_>>(), vec![100, 100, 50]);
    assert_eq!(s.snapshot.iter().filter(|c| c.last).count(), 1);
    assert!(s.snapshot[2].last);
    assert!(s.avatar_id() > 250);
    server.shutdown().await.unwrap();
}
