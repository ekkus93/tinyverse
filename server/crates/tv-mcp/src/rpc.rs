//! MCP over stdio: newline-delimited JSON-RPC 2.0.
//!
//! The server is dual-era. It serves the current revision, 2026-07-28, in
//! which every request carries its protocol version and client capabilities
//! in `_meta` and there is no handshake (`server/discover` describes the
//! server). It also serves the handshake-based revisions 2025-11-25 and
//! earlier: `initialize`, `notifications/initialized`, then requests without
//! the per-request `_meta`. Only stdout carries MCP messages.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use serde_json::{Value, json};
use tokio::io::{AsyncBufReadExt, AsyncRead, AsyncWrite, AsyncWriteExt, BufReader};
use tokio::sync::mpsc;
use tokio::task::AbortHandle;

use crate::tools;
use crate::world::Bridge;

/// Revisions with per-request metadata.
pub const MODERN_VERSIONS: &[&str] = &["2026-07-28"];
/// Handshake revisions answered through `initialize`, newest first.
pub const LEGACY_VERSIONS: &[&str] = &["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

const META_VERSION: &str = "io.modelcontextprotocol/protocolVersion";
const META_CAPS: &str = "io.modelcontextprotocol/clientCapabilities";
const META_SERVER: &str = "io.modelcontextprotocol/serverInfo";

pub const PARSE_ERROR: i64 = -32700;
pub const INVALID_REQUEST: i64 = -32600;
pub const METHOD_NOT_FOUND: i64 = -32601;
pub const INVALID_PARAMS: i64 = -32602;
pub const UNSUPPORTED_VERSION: i64 = -32022;

const INSTRUCTIONS: &str = "These tools put you in a shared 3D world (Tinyverse) as an avatar \
    that other people can see and talk to. Start with `look` (or `who` and `world_info`) to \
    see where you are. Talk with `say`; people's replies arrive as chat, which you read with \
    `wait_for_chat` (blocks until someone speaks) or `recent_chat`. Move with `move_to`, and \
    build with `create_object`, `edit_object` and `delete_object`. Coordinates are metres, \
    Y up, ground at y = 0; headings are degrees clockwise from +z seen from above.";

fn server_info() -> Value {
    json!({
        "name": "tv-mcp",
        "title": "Tinyverse world bridge",
        "version": env!("CARGO_PKG_VERSION"),
    })
}

fn capabilities() -> Value {
    json!({ "tools": { "listChanged": false } })
}

type Out = mpsc::UnboundedSender<Value>;

/// Serves MCP on `input`/`output` until `input` ends.
pub async fn serve<R, W>(bridge: Bridge, input: R, output: W) -> std::io::Result<()>
where
    R: AsyncRead + Unpin,
    W: AsyncWrite + Unpin + Send + 'static,
{
    let (tx, rx) = mpsc::unbounded_channel::<Value>();
    let writer = tokio::spawn(write_loop(output, rx));
    let inflight: Arc<Mutex<HashMap<String, AbortHandle>>> = Arc::default();

    let mut reader = BufReader::new(input);
    let mut buf = Vec::new();
    loop {
        buf.clear();
        let n = reader.read_until(b'\n', &mut buf).await?;
        if n == 0 {
            break;
        }
        let line = String::from_utf8_lossy(&buf);
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        match serde_json::from_str::<Value>(line) {
            Err(e) => {
                let _ =
                    tx.send(error(Value::Null, PARSE_ERROR, &format!("Parse error: {e}"), None));
            }
            Ok(msg) => handle(&bridge, msg, &tx, &inflight),
        }
    }

    // The client closed stdin: stop promptly.
    for (_, task) in inflight.lock().unwrap_or_else(|e| e.into_inner()).drain() {
        task.abort();
    }
    drop(tx);
    let _ = writer.await;
    Ok(())
}

async fn write_loop<W: AsyncWrite + Unpin>(mut out: W, mut rx: mpsc::UnboundedReceiver<Value>) {
    while let Some(msg) = rx.recv().await {
        // serde_json escapes control characters, so a message is one line.
        let mut line = serde_json::to_string(&msg).expect("JSON values always serialise");
        line.push('\n');
        if out.write_all(line.as_bytes()).await.is_err() || out.flush().await.is_err() {
            break;
        }
    }
}

fn error(id: Value, code: i64, message: &str, data: Option<Value>) -> Value {
    let mut err = json!({ "code": code, "message": message });
    if let Some(d) = data {
        err["data"] = d;
    }
    json!({ "jsonrpc": "2.0", "id": id, "error": err })
}

fn respond(id: Value, mut result: Value, modern: bool) -> Value {
    if modern {
        result["resultType"] = json!("complete");
        result["_meta"] = json!({ META_SERVER: server_info() });
    }
    json!({ "jsonrpc": "2.0", "id": id, "result": result })
}

/// Which era a request belongs to: `Ok(true)` for one carrying modern
/// `_meta`, `Ok(false)` for a legacy request, or the error to send.
fn era(params: &Value) -> Result<bool, (i64, String, Option<Value>)> {
    let Some(meta) = params.get("_meta").filter(|m| m.is_object()) else { return Ok(false) };
    let Some(version) = meta.get(META_VERSION) else { return Ok(false) };
    let supported: Vec<&str> = MODERN_VERSIONS.iter().chain(LEGACY_VERSIONS).copied().collect();
    match version.as_str() {
        Some(v) if MODERN_VERSIONS.contains(&v) => {}
        _ => {
            return Err((
                UNSUPPORTED_VERSION,
                "Unsupported protocol version".into(),
                Some(json!({ "supported": supported, "requested": version })),
            ));
        }
    }
    if !meta.get(META_CAPS).is_some_and(Value::is_object) {
        return Err((INVALID_PARAMS, format!("Missing _meta[\"{META_CAPS}\"]"), None));
    }
    Ok(true)
}

fn handle(
    bridge: &Bridge,
    msg: Value,
    tx: &Out,
    inflight: &Arc<Mutex<HashMap<String, AbortHandle>>>,
) {
    let Value::Object(obj) = &msg else {
        let what = if msg.is_array() { "Batches are not supported" } else { "Invalid Request" };
        let _ = tx.send(error(Value::Null, INVALID_REQUEST, what, None));
        return;
    };
    let id = obj.get("id").cloned();
    if obj.get("jsonrpc").and_then(Value::as_str) != Some("2.0") {
        let _ = tx.send(error(
            id.unwrap_or(Value::Null),
            INVALID_REQUEST,
            "Invalid Request: \"jsonrpc\" must be \"2.0\"",
            None,
        ));
        return;
    }
    let Some(method) = obj.get("method").and_then(Value::as_str) else {
        if obj.contains_key("result") || obj.contains_key("error") {
            return; // A response; this server sends no requests, so ignore it.
        }
        let _ = tx.send(error(
            id.unwrap_or(Value::Null),
            INVALID_REQUEST,
            "Invalid Request: missing \"method\"",
            None,
        ));
        return;
    };
    let params = obj.get("params").cloned().unwrap_or_else(|| json!({}));

    let Some(id) = id else {
        // A notification: never answered.
        if method == "notifications/cancelled"
            && let Some(rid) = params.get("requestId")
            && let Some(task) =
                inflight.lock().unwrap_or_else(|e| e.into_inner()).remove(&rid.to_string())
        {
            task.abort();
            tracing::debug!("cancelled request {rid}");
        }
        return;
    };
    if !(id.is_string() || id.is_i64() || id.is_u64()) {
        let _ = tx.send(error(
            Value::Null,
            INVALID_REQUEST,
            "Invalid Request: \"id\" must be a string or an integer",
            None,
        ));
        return;
    }
    if !params.is_object() {
        let _ = tx.send(error(id, INVALID_PARAMS, "Invalid params: must be an object", None));
        return;
    }

    if method == "initialize" {
        let requested = params.get("protocolVersion").and_then(Value::as_str).unwrap_or("");
        let version =
            if LEGACY_VERSIONS.contains(&requested) { requested } else { LEGACY_VERSIONS[0] };
        let client = params.pointer("/clientInfo/name").and_then(Value::as_str).unwrap_or("?");
        tracing::info!("MCP client {client} initialised with protocol {version}");
        let result = json!({
            "protocolVersion": version,
            "capabilities": capabilities(),
            "serverInfo": server_info(),
            "instructions": INSTRUCTIONS,
        });
        let _ = tx.send(respond(id, result, false));
        return;
    }

    let modern = match era(&params) {
        Ok(m) => m,
        Err((code, message, data)) => {
            let _ = tx.send(error(id, code, &message, data));
            return;
        }
    };

    match method {
        "ping" => {
            let _ = tx.send(respond(id, json!({}), modern));
        }
        "server/discover" => {
            let supported: Vec<&str> =
                MODERN_VERSIONS.iter().chain(LEGACY_VERSIONS).copied().collect();
            let result = json!({
                "supportedVersions": supported,
                "capabilities": capabilities(),
                "instructions": INSTRUCTIONS,
            });
            let _ = tx.send(respond(id, result, true));
        }
        "tools/list" => {
            let _ = tx.send(respond(id, json!({ "tools": tools::definitions() }), modern));
        }
        "tools/call" => {
            let Some(name) = params.get("name").and_then(Value::as_str).map(str::to_owned) else {
                let _ = tx.send(error(
                    id,
                    INVALID_PARAMS,
                    "Invalid params: tools/call needs a string \"name\"",
                    None,
                ));
                return;
            };
            let arguments = params.get("arguments").cloned();
            if arguments.as_ref().is_some_and(|a| !a.is_object() && !a.is_null()) {
                let _ = tx.send(error(
                    id,
                    INVALID_PARAMS,
                    "Invalid params: \"arguments\" must be an object",
                    None,
                ));
                return;
            }
            let key = id.to_string();
            let (bridge, tx2, inflight2, key2) =
                (bridge.clone(), tx.clone(), inflight.clone(), key.clone());
            // Holding the lock while spawning keeps the task from removing
            // its entry before it has been inserted.
            let mut map = inflight.lock().unwrap_or_else(|e| e.into_inner());
            let task = tokio::spawn(async move {
                tracing::debug!("tools/call {name} {arguments:?}");
                let reply = match tools::call(&bridge, &name, arguments.as_ref()).await {
                    None => error(id, INVALID_PARAMS, &format!("Unknown tool: {name}"), None),
                    Some(r) => {
                        if r.is_error {
                            tracing::info!("tool {name} failed: {}", r.text);
                        }
                        respond(
                            id,
                            json!({
                                "content": [{ "type": "text", "text": r.text }],
                                "isError": r.is_error,
                            }),
                            modern,
                        )
                    }
                };
                // Remove first: once a request is cancelled nothing more may be sent.
                if inflight2.lock().unwrap_or_else(|e| e.into_inner()).remove(&key2).is_some() {
                    let _ = tx2.send(reply);
                }
            });
            map.insert(key, task.abort_handle());
        }
        _ => {
            let _ =
                tx.send(error(id, METHOD_NOT_FOUND, &format!("Method not found: {method}"), None));
        }
    }
}
