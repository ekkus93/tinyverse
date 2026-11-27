//! Runs the bridge against the real `tinyverse-server` binary, over `ws://`
//! with a second participant present and over `wss://` with a self-signed
//! certificate, so the protocol and TLS assumptions stay verified.
//!
//! The server binary is looked up next to `tv-mcp` in the cargo target
//! directory. It is built by `cargo build --workspace` (or `cargo test
//! --workspace`); when it is missing the tests are skipped with a message.

mod common;

use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::Duration;

use common::{Client, WAIT};
use futures_util::{SinkExt, StreamExt};
use serde_json::json;
use tokio_tungstenite::tungstenite::Message;
use tv_proto::v1::{
    Chat, ChatChannel, ClientKind, ClientMessage, Hello, ServerMessage, client_message::Body as C,
    server_message::Body as S,
};
use tv_proto::{Encoding, Frame};

fn server_binary() -> Option<PathBuf> {
    let dir = Path::new(env!("CARGO_BIN_EXE_tv-mcp")).parent()?;
    let bin = dir.join("tinyverse-server");
    if bin.is_file() {
        Some(bin)
    } else {
        eprintln!(
            "SKIPPED: tinyverse-server not found next to tv-mcp in the target directory; \
             run `cargo build --workspace` first"
        );
        None
    }
}

/// Stops the server with SIGTERM when dropped, killing it if it lingers.
struct ServerProcess(Child);

impl ServerProcess {
    fn stop(&mut self) {
        let _ = Command::new("kill").args(["-TERM", &self.0.id().to_string()]).status();
        for _ in 0..100 {
            if let Ok(Some(_)) = self.0.try_wait() {
                return;
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

impl Drop for ServerProcess {
    fn drop(&mut self) {
        self.stop();
    }
}

/// A world directory with an agent token, served by the real server.
struct World {
    bin: PathBuf,
    _dir: tempfile::TempDir,
    world: PathBuf,
    port: u16,
    token: String,
    tls: bool,
    server: Option<ServerProcess>,
}

impl World {
    async fn start(bin: &Path, tls: bool) -> World {
        let dir = tempfile::tempdir().unwrap();
        let world = dir.path().join("world");
        let mut w = World {
            bin: bin.to_path_buf(),
            world,
            _dir: dir,
            port: 0,
            token: String::new(),
            tls,
            server: None,
        };
        w.cli(&["init", w.path()]);
        if tls {
            w.cli(&["tls", "self-signed", w.path()]);
        }
        let out = w.cli(&["token", "create", w.path(), "--name", "Scout", "--role", "builder"]);
        w.token = out
            .lines()
            .find_map(|l| l.strip_prefix("token:"))
            .map(|t| t.trim().to_string())
            .unwrap_or_else(|| panic!("no token in {out:?}"));
        w.port = std::net::TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
        w.run().await;
        w
    }

    fn path(&self) -> &str {
        self.world.to_str().unwrap()
    }

    fn cli(&self, args: &[&str]) -> String {
        let out = Command::new(&self.bin).args(args).output().unwrap();
        assert!(out.status.success(), "{args:?}: {}", String::from_utf8_lossy(&out.stderr));
        String::from_utf8(out.stdout).unwrap()
    }

    /// (Re)starts the server on the world's port and waits until it listens.
    async fn run(&mut self) {
        let child = Command::new(&self.bin)
            .args(["run", self.path(), "--port", &self.port.to_string(), "--bind", "127.0.0.1"])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        self.server = Some(ServerProcess(child));
        for _ in 0..100 {
            if tokio::net::TcpStream::connect(("127.0.0.1", self.port)).await.is_ok() {
                return;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        panic!("server did not start listening");
    }

    fn stop(&mut self) {
        if let Some(mut s) = self.server.take() {
            s.stop();
        }
    }

    fn url(&self, scheme: &str) -> String {
        format!("{scheme}://127.0.0.1:{}/ws", self.port)
    }

    fn cert(&self) -> PathBuf {
        self.world.join("cert.pem")
    }

    /// Bridge settings for this world, using the scheme it serves.
    fn config(&self) -> tv_mcp::Config {
        let mut cfg = tv_mcp::Config::new(self.url(if self.tls { "wss" } else { "ws" }));
        cfg.token = Some(self.token.clone());
        cfg.reconnect_delay = Duration::from_millis(200);
        if self.tls {
            cfg.ca_certs = tv_mcp::tls::load_ca_file(&self.cert()).unwrap();
        }
        cfg
    }
}

fn to_ws(msg: &ClientMessage) -> Message {
    match tv_proto::encode(msg, Encoding::Json) {
        Frame::Text(t) => Message::text(t),
        Frame::Binary(b) => Message::binary(b),
    }
}

#[tokio::test]
async fn bridge_works_against_the_real_server() {
    let Some(bin) = server_binary() else { return };
    let world = World::start(&bin, false).await;
    let url = world.url("ws");

    // A second participant, a human named Ada, standing at spawn.
    let (ws, _) = tokio_tungstenite::connect_async(url.as_str()).await.unwrap();
    let (mut ada_tx, mut ada_rx) = ws.split();
    let hello = ClientMessage {
        seq: 1,
        body: Some(C::Hello(Hello {
            protocol_version: tv_proto::PROTOCOL_VERSION,
            kind: ClientKind::Human as i32,
            name: "Ada".into(),
            ..Default::default()
        })),
    };
    ada_tx.send(to_ws(&hello)).await.unwrap();
    let welcome = tokio::time::timeout(WAIT, ada_rx.next()).await.unwrap().unwrap().unwrap();
    let Message::Text(t) = welcome else { panic!("expected a JSON frame") };
    let msg: ServerMessage = tv_proto::decode(&Frame::Text(t.as_str().into())).unwrap();
    assert!(matches!(msg.body, Some(S::Welcome(_))), "{msg:?}");
    // Keep reading so the server never sees Ada as a slow client.
    tokio::spawn(async move { while let Some(Ok(_)) = ada_rx.next().await {} });

    let mut c = Client::start(world.config());
    let init = c.initialize().await;
    assert_eq!(init["result"]["protocolVersion"], "2025-11-25");

    let text = c.ok("world_info", json!({})).await;
    assert!(text.contains("Your role: builder"), "{text}");
    let text = c.ok("who", json!({})).await;
    assert!(text.contains("you: Scout (agent, builder)") && text.contains("Ada (human"), "{text}");
    let text = c.ok("look", json!({"radius": 20})).await;
    assert!(text.contains("Avatars within 20 m (1)") && text.contains("Ada"), "{text}");

    // Our own line comes back from the server as confirmation.
    let text = c.ok("say", json!({"text": "Hello Ada"})).await;
    assert!(text.contains("Hello Ada"), "{text}");
    let call = c.start_call("wait_for_chat", json!({"timeout_s": 10})).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    let chat = ClientMessage {
        seq: 2,
        body: Some(C::Chat(Chat { channel: ChatChannel::Local as i32, text: "hi Scout".into() })),
    };
    ada_tx.send(to_ws(&chat)).await.unwrap();
    let (text, err) = c.finish_call(call).await;
    assert!(!err && text.contains("[local] Ada: hi Scout"), "{text}");
    assert!(text.contains("Scout (you): Hello Ada"), "{text}");
    let text = c.ok("recent_chat", json!({})).await;
    assert_eq!(text, "No new chat since you last checked.");

    // The server finishes a MoveTo by snapping to the target.
    let text = c.ok("move_to", json!({"x": 3, "z": 4})).await;
    assert_eq!(text, "Arrived at (3, 0, 4) (walk).");
    let text = c.ok("move_to", json!({"x": 3, "y": 2, "z": 4, "mode": "fly"})).await;
    assert_eq!(text, "Arrived at (3, 2, 4) (fly).");
    let text = c.ok("move_to", json!({"x": 3, "y": 0, "z": 4, "mode": "fly"})).await;
    assert_eq!(text, "Arrived at (3, 0, 4) (fly).");
    // Walking from the air comes down to the ground on the way.
    let text = c.ok("move_to", json!({"x": 3, "y": 5, "z": 4, "mode": "fly"})).await;
    assert_eq!(text, "Arrived at (3, 5, 4) (fly).");
    let text = c.ok("move_to", json!({"x": 9, "z": 4})).await;
    assert_eq!(text, "Arrived at (9, 0, 4) (walk).");
    let text = c.ok("move_to", json!({"x": 9, "y": 5, "z": 4, "mode": "fly"})).await;
    assert_eq!(text, "Arrived at (9, 5, 4) (fly).");
    // Straight down: no horizontal travel at all.
    let text = c.ok("move_to", json!({"x": 9, "z": 4, "mode": "run"})).await;
    assert_eq!(text, "Arrived at (9, 0, 4) (run).");
    let text = c.ok("move_to", json!({"x": 60, "z": 4, "wait": false})).await;
    assert!(text.contains("Not waiting"), "{text}");
    tokio::time::sleep(Duration::from_millis(300)).await;
    let text = c.ok("stop", json!({})).await;
    assert!(text.starts_with("Stopped at ("), "{text}");

    let text = c
        .ok(
            "create_object",
            json!({"shape": "box", "position": [1, 0.5, 1], "color": "#ff8000", "name": "crate"}),
        )
        .await;
    assert!(text.starts_with("Created object ") && text.contains("colour #ff8000"), "{text}");
    let id: u64 = text["Created object ".len()..].split(' ').next().unwrap().parse().unwrap();
    let text = c.ok("list_objects", json!({"owner": "me"})).await;
    assert!(text.contains(&format!("object {id} \"crate\"")), "{text}");
    let text = c.ok("edit_object", json!({"id": id.to_string(), "scale": 2, "locked": true})).await;
    assert!(text.contains("size (2, 2, 2)") && text.contains("locked"), "{text}");
    let text = c.ok("delete_object", json!({"id": id})).await;
    assert_eq!(text, format!("Deleted object {id}."));
    let text = c.fails("delete_object", json!({"id": id})).await;
    assert!(text.contains("ERROR_CODE_NOT_FOUND"), "{text}");

    c.shutdown().await;
}

#[tokio::test]
async fn bridge_works_over_wss_with_a_ca_file_and_reconnects() {
    let Some(bin) = server_binary() else { return };
    let mut world = World::start(&bin, true).await;
    let mut c = Client::start(world.config());
    c.initialize().await;

    let text = c.ok("who", json!({})).await;
    assert!(text.contains("you: Scout (agent, builder)"), "{text}");
    let text = c.ok("say", json!({"text": "hello over TLS", "channel": "world"})).await;
    assert!(text.contains("whole world"), "{text}");
    let text = c.ok("move_to", json!({"x": 2, "z": 1})).await;
    assert_eq!(text, "Arrived at (2, 0, 1) (walk).");
    let text = c.ok("create_object", json!({"shape": "sphere", "position": [0, 1, 3]})).await;
    assert!(text.starts_with("Created object "), "{text}");
    let text = c.ok("recent_chat", json!({"include_events": false})).await;
    assert!(text.contains("Scout (you): hello over TLS"), "{text}");

    // Restart the server: the bridge reconnects over TLS by itself.
    world.stop();
    tokio::time::sleep(Duration::from_millis(300)).await;
    world.run().await;
    let mut text = String::new();
    for _ in 0..50 {
        let (t, err) = c.call("who", json!({})).await;
        if !err {
            text = t;
            break;
        }
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
    assert!(text.contains("you: Scout"), "never reconnected over TLS: {text}");
    let log = c.ok("recent_chat", json!({})).await;
    assert!(log.contains("lost the connection") && log.contains("connected to world"), "{log}");
    let text = c.ok("list_objects", json!({})).await;
    assert!(text.contains("sphere"), "objects persist across the restart: {text}");
    c.shutdown().await;
}

#[tokio::test]
async fn tls_mismatches_give_readable_errors() {
    let Some(bin) = server_binary() else { return };
    let tls = World::start(&bin, true).await;
    let plain = World::start(&bin, false).await;

    // A self-signed server without --ca-file is refused, and says what to do.
    let mut cfg = tls.config();
    cfg.ca_certs.clear();
    let mut c = Client::start(cfg);
    for tool in ["who", "look"] {
        let text = c.fails(tool, json!({})).await;
        assert!(text.contains("invalid peer certificate") && text.contains("--ca-file"), "{text}");
    }
    c.shutdown().await;

    // The wrong CA: not trusted either.
    let other = World::start(&bin, true).await;
    let mut cfg = tls.config();
    cfg.ca_certs = tv_mcp::tls::load_ca_file(&other.cert()).unwrap();
    let mut c = Client::start(cfg);
    let text = c.fails("who", json!({})).await;
    assert!(text.contains("not signed by the --ca-file"), "{text}");
    c.shutdown().await;

    // ws:// to the TLS port.
    let mut cfg = tls.config();
    cfg.url = tls.url("ws");
    cfg.ca_certs.clear();
    let mut c = Client::start(cfg);
    let text = c.fails("who", json!({})).await;
    assert!(text.contains("use a wss:// URL"), "{text}");
    c.shutdown().await;

    // wss:// to a plain port.
    let mut cfg = plain.config();
    cfg.url = plain.url("wss");
    let mut c = Client::start(cfg);
    let text = c.fails("who", json!({})).await;
    assert!(text.contains("try a ws:// URL"), "{text}");
    c.shutdown().await;
}

#[test]
fn ca_file_start_up_errors() {
    let dir = tempfile::tempdir().unwrap();
    let not_pem = dir.path().join("not.pem");
    std::fs::write(&not_pem, "hello").unwrap();
    let missing = dir.path().join("missing.pem");
    let cases: [(&[&str], &str); 3] = [
        (
            &["--url", "ws://127.0.0.1:1/ws", "--ca-file", not_pem.to_str().unwrap()],
            "only applies to a wss:// URL",
        ),
        (
            &["--url", "wss://127.0.0.1:1/ws", "--ca-file", missing.to_str().unwrap()],
            "cannot read CA file",
        ),
        (
            &["--url", "wss://127.0.0.1:1/ws", "--ca-file", not_pem.to_str().unwrap()],
            "holds no PEM certificates",
        ),
    ];
    for (args, want) in cases {
        let out = Command::new(env!("CARGO_BIN_EXE_tv-mcp"))
            .args(args)
            .stdin(Stdio::null())
            .output()
            .unwrap();
        assert_eq!(out.status.code(), Some(2), "{args:?}");
        assert!(out.stdout.is_empty(), "{args:?}: nothing on stdout");
        let err = String::from_utf8_lossy(&out.stderr);
        assert!(err.contains(want), "{args:?}: wanted {want:?} in {err}");
    }
    // The environment variable is checked the same way.
    let out = Command::new(env!("CARGO_BIN_EXE_tv-mcp"))
        .args(["--url", "ws://127.0.0.1:1/ws"])
        .env("TINYVERSE_CA_FILE", &not_pem)
        .stdin(Stdio::null())
        .output()
        .unwrap();
    assert_eq!(out.status.code(), Some(2));
    assert!(String::from_utf8_lossy(&out.stderr).contains("TINYVERSE_CA_FILE"));
}
