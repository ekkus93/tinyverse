//! End-to-end tests: the MCP server runs in-process over in-memory pipes and
//! talks to a scripted mock world server over a real WebSocket.

mod common;

use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use common::*;
use serde_json::{Value, json};
use tv_proto::v1::{
    AvatarStates, ChatChannel, ChatMessage, ErrorCode, MoveMode, ObjectCreated, ObjectDeleted,
    ObjectUpdated, Observation, avatar_states, client_message::Body as C, observation,
    server_message::Body as S,
};

const TOOLS: [&str; 12] = [
    "look",
    "move_to",
    "stop",
    "say",
    "recent_chat",
    "wait_for_chat",
    "who",
    "create_object",
    "edit_object",
    "delete_object",
    "list_objects",
    "world_info",
];

fn modern_meta() -> Value {
    json!({
        "io.modelcontextprotocol/protocolVersion": "2026-07-28",
        "io.modelcontextprotocol/clientInfo": {"name": "test", "version": "0"},
        "io.modelcontextprotocol/clientCapabilities": {}
    })
}

// ------------------------------------------------------------ MCP protocol

#[tokio::test]
async fn initialize_negotiates_a_legacy_revision() {
    let mut c = Client::start(config("ws://127.0.0.1:1/ws"));
    let r = c.initialize().await;
    let result = &r["result"];
    assert_eq!(result["protocolVersion"], "2025-11-25");
    assert_eq!(result["capabilities"]["tools"]["listChanged"], false);
    assert_eq!(result["serverInfo"]["name"], "tv-mcp");
    assert!(result["instructions"].as_str().unwrap().contains("look"));

    // An older revision the server knows is echoed back; an unknown one gets the newest.
    let r =
        c.request("initialize", json!({"protocolVersion": "2025-06-18", "capabilities": {}})).await;
    assert_eq!(r["result"]["protocolVersion"], "2025-06-18");
    let r =
        c.request("initialize", json!({"protocolVersion": "1999-01-01", "capabilities": {}})).await;
    assert_eq!(r["result"]["protocolVersion"], "2025-11-25");

    // notifications/initialized got no reply: the next message is the ping's.
    let r = c.request("ping", json!({})).await;
    assert_eq!(r["result"], json!({}));
    c.shutdown().await;
}

#[tokio::test]
async fn modern_requests_carry_their_version_in_meta() {
    let mut c = Client::start(config("ws://127.0.0.1:1/ws"));
    let r = c.request("server/discover", json!({"_meta": modern_meta()})).await;
    let result = &r["result"];
    assert_eq!(result["resultType"], "complete");
    let versions: Vec<&str> = result["supportedVersions"]
        .as_array()
        .unwrap()
        .iter()
        .map(|v| v.as_str().unwrap())
        .collect();
    assert_eq!(versions[0], "2026-07-28");
    assert!(versions.contains(&"2025-11-25"));
    assert!(result["capabilities"]["tools"].is_object());
    assert_eq!(result["_meta"]["io.modelcontextprotocol/serverInfo"]["name"], "tv-mcp");

    let r = c.request("tools/list", json!({"_meta": modern_meta()})).await;
    assert_eq!(r["result"]["resultType"], "complete");
    assert_eq!(r["result"]["tools"].as_array().unwrap().len(), TOOLS.len());

    let mut meta = modern_meta();
    meta["io.modelcontextprotocol/protocolVersion"] = json!("1900-01-01");
    let r = c.request("tools/list", json!({"_meta": meta})).await;
    assert_eq!(r["error"]["code"], -32022);
    assert_eq!(r["error"]["data"]["requested"], "1900-01-01");
    assert_eq!(r["error"]["data"]["supported"][0], "2026-07-28");

    let mut meta = modern_meta();
    meta.as_object_mut().unwrap().remove("io.modelcontextprotocol/clientCapabilities");
    let r = c.request("tools/list", json!({"_meta": meta})).await;
    assert_eq!(r["error"]["code"], -32602);

    // A tool call in the modern era also gets resultType.
    let r = c
        .request("tools/call", json!({"name": "who", "arguments": {}, "_meta": modern_meta()}))
        .await;
    assert_eq!(r["result"]["resultType"], "complete");
    assert_eq!(r["result"]["isError"], true);
    c.shutdown().await;
}

/// A minimal structural check of the JSON Schema subset the tools use.
fn check_schema(tool: &str, path: &str, s: &Value) {
    let obj = s.as_object().unwrap_or_else(|| panic!("{tool}{path}: schema is not an object"));
    const KNOWN: &[&str] = &[
        "type",
        "description",
        "properties",
        "required",
        "additionalProperties",
        "enum",
        "minimum",
        "maximum",
        "exclusiveMinimum",
        "minLength",
        "anyOf",
    ];
    for k in obj.keys() {
        assert!(KNOWN.contains(&k.as_str()), "{tool}{path}: unexpected keyword {k}");
    }
    if let Some(t) = obj.get("type") {
        let types: Vec<&Value> = match t {
            Value::Array(a) => a.iter().collect(),
            other => vec![other],
        };
        for t in types {
            let t = t.as_str().unwrap_or_else(|| panic!("{tool}{path}: type must be a string"));
            assert!(
                ["object", "string", "number", "integer", "boolean", "array"].contains(&t),
                "{tool}{path}: bad type {t}"
            );
        }
    } else {
        assert!(obj.contains_key("anyOf"), "{tool}{path}: no type and no anyOf");
    }
    if let Some(any) = obj.get("anyOf") {
        for (i, sub) in any.as_array().expect("anyOf array").iter().enumerate() {
            check_schema(tool, &format!("{path}/anyOf/{i}"), sub);
        }
    }
    if let Some(e) = obj.get("enum") {
        assert!(e.as_array().is_some_and(|a| !a.is_empty() && a.iter().all(Value::is_string)));
    }
    if let Some(d) = obj.get("description") {
        assert!(d.as_str().is_some_and(|d| !d.is_empty()), "{tool}{path}: empty description");
    }
    if let Some(props) = obj.get("properties") {
        let props = props.as_object().expect("properties object");
        for (name, p) in props {
            check_schema(tool, &format!("{path}/{name}"), p);
        }
        for r in obj.get("required").and_then(Value::as_array).into_iter().flatten() {
            let r = r.as_str().expect("required entries are strings");
            assert!(props.contains_key(r), "{tool}{path}: required {r} is not a property");
        }
    }
    if let Some(a) = obj.get("additionalProperties") {
        assert!(a.is_boolean());
    }
}

#[tokio::test]
async fn tools_list_returns_every_tool_with_a_valid_schema() {
    let mut c = Client::start(config("ws://127.0.0.1:1/ws"));
    c.initialize().await;
    let r = c.request("tools/list", json!({})).await;
    assert!(r["result"].get("resultType").is_none(), "legacy replies stay legacy");
    let tools = r["result"]["tools"].as_array().unwrap();
    let names: Vec<&str> = tools.iter().map(|t| t["name"].as_str().unwrap()).collect();
    assert_eq!(names, TOOLS);
    for t in tools {
        let name = t["name"].as_str().unwrap();
        assert!(t["description"].as_str().unwrap().len() > 40, "{name}: description too short");
        let schema = &t["inputSchema"];
        assert_eq!(schema["type"], "object", "{name}");
        assert!(schema["properties"].is_object(), "{name}");
        assert_eq!(schema["additionalProperties"], false, "{name}");
        check_schema(name, "", schema);
    }
    // The coordinate convention is spelled out where positions are given.
    for name in ["look", "move_to", "create_object", "edit_object"] {
        let d = tools.iter().find(|t| t["name"] == name).unwrap()["description"].as_str().unwrap();
        assert!(d.contains("Y-up") && d.contains("clockwise"), "{name}: {d}");
    }
    let create = tools.iter().find(|t| t["name"] == "create_object").unwrap();
    let rot =
        create["inputSchema"]["properties"]["rotation_euler_deg"]["description"].as_str().unwrap();
    assert!(rot.contains("z (roll) first"), "Euler order documented: {rot}");
    c.shutdown().await;
}

#[tokio::test]
async fn protocol_errors_follow_json_rpc() {
    let mut c = Client::start(config("ws://127.0.0.1:1/ws"));
    c.initialize().await;

    let r = c.request("resources/list", json!({})).await;
    assert_eq!(r["error"]["code"], -32601);

    let r = c.request("tools/call", json!({"name": "teleport", "arguments": {}})).await;
    assert_eq!(r["error"]["code"], -32602);
    assert!(r["error"]["message"].as_str().unwrap().contains("teleport"));

    let r = c.request("tools/call", json!({"arguments": {}})).await;
    assert_eq!(r["error"]["code"], -32602);
    let r = c.request("tools/call", json!({"name": "who", "arguments": [1]})).await;
    assert_eq!(r["error"]["code"], -32602);

    c.send_raw("{not json").await;
    let r = c.recv().await;
    assert_eq!(r["error"]["code"], -32700);
    assert_eq!(r["id"], Value::Null);

    c.send(json!({"id": 99, "method": "ping"})).await;
    let r = c.response(99).await;
    assert_eq!(r["error"]["code"], -32600);

    c.send(json!([{"jsonrpc": "2.0", "id": 98, "method": "ping"}])).await;
    let r = c.recv().await;
    assert_eq!(r["error"]["code"], -32600);

    // Unknown notifications are ignored; string ids work.
    c.send(json!({"jsonrpc": "2.0", "method": "notifications/whatever"})).await;
    c.send(json!({"jsonrpc": "2.0", "id": "abc", "method": "ping"})).await;
    let r = c.recv().await;
    assert_eq!(r["id"], "abc");
    assert_eq!(r["result"], json!({}));
    c.shutdown().await;
}

#[tokio::test]
async fn cancelled_requests_get_no_reply() {
    let (_world, _conn, mut c) = connected().await;
    c.ok("recent_chat", json!({})).await;
    let id = c.start_call("wait_for_chat", json!({"timeout_s": 5})).await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    c.send(json!({"jsonrpc": "2.0", "method": "notifications/cancelled",
                  "params": {"requestId": id, "reason": "test"}}))
        .await;
    // The next message is the ping's reply, not the cancelled call's.
    let ping = c.start_request("ping", json!({})).await;
    let r = c.recv().await;
    assert_eq!(r["id"], ping);
    c.shutdown().await;
}

// ------------------------------------------------------------ tools

#[tokio::test]
async fn look_sends_observe_and_renders_the_observation() {
    let (_world, mut conn, mut c) = connected().await;
    let id = c.start_call("look", json!({"radius": 15})).await;
    let msg = conn.recv().await;
    let Some(C::Observe(o)) = msg.body else { panic!("expected Observe, got {msg:?}") };
    assert_eq!(o.radius, 15.0);
    let script = Script::default();
    conn.send(
        msg.seq,
        S::Observation(Observation {
            self_: script.welcome.avatar.clone(),
            avatars: vec![observation::SeenAvatar {
                avatar: Some(script.snapshot.avatars[0].clone()),
                distance: 5.0,
                bearing: 36.9,
            }],
            objects: vec![observation::SeenObject {
                object: Some(script.snapshot.objects[0].clone()),
                distance: 10.0,
                bearing: -2.0,
            }],
            summary: "Ada is 5 m away to your right. A door is ahead.".into(),
            truncated: false,
            radius: 15.0,
        }),
    );
    let (text, err) = c.finish_call(id).await;
    assert!(!err, "{text}");
    assert!(text.starts_with("Ada is 5 m away to your right."), "{text}");
    assert!(text.contains("Avatars within 15 m (1)"), "{text}");
    assert!(text.contains("Ada (human, owner), avatar 3: 5 m, 37° right"), "{text}");
    assert!(text.contains("object 7 \"door\": box at (0, 1, 10)"), "{text}");
    assert!(text.contains("colour #ff0000"), "{text}");
    assert!(text.contains("owner Ada"), "{text}");
    assert!(text.contains("10 m, ahead"), "{text}");
    c.shutdown().await;
}

fn me_at(conn: &MockConn, x: f32, z: f32, speed: f32) {
    conn.send(
        0,
        S::AvatarStates(AvatarStates {
            server_time_ms: 0,
            states: vec![avatar_states::Entry {
                avatar_id: ME,
                transform: Some(transform(v(x, 0.0, z), v(speed, 0.0, 0.0))),
            }],
        }),
    );
}

#[tokio::test]
async fn move_to_waits_for_arrival() {
    let (_world, mut conn, mut c) = connected().await;
    let id = c.start_call("move_to", json!({"x": 10, "y": 5, "z": 0, "mode": "run"})).await;
    let msg = conn.recv().await;
    let Some(C::MoveTo(m)) = &msg.body else { panic!("expected MoveTo, got {msg:?}") };
    assert_eq!(m.mode, MoveMode::Run as i32);
    let t = m.target.unwrap();
    assert_eq!((t.x, t.z), (10.0, 0.0));
    me_at(&conn, 4.0, 0.0, 8.0);
    me_at(&conn, 8.0, 0.0, 8.0);
    // Close but still moving is not arrived; the server's last state is.
    me_at(&conn, 9.7, 0.0, 8.0);
    tokio::time::sleep(Duration::from_millis(50)).await;
    me_at(&conn, 10.0, 0.0, 0.0);
    let (text, err) = c.finish_call(id).await;
    assert!(!err, "{text}");
    assert_eq!(text, "Arrived at (10, 0, 0) (run).");
    c.shutdown().await;
}

#[tokio::test]
async fn move_to_reports_a_timeout_and_an_early_stop() {
    let (_world, mut conn, mut c) = connected().await;
    let started = Instant::now();
    let id = c.start_call("move_to", json!({"x": 20, "z": 0, "timeout_s": 0.5})).await;
    let msg = conn.recv().await;
    assert!(matches!(msg.body, Some(C::MoveTo(_))));
    me_at(&conn, 1.0, 0.0, 4.0);
    let (text, err) = c.finish_call(id).await;
    assert!(!err, "{text}");
    assert!(
        text.starts_with("Not there yet after 0.5 s: now at (1, 0, 0), 19 m from (20, 0)"),
        "{text}"
    );
    assert!(started.elapsed() < Duration::from_secs(3));

    // The server ends the move short of the target (velocity drops to zero).
    let id = c.start_call("move_to", json!({"x": 20, "z": 0})).await;
    conn.recv().await;
    me_at(&conn, 3.0, 0.0, 4.0);
    me_at(&conn, 5.0, 0.0, 0.0);
    let (text, err) = c.finish_call(id).await;
    assert!(!err, "{text}");
    assert!(text.starts_with("Stopped at (5, 0, 0), 15 m short"), "{text}");

    // wait=false returns at once.
    let (text, err) = c.call("move_to", json!({"x": -3, "z": 2, "wait": false})).await;
    assert!(!err, "{text}");
    assert!(text.contains("Not waiting"), "{text}");
    assert!(matches!(conn.recv().await.body, Some(C::MoveTo(_))));
    c.shutdown().await;
}

#[tokio::test]
async fn move_to_checks_arguments_and_reports_server_errors() {
    let (_world, mut conn, mut c) =
        connected_with(Script { auto_reply: false, ..Script::default() }).await;
    let text = c.fails("move_to", json!({"x": 1})).await;
    assert!(text.contains("`z` is required"), "{text}");
    let text = c.fails("move_to", json!({"x": 1000, "z": 0})).await;
    assert!(text.contains("outside the world bounds"), "{text}");
    let text = c.fails("move_to", json!({"x": 1, "z": 1, "mode": "swim"})).await;
    assert!(text.contains("walk, run, fly"), "{text}");
    let text = c.fails("move_to", json!({"x": 1, "z": 1, "timeout_s": 500})).await;
    assert!(text.contains("at most 120"), "{text}");
    let text = c.fails("move_to", json!({"x": "east", "z": 1})).await;
    assert!(text.contains("`x` must be a number"), "{text}");
    conn.expect_silence(Duration::from_millis(200)).await;

    let id = c.start_call("move_to", json!({"x": 1, "z": 1, "mode": "fly", "y": 300})).await;
    let (text, err) = c.finish_call(id).await;
    assert!(err && text.contains("outside the world bounds"), "{text}");

    let id = c.start_call("move_to", json!({"x": 1, "z": 1})).await;
    let msg = conn.recv().await;
    conn.send(
        msg.seq,
        S::Error(tv_proto::v1::Error { code: ErrorCode::OutOfBounds as i32, message: "no".into() }),
    );
    let (text, err) = c.finish_call(id).await;
    assert!(err, "{text}");
    assert!(text.contains("ERROR_CODE_OUT_OF_BOUNDS: no"), "{text}");
    c.shutdown().await;
}

#[tokio::test]
async fn stop_sends_stop() {
    let (_world, mut conn, mut c) = connected().await;
    let text = c.ok("stop", json!({})).await;
    assert_eq!(text, "Stopped at (0, 0, 0).");
    assert!(matches!(conn.recv().await.body, Some(C::Stop(_))));
    let text = c.fails("stop", json!({"now": true})).await;
    assert!(text.contains("unknown argument `now`"), "{text}");
    c.shutdown().await;
}

#[tokio::test]
async fn say_sends_chat_on_the_right_channel() {
    let (_world, mut conn, mut c) =
        connected_with(Script { auto_reply: false, ..Script::default() }).await;
    let id = c.start_call("say", json!({"text": "  hello there "})).await;
    let msg = conn.recv().await;
    let Some(C::Chat(ch)) = &msg.body else { panic!("expected Chat, got {msg:?}") };
    assert_eq!(ch.channel, ChatChannel::Local as i32);
    assert_eq!(ch.text, "hello there");
    // Echo like a server would, with reply_to on our copy.
    conn.send(
        msg.seq,
        S::ChatMessage(ChatMessage { entry: Some(chat(ME, ME_USER, "Scout", 1, "hello there")) }),
    );
    let (text, err) = c.finish_call(id).await;
    assert!(!err);
    assert_eq!(text, "You said (heard within 20 m): hello there");

    let id = c.start_call("say", json!({"text": "hi all", "channel": "world"})).await;
    let msg = conn.recv().await;
    let Some(C::Chat(ch)) = &msg.body else { panic!("expected Chat, got {msg:?}") };
    assert_eq!(ch.channel, ChatChannel::World as i32);
    conn.send(
        msg.seq,
        S::ChatMessage(ChatMessage { entry: Some(chat(ME, ME_USER, "Scout", 2, "hi all")) }),
    );
    let (text, err) = c.finish_call(id).await;
    assert!(!err && text.contains("whole world"), "{text}");

    // No answer at all is reported, not mistaken for success.
    let id = c.start_call("say", json!({"text": "hello?"})).await;
    conn.recv().await;
    let (text, err) = c.finish_call(id).await;
    assert!(err && text.contains("did not answer"), "{text}");

    let text = c.fails("say", json!({"text": "x".repeat(501)})).await;
    assert!(text.contains("at most 500"), "{text}");
    let text = c.fails("say", json!({"text": "   "})).await;
    assert!(text.contains("empty"), "{text}");
    let text = c.fails("say", json!({})).await;
    assert!(text.contains("`text` is required"), "{text}");

    let id = c.start_call("say", json!({"text": "spam"})).await;
    let msg = conn.recv().await;
    conn.send(
        msg.seq,
        S::Error(tv_proto::v1::Error {
            code: ErrorCode::RateLimited as i32,
            message: "slow down".into(),
        }),
    );
    let (text, err) = c.finish_call(id).await;
    assert!(err && text.contains("ERROR_CODE_RATE_LIMITED: slow down"), "{text}");
    c.shutdown().await;
}

#[tokio::test]
async fn recent_chat_returns_each_line_once() {
    let (_world, conn, mut c) = connected().await;
    // The join history is the first unread line.
    let text = c.ok("recent_chat", json!({"include_events": false})).await;
    assert!(text.starts_with("1 new line(s)"), "{text}");
    assert!(text.contains("[world] Ada: welcome, everyone"), "{text}");
    let text = c.ok("recent_chat", json!({})).await;
    assert_eq!(text, "No new chat since you last checked.");

    conn.send(
        0,
        S::ChatMessage(ChatMessage { entry: Some(chat(ADA, ADA_USER, "Ada", 1, "hi Scout")) }),
    );
    conn.send(
        0,
        S::ChatMessage(ChatMessage { entry: Some(chat(ADA, ADA_USER, "Ada", 1, "you there?")) }),
    );
    tokio::time::sleep(Duration::from_millis(100)).await;
    let text = c.ok("recent_chat", json!({"limit": 1})).await;
    assert!(text.contains("you there?") && !text.contains("hi Scout"), "{text}");
    assert!(text.contains("1 older line(s) not shown"), "{text}");
    let text = c.ok("recent_chat", json!({})).await;
    assert_eq!(text, "No new chat since you last checked.");

    let text = c.ok("recent_chat", json!({"since_last": false, "include_events": false})).await;
    assert!(text.starts_with("Last 3 line(s)"), "{text}");
    let lines: Vec<&str> = text.lines().skip(1).collect();
    assert!(
        lines[0].contains("welcome") && lines[2].contains("you there?"),
        "oldest first: {text}"
    );

    let text = c.fails("recent_chat", json!({"limit": 0})).await;
    assert!(text.contains("from 1 to 300"), "{text}");
    c.shutdown().await;
}

#[tokio::test]
async fn recent_chat_includes_notable_events() {
    let (_world, conn, mut c) = connected().await;
    c.ok("recent_chat", json!({})).await;
    conn.send(
        0,
        S::ObjectCreated(ObjectCreated {
            object: Some(object(50, ADA_USER, "tower", v(5.0, 2.0, 5.0))),
            by_avatar_id: ADA,
        }),
    );
    for _ in 0..5 {
        conn.send(
            0,
            S::ObjectUpdated(ObjectUpdated {
                object: Some(object(50, ADA_USER, "tower", v(5.0, 3.0, 5.0))),
                by_avatar_id: ADA,
            }),
        );
    }
    conn.send(0, S::AvatarLeft(tv_proto::v1::AvatarLeft { avatar_id: ADA }));
    tokio::time::sleep(Duration::from_millis(100)).await;
    let text = c.ok("recent_chat", json!({})).await;
    assert!(text.contains("* Ada created object 50 (box \"tower\") at (5, 2, 5)"), "{text}");
    assert_eq!(text.matches("changed object 50").count(), 1, "updates coalesce: {text}");
    assert!(text.contains("* Ada left"), "{text}");
    c.shutdown().await;
}

#[tokio::test]
async fn wait_for_chat_wakes_on_chat_and_times_out() {
    let (_world, conn, mut c) = connected().await;
    c.ok("recent_chat", json!({})).await;

    // Times out, and our own lines do not wake it.
    let started = Instant::now();
    let id = c.start_call("wait_for_chat", json!({"timeout_s": 0.5})).await;
    conn.send(
        0,
        S::ChatMessage(ChatMessage { entry: Some(chat(ME, ME_USER, "Scout", 1, "anyone?")) }),
    );
    let (text, err) = c.finish_call(id).await;
    assert!(!err);
    assert!(text.starts_with("No one said anything you could hear in 0.5 s."), "{text}");
    assert!(text.contains("Scout (you): anyone?"), "{text}");
    assert!(started.elapsed() >= Duration::from_millis(450));

    // Wakes as soon as someone else speaks.
    let started = Instant::now();
    let id = c.start_call("wait_for_chat", json!({"timeout_s": 20})).await;
    tokio::time::sleep(Duration::from_millis(200)).await;
    conn.send(
        0,
        S::ChatMessage(ChatMessage { entry: Some(chat(ADA, ADA_USER, "Ada", 1, "yes, hi!")) }),
    );
    let (text, err) = c.finish_call(id).await;
    assert!(!err);
    assert!(text.starts_with("New chat:"), "{text}");
    assert!(text.contains("[local] Ada: yes, hi!"), "{text}");
    assert!(started.elapsed() < Duration::from_secs(5));

    // The line was returned, so recent_chat has nothing new.
    assert_eq!(c.ok("recent_chat", json!({})).await, "No new chat since you last checked.");

    // Unread lines from others return at once.
    conn.send(
        0,
        S::ChatMessage(ChatMessage { entry: Some(chat(ADA, ADA_USER, "Ada", 1, "hello?")) }),
    );
    tokio::time::sleep(Duration::from_millis(100)).await;
    let started = Instant::now();
    let text = c.ok("wait_for_chat", json!({})).await;
    assert!(text.contains("hello?"), "{text}");
    assert!(started.elapsed() < Duration::from_secs(1));

    let text = c.fails("wait_for_chat", json!({"timeout_s": 121})).await;
    assert!(text.contains("at most 120"), "{text}");
    c.shutdown().await;
}

#[tokio::test]
async fn who_lists_avatars_with_distance() {
    let (_world, _conn, mut c) = connected().await;
    let text = c.ok("who", json!({})).await;
    assert!(text.starts_with("2 present, including you:"), "{text}");
    assert!(text.contains("- you: Scout (agent, builder), avatar 100, at (0, 0, 0)"), "{text}");
    assert!(
        text.contains("- Ada (human, owner), avatar 3: 5 m, 37° right, at (3, 0, 4)"),
        "{text}"
    );
    c.shutdown().await;
}

#[tokio::test]
async fn world_info_reports_world_role_and_limits() {
    let (_world, _conn, mut c) = connected().await;
    let text = c.ok("world_info", json!({})).await;
    for want in [
        "World: Test Island",
        "Description: a small island",
        "Bounds: (-128, -16, -128) to (128, 256, 128)",
        "Spawn point: (0, 0, 0)",
        "Your role: builder",
        "chat 500 characters",
        "look radius up to 100 m",
    ] {
        assert!(text.contains(want), "missing {want:?} in {text}");
    }
    c.shutdown().await;
}

#[tokio::test]
async fn create_edit_and_delete_objects() {
    let (_world, mut conn, mut c) = connected().await;
    let id = c
        .start_call(
            "create_object",
            json!({
                "shape": "box",
                "position": {"x": 1, "y": 0.5, "z": 2},
                "scale": [2, 1, 1],
                "rotation_euler_deg": {"x": 0, "y": 90, "z": 0},
                "color": "#ff0000",
                "material": "glossy",
                "name": "crate",
                "locked": true
            }),
        )
        .await;
    let msg = conn.recv().await;
    let Some(C::ObjectCreate(create)) = &msg.body else { panic!("expected ObjectCreate: {msg:?}") };
    let spec = create.spec.clone().unwrap();
    assert_eq!(spec.shape, 1);
    assert_eq!(spec.name, "crate");
    assert_eq!(spec.position.unwrap(), v(1.0, 0.5, 2.0));
    assert_eq!(spec.scale.unwrap(), v(2.0, 1.0, 1.0));
    let q = spec.rotation.unwrap();
    assert!(
        (q.y - std::f32::consts::FRAC_1_SQRT_2).abs() < 1e-4
            && (q.w - std::f32::consts::FRAC_1_SQRT_2).abs() < 1e-4
            && q.x == 0.0,
        "{q:?}"
    );
    let col = spec.color.unwrap();
    assert_eq!((col.r, col.g, col.b, col.a), (1.0, 0.0, 0.0, 1.0));
    assert_eq!(spec.material, 2);
    assert!(spec.solid && spec.locked);
    conn.send(
        msg.seq,
        S::ObjectCreated(ObjectCreated {
            object: Some(tv_proto::v1::ObjectInfo {
                id: 9,
                owner_id: ME_USER.into(),
                spec: Some(spec),
            }),
            by_avatar_id: ME,
        }),
    );
    let (text, err) = c.finish_call(id).await;
    assert!(!err, "{text}");
    assert!(
        text.starts_with("Created object 9 \"crate\": box at (1, 0.5, 2), size (2, 1, 1)"),
        "{text}"
    );
    assert!(text.contains("rotation (0, 90, 0)°") && text.contains("owner you"), "{text}");
    assert!(text.contains("glossy, solid, locked"), "{text}");

    // Own creations are not "notable events by others".
    let log = c.ok("recent_chat", json!({"since_last": false})).await;
    assert!(!log.contains("created object 9"), "{log}");

    // Ids as strings or numbers.
    for id_arg in [json!("9"), json!(9)] {
        let call =
            c.start_call("edit_object", json!({"id": id_arg, "scale": 3, "colour": "navy"})).await;
        let msg = conn.recv().await;
        let Some(C::ObjectUpdate(u)) = &msg.body else { panic!("expected ObjectUpdate: {msg:?}") };
        assert_eq!(u.id, 9);
        assert_eq!(u.scale.unwrap(), v(3.0, 3.0, 3.0));
        assert!(u.color.is_some());
        assert!(
            u.position.is_none() && u.name.is_none() && u.solid.is_none() && u.rotation.is_none()
        );
        let mut obj = object(9, ME_USER, "crate", v(1.0, 0.5, 2.0));
        obj.spec.as_mut().unwrap().scale = u.scale;
        obj.spec.as_mut().unwrap().color = u.color;
        conn.send(msg.seq, S::ObjectUpdated(ObjectUpdated { object: Some(obj), by_avatar_id: ME }));
        let (text, err) = c.finish_call(call).await;
        assert!(!err, "{text}");
        assert!(text.starts_with("Updated object 9") && text.contains("size (3, 3, 3)"), "{text}");
        assert!(text.contains("colour #000080"), "{text}");
    }

    let call = c.start_call("delete_object", json!({"id": "9"})).await;
    let msg = conn.recv().await;
    let Some(C::ObjectDelete(d)) = &msg.body else { panic!("expected ObjectDelete: {msg:?}") };
    assert_eq!(d.id, 9);
    conn.send(msg.seq, S::ObjectDeleted(ObjectDeleted { id: 9, by_avatar_id: ME }));
    let (text, err) = c.finish_call(call).await;
    assert!(!err);
    assert_eq!(text, "Deleted object 9.");
    let list = c.ok("list_objects", json!({"owner": "me"})).await;
    assert!(!list.contains("object 9"), "{list}");
    c.shutdown().await;
}

#[tokio::test]
async fn server_errors_become_tool_errors() {
    let (_world, mut conn, mut c) = connected().await;
    let call = c.start_call("delete_object", json!({"id": 7})).await;
    let msg = conn.recv().await;
    conn.send(
        msg.seq,
        S::Error(tv_proto::v1::Error {
            code: ErrorCode::PermissionDenied as i32,
            message: "object 7 is locked by Ada".into(),
        }),
    );
    let (text, err) = c.finish_call(call).await;
    assert!(err);
    assert_eq!(
        text,
        "The world refused to delete object 7: ERROR_CODE_PERMISSION_DENIED: object 7 is locked by Ada"
    );

    let call = c.start_call("look", json!({})).await;
    let msg = conn.recv().await;
    conn.send(
        msg.seq,
        S::Error(tv_proto::v1::Error { code: ErrorCode::Internal as i32, message: "oops".into() }),
    );
    let (text, err) = c.finish_call(call).await;
    assert!(err && text.contains("ERROR_CODE_INTERNAL: oops"), "{text}");

    // No reply at all: a timeout, not a hang.
    let call =
        c.start_call("create_object", json!({"shape": "sphere", "position": [0, 1, 0]})).await;
    conn.recv().await;
    let (text, err) = c.finish_call(call).await;
    assert!(err && text.contains("did not answer within 2 s"), "{text}");
    c.shutdown().await;
}

#[tokio::test]
async fn object_arguments_are_checked_before_sending() {
    let (_world, mut conn, mut c) = connected().await;
    let p = json!({"x": 0, "y": 0.5, "z": 0});
    let cases = [
        ("create_object", json!({"shape": "pyramid", "position": p}), "box, sphere, cylinder"),
        ("create_object", json!({"position": p}), "`shape` is required"),
        ("create_object", json!({"shape": "box"}), "`position` is required"),
        ("create_object", json!({"shape": "box", "position": {"x": 1, "y": 2}}), "\"z\": number"),
        ("create_object", json!({"shape": "box", "position": p, "color": "blurple"}), "#rrggbb"),
        (
            "create_object",
            json!({"shape": "box", "position": p, "colr": "red"}),
            "unknown argument `colr`",
        ),
        ("create_object", json!({"shape": "box", "position": p, "scale": 100}), "scale.x is 100 m"),
        (
            "create_object",
            json!({"shape": "box", "position": p, "material": "wood"}),
            "matte, glossy",
        ),
        (
            "create_object",
            json!({"shape": "box", "position": {"x": 0, "y": 0, "z": 500}}),
            "outside the world bounds",
        ),
        (
            "create_object",
            json!({"shape": "box", "position": p, "name": "n".repeat(65)}),
            "longer than 64",
        ),
        ("create_object", json!({"shape": "box", "position": p, "solid": "yes"}), "true or false"),
        ("edit_object", json!({"id": 7}), "nothing to change"),
        ("edit_object", json!({"id": "abc", "solid": false}), "must be an object id"),
        ("edit_object", json!({"solid": false}), "`id` is required"),
        ("delete_object", json!({"id": -4}), "must be an object id"),
        ("delete_object", json!({}), "`id` is required"),
        ("list_objects", json!({"owner": "them"}), "me, anyone"),
        ("look", json!({"radius": -1}), "0 or more"),
    ];
    for (tool, args, want) in cases {
        let text = c.fails(tool, args.clone()).await;
        assert!(text.contains(want), "{tool} {args}: wanted {want:?} in {text:?}");
    }
    conn.expect_silence(Duration::from_millis(200)).await;
    c.shutdown().await;
}

#[tokio::test]
async fn visitors_are_told_they_cannot_build() {
    let mut script = Script::default();
    script.welcome.role = tv_proto::v1::Role::Visitor as i32;
    let mut world = MockWorld::start(script).await;
    let mut c = Client::start(config(&world.url()));
    let mut conn = world.next_conn().await;
    let text = c.fails("create_object", json!({"shape": "box", "position": [0, 0.5, 0]})).await;
    assert!(text.contains("your role is visitor"), "{text}");
    conn.expect_silence(Duration::from_millis(200)).await;
    c.shutdown().await;
}

#[tokio::test]
async fn list_objects_filters_by_owner_and_radius() {
    let (_world, _conn, mut c) = connected().await;
    let text = c.ok("list_objects", json!({})).await;
    assert!(text.starts_with("2 object(s) found:"), "{text}");
    let first = text.lines().nth(1).unwrap();
    assert!(first.contains("object 8 \"my crate\""), "nearest first: {text}");
    assert!(first.contains("owner you"), "{text}");
    let text = c.ok("list_objects", json!({"owner": "me"})).await;
    assert!(text.starts_with("1 object(s) of yours found:") && !text.contains("door"), "{text}");
    let text = c.ok("list_objects", json!({"radius": 1})).await;
    assert_eq!(text, "No objects found within 1 m.");
    let text = c.ok("list_objects", json!({"limit": 1})).await;
    assert!(text.contains("showing the nearest 1"), "{text}");
    c.shutdown().await;
}

// ------------------------------------------------------------ connection

#[tokio::test]
async fn hello_joins_as_an_agent_with_the_token() {
    let mut world = MockWorld::start(Script::default()).await;
    let mut cfg = config(&world.url());
    cfg.name = Some("Scout".into());
    cfg.password = Some("pw".into());
    let c = Client::start(cfg);
    let conn = world.next_conn().await;
    assert_eq!(conn.hello.kind, tv_proto::v1::ClientKind::Agent as i32);
    assert_eq!(conn.hello.protocol_version, tv_proto::PROTOCOL_VERSION);
    assert_eq!(conn.hello.token, "agent-token");
    assert_eq!(conn.hello.name, "Scout");
    assert_eq!(conn.hello.password, "pw");
    assert!(conn.hello.client_name.starts_with("tv-mcp/"));
    c.shutdown().await;
}

#[tokio::test]
async fn keepalive_pings_are_sent() {
    let mut world = MockWorld::start(Script::default()).await;
    let mut cfg = config(&world.url());
    cfg.ping_interval = Duration::from_millis(100);
    let c = Client::start(cfg);
    let conn = world.next_conn().await;
    tokio::time::sleep(Duration::from_millis(450)).await;
    assert!(conn.pings.load(Ordering::Relaxed) >= 3);
    c.shutdown().await;
}

#[tokio::test]
async fn unreachable_world_gives_errors_then_connects_when_it_appears() {
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    drop(listener);
    let url = format!("ws://{addr}/ws");
    let mut c = Client::start(config(&url));
    c.initialize().await;
    // The MCP side works without the world.
    let r = c.request("tools/list", json!({})).await;
    assert_eq!(r["result"]["tools"].as_array().unwrap().len(), TOOLS.len());
    for tool in ["who", "look", "world_info"] {
        let text = c.fails(tool, json!({})).await;
        assert!(
            text.starts_with("Not connected to the Tinyverse world: cannot reach the world at"),
            "{text}"
        );
        assert!(text.contains(&url) && text.contains("retried on the next tool call"), "{text}");
    }
    // Arguments are still checked first.
    let text = c.fails("say", json!({})).await;
    assert!(text.contains("`text` is required"), "{text}");

    let listener = tokio::net::TcpListener::bind(addr).await.unwrap();
    let mut world = MockWorld::start_on(listener, Script::default());
    let text = c.ok("who", json!({})).await;
    assert!(
        text.starts_with("(Connected to the world again; the previous problem was: cannot reach"),
        "{text}"
    );
    assert!(text.contains("Ada"), "{text}");
    world.next_conn().await;
    c.shutdown().await;
}

#[tokio::test]
async fn refusal_is_explained() {
    let script = Script {
        refuse: Some((ErrorCode::AuthFailed, "wrong password".into())),
        ..Script::default()
    };
    let world = MockWorld::start(script).await;
    let mut c = Client::start(config(&world.url()));
    let text = c.fails("who", json!({})).await;
    assert!(
        text.contains("refused to let us in: ERROR_CODE_AUTH_FAILED: wrong password"),
        "{text}"
    );
    assert!(text.contains("--password"), "{text}");

    // Fixed on the host side: the next call gets in.
    world.script.lock().unwrap().refuse = None;
    let text = c.ok("who", json!({})).await;
    assert!(text.contains("Connected to the world again"), "{text}");
    c.shutdown().await;
}

#[tokio::test]
async fn reconnects_after_the_world_drops_the_socket() {
    let mut script = Script::default();
    script.welcome.session_token = "session-1".into();
    let mut world = MockWorld::start(script).await;
    let mut cfg = config(&world.url());
    cfg.token = None;
    cfg.name = Some("Scout".into());
    let mut c = Client::start(cfg);
    let first = world.next_conn().await;
    assert_eq!(first.hello.token, "");
    c.ok("who", json!({})).await;

    first.close();
    // Reconnects in the background, presenting the session token it was given.
    let mut second = world.next_conn().await;
    assert_eq!(second.hello.token, "session-1");
    let text = c.ok("who", json!({})).await;
    assert!(text.contains("Ada"), "{text}");

    let log = c.ok("recent_chat", json!({"since_last": false})).await;
    assert!(log.contains("lost the connection to the world"), "{log}");
    // History resent on the second join is not duplicated.
    assert_eq!(log.matches("welcome, everyone").count(), 1, "{log}");

    // Requests flow on the new connection.
    let call = c.start_call("stop", json!({})).await;
    assert!(matches!(second.recv().await.body, Some(C::Stop(_))));
    c.finish_call(call).await;
    c.shutdown().await;
}

#[tokio::test]
async fn a_drop_during_a_wait_is_reported() {
    let (mut world, conn, mut c) = connected().await;
    let call = c.start_call("move_to", json!({"x": 50, "z": 0, "timeout_s": 10})).await;
    tokio::time::sleep(Duration::from_millis(100)).await;
    conn.close();
    let (text, err) = c.finish_call(call).await;
    assert!(err && text.contains("lost while moving"), "{text}");
    world.next_conn().await;
    c.shutdown().await;
}

// ------------------------------------------------------------ the binary

#[tokio::test]
async fn binary_writes_only_json_rpc_to_stdout() {
    use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt};
    let mut child = tokio::process::Command::new(env!("CARGO_BIN_EXE_tv-mcp"))
        .args(["--url", "ws://127.0.0.1:1/ws", "--token", "t", "--name", "Bin", "-vv"])
        .env_remove("RUST_LOG")
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .unwrap();
    let mut stdin = child.stdin.take().unwrap();
    let mut stdout = tokio::io::BufReader::new(child.stdout.take().unwrap()).lines();
    let requests = [
        json!({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "t", "version": "0"}}}),
        json!({"jsonrpc": "2.0", "method": "notifications/initialized"}),
        json!({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}),
        json!({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "who", "arguments": {}}}),
        json!({"jsonrpc": "2.0", "id": 4, "method": "nope"}),
    ];
    for r in &requests {
        stdin.write_all(format!("{r}\n").as_bytes()).await.unwrap();
    }
    stdin.flush().await.unwrap();
    let mut ids = Vec::new();
    for _ in 0..4 {
        let line = tokio::time::timeout(WAIT, stdout.next_line()).await.unwrap().unwrap().unwrap();
        let m = assert_jsonrpc(&line);
        ids.push(m["id"].as_i64().unwrap());
        if m["id"] == 3 {
            assert_eq!(m["result"]["isError"], true);
        }
    }
    ids.sort();
    assert_eq!(ids, [1, 2, 3, 4]);
    drop(stdin);
    let status = tokio::time::timeout(WAIT, child.wait()).await.expect("exits on EOF").unwrap();
    assert!(status.success());
    // Nothing else came out on stdout.
    while let Some(line) = stdout.next_line().await.unwrap() {
        assert_jsonrpc(&line);
    }
    let mut err = String::new();
    child.stderr.take().unwrap().read_to_string(&mut err).await.unwrap();
    assert!(err.contains("tv-mcp") && err.contains("starting"), "logs go to stderr: {err}");
}
