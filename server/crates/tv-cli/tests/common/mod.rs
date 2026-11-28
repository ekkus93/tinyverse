//! Test support: a scripted mock server and a small fake world.
#![allow(dead_code)]

pub mod fake_world;

use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use tokio::{
    net::{TcpListener, TcpStream},
    sync::mpsc,
};
use tokio_tungstenite::{WebSocketStream, accept_async, tungstenite::Message};
use tv_proto::{
    Encoding, Frame, PROTOCOL_VERSION, decode, encode,
    v1::{
        AvatarInfo, AvatarTransform, ChatHistory, ClientKind, ClientMessage, Hello, Limits, Role,
        ServerMessage, Snapshot, Vec3, Welcome, WorldInfo, client_message::Body as C,
        server_message::Body as S,
    },
};

/// How long the mock waits for the client before failing the test.
pub const WAIT: Duration = Duration::from_secs(5);

/// Accepts WebSocket connections on a random local port.
pub struct MockServer {
    pub url: String,
    conns: mpsc::UnboundedReceiver<MockConn>,
}

impl MockServer {
    pub async fn start() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("ws://{}/ws", listener.local_addr().unwrap());
        let (tx, conns) = mpsc::unbounded_channel();
        tokio::spawn(async move {
            while let Ok((stream, _)) = listener.accept().await {
                if let Ok(ws) = accept_async(stream).await
                    && tx.send(MockConn { ws, encoding: Encoding::Binary }).is_err()
                {
                    return;
                }
            }
        });
        MockServer { url, conns }
    }

    pub async fn accept(&mut self) -> MockConn {
        tokio::time::timeout(WAIT, self.conns.recv())
            .await
            .expect("no client connected")
            .expect("listener gone")
    }
}

/// One client connection, driven by the test script.
pub struct MockConn {
    ws: WebSocketStream<TcpStream>,
    /// The encoding of the client's first frame; replies use it.
    pub encoding: Encoding,
}

impl MockConn {
    /// The next data frame, or `None` once the client closed.
    pub async fn next_frame(&mut self) -> Option<Frame> {
        loop {
            let m = tokio::time::timeout(WAIT, self.ws.next()).await.expect("client sent nothing");
            match m {
                Some(Ok(Message::Binary(b))) => return Some(Frame::Binary(b.to_vec())),
                Some(Ok(Message::Text(t))) => return Some(Frame::Text(t.as_str().to_owned())),
                Some(Ok(Message::Close(_))) | None | Some(Err(_)) => return None,
                Some(Ok(_)) => continue,
            }
        }
    }

    /// The next client message, including pings.
    pub async fn next_any(&mut self) -> ClientMessage {
        let f = self.next_frame().await.expect("client closed unexpectedly");
        self.encoding = f.encoding();
        decode(&f).expect("client sent an undecodable frame")
    }

    /// The next client message that is not a keepalive Ping.
    pub async fn next(&mut self) -> ClientMessage {
        loop {
            let m = self.next_any().await;
            if !matches!(m.body, Some(C::Ping(_))) {
                return m;
            }
        }
    }

    pub async fn send(&mut self, reply_to: u32, body: S) {
        let msg = ServerMessage { reply_to, body: Some(body) };
        let msg = match encode(&msg, self.encoding) {
            Frame::Binary(b) => Message::Binary(b.into()),
            Frame::Text(t) => Message::Text(t.into()),
        };
        self.ws.send(msg).await.unwrap();
    }

    /// Expects `Hello`; returns it and its seq.
    pub async fn hello(&mut self) -> (Hello, u32) {
        let m = self.next().await;
        match m.body {
            Some(C::Hello(h)) => (h, m.seq),
            other => panic!("expected Hello, got {other:?}"),
        }
    }

    /// Hello, then Welcome, an empty final Snapshot and empty ChatHistory.
    pub async fn handshake(&mut self, welcome: Welcome) -> Hello {
        let (h, seq) = self.hello().await;
        self.send(seq, S::Welcome(welcome)).await;
        self.send(0, S::Snapshot(Snapshot { avatars: vec![], objects: vec![], last: true })).await;
        self.send(0, S::ChatHistory(ChatHistory { entries: vec![] })).await;
        h
    }

    /// Waits for the client to close the socket.
    pub async fn expect_close(&mut self) {
        loop {
            match self.next_frame().await {
                None => return,
                Some(f) => {
                    let m: ClientMessage = decode(&f).unwrap();
                    assert!(
                        matches!(
                            m.body,
                            Some(C::Ping(_)) | Some(C::AvatarState(_)) | Some(C::Stop(_))
                        ),
                        "expected the client to leave, got {m:?}"
                    );
                }
            }
        }
    }

    pub async fn close(mut self) {
        let _ = self.ws.close(None).await;
    }
}

pub fn v(x: f32, y: f32, z: f32) -> Vec3 {
    Vec3 { x, y, z }
}

pub fn avatar(id: u64, name: &str, pos: Vec3) -> AvatarInfo {
    AvatarInfo {
        id,
        user_id: format!("user-{id}"),
        name: name.into(),
        kind: ClientKind::Human as i32,
        role: Role::Builder as i32,
        appearance: None,
        transform: Some(AvatarTransform { position: Some(pos), ..Default::default() }),
    }
}

pub fn limits() -> Limits {
    Limits {
        max_message_bytes: 65536,
        max_chat_chars: 500,
        max_name_chars: 64,
        max_objects: 10_000,
        local_chat_radius: 20.0,
        min_scale: 0.01,
        max_scale: 64.0,
        walk_speed: 4.0,
        run_speed: 8.0,
        fly_speed: 16.0,
        max_observe_radius: 100.0,
    }
}

/// A Welcome for avatar `id` standing at `spawn`.
pub fn welcome(id: u64, spawn: Vec3) -> Welcome {
    Welcome {
        protocol_version: PROTOCOL_VERSION,
        user_id: format!("user-{id}"),
        avatar_id: id,
        role: Role::Builder as i32,
        session_token: "session".into(),
        world: Some(WorldInfo {
            name: "Mock".into(),
            description: "scripted".into(),
            bounds_min: Some(v(-128.0, -16.0, -128.0)),
            bounds_max: Some(v(128.0, 256.0, 128.0)),
            spawn_point: Some(spawn),
            ground_size: 256.0,
            sky: String::new(),
            time_of_day: 12.0,
            ground: String::new(),
        }),
        limits: Some(limits()),
        tick_rate_hz: 20,
        server_time_ms: 0,
        server_version: "mock".into(),
        avatar: Some(avatar(id, "me", spawn)),
    }
}

/// Runs the CLI with `args` (after the program name) and returns the exit
/// code and what it printed to stdout.
pub async fn run_cli(args: &[&str]) -> (i32, String) {
    let mut out = Vec::new();
    let argv = std::iter::once("tv-cli").chain(args.iter().copied());
    let code = tv_cli::cli::run_args(argv, &mut out).await;
    (code, String::from_utf8(out).unwrap())
}

/// A URL where nothing listens.
pub async fn dead_url() -> String {
    let l = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = l.local_addr().unwrap();
    drop(l);
    format!("ws://{addr}/ws")
}
