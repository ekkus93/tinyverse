//! One task per WebSocket connection.
//!
//! A session reads the `Hello`, authenticates it against the store, asks the
//! world task to join, and then shuttles frames: inbound frames are decoded,
//! rate limited, converted to world commands and sent to the world task;
//! frames the world task queued for this session are written to the socket.
//! `Ping` is answered here without touching the world.

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use axum::extract::ws::{CloseFrame, Message, WebSocket, close_code};
use tokio::sync::{mpsc, oneshot, watch};
use tokio::time::Instant;
use tv_proto::v1::{self, ErrorCode, client_message::Body as C, server_message::Body as S};
use tv_proto::{Encoding, Frame};
use tv_world::{ClientKind, EntityId, JoinRequest, Role, UserId};

use crate::convert::{self, MAX_MESSAGE_BYTES, Refusal};
use crate::hub::{JoinMsg, Joined, Outbound, ToWorld};
use crate::now_ms;
use crate::ratelimit::{RateLimiter, RateLimits};
use crate::store::StoreHandle;

/// After this many consecutive undecodable frames the connection is closed.
pub const MAX_BAD_FRAMES: u32 = 5;

/// Timing and capacity knobs of a session.
#[derive(Clone, Debug)]
pub struct SessionTiming {
    /// How long a new connection may take to send `Hello`.
    pub hello_timeout: Duration,
    /// A connection that sends nothing for this long is closed.
    pub idle_timeout: Duration,
    /// Capacity of the outbound queue. A client that falls this far behind
    /// is dropped.
    pub outbound_queue: usize,
    pub rate_limits: RateLimits,
}

impl Default for SessionTiming {
    fn default() -> Self {
        SessionTiming {
            hello_timeout: Duration::from_secs(10),
            idle_timeout: Duration::from_secs(30),
            outbound_queue: 512,
            rate_limits: RateLimits::default(),
        }
    }
}

/// What every session needs; shared by all of them.
pub struct SessionCtx {
    pub hub: mpsc::Sender<ToWorld>,
    pub store: StoreHandle,
    pub password_hash: Option<String>,
    pub default_role: Role,
    pub max_users: usize,
    pub max_name_chars: usize,
    pub users: Arc<AtomicUsize>,
    pub timing: SessionTiming,
    pub shutdown: watch::Receiver<bool>,
}

/// Who a `Hello` turned out to be.
#[derive(Clone, Debug, PartialEq)]
pub struct Admitted {
    pub user_id: UserId,
    pub name: String,
    pub kind: ClientKind,
    pub role: Role,
    /// The fresh token of a new user; empty for token logins.
    pub session_token: String,
}

/// What the read side of the socket produced.
enum Incoming {
    Data(Frame),
    TooLarge(usize),
    Control,
    Closed,
}

/// The socket plus the encoding chosen by the client's `Hello`.
struct Conn {
    socket: WebSocket,
    encoding: Encoding,
}

impl Conn {
    async fn recv(&mut self) -> Incoming {
        match self.socket.recv().await {
            None | Some(Err(_)) | Some(Ok(Message::Close(_))) => Incoming::Closed,
            Some(Ok(Message::Ping(_) | Message::Pong(_))) => Incoming::Control,
            Some(Ok(Message::Text(text))) => {
                if text.len() > MAX_MESSAGE_BYTES {
                    Incoming::TooLarge(text.len())
                } else {
                    Incoming::Data(Frame::Text(text.as_str().to_owned()))
                }
            }
            Some(Ok(Message::Binary(bytes))) => {
                if bytes.len() > MAX_MESSAGE_BYTES {
                    Incoming::TooLarge(bytes.len())
                } else {
                    Incoming::Data(Frame::Binary(bytes.to_vec()))
                }
            }
        }
    }

    async fn send_frame(&mut self, frame: Frame) -> bool {
        let msg = match frame {
            Frame::Binary(bytes) => Message::Binary(bytes.into()),
            Frame::Text(text) => Message::Text(text.into()),
        };
        self.socket.send(msg).await.is_ok()
    }

    async fn send(&mut self, reply_to: u32, body: S) -> bool {
        let frame =
            tv_proto::encode(&v1::ServerMessage { reply_to, body: Some(body) }, self.encoding);
        self.send_frame(frame).await
    }

    async fn refuse(&mut self, reply_to: u32, refusal: &Refusal) -> bool {
        self.send(reply_to, refusal.body()).await
    }

    async fn close(&mut self, code: u16, reason: &str) {
        let frame = CloseFrame { code, reason: reason.into() };
        let _ = self.socket.send(Message::Close(Some(frame))).await;
    }
}

/// Best effort at finding the `seq` of a JSON frame that failed to decode,
/// so the error can still be correlated.
fn json_seq(frame: &Frame) -> u32 {
    let Frame::Text(text) = frame else { return 0 };
    let Ok(value) = serde_json::from_str::<serde_json::Value>(text) else { return 0 };
    match value.get("seq") {
        Some(serde_json::Value::Number(n)) => n.as_u64().and_then(|n| u32::try_from(n).ok()),
        Some(serde_json::Value::String(s)) => s.parse().ok(),
        _ => None,
    }
    .unwrap_or(0)
}

/// The error text for a frame that is not a valid `ClientMessage`.
pub fn decode_error_message(error: &tv_proto::DecodeError) -> String {
    format!(
        "could not decode the message ({error}). Send one ClientMessage per frame, e.g. \
         {{\"seq\":1,\"ping\":{{\"clientTimeMs\":0}}}}"
    )
}

const NO_BODY: &str = "the message has no body; set exactly one of hello, avatarState, moveTo, \
                       stop, chat, chatHistoryRequest, objectCreate, objectUpdate, objectDelete, \
                       observe, setAppearance, admin or ping";

/// Serves one connection until it closes.
pub async fn run(socket: WebSocket, ctx: Arc<SessionCtx>) {
    let mut conn = Conn { socket, encoding: Encoding::Json };
    let Some((joined, outbound)) = handshake(&mut conn, &ctx).await else { return };
    let avatar = joined.avatar;
    serve(&mut conn, &ctx, avatar, outbound).await;
    let _ = ctx.hub.send(ToWorld::Leave { avatar }).await;
    tracing::debug!(avatar, "session ended");
}

/// Reads `Hello`, admits the user and joins the world. On failure the client
/// has been told why and the socket is closed.
async fn handshake(
    conn: &mut Conn,
    ctx: &SessionCtx,
) -> Option<(Joined, mpsc::Receiver<Outbound>)> {
    let deadline = Instant::now() + ctx.timing.hello_timeout;
    let frame = loop {
        match tokio::time::timeout_at(deadline, conn.recv()).await {
            Err(_) => {
                let refusal = Refusal::bad_request("no Hello within the time limit");
                conn.refuse(0, &refusal).await;
                conn.close(close_code::POLICY, "no hello").await;
                return None;
            }
            Ok(Incoming::Closed) => return None,
            Ok(Incoming::Control) => continue,
            Ok(Incoming::TooLarge(len)) => {
                let refusal = too_large(len);
                conn.refuse(0, &refusal).await;
                conn.close(close_code::SIZE, "message too large").await;
                return None;
            }
            Ok(Incoming::Data(frame)) => break frame,
        }
    };
    conn.encoding = frame.encoding();

    let msg: v1::ClientMessage = match tv_proto::decode(&frame) {
        Ok(msg) => msg,
        Err(e) => {
            conn.refuse(json_seq(&frame), &Refusal::bad_request(decode_error_message(&e))).await;
            conn.close(close_code::POLICY, "bad hello").await;
            return None;
        }
    };
    let seq = msg.seq;
    let Some(C::Hello(hello)) = msg.body else {
        let refusal = Refusal::bad_request("the first message must be Hello");
        conn.refuse(seq, &refusal).await;
        conn.close(close_code::POLICY, "bad hello").await;
        return None;
    };

    let admitted = match admit(ctx, &hello).await {
        Ok(a) => a,
        Err(refusal) => {
            tracing::info!(client = %hello.client_name, "refused: {refusal}");
            conn.refuse(seq, &refusal).await;
            conn.close(close_code::POLICY, refusal.code.as_str_name()).await;
            return None;
        }
    };

    let (tx, rx) = mpsc::channel(ctx.timing.outbound_queue.max(1));
    let (reply_tx, reply_rx) = oneshot::channel();
    let new_name = (!hello.name.is_empty()).then(|| admitted.name.clone());
    let join = JoinMsg {
        request: JoinRequest {
            user_id: admitted.user_id.clone(),
            name: admitted.name,
            kind: admitted.kind,
            role: admitted.role,
            appearance: convert::appearance_from(hello.appearance.as_ref()),
        },
        session_token: admitted.session_token,
        encoding: conn.encoding,
        hello_seq: seq,
        outbound: tx,
        reply: reply_tx,
    };
    let gone = || Refusal::internal("the world is shutting down");
    let outcome = if ctx.hub.send(ToWorld::Join(Box::new(join))).await.is_err() {
        Err(gone())
    } else {
        reply_rx.await.unwrap_or_else(|_| Err(gone()))
    };
    match outcome {
        Ok(joined) => {
            let id = joined.user_id.clone();
            ctx.store.submit(move |s| {
                if let Err(e) = s.touch_user(&id, new_name.as_deref(), now_ms()) {
                    tracing::warn!(user = %id, "cannot record visit: {e}");
                }
            });
            Some((joined, rx))
        }
        Err(refusal) => {
            conn.refuse(seq, &refusal).await;
            conn.close(close_code::POLICY, refusal.code.as_str_name()).await;
            None
        }
    }
}

fn too_large(len: usize) -> Refusal {
    Refusal::bad_request(format!(
        "the message is {len} bytes; the limit is {MAX_MESSAGE_BYTES} bytes per message"
    ))
}

fn internal(e: impl std::fmt::Display) -> Refusal {
    tracing::error!("store error during login: {e}");
    Refusal::internal("the server could not read its user database")
}

/// Decides who a `Hello` is. Checks, in order: protocol version, credential
/// (token, or password for a new user), ban, capacity. A new user is created
/// in the store only after all checks pass.
pub async fn admit(ctx: &SessionCtx, hello: &v1::Hello) -> Result<Admitted, Refusal> {
    check_version(hello.protocol_version)?;
    let hello_kind = convert::client_kind_from(hello.kind, "hello.kind")?;

    if !hello.token.is_empty() {
        let token = hello.token.clone();
        let record = ctx
            .store
            .call(move |s| s.user_by_token(&token))
            .await
            .map_err(internal)?
            .map_err(internal)?;
        let Some(record) = record else {
            return Err(Refusal::new(
                ErrorCode::InvalidToken,
                "the token is unknown or has been revoked; leave `token` empty to join as a new \
                 user",
            ));
        };
        if let Some(reason) = &record.banned {
            return Err(banned(reason));
        }
        let name = if hello.name.is_empty() { record.name.clone() } else { hello.name.clone() };
        return Ok(Admitted {
            user_id: record.id,
            name,
            kind: record.kind,
            role: record.role,
            session_token: String::new(),
        });
    }

    if let Some(hash) = ctx.password_hash.clone() {
        if hello.password.is_empty() {
            return Err(Refusal::new(
                ErrorCode::AuthFailed,
                "this world needs a password; set `password` in Hello",
            ));
        }
        let password = hello.password.clone();
        let ok =
            tokio::task::spawn_blocking(move || crate::auth::verify_password(&password, &hash))
                .await
                .unwrap_or(false);
        if !ok {
            return Err(Refusal::new(ErrorCode::AuthFailed, "wrong password"));
        }
    }

    let name = hello.name.trim();
    if name.is_empty() {
        return Err(Refusal::invalid("`hello.name` is required when joining without a token"));
    }
    if name.chars().count() > ctx.max_name_chars {
        return Err(Refusal::invalid(format!(
            "`hello.name` is longer than {} characters",
            ctx.max_name_chars
        )));
    }
    check_capacity(ctx.users.load(Ordering::Relaxed), ctx.max_users)?;

    let (name, kind, role) = (name.to_owned(), hello_kind, ctx.default_role);
    let created = {
        let name = name.clone();
        ctx.store
            .call(move |s| s.create_user(&name, kind, role, now_ms()))
            .await
            .map_err(internal)?
            .map_err(internal)?
    };
    let (record, token) = created;
    tracing::info!(user = %record.id, %name, ?kind, ?role, "new user");
    Ok(Admitted { user_id: record.id, name, kind, role: record.role, session_token: token })
}

/// `UNSUPPORTED_VERSION` unless the client speaks our protocol version.
pub fn check_version(version: u32) -> Result<(), Refusal> {
    if version == tv_proto::PROTOCOL_VERSION {
        Ok(())
    } else {
        Err(Refusal::new(
            ErrorCode::UnsupportedVersion,
            format!(
                "this server speaks protocol version {}, the client sent {version}",
                tv_proto::PROTOCOL_VERSION
            ),
        ))
    }
}

/// `WORLD_FULL` if `present` avatars already fill the world.
pub fn check_capacity(present: usize, max_users: usize) -> Result<(), Refusal> {
    if present >= max_users {
        Err(Refusal::new(ErrorCode::WorldFull, format!("the world is full ({max_users} users)")))
    } else {
        Ok(())
    }
}

fn banned(reason: &str) -> Refusal {
    let message = if reason.is_empty() {
        "you are banned from this world".to_owned()
    } else {
        format!("you are banned from this world: {reason}")
    };
    Refusal::new(ErrorCode::Banned, message)
}

/// What to do after handling one inbound frame.
#[derive(Debug, PartialEq)]
pub enum Step {
    /// Nothing more to do.
    Done,
    /// Reply directly with this message (`reply_to`, body).
    Reply(u32, S),
    /// Forward this command to the world.
    Forward(u32, tv_world::Command),
    /// Reply, then close the connection.
    ReplyAndClose(u32, S),
}

/// The per-connection state that decides what to do with each frame.
pub struct FrameHandler {
    limiter: RateLimiter,
    bad_frames: u32,
}

impl FrameHandler {
    pub fn new(limits: &RateLimits) -> Self {
        FrameHandler { limiter: RateLimiter::new(limits, std::time::Instant::now()), bad_frames: 0 }
    }

    fn bad(&mut self, reply_to: u32, refusal: Refusal) -> Step {
        self.bad_frames += 1;
        if self.bad_frames >= MAX_BAD_FRAMES {
            let refusal = Refusal::bad_request(format!(
                "{} (closing after {MAX_BAD_FRAMES} bad messages in a row)",
                refusal.message
            ));
            Step::ReplyAndClose(reply_to, refusal.body())
        } else {
            Step::Reply(reply_to, refusal.body())
        }
    }

    /// Handles one inbound data frame of a joined session.
    pub fn handle(&mut self, frame: &Frame, now: std::time::Instant) -> Step {
        let msg: v1::ClientMessage = match tv_proto::decode(frame) {
            Ok(msg) => msg,
            Err(e) => {
                return self.bad(json_seq(frame), Refusal::bad_request(decode_error_message(&e)));
            }
        };
        let seq = msg.seq;
        let body = match msg.body {
            None => return self.bad(seq, Refusal::bad_request(NO_BODY)),
            Some(C::Hello(_)) => {
                return self
                    .bad(seq, Refusal::bad_request("already joined; Hello may only be sent once"));
            }
            Some(body) => body,
        };
        self.bad_frames = 0;
        let kind = convert::message_type(&body).expect("Hello was handled above");
        if !self.limiter.allow(kind, now) {
            let rate = self.limiter.rate(kind);
            let refusal = Refusal::new(
                ErrorCode::RateLimited,
                format!(
                    "too many {} messages; the limit is {} per second (burst {})",
                    kind.name(),
                    rate.per_second,
                    rate.burst
                ),
            );
            return Step::Reply(seq, refusal.body());
        }
        if let C::Ping(ping) = &body {
            return Step::Reply(
                seq,
                S::Pong(v1::Pong { client_time_ms: ping.client_time_ms, server_time_ms: now_ms() }),
            );
        }
        match convert::command_from(&body) {
            Ok(command) => Step::Forward(seq, command),
            Err(refusal) => Step::Reply(seq, refusal.body()),
        }
    }
}

async fn serve(
    conn: &mut Conn,
    ctx: &SessionCtx,
    avatar: EntityId,
    mut outbound: mpsc::Receiver<Outbound>,
) {
    let mut handler = FrameHandler::new(&ctx.timing.rate_limits);
    let mut shutdown = ctx.shutdown.clone();
    let mut last_seen = Instant::now();
    loop {
        let idle = tokio::time::sleep_until(last_seen + ctx.timing.idle_timeout);
        tokio::select! {
            changed = shutdown.changed() => {
                if changed.is_err() || *shutdown.borrow() {
                    conn.close(close_code::AWAY, "server shutting down").await;
                    return;
                }
            }
            out = outbound.recv() => match out {
                Some(Outbound::Frame(frame)) => {
                    if !conn.send_frame(frame).await {
                        return;
                    }
                }
                Some(Outbound::Frames(frames)) => {
                    for frame in frames {
                        if !conn.send_frame(frame).await {
                            return;
                        }
                    }
                }
                Some(Outbound::Close) => {
                    conn.close(close_code::NORMAL, "kicked").await;
                    return;
                }
                None => {
                    // The world task dropped us (queue overflow or shutdown).
                    conn.close(close_code::AGAIN, "dropped").await;
                    return;
                }
            },
            incoming = conn.recv() => {
                last_seen = Instant::now();
                match incoming {
                    Incoming::Closed => return,
                    Incoming::Control => {}
                    Incoming::TooLarge(len) => {
                        conn.refuse(0, &too_large(len)).await;
                        conn.close(close_code::SIZE, "message too large").await;
                        return;
                    }
                    Incoming::Data(frame) => {
                        match handler.handle(&frame, std::time::Instant::now()) {
                            Step::Done => {}
                            Step::Reply(reply_to, body) => {
                                if !conn.send(reply_to, body).await {
                                    return;
                                }
                            }
                            Step::ReplyAndClose(reply_to, body) => {
                                conn.send(reply_to, body).await;
                                conn.close(close_code::POLICY, "too many bad messages").await;
                                return;
                            }
                            Step::Forward(seq, command) => {
                                let msg = ToWorld::Command { avatar, seq, command };
                                if ctx.hub.send(msg).await.is_err() {
                                    conn.close(close_code::AWAY, "server shutting down").await;
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            _ = idle => {
                tracing::info!(avatar, "idle timeout");
                conn.close(close_code::POLICY, "idle timeout").await;
                return;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use std::time::Instant;

    use tv_proto::encode;

    use super::*;

    fn json(text: &str) -> Frame {
        Frame::Text(text.to_owned())
    }

    fn error_of(step: &Step) -> (u32, ErrorCode, String) {
        match step {
            Step::Reply(r, S::Error(e)) | Step::ReplyAndClose(r, S::Error(e)) => {
                (*r, ErrorCode::try_from(e.code).unwrap(), e.message.clone())
            }
            other => panic!("expected an error, got {other:?}"),
        }
    }

    #[test]
    fn ping_is_answered_directly() {
        let mut h = FrameHandler::new(&RateLimits::default());
        let step = h.handle(&json(r#"{"seq":3,"ping":{"clientTimeMs":"77"}}"#), Instant::now());
        match step {
            Step::Reply(3, S::Pong(p)) => {
                assert_eq!(p.client_time_ms, 77);
                assert!(p.server_time_ms > 0);
            }
            other => panic!("{other:?}"),
        }
        let bin = encode(
            &v1::ClientMessage { seq: 4, body: Some(C::Ping(v1::Ping { client_time_ms: 1 })) },
            Encoding::Binary,
        );
        assert!(matches!(h.handle(&bin, Instant::now()), Step::Reply(4, S::Pong(_))));
    }

    #[test]
    fn commands_are_forwarded() {
        let mut h = FrameHandler::new(&RateLimits::default());
        let step = h.handle(
            &json(r#"{"seq":9,"chat":{"channel":"CHAT_CHANNEL_WORLD","text":"hi"}}"#),
            Instant::now(),
        );
        assert_eq!(
            step,
            Step::Forward(
                9,
                tv_world::Command::Chat {
                    channel: tv_world::ChatChannel::World,
                    text: "hi".into()
                }
            )
        );
    }

    #[test]
    fn conversion_errors_keep_the_seq() {
        let mut h = FrameHandler::new(&RateLimits::default());
        let (r, code, msg) = error_of(&h.handle(&json(r#"{"seq":5,"moveTo":{}}"#), Instant::now()));
        assert_eq!((r, code), (5, ErrorCode::InvalidArgument));
        assert!(msg.contains("move_to.target"));
    }

    #[test]
    fn malformed_json_gets_a_helpful_error() {
        let mut h = FrameHandler::new(&RateLimits::default());
        let (r, code, msg) =
            error_of(&h.handle(&json(r#"{"seq":6,"chat":{"channel":"LOUD"}}"#), Instant::now()));
        assert_eq!((r, code), (6, ErrorCode::BadRequest));
        assert!(msg.contains("LOUD") || msg.contains("channel"), "{msg}");
        assert!(msg.contains("invalid JSON"), "{msg}");

        let (r, _, msg) = error_of(&h.handle(&json("{not json"), Instant::now()));
        assert_eq!(r, 0);
        assert!(msg.contains("line 1"), "{msg}");

        let (_, _, msg) = error_of(&h.handle(&json(r#"{"seq":1,"chatt":{}}"#), Instant::now()));
        assert!(msg.contains("chatt"), "{msg}");
    }

    #[test]
    fn no_body_and_second_hello_are_bad_requests() {
        let mut h = FrameHandler::new(&RateLimits::default());
        let (r, code, msg) = error_of(&h.handle(&json(r#"{"seq":2}"#), Instant::now()));
        assert_eq!((r, code), (2, ErrorCode::BadRequest));
        assert!(msg.contains("no body"));
        let (_, code, _) = error_of(&h.handle(&Frame::Binary(vec![]), Instant::now()));
        assert_eq!(code, ErrorCode::BadRequest);
        let (r, code, msg) = error_of(
            &h.handle(&json(r#"{"seq":3,"hello":{"protocolVersion":1}}"#), Instant::now()),
        );
        assert_eq!((r, code), (3, ErrorCode::BadRequest));
        assert!(msg.contains("Hello"));
    }

    #[test]
    fn five_bad_frames_in_a_row_close() {
        let mut h = FrameHandler::new(&RateLimits::default());
        let now = Instant::now();
        for _ in 0..4 {
            assert!(matches!(h.handle(&json("nope"), now), Step::Reply(..)));
        }
        // A good frame resets the count.
        assert!(matches!(h.handle(&json(r#"{"ping":{}}"#), now), Step::Reply(0, S::Pong(_))));
        for _ in 0..4 {
            assert!(matches!(h.handle(&json("nope"), now), Step::Reply(..)));
        }
        assert!(matches!(h.handle(&json("nope"), now), Step::ReplyAndClose(..)));
    }

    #[test]
    fn rate_limited() {
        let mut h = FrameHandler::new(&RateLimits::default());
        let now = Instant::now();
        let frame = json(r#"{"seq":1,"chat":{"text":"x"}}"#);
        for _ in 0..10 {
            assert!(matches!(h.handle(&frame, now), Step::Forward(..)));
        }
        let (r, code, msg) = error_of(&h.handle(&frame, now));
        assert_eq!((r, code), (1, ErrorCode::RateLimited));
        assert!(msg.contains("Chat"), "{msg}");
        // Rate-limited frames are not "bad" frames.
        for _ in 0..10 {
            assert!(!matches!(h.handle(&frame, now), Step::ReplyAndClose(..)));
        }
    }

    #[test]
    fn json_seq_is_recovered_when_possible() {
        assert_eq!(json_seq(&json(r#"{"seq":12,"bogus":1}"#)), 12);
        assert_eq!(json_seq(&json(r#"{"seq":"13","bogus":1}"#)), 13);
        assert_eq!(json_seq(&json("{")), 0);
        assert_eq!(json_seq(&Frame::Binary(vec![1, 2])), 0);
    }

    #[test]
    fn version_and_capacity_checks() {
        assert!(check_version(tv_proto::PROTOCOL_VERSION).is_ok());
        let err = check_version(99).unwrap_err();
        assert_eq!(err.code, ErrorCode::UnsupportedVersion);
        assert!(err.message.contains("99"));
        assert!(check_capacity(1, 2).is_ok());
        assert_eq!(check_capacity(2, 2).unwrap_err().code, ErrorCode::WorldFull);
    }

    #[test]
    fn banned_messages() {
        assert_eq!(banned("").message, "you are banned from this world");
        assert!(banned("spam").message.ends_with("spam"));
        assert_eq!(banned("x").code, ErrorCode::Banned);
    }
}
