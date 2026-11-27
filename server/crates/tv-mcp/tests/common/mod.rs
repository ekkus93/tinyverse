//! Test fixtures: a scripted mock world server and an MCP client that drives
//! `tv_mcp::run` in-process over in-memory pipes.
#![allow(dead_code)]

use std::net::SocketAddr;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use serde_json::{Value, json};
use tokio::io::{
    AsyncBufReadExt, AsyncWriteExt, BufReader, DuplexStream, Lines, ReadHalf, WriteHalf,
};
use tokio::net::TcpListener;
use tokio::sync::mpsc;
use tokio_tungstenite::tungstenite::Message;
use tv_proto::v1::{
    AvatarInfo, AvatarTransform, ChatEntry, ChatHistory, ClientKind, ClientMessage, ErrorCode,
    Hello, Limits, ObjectInfo, ObjectSpec, Pong, Role, ServerMessage, Snapshot, Vec3, Welcome,
    WorldInfo, client_message::Body as C, server_message::Body as S,
};
use tv_proto::{Encoding, Frame};

pub const WAIT: Duration = Duration::from_secs(10);
pub const ME: u64 = 100;
pub const ME_USER: &str = "u-me";
pub const ADA: u64 = 3;
pub const ADA_USER: &str = "u-ada";

pub fn v(x: f32, y: f32, z: f32) -> Vec3 {
    Vec3 { x, y, z }
}

pub fn transform(p: Vec3, velocity: Vec3) -> AvatarTransform {
    AvatarTransform {
        position: Some(p),
        yaw: 0.0,
        velocity: Some(velocity),
        move_mode: 1,
        animation: String::new(),
    }
}

pub fn chat(sender: u64, user: &str, name: &str, channel: i32, text: &str) -> ChatEntry {
    ChatEntry {
        sender_avatar_id: sender,
        sender_user_id: user.into(),
        sender_name: name.into(),
        sender_kind: ClientKind::Human as i32,
        channel,
        text: text.into(),
        timestamp_ms: 1_795_000_000_000,
    }
}

pub fn object(id: u64, owner: &str, name: &str, p: Vec3) -> ObjectInfo {
    ObjectInfo {
        id,
        owner_id: owner.into(),
        spec: Some(ObjectSpec {
            name: name.into(),
            shape: 1,
            position: Some(p),
            rotation: None,
            scale: Some(v(1.0, 1.0, 1.0)),
            color: Some(tv_proto::v1::Color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 }),
            material: 1,
            solid: true,
            locked: false,
        }),
    }
}

/// What the mock world does with a Hello.
#[derive(Clone)]
pub struct Script {
    pub welcome: Welcome,
    pub snapshot: Snapshot,
    pub history: Vec<ChatEntry>,
    pub refuse: Option<(ErrorCode, String)>,
    /// Answer like the real server: `Ack` to MoveTo and Stop, and the
    /// speaker's own ChatMessage (with reply_to) to Chat. Off for tests that
    /// answer by hand, e.g. with an Error.
    pub auto_reply: bool,
}

impl Default for Script {
    fn default() -> Self {
        let me = AvatarInfo {
            id: ME,
            user_id: ME_USER.into(),
            name: "Scout".into(),
            kind: ClientKind::Agent as i32,
            role: Role::Builder as i32,
            appearance: None,
            transform: Some(transform(v(0.0, 0.0, 0.0), v(0.0, 0.0, 0.0))),
        };
        let ada = AvatarInfo {
            id: ADA,
            user_id: ADA_USER.into(),
            name: "Ada".into(),
            kind: ClientKind::Human as i32,
            role: Role::Owner as i32,
            appearance: None,
            transform: Some(transform(v(3.0, 0.0, 4.0), v(0.0, 0.0, 0.0))),
        };
        Script {
            welcome: Welcome {
                protocol_version: 1,
                user_id: ME_USER.into(),
                avatar_id: ME,
                role: Role::Builder as i32,
                session_token: String::new(),
                world: Some(WorldInfo {
                    name: "Test Island".into(),
                    description: "a small island".into(),
                    bounds_min: Some(v(-128.0, -16.0, -128.0)),
                    bounds_max: Some(v(128.0, 256.0, 128.0)),
                    spawn_point: Some(v(0.0, 0.0, 0.0)),
                    ground_size: 256.0,
                    sky: "day".into(),
                    time_of_day: 13.5,
                }),
                limits: Some(Limits {
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
                }),
                tick_rate_hz: 20,
                server_time_ms: 1_795_000_000_000,
                server_version: "mock".into(),
                avatar: Some(me),
            },
            snapshot: Snapshot {
                avatars: vec![ada],
                objects: vec![
                    object(7, ADA_USER, "door", v(0.0, 1.0, 10.0)),
                    object(8, ME_USER, "my crate", v(2.0, 0.5, 0.0)),
                ],
                last: true,
            },
            history: vec![chat(ADA, ADA_USER, "Ada", 2, "welcome, everyone")],
            refuse: None,
            auto_reply: true,
        }
    }
}

enum Cmd {
    Send(Box<ServerMessage>),
    Close,
}

/// One accepted connection, after the login.
pub struct MockConn {
    pub hello: Hello,
    rx: mpsc::UnboundedReceiver<ClientMessage>,
    tx: mpsc::UnboundedSender<Cmd>,
    pub pings: Arc<AtomicUsize>,
}

impl MockConn {
    /// The next non-Ping message from the bridge.
    pub async fn recv(&mut self) -> ClientMessage {
        tokio::time::timeout(WAIT, self.rx.recv())
            .await
            .expect("timed out waiting for a client message")
            .expect("connection ended")
    }

    /// Asserts that nothing arrives for a while.
    pub async fn expect_silence(&mut self, d: Duration) {
        if let Ok(Some(m)) = tokio::time::timeout(d, self.rx.recv()).await {
            panic!("expected no message, got {m:?}");
        }
    }

    pub fn send(&self, reply_to: u32, body: S) {
        let _ = self.tx.send(Cmd::Send(Box::new(ServerMessage { reply_to, body: Some(body) })));
    }

    pub fn close(&self) {
        let _ = self.tx.send(Cmd::Close);
    }
}

pub struct MockWorld {
    pub addr: SocketAddr,
    pub script: Arc<Mutex<Script>>,
    conns: mpsc::UnboundedReceiver<MockConn>,
}

impl MockWorld {
    pub async fn start(script: Script) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        Self::start_on(listener, script)
    }

    pub fn start_on(listener: TcpListener, script: Script) -> Self {
        let addr = listener.local_addr().unwrap();
        let script = Arc::new(Mutex::new(script));
        let (conn_tx, conns) = mpsc::unbounded_channel();
        let s = script.clone();
        tokio::spawn(async move {
            while let Ok((tcp, _)) = listener.accept().await {
                tokio::spawn(serve(tcp, s.clone(), conn_tx.clone()));
            }
        });
        MockWorld { addr, script, conns }
    }

    pub fn url(&self) -> String {
        format!("ws://{}/ws", self.addr)
    }

    pub async fn next_conn(&mut self) -> MockConn {
        tokio::time::timeout(WAIT, self.conns.recv())
            .await
            .expect("timed out waiting for the bridge to connect")
            .expect("acceptor stopped")
    }
}

fn frame(msg: &ServerMessage) -> Message {
    match tv_proto::encode(msg, Encoding::Json) {
        Frame::Text(t) => Message::text(t),
        Frame::Binary(b) => Message::binary(b),
    }
}

fn decode(m: Message) -> Option<ClientMessage> {
    let f = match m {
        Message::Text(t) => {
            // Agents must speak proto3 JSON; check it is the canonical form.
            let v: Value = serde_json::from_str(t.as_str()).expect("text frame is JSON");
            assert!(v.is_object());
            Frame::Text(t.as_str().to_owned())
        }
        Message::Binary(b) => Frame::Binary(b.to_vec()),
        _ => return None,
    };
    Some(tv_proto::decode(&f).expect("bridge sent an undecodable frame"))
}

async fn serve(
    tcp: tokio::net::TcpStream,
    script: Arc<Mutex<Script>>,
    conns: mpsc::UnboundedSender<MockConn>,
) {
    let Ok(ws) = tokio_tungstenite::accept_async(tcp).await else { return };
    let (mut sink, mut stream) = ws.split();
    let (seq, hello) = loop {
        match stream.next().await {
            Some(Ok(m)) => {
                if let Some(ClientMessage { seq, body: Some(C::Hello(h)) }) = decode(m) {
                    break (seq, h);
                }
            }
            _ => return,
        }
    };
    let script = script.lock().unwrap().clone();
    if let Some((code, message)) = script.refuse {
        let err = ServerMessage {
            reply_to: seq,
            body: Some(S::Error(tv_proto::v1::Error { code: code as i32, message })),
        };
        let _ = sink.send(frame(&err)).await;
        let _ = sink.close().await;
        return;
    }
    for msg in [
        ServerMessage { reply_to: seq, body: Some(S::Welcome(script.welcome.clone())) },
        ServerMessage { reply_to: 0, body: Some(S::Snapshot(script.snapshot.clone())) },
        ServerMessage {
            reply_to: 0,
            body: Some(S::ChatHistory(ChatHistory { entries: script.history.clone() })),
        },
    ] {
        if sink.send(frame(&msg)).await.is_err() {
            return;
        }
    }
    let (in_tx, rx) = mpsc::unbounded_channel();
    let (tx, mut cmds) = mpsc::unbounded_channel();
    let pings = Arc::new(AtomicUsize::new(0));
    let _ = conns.send(MockConn { hello, rx, tx, pings: pings.clone() });
    loop {
        tokio::select! {
            m = stream.next() => {
                let Some(Ok(m)) = m else { return };
                let Some(msg) = decode(m) else { continue };
                if let Some(C::Ping(p)) = &msg.body {
                    pings.fetch_add(1, Ordering::Relaxed);
                    let pong = ServerMessage {
                        reply_to: msg.seq,
                        body: Some(S::Pong(Pong { client_time_ms: p.client_time_ms, server_time_ms: 1 })),
                    };
                    let _ = sink.send(frame(&pong)).await;
                    continue;
                }
                if script.auto_reply {
                    let reply = match &msg.body {
                        Some(C::MoveTo(_) | C::Stop(_)) => Some(S::Ack(tv_proto::v1::Ack {})),
                        Some(C::Chat(c)) => Some(S::ChatMessage(tv_proto::v1::ChatMessage {
                            entry: Some(chat(ME, ME_USER, "Scout", c.channel, &c.text)),
                        })),
                        _ => None,
                    };
                    if let Some(body) = reply {
                        let m = ServerMessage { reply_to: msg.seq, body: Some(body) };
                        let _ = sink.send(frame(&m)).await;
                    }
                }
                let _ = in_tx.send(msg);
            }
            c = cmds.recv() => match c {
                Some(Cmd::Send(m)) => {
                    let _ = sink.send(frame(&m)).await;
                }
                Some(Cmd::Close) | None => {
                    let _ = sink.close().await;
                    return;
                }
            }
        }
    }
}

/// Bridge settings suited to tests: short timeouts, quick reconnects.
pub fn config(url: &str) -> tv_mcp::Config {
    let mut c = tv_mcp::Config::new(url);
    c.token = Some("agent-token".into());
    c.connect_timeout = Duration::from_secs(2);
    c.reply_timeout = Duration::from_secs(2);
    c.reconnect_delay = Duration::from_millis(100);
    c
}

/// Asserts that a line written by the server is a valid JSON-RPC 2.0 message.
pub fn assert_jsonrpc(line: &str) -> Value {
    let v: Value = serde_json::from_str(line)
        .unwrap_or_else(|e| panic!("stdout line is not JSON ({e}): {line:?}"));
    assert_eq!(v["jsonrpc"], "2.0", "not JSON-RPC 2.0: {line}");
    let obj = v.as_object().expect("message is an object");
    if obj.contains_key("method") {
        assert!(v["method"].is_string(), "{line}");
    } else {
        assert!(obj.contains_key("id"), "response without id: {line}");
        assert!(
            obj.contains_key("result") ^ obj.contains_key("error"),
            "exactly one of result/error: {line}"
        );
        if let Some(e) = obj.get("error") {
            assert!(e["code"].is_i64() && e["message"].is_string(), "{line}");
        }
    }
    v
}

/// An MCP client talking to an in-process `tv_mcp::run`.
pub struct Client {
    w: WriteHalf<DuplexStream>,
    lines: Lines<BufReader<ReadHalf<DuplexStream>>>,
    next_id: i64,
    stash: Vec<Value>,
    pub task: tokio::task::JoinHandle<std::io::Result<()>>,
}

impl Client {
    pub fn start(cfg: tv_mcp::Config) -> Self {
        let (client, server) = tokio::io::duplex(1 << 20);
        let (sr, sw) = tokio::io::split(server);
        let task = tokio::spawn(tv_mcp::run(cfg, sr, sw));
        let (cr, w) = tokio::io::split(client);
        Client { w, lines: BufReader::new(cr).lines(), next_id: 1, stash: Vec::new(), task }
    }

    pub async fn send_raw(&mut self, line: &str) {
        self.w.write_all(line.as_bytes()).await.unwrap();
        self.w.write_all(b"\n").await.unwrap();
        self.w.flush().await.unwrap();
    }

    pub async fn send(&mut self, v: Value) {
        self.send_raw(&v.to_string()).await;
    }

    /// The next message from the server, checked to be JSON-RPC.
    pub async fn recv(&mut self) -> Value {
        if !self.stash.is_empty() {
            return self.stash.remove(0);
        }
        let line = tokio::time::timeout(WAIT, self.lines.next_line())
            .await
            .expect("timed out waiting for the MCP server")
            .unwrap()
            .expect("MCP server closed its output");
        assert_jsonrpc(&line)
    }

    /// Waits for the response with this id, keeping others for later.
    pub async fn response(&mut self, id: i64) -> Value {
        if let Some(i) = self.stash.iter().position(|m| m["id"] == id) {
            return self.stash.remove(i);
        }
        loop {
            let line = tokio::time::timeout(WAIT, self.lines.next_line())
                .await
                .expect("timed out waiting for the MCP server")
                .unwrap()
                .expect("MCP server closed its output");
            let m = assert_jsonrpc(&line);
            if m["id"] == id {
                return m;
            }
            self.stash.push(m);
        }
    }

    pub async fn start_request(&mut self, method: &str, params: Value) -> i64 {
        let id = self.next_id;
        self.next_id += 1;
        self.send(json!({"jsonrpc": "2.0", "id": id, "method": method, "params": params})).await;
        id
    }

    pub async fn request(&mut self, method: &str, params: Value) -> Value {
        let id = self.start_request(method, params).await;
        self.response(id).await
    }

    pub async fn initialize(&mut self) -> Value {
        let r = self
            .request(
                "initialize",
                json!({
                    "protocolVersion": "2025-11-25",
                    "capabilities": {},
                    "clientInfo": {"name": "test", "version": "0"}
                }),
            )
            .await;
        self.send(json!({"jsonrpc": "2.0", "method": "notifications/initialized"})).await;
        r
    }

    pub async fn start_call(&mut self, tool: &str, args: Value) -> i64 {
        self.start_request("tools/call", json!({"name": tool, "arguments": args})).await
    }

    /// Text and isError of a finished tools/call.
    pub async fn finish_call(&mut self, id: i64) -> (String, bool) {
        let r = self.response(id).await;
        let result = &r["result"];
        assert!(result.is_object(), "tools/call failed at protocol level: {r}");
        let content = result["content"].as_array().expect("content array");
        assert_eq!(content[0]["type"], "text");
        let text = content
            .iter()
            .map(|c| c["text"].as_str().unwrap().to_string())
            .collect::<Vec<_>>()
            .join("\n");
        (text, result["isError"].as_bool().expect("isError is a boolean"))
    }

    pub async fn call(&mut self, tool: &str, args: Value) -> (String, bool) {
        let id = self.start_call(tool, args).await;
        self.finish_call(id).await
    }

    /// A tools/call that must succeed.
    pub async fn ok(&mut self, tool: &str, args: Value) -> String {
        let (text, err) = self.call(tool, args).await;
        assert!(!err, "{tool} failed: {text}");
        text
    }

    /// A tools/call that must be a tool error.
    pub async fn fails(&mut self, tool: &str, args: Value) -> String {
        let (text, err) = self.call(tool, args).await;
        assert!(err, "{tool} should have failed but said: {text}");
        text
    }

    /// Closes the input and returns once the server has exited.
    pub async fn shutdown(mut self) {
        self.w.shutdown().await.unwrap();
        drop(self.w);
        tokio::time::timeout(WAIT, self.task).await.expect("server did not exit").unwrap().unwrap();
    }
}

/// Starts a mock world and a connected client.
pub async fn connected() -> (MockWorld, MockConn, Client) {
    connected_with(Script::default()).await
}

/// Like [`connected`], with a custom script.
pub async fn connected_with(script: Script) -> (MockWorld, MockConn, Client) {
    let mut world = MockWorld::start(script).await;
    let mut client = Client::start(config(&world.url()));
    client.initialize().await;
    let conn = world.next_conn().await;
    // Make sure the bridge has processed the login before tests start.
    client.ok("who", json!({})).await;
    (world, conn, client)
}
