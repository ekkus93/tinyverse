//! Runs the bridge against the real `tinyverse-server` binary, with a second
//! participant present, so the protocol assumptions stay verified.
//!
//! The server binary is looked up next to `tv-mcp` in the cargo target
//! directory. It is built by `cargo build --workspace` (or `cargo test
//! --workspace`); when it is missing the test is skipped with a message.

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
    bin.is_file().then_some(bin)
}

/// Stops the server with SIGTERM when dropped, killing it if it lingers.
struct ServerProcess(Child);

impl Drop for ServerProcess {
    fn drop(&mut self) {
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

fn free_port() -> u16 {
    std::net::TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

fn to_ws(msg: &ClientMessage) -> Message {
    match tv_proto::encode(msg, Encoding::Json) {
        Frame::Text(t) => Message::text(t),
        Frame::Binary(b) => Message::binary(b),
    }
}

#[tokio::test]
async fn bridge_works_against_the_real_server() {
    let Some(bin) = server_binary() else {
        eprintln!(
            "SKIPPED: tinyverse-server not found next to tv-mcp in the target directory; \
             run `cargo build --workspace` first"
        );
        return;
    };
    let dir = tempfile::tempdir().unwrap();
    let world = dir.path().join("world");
    let run = |args: &[&str]| {
        let out = Command::new(&bin).args(args).output().unwrap();
        assert!(out.status.success(), "{args:?}: {}", String::from_utf8_lossy(&out.stderr));
        String::from_utf8(out.stdout).unwrap()
    };
    run(&["init", world.to_str().unwrap()]);
    let out = run(&["token", "create", world.to_str().unwrap(), "--name", "Scout"]);
    let token = out
        .lines()
        .find_map(|l| l.strip_prefix("token:"))
        .map(|t| t.trim().to_string())
        .unwrap_or_else(|| panic!("no token in {out:?}"));

    let port = free_port();
    let _server = ServerProcess(
        Command::new(&bin)
            .args(["run", world.to_str().unwrap(), "--port", &port.to_string()])
            .args(["--bind", "127.0.0.1"])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap(),
    );
    let url = format!("ws://127.0.0.1:{port}/ws");
    let mut up = false;
    for _ in 0..100 {
        if tokio::net::TcpStream::connect(("127.0.0.1", port)).await.is_ok() {
            up = true;
            break;
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    assert!(up, "server did not start listening");

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

    let mut cfg = tv_mcp::Config::new(url.as_str());
    cfg.token = Some(token);
    let mut c = Client::start(cfg);
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
