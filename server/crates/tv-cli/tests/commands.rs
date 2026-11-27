//! The CLI commands against a scripted mock server: what they send, what
//! they print and their exit codes.

mod common;

use std::time::Instant;

use common::{MockServer, avatar, run_cli, v, welcome};
use tv_cli::{
    cli::{EXIT_CONNECT, EXIT_FAILED, EXIT_OK},
    motion::{V3, norm180, yaw_of},
};
use tv_proto::v1::{
    ChatChannel, ChatEntry, ChatMessage, ClientKind, Color, Error, ErrorCode, Material, MoveMode,
    ObjectCreated, ObjectDeleted, ObjectInfo, ObjectSpec, ObjectUpdated, Observation, Shape,
    client_message::Body as C, observation::SeenAvatar, server_message::Body as S,
};

#[tokio::test]
async fn say_sends_world_chat_and_waits_for_the_echo() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        let h = c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        assert_eq!(h.kind, ClientKind::Agent as i32);
        assert!(h.name.starts_with("tv-cli-"), "{}", h.name);
        let m = c.next().await;
        let Some(C::Chat(chat)) = m.body else { panic!("{m:?}") };
        assert_eq!(chat.channel, ChatChannel::World as i32);
        assert_eq!(chat.text, "hello big world");
        let entry = ChatEntry {
            sender_avatar_id: 3,
            sender_name: "me".into(),
            channel: chat.channel,
            text: chat.text,
            ..Default::default()
        };
        c.send(m.seq, S::ChatMessage(ChatMessage { entry: Some(entry) })).await;
        c.expect_close().await;
    });
    let (code, out) = run_cli(&["--url", &url, "say", "hello", "big", "world"]).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_OK);
    assert!(out.contains("[world] me (unspecified): hello big world"), "{out}");
}

#[tokio::test]
async fn say_local_in_json_matches_an_echo_without_reply_to() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        let h = c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        assert_eq!(c.encoding, tv_proto::Encoding::Json);
        assert_eq!(h.name, "zed");
        assert_eq!(h.kind, ClientKind::Human as i32);
        let m = c.next().await;
        let Some(C::Chat(chat)) = m.body else { panic!("{m:?}") };
        assert_eq!(chat.channel, ChatChannel::Local as i32);
        // Someone else says the same thing first; then our echo with no reply_to.
        for sender in [9, 3] {
            let entry = ChatEntry {
                sender_avatar_id: sender,
                sender_name: format!("a{sender}"),
                channel: chat.channel,
                text: chat.text.clone(),
                ..Default::default()
            };
            c.send(0, S::ChatMessage(ChatMessage { entry: Some(entry) })).await;
        }
        c.expect_close().await;
    });
    let args =
        ["--url", &url, "--json", "--name", "zed", "--kind", "human", "say", "--local", "hi"];
    let (code, out) = run_cli(&args).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_OK);
    assert!(out.contains("[local] a3"), "{out}");
}

#[tokio::test]
async fn say_refused_with_error_exits_1() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let m = c.next().await;
        let e = Error { code: ErrorCode::RateLimited as i32, message: "slow down".into() };
        c.send(m.seq, S::Error(e)).await;
        c.expect_close().await;
    });
    let (code, _) = run_cli(&["--url", &url, "say", "spam"]).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_FAILED);
}

#[tokio::test]
async fn refused_hello_exits_2() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        let (h, seq) = c.hello().await;
        assert_eq!(h.password, "nope");
        let e = Error { code: ErrorCode::AuthFailed as i32, message: "wrong password".into() };
        c.send(seq, S::Error(e)).await;
        c.close().await;
    });
    let (code, _) = run_cli(&["--url", &url, "--password", "nope", "look"]).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_CONNECT);
}

#[tokio::test]
async fn nothing_listening_exits_2() {
    let url = common::dead_url().await;
    for args in [&["say", "x"][..], &["look"], &["smoke"], &["bot", "circle", "--duration", "1"]] {
        let mut argv = vec!["--url", url.as_str()];
        argv.extend_from_slice(args);
        let (code, _) = run_cli(&argv).await;
        assert_eq!(code, EXIT_CONNECT, "{args:?}");
    }
}

#[tokio::test]
async fn usage_errors_exit_1_and_help_exits_0() {
    assert_eq!(run_cli(&["create", "--pos", "1,2"]).await.0, EXIT_FAILED);
    assert_eq!(run_cli(&["frobnicate"]).await.0, EXIT_FAILED);
    assert_eq!(run_cli(&["smoke", "--skip", "nonsense"]).await.0, EXIT_FAILED);
    assert_eq!(
        run_cli(&["--token", "a", "--token-file", "/nonexistent", "look"]).await.0,
        EXIT_FAILED
    );
    assert_eq!(run_cli(&["--token-file", "/nonexistent/token", "look"]).await.0, EXIT_FAILED);
    // --ca-file is only for wss://, and must hold certificates.
    let not_pem = concat!(env!("CARGO_MANIFEST_DIR"), "/Cargo.toml");
    let ws = ["--url", "ws://127.0.0.1:1/ws", "--ca-file", not_pem, "say", "x"];
    assert_eq!(run_cli(&ws).await.0, EXIT_FAILED);
    let missing = ["--url", "wss://127.0.0.1:1/ws", "--ca-file", "/nonexistent.pem", "look"];
    assert_eq!(run_cli(&missing).await.0, EXIT_FAILED);
    let no_certs = ["--url", "wss://127.0.0.1:1/ws", "--ca-file", not_pem, "smoke"];
    assert_eq!(run_cli(&no_certs).await.0, EXIT_FAILED);
    assert_eq!(run_cli(&["--help"]).await.0, EXIT_OK);
}

#[tokio::test]
async fn token_file_is_trimmed_and_name_left_to_the_server() {
    let dir = std::env::temp_dir().join(format!("tv-cli-test-{}", std::process::id()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("token");
    std::fs::write(&path, "  secret-token\n").unwrap();
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        let h = c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        assert_eq!(h.token, "secret-token");
        assert_eq!(h.name, "");
        let m = c.next().await;
        c.send(m.seq, S::Observation(Observation::default())).await;
        c.expect_close().await;
    });
    let (code, _) = run_cli(&["--url", &url, "--token-file", path.to_str().unwrap(), "look"]).await;
    script.await.unwrap();
    let _ = std::fs::remove_dir_all(&dir);
    assert_eq!(code, EXIT_OK);
}

#[tokio::test]
async fn look_sends_observe_and_prints_the_lists() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let m = c.next().await;
        let Some(C::Observe(o)) = m.body else { panic!("{m:?}") };
        assert_eq!(o.radius, 12.5);
        let obs = Observation {
            self_: Some(avatar(3, "me", v(0.0, 0.0, 0.0))),
            avatars: vec![SeenAvatar {
                avatar: Some(avatar(4, "Ada", v(0.0, 0.0, 2.0))),
                distance: 2.0,
                bearing: 0.0,
            }],
            objects: vec![],
            summary: "Ada is 2 m ahead.".into(),
            truncated: false,
            radius: 12.5,
        };
        c.send(m.seq, S::Observation(obs)).await;
        c.expect_close().await;
    });
    let (code, out) = run_cli(&["--url", &url, "look", "--radius", "12.5"]).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_OK);
    assert!(out.contains("Ada is 2 m ahead."), "{out}");
    assert!(out.contains("Ada (avatar 4"), "{out}");
}

fn created(spec: ObjectSpec, id: u64) -> S {
    S::ObjectCreated(ObjectCreated {
        object: Some(ObjectInfo { id, owner_id: "user-3".into(), spec: Some(spec) }),
        by_avatar_id: 3,
    })
}

#[tokio::test]
async fn create_sends_every_spec_field() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let m = c.next().await;
        let Some(C::ObjectCreate(oc)) = m.body else { panic!("{m:?}") };
        let s = oc.spec.unwrap();
        assert_eq!(s.shape, Shape::Cylinder as i32);
        assert_eq!(s.position, Some(v(-1.0, 0.5, 2.0)));
        assert_eq!(s.scale, Some(v(0.5, 2.0, 0.5)));
        let r = s.rotation.unwrap();
        assert_eq!((r.x, r.y, r.z, r.w), (0.0, 0.0, 0.0, 1.0));
        assert_eq!(s.color, Some(Color { r: 0.0, g: 1.0, b: 0.0, a: 1.0 }));
        assert_eq!(s.material, Material::Emissive as i32);
        assert_eq!(s.name, "pillar");
        assert!(s.solid);
        assert!(s.locked);
        c.send(m.seq, created(s, 42)).await;
        c.expect_close().await;
    });
    let args = [
        "--url",
        &url,
        "create",
        "--shape",
        "cylinder",
        "--pos",
        "-1,0.5,2",
        "--scale",
        "0.5,2,0.5",
        "--rot",
        "0,0,0,1",
        "--color",
        "#00ff00",
        "--material",
        "emissive",
        "--object-name",
        "pillar",
        "--locked",
        "true",
    ];
    let (code, out) = run_cli(&args).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_OK);
    assert!(out.contains("object 42 cylinder \"pillar\""), "{out}");
    assert!(out.contains("#00ff00 emissive solid locked"), "{out}");
}

#[tokio::test]
async fn create_defaults() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let m = c.next().await;
        let Some(C::ObjectCreate(oc)) = m.body else { panic!("{m:?}") };
        let s = oc.spec.unwrap();
        assert_eq!(s.shape, Shape::Box as i32);
        assert_eq!(s.scale, Some(v(1.0, 1.0, 1.0)));
        assert_eq!(s.material, Material::Matte as i32);
        assert!(s.solid && !s.locked);
        assert_eq!(s.name, "");
        let e = Error { code: ErrorCode::PermissionDenied as i32, message: "visitor".into() };
        c.send(m.seq, S::Error(e)).await;
        c.expect_close().await;
    });
    let (code, _) = run_cli(&["--url", &url, "create", "--pos", "0,0.5,0"]).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_FAILED);
}

#[tokio::test]
async fn update_sends_only_given_fields() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let m = c.next().await;
        let Some(C::ObjectUpdate(u)) = m.body else { panic!("{m:?}") };
        assert_eq!(u.id, 42);
        assert_eq!(u.position, Some(v(1.0, 2.0, 3.0)));
        assert_eq!(u.solid, Some(false));
        assert_eq!(u.name, Some(String::new()));
        assert_eq!(u.material, Some(Material::Glass as i32));
        assert_eq!(
            (u.shape, u.rotation, u.scale, u.color, u.locked),
            (None, None, None, None, None)
        );
        let spec = ObjectSpec { position: u.position, ..Default::default() };
        let obj = ObjectInfo { id: 42, owner_id: "user-3".into(), spec: Some(spec) };
        c.send(m.seq, S::ObjectUpdated(ObjectUpdated { object: Some(obj), by_avatar_id: 3 })).await;
        c.expect_close().await;
    });
    let args = [
        "--url",
        &url,
        "update",
        "42",
        "--pos",
        "1,2,3",
        "--solid",
        "false",
        "--object-name",
        "",
        "--material",
        "glass",
    ];
    let (code, out) = run_cli(&args).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_OK);
    assert!(out.contains("object 42"), "{out}");
}

#[tokio::test]
async fn update_unknown_object_exits_1() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let m = c.next().await;
        let e = Error { code: ErrorCode::NotFound as i32, message: "no object 77".into() };
        c.send(m.seq, S::Error(e)).await;
        c.expect_close().await;
    });
    let (code, _) = run_cli(&["--url", &url, "update", "77", "--color", "#ffffff"]).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_FAILED);
}

#[tokio::test]
async fn delete_sends_the_id() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let m = c.next().await;
        assert_eq!(m.body, Some(C::ObjectDelete(tv_proto::v1::ObjectDelete { id: 42 })));
        // An unrelated event first, then the reply.
        c.send(0, S::ObjectDeleted(ObjectDeleted { id: 41, by_avatar_id: 8 })).await;
        c.send(m.seq, S::ObjectDeleted(ObjectDeleted { id: 42, by_avatar_id: 3 })).await;
        c.expect_close().await;
    });
    let (code, out) = run_cli(&["--url", &url, "--json", "delete", "42"]).await;
    script.await.unwrap();
    assert_eq!(code, EXIT_OK);
    assert!(out.contains("deleted object 42"), "{out}");
}

#[tokio::test]
async fn circle_bot_streams_legal_states_on_the_circle() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let center = v(4.0, 0.0, -3.0);
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, center)).await;
        let mut states = Vec::new();
        while let Some(f) = c.next_frame().await {
            let m: tv_proto::v1::ClientMessage = tv_proto::decode(&f).unwrap();
            if let Some(C::AvatarState(s)) = m.body {
                states.push((Instant::now(), s.transform.unwrap()));
            }
        }
        states
    });
    let args =
        ["--url", &url, "bot", "circle", "--radius", "2", "--speed", "run", "--duration", "2.5"];
    let (code, _) = run_cli(&args).await;
    assert_eq!(code, EXIT_OK);
    let states = script.await.unwrap();

    let limit = 8.0;
    let c = V3::from(center);
    assert!((20..=30).contains(&states.len()), "{} states in 2.5 s", states.len());
    let (last_t, last) = states.last().unwrap();
    assert_eq!(V3::from(last.velocity), V3::ZERO, "final state should be idle");
    assert_eq!(last.animation, "idle");
    let (first_t, _) = states[0];
    let mut path = 0.0;
    for w in states.windows(2) {
        let ((t0, a), (t1, b)) = (&w[0], &w[1]);
        let (pa, pb) = (V3::from(a.position), V3::from(b.position));
        let d = pa.dist(pb);
        path += d;
        // Receive-time jitter on localhost is small; allow 50% plus 5 cm.
        let dt = (*t1 - *t0).as_secs_f32();
        assert!(d <= limit * dt * 1.5 + 0.05, "{d} m in {dt} s");
    }
    let elapsed = (*last_t - first_t).as_secs_f32();
    assert!(path / elapsed <= limit, "average {} m/s", path / elapsed);
    for (_, t) in &states[..states.len() - 1] {
        let vel = V3::from(t.velocity);
        assert!(vel.len() <= limit, "velocity {vel:?}");
        assert_eq!(t.move_mode, MoveMode::Run as i32);
        assert_eq!(t.animation, "run");
        assert!(
            norm180(yaw_of(vel) - t.yaw).abs() < 0.1,
            "yaw {} vs heading {}",
            t.yaw,
            yaw_of(vel)
        );
    }
    // 2 m at 6.8 m/s takes 0.3 s; everything after the first second is on the circle.
    let on_circle: Vec<_> =
        states.iter().filter(|(t, _)| (*t - first_t).as_secs_f32() > 1.0).collect();
    assert!(on_circle.len() > 10);
    for (_, t) in on_circle {
        let r = V3::from(t.position).hdist(c);
        assert!((r - 2.0).abs() < 0.01, "radius {r}");
    }
}

#[tokio::test]
async fn bot_applies_corrections() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        // Let it get going, then snap it somewhere else.
        for _ in 0..3 {
            c.next().await;
        }
        let snap = tv_proto::v1::AvatarTransform {
            position: Some(v(10.0, 0.0, 10.0)),
            ..Default::default()
        };
        c.send(
            0,
            S::AvatarCorrection(tv_proto::v1::AvatarCorrection {
                transform: Some(snap),
                reason: "test".into(),
            }),
        )
        .await;
        let mut after = Vec::new();
        while let Some(f) = c.next_frame().await {
            let m: tv_proto::v1::ClientMessage = tv_proto::decode(&f).unwrap();
            if let Some(C::AvatarState(s)) = m.body {
                after.push(V3::from(s.transform.unwrap().position));
            }
        }
        after
    });
    let (code, _) = run_cli(&["--url", &url, "bot", "circle", "--duration", "1"]).await;
    assert_eq!(code, EXIT_OK);
    let after = script.await.unwrap();
    // After the correction the states continue from (10, 0, 10).
    let near = after.iter().position(|p| p.hdist(V3::new(10.0, 0.0, 10.0)) < 0.5);
    let i = near.expect("the bot never moved to the corrected position");
    for p in &after[i..] {
        assert!(p.hdist(V3::new(10.0, 0.0, 10.0)) < 4.0, "jumped to {p:?}");
    }
}

#[tokio::test]
async fn wander_moveto_waits_for_arrival() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let mut targets = Vec::new();
        while let Some(f) = c.next_frame().await {
            let m: tv_proto::v1::ClientMessage = tv_proto::decode(&f).unwrap();
            match m.body {
                Some(C::MoveTo(mt)) => {
                    let t = mt.target.unwrap();
                    assert!(V3::from(t).hlen() <= 20.0 + 1e-3, "target {t:?} too far");
                    assert_eq!(mt.mode, MoveMode::Walk as i32);
                    targets.push(t);
                    // Arrive at once.
                    let e = tv_proto::v1::avatar_states::Entry {
                        avatar_id: 3,
                        transform: Some(tv_proto::v1::AvatarTransform {
                            position: Some(t),
                            ..Default::default()
                        }),
                    };
                    let states = tv_proto::v1::AvatarStates { server_time_ms: 0, states: vec![e] };
                    c.send(0, S::AvatarStates(states)).await;
                }
                Some(C::AvatarState(_)) => panic!("MoveTo mode must not stream AvatarState"),
                _ => {}
            }
        }
        targets
    });
    let (code, _) = run_cli(&["--url", &url, "bot", "wander", "--moveto", "--duration", "6"]).await;
    assert_eq!(code, EXIT_OK);
    let targets = script.await.unwrap();
    // Pauses are 1..4 s, so 6 s gives at least two legs.
    assert!(targets.len() >= 2, "{} MoveTo(s)", targets.len());
}

fn global(url: &str, extra: &[&str]) -> tv_cli::cli::Global {
    let mut argv = vec!["tv-cli", "--url", url];
    argv.extend_from_slice(extra);
    argv.push("connect");
    <tv_cli::cli::Cli as clap::Parser>::try_parse_from(argv).unwrap().global
}

#[tokio::test]
async fn connect_turns_lines_into_messages() {
    use tv_proto::v1::*;
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let mut got = Vec::new();
        while let Some(f) = c.next_frame().await {
            let m: ClientMessage = tv_proto::decode(&f).unwrap();
            if let Some(C::Ping(_)) = m.body {
                continue;
            }
            if let Some(C::ObjectCreate(oc)) = &m.body {
                c.send(m.seq, created(oc.spec.clone().unwrap(), 50)).await;
            }
            got.push(m.body.unwrap());
        }
        got
    });
    let input = b"hello all\n/local psst\n/goto 1 0 2\n/fly 1 5 2\n/stop\n/look 9\n\
                  /box 1 0.5 1 crate\n/move 50 2 0.5 2\n/color 50 #ff0000\n/delete 50\n\
                  /history\n/who\n/bogus\n/quit\nnot sent\n";
    let mut out = Vec::new();
    let r = tv_cli::cli::connect_session(&global(&url, &[]), false, &input[..], &mut out).await;
    assert!(r.is_ok(), "{r:?}");
    let got = script.await.unwrap();
    let out = String::from_utf8(out).unwrap();
    let expected = [
        C::Chat(Chat { channel: ChatChannel::World as i32, text: "hello all".into() }),
        C::Chat(Chat { channel: ChatChannel::Local as i32, text: "psst".into() }),
        C::MoveTo(MoveTo { target: Some(v(1.0, 0.0, 2.0)), mode: MoveMode::Walk as i32 }),
        C::MoveTo(MoveTo { target: Some(v(1.0, 5.0, 2.0)), mode: MoveMode::Fly as i32 }),
        C::Stop(Stop {}),
        C::Observe(Observe { radius: 9.0 }),
    ];
    assert_eq!(got[..6], expected[..]);
    let C::ObjectCreate(oc) = &got[6] else { panic!("{:?}", got[6]) };
    assert_eq!(oc.spec.as_ref().unwrap().name, "crate");
    assert_eq!(
        got[7],
        C::ObjectUpdate(ObjectUpdate {
            id: 50,
            position: Some(v(2.0, 0.5, 2.0)),
            ..Default::default()
        })
    );
    let C::ObjectUpdate(u) = &got[8] else { panic!("{:?}", got[8]) };
    assert_eq!((u.id, u.color.unwrap().r, u.position), (50, 1.0, None));
    assert_eq!(got[9], C::ObjectDelete(ObjectDelete { id: 50 }));
    assert_eq!(got[10], C::ChatHistoryRequest(ChatHistoryRequest { since_ms: 0, limit: 0 }));
    assert_eq!(got.len(), 11, "{got:?}");
    assert!(out.contains("unknown command /bogus"), "{out}");
    assert!(out.contains("me (avatar 3"), "/who should list us: {out}");
}

#[tokio::test]
async fn connect_with_input_closed_prints_events_until_the_server_leaves() {
    use tv_proto::v1::*;
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        let bob = avatar(4, "bob", v(1.0, 0.0, 1.0));
        c.send(0, S::AvatarJoined(AvatarJoined { avatar: Some(bob) })).await;
        let entry = ChatEntry {
            sender_avatar_id: 4,
            sender_name: "bob".into(),
            sender_kind: ClientKind::Human as i32,
            channel: ChatChannel::Local as i32,
            text: "anyone?".into(),
            ..Default::default()
        };
        c.send(0, S::ChatMessage(ChatMessage { entry: Some(entry) })).await;
        let e = avatar_states::Entry {
            avatar_id: 4,
            transform: Some(AvatarTransform {
                position: Some(v(2.0, 0.0, 1.0)),
                yaw: 90.0,
                move_mode: MoveMode::Walk as i32,
                animation: "walk".into(),
                ..Default::default()
            }),
        };
        c.send(0, S::AvatarStates(AvatarStates { server_time_ms: 1, states: vec![e] })).await;
        c.send(0, S::AvatarLeft(AvatarLeft { avatar_id: 4 })).await;
        c.send(0, S::Kicked(Kicked { reason: "closing time".into() })).await;
        c.close().await;
    });
    let mut out = Vec::new();
    let r = tv_cli::cli::connect_session(&global(&url, &[]), false, &b""[..], &mut out).await;
    script.await.unwrap();
    let out = String::from_utf8(out).unwrap();
    assert!(r.is_err(), "the server leaving is a failure");
    for want in [
        "+ joined: bob (avatar 4, human, builder",
        "[local] bob (human): anyone?",
        "states: bob (2.00, 0.00, 1.00) yaw 90 walk \"walk\"",
        "- left: bob (avatar 4)",
        "kicked: closing time",
    ] {
        assert!(out.contains(want), "missing {want:?} in:\n{out}");
    }
}

#[tokio::test]
async fn connect_raw_prints_json() {
    let mut server = MockServer::start().await;
    let url = server.url.clone();
    let script = tokio::spawn(async move {
        let mut c = server.accept().await;
        c.handshake(welcome(3, v(0.0, 0.0, 0.0))).await;
        c.send(0, S::AvatarLeft(tv_proto::v1::AvatarLeft { avatar_id: 9 })).await;
        c.close().await;
    });
    let mut out = Vec::new();
    let g = global(&url, &["--json"]);
    let _ = tv_cli::cli::connect_session(&g, true, &b""[..], &mut out).await;
    script.await.unwrap();
    let out = String::from_utf8(out).unwrap();
    let first: serde_json::Value = serde_json::from_str(out.lines().next().unwrap()).unwrap();
    assert_eq!(first["welcome"]["avatarId"], "3", "{out}");
    let last: serde_json::Value = serde_json::from_str(out.lines().last().unwrap()).unwrap();
    assert_eq!(last["avatarLeft"]["avatarId"], "9", "{out}");
}
