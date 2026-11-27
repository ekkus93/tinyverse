//! A small async client for the Tinyverse protocol.
//!
//! [`Client::connect`] opens the WebSocket, sends `Hello`, waits for `Welcome`
//! and collects the join-time `Snapshot` chunks and `ChatHistory`. Afterwards
//! [`Client::send`] sends a message and returns its `seq`, [`Client::recv`]
//! returns the next server message, and [`Client::request`] waits for the
//! message whose `reply_to` matches while queueing unrelated events for later
//! `recv` calls. A `Ping` is sent every 10 seconds; the matching `Pong`s are
//! swallowed.

use std::{
    collections::{HashSet, VecDeque},
    sync::{
        Arc, Mutex,
        atomic::{AtomicU32, Ordering},
    },
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use futures_util::{SinkExt, StreamExt};
use tokio::{
    sync::mpsc,
    task::JoinHandle,
    time::{Instant, timeout_at},
};
use tokio_tungstenite::tungstenite::Message;
use tracing::{debug, trace, warn};
use tv_proto::{
    Encoding, Frame, PROTOCOL_VERSION, decode, encode,
    v1::{
        self, AvatarInfo, ChatEntry, ClientKind, ErrorCode, ObjectInfo, ServerMessage, Welcome,
        client_message::Body as CBody, server_message::Body as SBody,
    },
};

/// How often the client pings when nothing else asks it to.
pub const PING_INTERVAL: Duration = Duration::from_secs(10);

/// Milliseconds since the Unix epoch.
pub fn now_ms() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis() as i64).unwrap_or(0)
}

/// `NOT_FOUND` for `ERROR_CODE_NOT_FOUND`, or the number for unknown codes.
pub fn error_code_name(code: i32) -> String {
    match ErrorCode::try_from(code) {
        Ok(c) => c.as_str_name().trim_start_matches("ERROR_CODE_").to_string(),
        Err(_) => format!("code {code}"),
    }
}

/// The name of a server message body, for diagnostics.
pub fn body_name(body: &Option<SBody>) -> &'static str {
    match body {
        None => "an empty message",
        Some(b) => match b {
            SBody::Welcome(_) => "Welcome",
            SBody::Snapshot(_) => "Snapshot",
            SBody::AvatarJoined(_) => "AvatarJoined",
            SBody::AvatarLeft(_) => "AvatarLeft",
            SBody::AvatarStates(_) => "AvatarStates",
            SBody::AvatarCorrection(_) => "AvatarCorrection",
            SBody::AvatarUpdated(_) => "AvatarUpdated",
            SBody::ChatMessage(_) => "ChatMessage",
            SBody::ChatHistory(_) => "ChatHistory",
            SBody::ObjectCreated(_) => "ObjectCreated",
            SBody::ObjectUpdated(_) => "ObjectUpdated",
            SBody::ObjectDeleted(_) => "ObjectDeleted",
            SBody::Observation(_) => "Observation",
            SBody::Error(_) => "Error",
            SBody::Kicked(_) => "Kicked",
            SBody::Pong(_) => "Pong",
            SBody::Ack(_) => "Ack",
        },
    }
}

/// What can go wrong talking to a server.
#[derive(Debug, thiserror::Error)]
pub enum ClientError {
    /// The WebSocket could not be opened.
    #[error("could not connect to {url}: {reason}")]
    Connect { url: String, reason: String },
    /// The server answered `Hello` with `Error`.
    #[error("server refused the connection: {} {message}", error_code_name(*code))]
    Refused { code: i32, message: String },
    /// The server sent something the protocol does not allow at this point.
    #[error("protocol error: {0}")]
    Protocol(String),
    /// The connection is gone.
    #[error("connection closed: {0}")]
    Closed(String),
    /// Nothing suitable arrived in time.
    #[error("timed out waiting for {0}")]
    Timeout(String),
    /// The server answered a request with `Error`.
    #[error("server error {}: {message}", error_code_name(*code))]
    Server { code: i32, message: String },
}

impl ClientError {
    /// The protocol error code, for refusals and error replies.
    pub fn code(&self) -> Option<ErrorCode> {
        match self {
            ClientError::Refused { code, .. } | ClientError::Server { code, .. } => {
                ErrorCode::try_from(*code).ok()
            }
            _ => None,
        }
    }
}

/// How to connect and who to be.
#[derive(Clone, Debug)]
pub struct ClientOptions {
    pub url: String,
    pub name: String,
    pub password: String,
    pub token: String,
    pub kind: ClientKind,
    pub encoding: Encoding,
    pub appearance: Option<v1::Appearance>,
    pub client_name: String,
    /// `None` disables the automatic keepalive ping.
    pub ping_interval: Option<Duration>,
    /// Limit for opening the socket and for the whole join sequence.
    pub join_timeout: Duration,
    /// Extra trusted certificates for `wss://` (see [`crate::tls::load_ca_file`]).
    /// Must be empty for `ws://` URLs.
    pub ca_certs: Vec<rustls::pki_types::CertificateDer<'static>>,
}

impl ClientOptions {
    pub fn new(url: impl Into<String>) -> Self {
        ClientOptions {
            url: url.into(),
            name: String::new(),
            password: String::new(),
            token: String::new(),
            kind: ClientKind::Agent,
            encoding: Encoding::Binary,
            appearance: None,
            client_name: format!("tv-cli/{}", env!("CARGO_PKG_VERSION")),
            ping_interval: Some(PING_INTERVAL),
            join_timeout: Duration::from_secs(10),
            ca_certs: Vec::new(),
        }
    }
}

/// The world as the server described it at join time.
#[derive(Clone, Debug, Default)]
pub struct JoinSnapshot {
    /// Avatars from all chunks, in arrival order.
    pub avatars: Vec<AvatarInfo>,
    /// Objects from all chunks, in arrival order.
    pub objects: Vec<ObjectInfo>,
    /// How many `Snapshot` messages arrived.
    pub chunks: usize,
    /// Whether a chunk with `last = true` arrived before `ChatHistory`.
    pub complete: bool,
}

enum Outgoing {
    Frame(Frame),
    Close,
}

enum Incoming {
    Message(Box<ServerMessage>),
    Closed(String),
}

/// A joined session.
pub struct Client {
    out_tx: mpsc::UnboundedSender<Outgoing>,
    in_rx: mpsc::UnboundedReceiver<Incoming>,
    pending: VecDeque<ServerMessage>,
    seq: Arc<AtomicU32>,
    auto_pings: Arc<Mutex<HashSet<u32>>>,
    encoding: Encoding,
    welcome: Welcome,
    snapshot: JoinSnapshot,
    history: Vec<ChatEntry>,
    closed: Option<String>,
    pinger: Option<JoinHandle<()>>,
}

impl Drop for Client {
    fn drop(&mut self) {
        if let Some(p) = self.pinger.take() {
            p.abort();
        }
        // Dropping out_tx makes the writer send a Close frame.
    }
}

fn to_ws(frame: Frame) -> Message {
    match frame {
        Frame::Binary(b) => Message::Binary(b.into()),
        Frame::Text(t) => Message::Text(t.into()),
    }
}

impl Client {
    /// Connects, sends `Hello` and completes the join sequence.
    pub async fn connect(opts: ClientOptions) -> Result<Client, ClientError> {
        let deadline = Instant::now() + opts.join_timeout;
        let url = opts.url.clone();
        let ws = timeout_at(deadline, crate::tls::open(&url, &opts.ca_certs))
            .await
            .map_err(|_| ClientError::Connect { url: url.clone(), reason: "timed out".into() })?
            .map_err(|reason| ClientError::Connect { url: url.clone(), reason })?;
        let (mut sink, mut stream) = ws.split();

        let (out_tx, mut out_rx) = mpsc::unbounded_channel::<Outgoing>();
        let (in_tx, in_rx) = mpsc::unbounded_channel::<Incoming>();
        let auto_pings: Arc<Mutex<HashSet<u32>>> = Arc::default();

        tokio::spawn(async move {
            while let Some(out) = out_rx.recv().await {
                match out {
                    Outgoing::Frame(f) => {
                        if let Err(e) = sink.send(to_ws(f)).await {
                            debug!("send failed: {e}");
                            break;
                        }
                    }
                    Outgoing::Close => break,
                }
            }
            let _ = sink.send(Message::Close(None)).await;
            let _ = sink.flush().await;
        });

        let pings = auto_pings.clone();
        tokio::spawn(async move {
            let reason = loop {
                let frame = match stream.next().await {
                    None => break "the server closed the socket".to_string(),
                    Some(Err(e)) => break e.to_string(),
                    Some(Ok(Message::Binary(b))) => Frame::Binary(b.to_vec()),
                    Some(Ok(Message::Text(t))) => Frame::Text(t.as_str().to_owned()),
                    Some(Ok(Message::Close(cf))) => {
                        break match cf {
                            Some(cf) if !cf.reason.is_empty() => {
                                format!("the server closed the socket ({}: {})", cf.code, cf.reason)
                            }
                            Some(cf) => format!("the server closed the socket ({})", cf.code),
                            None => "the server closed the socket".to_string(),
                        };
                    }
                    Some(Ok(_)) => continue,
                };
                let msg: ServerMessage = match decode(&frame) {
                    Ok(m) => m,
                    Err(e) => {
                        warn!("ignoring an undecodable server frame: {e}");
                        continue;
                    }
                };
                trace!("<- {msg:?}");
                if let Some(SBody::Pong(_)) = msg.body
                    && pings.lock().unwrap().remove(&msg.reply_to)
                {
                    continue;
                }
                if in_tx.send(Incoming::Message(Box::new(msg))).is_err() {
                    break "the client was dropped".to_string();
                }
            };
            let _ = in_tx.send(Incoming::Closed(reason));
            // Keep reading briefly so tungstenite can flush its reply to the
            // server's Close frame (or finish our own close handshake);
            // otherwise the server never sees the socket end.
            let _ = tokio::time::timeout(Duration::from_secs(2), async {
                while let Some(Ok(_)) = stream.next().await {}
            })
            .await;
        });

        let mut client = Client {
            out_tx,
            in_rx,
            pending: VecDeque::new(),
            seq: Arc::new(AtomicU32::new(1)),
            auto_pings,
            encoding: opts.encoding,
            welcome: Welcome::default(),
            snapshot: JoinSnapshot::default(),
            history: Vec::new(),
            closed: None,
            pinger: None,
        };
        client.join(&opts, deadline).await?;
        if let Some(every) = opts.ping_interval {
            client.start_pinger(every);
        }
        Ok(client)
    }

    async fn join(&mut self, opts: &ClientOptions, deadline: Instant) -> Result<(), ClientError> {
        let hello = v1::Hello {
            protocol_version: PROTOCOL_VERSION,
            kind: opts.kind as i32,
            name: opts.name.clone(),
            password: opts.password.clone(),
            token: opts.token.clone(),
            appearance: opts.appearance.clone(),
            client_name: opts.client_name.clone(),
        };
        self.send(CBody::Hello(hello))?;

        let first = self.next_until(deadline, "Welcome").await?;
        match first.body {
            Some(SBody::Welcome(w)) => self.welcome = w,
            Some(SBody::Error(e)) => {
                return Err(ClientError::Refused { code: e.code, message: e.message });
            }
            other => {
                return Err(ClientError::Protocol(format!(
                    "expected Welcome or Error in reply to Hello, got {}",
                    body_name(&other)
                )));
            }
        }

        // Snapshot chunks, then ChatHistory. Anything else that arrives in
        // between is kept for later.
        loop {
            let m = self.next_until(deadline, "the join Snapshot and ChatHistory").await?;
            match m.body {
                Some(SBody::Snapshot(s)) => {
                    self.snapshot.chunks += 1;
                    self.snapshot.avatars.extend(s.avatars);
                    self.snapshot.objects.extend(s.objects);
                    if s.last {
                        self.snapshot.complete = true;
                    }
                }
                Some(SBody::ChatHistory(h)) => {
                    if !self.snapshot.complete {
                        warn!("ChatHistory arrived before the last Snapshot chunk");
                    }
                    self.history = h.entries;
                    return Ok(());
                }
                _ => self.pending.push_back(m),
            }
        }
    }

    fn start_pinger(&mut self, every: Duration) {
        let tx = self.out_tx.downgrade();
        let seq = self.seq.clone();
        let pings = self.auto_pings.clone();
        let encoding = self.encoding;
        self.pinger = Some(tokio::spawn(async move {
            let mut tick = tokio::time::interval_at(Instant::now() + every, every);
            loop {
                tick.tick().await;
                let Some(tx) = tx.upgrade() else { return };
                let s = next_seq(&seq);
                pings.lock().unwrap().insert(s);
                let msg = v1::ClientMessage {
                    seq: s,
                    body: Some(CBody::Ping(v1::Ping { client_time_ms: now_ms() })),
                };
                trace!("-> keepalive {msg:?}");
                if tx.send(Outgoing::Frame(encode(&msg, encoding))).is_err() {
                    return;
                }
            }
        }));
    }

    /// The server's `Welcome`.
    pub fn welcome(&self) -> &Welcome {
        &self.welcome
    }

    /// The avatars and objects present at join time.
    pub fn snapshot(&self) -> &JoinSnapshot {
        &self.snapshot
    }

    /// World chat retained at join time, oldest first.
    pub fn history(&self) -> &[ChatEntry] {
        &self.history
    }

    /// The id of the avatar this connection controls.
    pub fn avatar_id(&self) -> u64 {
        self.welcome.avatar_id
    }

    pub fn user_id(&self) -> &str {
        &self.welcome.user_id
    }

    pub fn encoding(&self) -> Encoding {
        self.encoding
    }

    /// Sends a message and returns the `seq` it was given.
    pub fn send(&mut self, body: CBody) -> Result<u32, ClientError> {
        let seq = next_seq(&self.seq);
        let msg = v1::ClientMessage { seq, body: Some(body) };
        debug!("-> {msg:?}");
        self.send_frame(encode(&msg, self.encoding))?;
        Ok(seq)
    }

    /// Sends a frame as is, for testing how the server handles bad input.
    pub fn send_frame(&mut self, frame: Frame) -> Result<(), ClientError> {
        self.out_tx.send(Outgoing::Frame(frame)).map_err(|_| self.closed_error())
    }

    fn closed_error(&self) -> ClientError {
        ClientError::Closed(self.closed.clone().unwrap_or_else(|| "connection is gone".into()))
    }

    async fn next_incoming(&mut self) -> Result<ServerMessage, ClientError> {
        match self.in_rx.recv().await {
            Some(Incoming::Message(m)) => {
                let m = *m;
                debug!("<- {m:?}");
                Ok(m)
            }
            Some(Incoming::Closed(reason)) => {
                self.closed = Some(reason);
                Err(self.closed_error())
            }
            None => Err(self.closed_error()),
        }
    }

    async fn next_until(
        &mut self,
        deadline: Instant,
        what: &str,
    ) -> Result<ServerMessage, ClientError> {
        timeout_at(deadline, self.next_incoming())
            .await
            .map_err(|_| ClientError::Timeout(what.to_string()))?
    }

    /// The next server message: queued ones first, then from the socket.
    pub async fn recv(&mut self) -> Result<ServerMessage, ClientError> {
        if let Some(m) = self.pending.pop_front() {
            return Ok(m);
        }
        self.next_incoming().await
    }

    /// Like [`recv`](Self::recv) but gives up after `timeout`, returning `None`.
    pub async fn recv_timeout(
        &mut self,
        timeout: Duration,
    ) -> Result<Option<ServerMessage>, ClientError> {
        if let Some(m) = self.pending.pop_front() {
            return Ok(Some(m));
        }
        match tokio::time::timeout(timeout, self.next_incoming()).await {
            Ok(r) => r.map(Some),
            Err(_) => Ok(None),
        }
    }

    /// Sends `body` and waits up to `timeout` for the message whose
    /// `reply_to` is its `seq`. An `Error` reply is returned as
    /// [`ClientError::Server`]. Everything else that arrives meanwhile is
    /// queued for [`recv`](Self::recv).
    pub async fn request(
        &mut self,
        body: CBody,
        timeout: Duration,
    ) -> Result<ServerMessage, ClientError> {
        let seq = self.send(body)?;
        self.reply(seq, timeout).await
    }

    /// Waits for the reply to an already sent `seq`. See [`request`](Self::request).
    pub async fn reply(
        &mut self,
        seq: u32,
        timeout: Duration,
    ) -> Result<ServerMessage, ClientError> {
        let what = format!("a reply to seq {seq}");
        let m = self.wait_for(timeout, &what, |m| m.reply_to == seq).await?;
        match m.body {
            Some(SBody::Error(e)) => Err(ClientError::Server { code: e.code, message: e.message }),
            _ => Ok(m),
        }
    }

    /// Waits up to `timeout` for a message matching `pred`, looking at queued
    /// messages first. Messages that do not match stay queued in order.
    pub async fn wait_for(
        &mut self,
        timeout: Duration,
        what: &str,
        mut pred: impl FnMut(&ServerMessage) -> bool,
    ) -> Result<ServerMessage, ClientError> {
        if let Some(i) = self.pending.iter().position(&mut pred) {
            return Ok(self.pending.remove(i).expect("index is in range"));
        }
        let deadline = Instant::now() + timeout;
        loop {
            let m = self.next_until(deadline, what).await?;
            if pred(&m) {
                return Ok(m);
            }
            self.pending.push_back(m);
        }
    }

    /// Takes every message queued or already received, without waiting.
    pub fn drain(&mut self) -> Vec<ServerMessage> {
        let mut out: Vec<ServerMessage> = self.pending.drain(..).collect();
        while let Ok(i) = self.in_rx.try_recv() {
            match i {
                Incoming::Message(m) => out.push(*m),
                Incoming::Closed(r) => {
                    self.closed = Some(r);
                    break;
                }
            }
        }
        out
    }

    /// Why the connection closed, once it has.
    pub fn closed_reason(&self) -> Option<&str> {
        self.closed.as_deref()
    }

    /// Leaves cleanly: sends a Close frame and waits briefly for the server
    /// to close its side.
    pub async fn close(mut self) {
        if let Some(p) = self.pinger.take() {
            p.abort();
        }
        let _ = self.out_tx.send(Outgoing::Close);
        let _ = tokio::time::timeout(Duration::from_secs(2), async {
            while let Some(i) = self.in_rx.recv().await {
                if let Incoming::Closed(_) = i {
                    break;
                }
            }
        })
        .await;
    }
}

fn next_seq(seq: &AtomicU32) -> u32 {
    loop {
        let s = seq.fetch_add(1, Ordering::Relaxed);
        if s != 0 {
            return s;
        }
    }
}
