//! The bridge's one WebSocket connection to the world: login, the reader and
//! writer tasks, request/reply correlation, keepalive and reconnection.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

use futures_util::{SinkExt, StreamExt};
use tokio::sync::{mpsc, oneshot, watch};
use tokio_tungstenite::tungstenite::Message;
use tv_proto::v1::{
    ClientKind, ClientMessage, ErrorCode, Hello, Ping, ServerMessage, client_message,
    server_message::Body,
};
use tv_proto::{Encoding, Frame};

use crate::format;
use crate::model::{Log, Session};

/// How the bridge reaches and joins the world.
#[derive(Clone, Debug)]
pub struct Config {
    /// WebSocket URL, e.g. `ws://127.0.0.1:7878/ws`.
    pub url: String,
    /// Agent token created by the host, or a session token.
    pub token: Option<String>,
    /// Display name.
    pub name: Option<String>,
    /// The world's join password.
    pub password: Option<String>,
    /// Limit for the TCP + WebSocket handshake and for the Welcome.
    pub connect_timeout: Duration,
    /// How long to wait for the reply to a request.
    pub reply_timeout: Duration,
    /// Interval between keepalive pings.
    pub ping_interval: Duration,
    /// First delay before reconnecting after a drop; doubles up to 30 s.
    pub reconnect_delay: Duration,
}

impl Config {
    pub fn new(url: impl Into<String>) -> Self {
        Config {
            url: url.into(),
            token: None,
            name: None,
            password: None,
            connect_timeout: Duration::from_secs(5),
            reply_timeout: Duration::from_secs(10),
            ping_interval: Duration::from_secs(10),
            reconnect_delay: Duration::from_secs(1),
        }
    }
}

/// Shared mutable state, behind a short-lived lock.
#[derive(Debug, Default)]
pub struct State {
    /// The current session; `None` while disconnected.
    pub session: Option<Session>,
    /// Chat and events; survives reconnects.
    pub log: Log,
    conn: Option<Conn>,
    /// Why the last connection attempt or connection failed.
    last_error: Option<String>,
    /// Session token from Welcome, preferred over the configured one.
    session_token: Option<String>,
    reconnecting: bool,
}

#[derive(Debug)]
struct Conn {
    id: u64,
    tx: mpsc::UnboundedSender<ClientMessage>,
    pending: HashMap<u32, oneshot::Sender<ServerMessage>>,
}

struct Inner {
    cfg: Config,
    state: Mutex<State>,
    changed: watch::Sender<u64>,
    connect_lock: tokio::sync::Mutex<()>,
    seq: AtomicU32,
    conn_ids: AtomicU64,
}

/// Handle to the world connection. Cheap to clone.
#[derive(Clone)]
pub struct Bridge {
    inner: Arc<Inner>,
}

/// Why a connection attempt failed.
#[derive(Debug)]
struct ConnectError {
    message: String,
    /// Retrying unchanged will not help (bad password, banned, ...).
    permanent: bool,
    /// The remembered session token was rejected; retry with the configured one.
    retry_without_session_token: bool,
}

impl ConnectError {
    fn transient(message: String) -> Self {
        ConnectError { message, permanent: false, retry_without_session_token: false }
    }

    fn permanent(message: String) -> Self {
        ConnectError { message, permanent: true, retry_without_session_token: false }
    }
}

/// A request in flight. Dropping it forgets the reply.
pub struct Pending {
    pub seq: u32,
    rx: Option<oneshot::Receiver<ServerMessage>>,
    bridge: Bridge,
}

/// The outcome of waiting for a reply.
pub enum Reply {
    Message(Box<ServerMessage>),
    TimedOut,
    ConnectionLost,
}

impl Pending {
    /// Waits up to `limit` for the reply. Only the first message with
    /// `reply_to == seq` is delivered.
    pub async fn wait(&mut self, limit: Duration) -> Reply {
        let Some(rx) = self.rx.as_mut() else { return Reply::ConnectionLost };
        match tokio::time::timeout(limit, rx).await {
            Ok(Ok(msg)) => {
                self.rx = None;
                Reply::Message(Box::new(msg))
            }
            Ok(Err(_)) => {
                self.rx = None;
                Reply::ConnectionLost
            }
            Err(_) => Reply::TimedOut,
        }
    }
}

impl Drop for Pending {
    fn drop(&mut self) {
        if self.rx.is_some() {
            let mut st = self.bridge.lock();
            if let Some(conn) = st.conn.as_mut() {
                conn.pending.remove(&self.seq);
            }
        }
    }
}

/// The message of a server `Error` reply, as `CODE: message`.
pub fn error_text(msg: &ServerMessage) -> Option<String> {
    match &msg.body {
        Some(Body::Error(e)) => Some(format!("{}: {}", format::error_code_name(e.code), e.message)),
        _ => None,
    }
}

fn hint_for(code: i32) -> &'static str {
    match ErrorCode::try_from(code) {
        Ok(ErrorCode::AuthFailed) => {
            " (the world needs a join password: start tv-mcp with --password, or use a token)"
        }
        Ok(ErrorCode::InvalidToken) => {
            " (the token is unknown or revoked: ask the host for a new one with \
             `tinyverse-server token create`)"
        }
        Ok(ErrorCode::UnsupportedVersion) => " (tv-mcp and the server speak different versions)",
        Ok(ErrorCode::WorldFull) => " (try again later)",
        _ => "",
    }
}

impl Bridge {
    pub fn new(cfg: Config) -> Self {
        let (changed, _) = watch::channel(0);
        Bridge {
            inner: Arc::new(Inner {
                cfg,
                state: Mutex::new(State::default()),
                changed,
                connect_lock: tokio::sync::Mutex::new(()),
                seq: AtomicU32::new(1),
                conn_ids: AtomicU64::new(1),
            }),
        }
    }

    pub fn config(&self) -> &Config {
        &self.inner.cfg
    }

    /// Locks the shared state. Never hold the guard across an await.
    pub fn lock(&self) -> MutexGuard<'_, State> {
        self.inner.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Wakes everyone waiting for a change of the model.
    fn bump(&self) {
        self.inner.changed.send_modify(|v| *v = v.wrapping_add(1));
    }

    /// A receiver that changes whenever the model or the connection changes.
    pub fn subscribe(&self) -> watch::Receiver<u64> {
        self.inner.changed.subscribe()
    }

    fn next_seq(&self) -> u32 {
        loop {
            let s = self.inner.seq.fetch_add(1, Ordering::Relaxed);
            if s != 0 {
                return s;
            }
        }
    }

    pub fn is_connected(&self) -> bool {
        self.lock().conn.is_some()
    }

    /// Starts the first connection attempt in the background.
    pub fn start(&self) {
        let me = self.clone();
        tokio::spawn(async move {
            let _guard = me.inner.connect_lock.lock().await;
            if me.is_connected() {
                return;
            }
            if let Err(e) = me.connect().await {
                tracing::warn!("could not join the world: {}", e.message);
            }
        });
    }

    /// Makes sure there is a live connection, connecting now if needed.
    /// Returns a note to show the assistant when a connection was just made,
    /// or a clear explanation when the world cannot be joined.
    pub async fn ensure_connected(&self) -> Result<Option<String>, String> {
        let ready = {
            let st = self.lock();
            st.conn.is_some() && st.session.as_ref().is_some_and(Session::is_ready)
        };
        if ready {
            return Ok(None);
        }
        // Either not connected, or a login is still receiving its snapshot
        // (the login holds the lock until then).
        let _guard = self.inner.connect_lock.lock().await;
        if self.is_connected() {
            return Ok(None);
        }
        let previous = self.lock().last_error.clone();
        match self.connect().await {
            Ok(()) => Ok(Some(match previous {
                Some(p) => format!("(Connected to the world again; the previous problem was: {p})"),
                None => "(Connected to the world.)".to_string(),
            })),
            Err(e) => Err(format!(
                "Not connected to the Tinyverse world: {}. Nothing was done; the connection \
                 will be retried on the next tool call.",
                e.message
            )),
        }
    }

    /// Sends a request and returns a handle to await its reply.
    pub fn send(&self, body: client_message::Body) -> Result<Pending, String> {
        let seq = self.next_seq();
        let (tx, rx) = oneshot::channel();
        let mut st = self.lock();
        let Some(conn) = st.conn.as_mut() else {
            return Err("Not connected to the Tinyverse world; try again.".into());
        };
        conn.pending.insert(seq, tx);
        if conn.tx.send(ClientMessage { seq, body: Some(body) }).is_err() {
            conn.pending.remove(&seq);
            return Err("The connection to the world is closing; try again.".into());
        }
        drop(st);
        Ok(Pending { seq, rx: Some(rx), bridge: self.clone() })
    }

    /// Sends a request and waits for its reply with the configured timeout.
    pub async fn request(&self, body: client_message::Body) -> Result<ServerMessage, String> {
        let mut pending = self.send(body)?;
        match pending.wait(self.inner.cfg.reply_timeout).await {
            Reply::Message(m) => Ok(*m),
            Reply::TimedOut => Err(format!(
                "The world did not answer within {} s.",
                self.inner.cfg.reply_timeout.as_secs_f32()
            )),
            Reply::ConnectionLost => {
                Err("The connection to the world was lost before it answered.".into())
            }
        }
    }

    /// One login attempt. The caller holds `connect_lock`.
    async fn connect(&self) -> Result<(), ConnectError> {
        let result = match self.connect_inner().await {
            Err(e) if e.retry_without_session_token => self.connect_inner().await,
            other => other,
        };
        if let Err(e) = &result {
            self.lock().last_error = Some(e.message.clone());
        }
        result
    }

    async fn connect_inner(&self) -> Result<(), ConnectError> {
        let cfg = &self.inner.cfg;
        let url = &cfg.url;
        let ws = match tokio::time::timeout(
            cfg.connect_timeout,
            tokio_tungstenite::connect_async(url.as_str()),
        )
        .await
        {
            Err(_) => {
                return Err(ConnectError::transient(format!(
                    "timed out connecting to {url} after {} s",
                    cfg.connect_timeout.as_secs_f32()
                )));
            }
            Ok(Err(e)) => {
                return Err(ConnectError::transient(format!(
                    "cannot reach the world at {url} ({e})"
                )));
            }
            Ok(Ok((ws, _))) => ws,
        };
        let (mut sink, mut stream) = ws.split();

        let session_token = self.lock().session_token.clone();
        let used_session_token = session_token.is_some();
        let token = session_token.or_else(|| cfg.token.clone()).unwrap_or_default();
        let name = match &cfg.name {
            Some(n) => n.clone(),
            None if token.is_empty() => "Agent".to_string(),
            None => String::new(),
        };
        let hello = ClientMessage {
            seq: self.next_seq(),
            body: Some(client_message::Body::Hello(Hello {
                protocol_version: tv_proto::PROTOCOL_VERSION,
                kind: ClientKind::Agent as i32,
                name,
                password: cfg.password.clone().unwrap_or_default(),
                token,
                appearance: None,
                client_name: format!("tv-mcp/{}", env!("CARGO_PKG_VERSION")),
            })),
        };
        let lost =
            |e: String| ConnectError::transient(format!("lost the connection during login ({e})"));
        sink.send(to_ws(&hello)).await.map_err(|e| lost(e.to_string()))?;

        let welcome = tokio::time::timeout(cfg.connect_timeout, async {
            while let Some(frame) = stream.next().await {
                let frame = frame.map_err(|e| lost(e.to_string()))?;
                let Some(msg) = from_ws(frame) else { continue };
                match msg.body {
                    Some(Body::Welcome(w)) => return Ok(w),
                    Some(Body::Error(e)) => {
                        if e.code == ErrorCode::InvalidToken as i32 && used_session_token {
                            // Our remembered session token went stale; fall
                            // back to the configured credential once.
                            self.lock().session_token = None;
                            return Err(ConnectError {
                                retry_without_session_token: true,
                                ..ConnectError::transient("the session token was rejected".into())
                            });
                        }
                        let permanent = e.code != ErrorCode::WorldFull as i32
                            && e.code != ErrorCode::RateLimited as i32
                            && e.code != ErrorCode::Internal as i32;
                        let message = format!(
                            "the world at {url} refused to let us in: {}: {}{}",
                            format::error_code_name(e.code),
                            e.message,
                            hint_for(e.code)
                        );
                        return Err(if permanent {
                            ConnectError::permanent(message)
                        } else {
                            ConnectError::transient(message)
                        });
                    }
                    Some(Body::Kicked(k)) => {
                        return Err(ConnectError::permanent(format!(
                            "the world turned us away: {}",
                            k.reason
                        )));
                    }
                    _ => {}
                }
            }
            Err(lost("the server closed the socket".into()))
        })
        .await
        .map_err(|_| {
            ConnectError::transient(format!(
                "the world at {url} did not answer Hello within {} s",
                cfg.connect_timeout.as_secs_f32()
            ))
        })??;

        let id = self.inner.conn_ids.fetch_add(1, Ordering::Relaxed);
        let (tx, rx) = mpsc::unbounded_channel();
        {
            let mut st = self.lock();
            if !welcome.session_token.is_empty() {
                st.session_token = Some(welcome.session_token.clone());
            }
            let session = Session::from_welcome(welcome);
            let text = format!(
                "connected to world {:?} as {} ({})",
                session.world.name,
                session.me.name,
                format::role_name(session.role)
            );
            tracing::info!("{text}");
            st.log.push_event(text, None);
            st.session = Some(session);
            st.conn = Some(Conn { id, tx, pending: HashMap::new() });
            st.last_error = None;
        }
        self.bump();
        tokio::spawn(writer(sink, rx, cfg.ping_interval));
        tokio::spawn(self.clone().reader(stream, id));

        // Wait (briefly) for the snapshot and chat history so that `who`,
        // `list_objects` and `recent_chat` are complete from the first call.
        let mut changed = self.subscribe();
        let _ = tokio::time::timeout(cfg.connect_timeout, async {
            loop {
                {
                    let st = self.lock();
                    match &st.session {
                        Some(s) if s.is_ready() => return,
                        None => return,
                        _ => {}
                    }
                }
                if changed.changed().await.is_err() {
                    return;
                }
            }
        })
        .await;
        Ok(())
    }

    async fn reader<S>(self, mut stream: S, id: u64)
    where
        S: futures_util::Stream<Item = Result<Message, tokio_tungstenite::tungstenite::Error>>
            + Unpin,
    {
        let mut kicked = None;
        let reason = loop {
            let frame = match stream.next().await {
                None => break "the server closed the connection".to_string(),
                Some(Err(e)) => break format!("connection error: {e}"),
                Some(Ok(Message::Close(f))) => {
                    break match f {
                        Some(f) if !f.reason.is_empty() => {
                            format!("the server closed the connection: {}", f.reason)
                        }
                        _ => "the server closed the connection".to_string(),
                    };
                }
                Some(Ok(frame)) => frame,
            };
            let Some(msg) = from_ws(frame) else { continue };
            if let Some(Body::Kicked(k)) = &msg.body {
                kicked = Some(k.reason.clone());
            }
            let reply = {
                let mut st = self.lock();
                if st.conn.as_ref().map(|c| c.id) != Some(id) {
                    return;
                }
                let st = &mut *st;
                if let Some(session) = st.session.as_mut() {
                    let notes = session.apply(&msg);
                    st.log.record(notes, &session.user_id);
                }
                let conn = st.conn.as_mut().expect("checked above");
                if msg.reply_to != 0 { conn.pending.remove(&msg.reply_to) } else { None }
            };
            if let Some(tx) = reply {
                let _ = tx.send(msg);
            }
            self.bump();
        };

        let reconnect = {
            let mut st = self.lock();
            if st.conn.as_ref().map(|c| c.id) != Some(id) {
                return;
            }
            st.conn = None;
            st.session = None;
            let why = match &kicked {
                Some(k) => format!("you were kicked from the world ({k})"),
                None => reason,
            };
            tracing::warn!("lost the world connection: {why}");
            st.log.push_event(format!("lost the connection to the world: {why}"), None);
            st.last_error = Some(why);
            let start = kicked.is_none() && !st.reconnecting;
            if start {
                st.reconnecting = true;
            }
            start
        };
        self.bump();
        if reconnect {
            tokio::spawn(self.reconnect_loop());
        }
    }

    /// Boxed so that the connect → reader → reconnect → connect cycle has a
    /// nameable future type.
    fn reconnect_loop(self) -> std::pin::Pin<Box<dyn std::future::Future<Output = ()> + Send>> {
        Box::pin(self.reconnect_loop_inner())
    }

    async fn reconnect_loop_inner(self) {
        let mut delay = self.inner.cfg.reconnect_delay;
        loop {
            tokio::time::sleep(delay).await;
            let guard = self.inner.connect_lock.lock().await;
            if self.is_connected() {
                break;
            }
            match self.connect().await {
                Ok(()) => break,
                Err(e) if e.permanent => {
                    tracing::warn!("not reconnecting: {}", e.message);
                    break;
                }
                Err(e) => tracing::info!("reconnect failed: {}", e.message),
            }
            drop(guard);
            delay = (delay * 2).min(Duration::from_secs(30));
        }
        self.lock().reconnecting = false;
    }
}

async fn writer<S>(mut sink: S, mut rx: mpsc::UnboundedReceiver<ClientMessage>, every: Duration)
where
    S: futures_util::Sink<Message> + Unpin,
{
    let mut ping = tokio::time::interval_at(tokio::time::Instant::now() + every, every);
    loop {
        let msg = tokio::select! {
            m = rx.recv() => match m {
                Some(m) => m,
                None => break,
            },
            _ = ping.tick() => ClientMessage {
                seq: 0,
                body: Some(client_message::Body::Ping(Ping { client_time_ms: format::now_ms() })),
            },
        };
        if sink.send(to_ws(&msg)).await.is_err() {
            break;
        }
    }
    let _ = sink.close().await;
}

fn to_ws(msg: &ClientMessage) -> Message {
    match tv_proto::encode(msg, Encoding::Json) {
        Frame::Text(t) => Message::text(t),
        Frame::Binary(b) => Message::binary(b),
    }
}

fn from_ws(msg: Message) -> Option<ServerMessage> {
    let frame = match msg {
        Message::Text(t) => Frame::Text(t.as_str().to_owned()),
        Message::Binary(b) => Frame::Binary(b.to_vec()),
        _ => return None,
    };
    match tv_proto::decode::<ServerMessage>(&frame) {
        Ok(m) => Some(m),
        Err(e) => {
            tracing::warn!("ignoring a frame from the server that does not decode: {e}");
            None
        }
    }
}
