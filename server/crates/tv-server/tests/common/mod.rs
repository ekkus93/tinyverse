//! Helpers shared by the integration tests: a temporary world, an
//! in-process server and a raw WebSocket client.
#![allow(dead_code)]

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use clap::Parser;
use futures_util::{SinkExt, StreamExt};
use tokio::net::TcpStream;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::{MaybeTlsStream, WebSocketStream};
use tv_proto::v1::{self, client_message::Body as C, server_message::Body as S};
use tv_proto::{Encoding, Frame};
use tv_server::cli::{Cli, Inputs, execute_with};
use tv_server::config::{WorldDir, WorldFile};
use tv_server::{ServerHandle, Tuning};
use tv_store::Store;
use tv_world::{ClientKind, Role, UserId};

pub const WAIT: Duration = Duration::from_secs(5);

/// A world directory made by `init`, with the default limits.
pub struct TestWorld {
    _tmp: tempfile::TempDir,
    dir: PathBuf,
}

impl TestWorld {
    pub fn new() -> Self {
        Self::with(|_| {})
    }

    /// A world whose speed limits let a test move an avatar far in one step.
    pub fn fast() -> Self {
        Self::with(|f| {
            f.limits.walk_speed = Some(1000.0);
            f.limits.run_speed = Some(1000.0);
            f.limits.fly_speed = Some(1000.0);
        })
    }

    pub fn with(edit: impl FnOnce(&mut WorldFile)) -> Self {
        let tmp = tempfile::tempdir().unwrap();
        let dir = tmp.path().join("world");
        let cli = Cli::try_parse_from([
            "tinyverse-server".as_ref(),
            "init".as_ref(),
            dir.as_os_str(),
            "--name".as_ref(),
            "Test World".as_ref(),
        ])
        .unwrap();
        // Never inherit TINYVERSE_PASSWORD from the developer's shell.
        let mut stdin = std::io::empty();
        let mut inputs = Inputs { stdin: &mut stdin, env_password: None };
        execute_with(cli, &mut inputs, &mut Vec::new()).unwrap();
        let paths = WorldDir::new(&dir);
        let mut file = WorldFile::load(&paths).unwrap();
        edit(&mut file);
        std::fs::write(paths.toml(), file.to_toml()).unwrap();
        TestWorld { _tmp: tmp, dir }
    }

    pub fn path(&self) -> &Path {
        &self.dir
    }

    pub fn owner_token(&self) -> String {
        std::fs::read_to_string(WorldDir::new(&self.dir).owner_token()).unwrap().trim().to_owned()
    }

    pub fn store(&self) -> Store {
        Store::open(&WorldDir::new(&self.dir).db()).unwrap()
    }

    /// Creates a user directly in the store and returns its id and token.
    pub fn create_token(&self, name: &str, kind: ClientKind, role: Role) -> (UserId, String) {
        let (user, token) = self.store().create_user(name, kind, role, 1).unwrap();
        (user.id, token)
    }

    pub async fn start(&self) -> ServerHandle {
        self.start_with(Tuning::default()).await
    }

    pub async fn start_with(&self, tuning: Tuning) -> ServerHandle {
        tv_server::start(&self.dir, "127.0.0.1:0".parse().unwrap(), tuning).await.unwrap()
    }
}

pub fn hello(name: &str) -> v1::Hello {
    v1::Hello {
        protocol_version: tv_proto::PROTOCOL_VERSION,
        kind: v1::ClientKind::Human as i32,
        name: name.to_owned(),
        client_name: "tv-server tests".into(),
        ..Default::default()
    }
}

pub fn with_token(name: &str, token: &str) -> v1::Hello {
    v1::Hello { token: token.to_owned(), ..hello(name) }
}

pub fn v(x: f32, y: f32, z: f32) -> v1::Vec3 {
    v1::Vec3 { x, y, z }
}

pub fn state_at(x: f32, y: f32, z: f32) -> C {
    C::AvatarState(v1::AvatarState {
        transform: Some(v1::AvatarTransform {
            position: Some(v(x, y, z)),
            move_mode: v1::MoveMode::Walk as i32,
            ..Default::default()
        }),
    })
}

pub fn box_at(x: f32, y: f32, z: f32) -> v1::ObjectSpec {
    v1::ObjectSpec {
        name: "box".into(),
        shape: v1::Shape::Box as i32,
        position: Some(v(x, y, z)),
        rotation: Some(v1::Quat { x: 0.0, y: 0.0, z: 0.0, w: 1.0 }),
        scale: Some(v(1.0, 1.0, 1.0)),
        color: Some(v1::Color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 }),
        material: v1::Material::Matte as i32,
        solid: true,
        locked: false,
    }
}

/// Whether the connection is still open after a read attempt.
#[derive(Debug)]
pub enum Got {
    Msg(Box<v1::ServerMessage>),
    Closed,
    Timeout,
}

pub struct Client {
    ws: WebSocketStream<MaybeTlsStream<TcpStream>>,
    pub encoding: Encoding,
    seq: u32,
}

impl Client {
    pub async fn connect(server: &ServerHandle, encoding: Encoding) -> Client {
        let (ws, _) = tokio_tungstenite::connect_async(server.ws_url()).await.unwrap();
        Client { ws, encoding, seq: 0 }
    }

    /// Connects to `url` (`ws://` or `wss://`), trusting `tls` for `wss://`.
    pub async fn connect_url(
        url: &str,
        encoding: Encoding,
        tls: Option<Arc<rustls::ClientConfig>>,
    ) -> Result<Client, tokio_tungstenite::tungstenite::Error> {
        let connector = tls.map(tokio_tungstenite::Connector::Rustls);
        let (ws, _) =
            tokio_tungstenite::connect_async_tls_with_config(url, None, false, connector).await?;
        Ok(Client { ws, encoding, seq: 0 })
    }

    pub async fn send_frame(&mut self, frame: Frame) {
        let msg = match frame {
            Frame::Binary(b) => Message::Binary(b.into()),
            Frame::Text(t) => Message::Text(t.into()),
        };
        self.ws.send(msg).await.unwrap();
    }

    pub async fn send_msg(&mut self, msg: &v1::ClientMessage) {
        self.send_frame(tv_proto::encode(msg, self.encoding)).await;
    }

    /// Sends a body with the next seq and returns that seq.
    pub async fn send(&mut self, body: C) -> u32 {
        self.seq += 1;
        let seq = self.seq;
        self.send_msg(&v1::ClientMessage { seq, body: Some(body) }).await;
        seq
    }

    pub async fn send_text(&mut self, text: &str) {
        self.ws.send(Message::Text(text.to_owned().into())).await.unwrap();
    }

    pub async fn close(mut self) {
        let _ = self.ws.close(None).await;
    }

    pub async fn next(&mut self, timeout: Duration) -> Got {
        let deadline = tokio::time::Instant::now() + timeout;
        loop {
            let item = match tokio::time::timeout_at(deadline, self.ws.next()).await {
                Err(_) => return Got::Timeout,
                Ok(None) | Ok(Some(Err(_))) | Ok(Some(Ok(Message::Close(_)))) => {
                    return Got::Closed;
                }
                Ok(Some(Ok(m))) => m,
            };
            let frame = match item {
                Message::Text(t) => Frame::Text(t.as_str().to_owned()),
                Message::Binary(b) => Frame::Binary(b.to_vec()),
                _ => continue,
            };
            assert_eq!(frame.encoding(), self.encoding, "server answered in the wrong encoding");
            let msg: v1::ServerMessage = tv_proto::decode(&frame).expect("server frame decodes");
            return Got::Msg(Box::new(msg));
        }
    }

    pub async fn recv(&mut self) -> v1::ServerMessage {
        match self.next(WAIT).await {
            Got::Msg(m) => *m,
            other => panic!("expected a message, got {other:?}"),
        }
    }

    /// The next message matching `pred`, skipping the others.
    pub async fn recv_where(
        &mut self,
        what: &str,
        pred: impl Fn(&v1::ServerMessage) -> bool,
    ) -> v1::ServerMessage {
        let deadline = tokio::time::Instant::now() + WAIT;
        loop {
            let left = deadline.saturating_duration_since(tokio::time::Instant::now());
            match self.next(left).await {
                Got::Msg(m) if pred(&m) => return *m,
                Got::Msg(_) => continue,
                other => panic!("waiting for {what}: {other:?}"),
            }
        }
    }

    /// Everything that arrives within `duration`. Panics if the connection closes.
    pub async fn collect(&mut self, duration: Duration) -> Vec<v1::ServerMessage> {
        let deadline = tokio::time::Instant::now() + duration;
        let mut out = Vec::new();
        loop {
            let left = deadline.saturating_duration_since(tokio::time::Instant::now());
            match self.next(left).await {
                Got::Msg(m) => out.push(*m),
                Got::Timeout => return out,
                Got::Closed => panic!("connection closed while collecting; got {out:?}"),
            }
        }
    }

    /// Waits for the server to close the connection, returning what came first.
    pub async fn expect_closed(&mut self) -> Vec<v1::ServerMessage> {
        let deadline = tokio::time::Instant::now() + WAIT;
        let mut out = Vec::new();
        loop {
            let left = deadline.saturating_duration_since(tokio::time::Instant::now());
            match self.next(left).await {
                Got::Msg(m) => out.push(*m),
                Got::Closed => return out,
                Got::Timeout => panic!("connection did not close; got {out:?}"),
            }
        }
    }

    pub async fn error(&mut self) -> (u32, v1::Error) {
        let msg = self.recv_where("Error", |m| matches!(m.body, Some(S::Error(_)))).await;
        let Some(S::Error(e)) = msg.body else { unreachable!() };
        (msg.reply_to, e)
    }
}

/// A joined connection and what the server sent on joining.
pub struct Session {
    pub client: Client,
    pub welcome: v1::Welcome,
    pub snapshot: Vec<v1::Snapshot>,
    pub history: Vec<v1::ChatEntry>,
}

impl Session {
    pub fn avatar_id(&self) -> u64 {
        self.welcome.avatar_id
    }
    pub fn user_id(&self) -> &str {
        &self.welcome.user_id
    }
    pub fn snapshot_avatars(&self) -> Vec<v1::AvatarInfo> {
        self.snapshot.iter().flat_map(|s| s.avatars.clone()).collect()
    }
    pub fn snapshot_objects(&self) -> Vec<v1::ObjectInfo> {
        self.snapshot.iter().flat_map(|s| s.objects.clone()).collect()
    }
}

impl std::ops::Deref for Session {
    type Target = Client;
    fn deref(&self) -> &Client {
        &self.client
    }
}

impl std::ops::DerefMut for Session {
    fn deref_mut(&mut self) -> &mut Client {
        &mut self.client
    }
}

/// Sends `hello` and reads Welcome, every Snapshot chunk and ChatHistory.
pub async fn join_with(server: &ServerHandle, encoding: Encoding, hello: v1::Hello) -> Session {
    join_client(Client::connect(server, encoding).await, hello).await
}

/// Sends `hello` on a connected client and reads the join sequence.
pub async fn join_client(mut client: Client, hello: v1::Hello) -> Session {
    let seq = client.send(C::Hello(hello)).await;
    let msg = client.recv().await;
    assert_eq!(msg.reply_to, seq, "Welcome carries the Hello's seq");
    let Some(S::Welcome(welcome)) = msg.body else { panic!("expected Welcome, got {msg:?}") };
    let mut snapshot = Vec::new();
    loop {
        let msg = client.recv().await;
        let Some(S::Snapshot(chunk)) = msg.body else { panic!("expected Snapshot, got {msg:?}") };
        let last = chunk.last;
        snapshot.push(chunk);
        if last {
            break;
        }
    }
    let msg = client.recv().await;
    let Some(S::ChatHistory(history)) = msg.body else {
        panic!("expected ChatHistory, got {msg:?}")
    };
    Session { client, welcome, snapshot, history: history.entries }
}

pub async fn join(server: &ServerHandle, name: &str) -> Session {
    join_with(server, Encoding::Json, hello(name)).await
}

/// Sends `hello`, expects an Error and then the socket closing.
pub async fn refused(server: &ServerHandle, encoding: Encoding, hello: v1::Hello) -> v1::Error {
    let mut client = Client::connect(server, encoding).await;
    let seq = client.send(C::Hello(hello)).await;
    let msg = client.recv().await;
    assert_eq!(msg.reply_to, seq);
    let Some(S::Error(error)) = msg.body else { panic!("expected Error, got {msg:?}") };
    let rest = client.expect_closed().await;
    assert!(rest.is_empty(), "nothing after the Error: {rest:?}");
    error
}

pub fn code(e: &v1::Error) -> v1::ErrorCode {
    v1::ErrorCode::try_from(e.code).unwrap()
}

pub fn is_avatar_states(m: &v1::ServerMessage) -> bool {
    matches!(m.body, Some(S::AvatarStates(_)))
}

/// The position of `avatar` if `m` is an AvatarStates batch holding it.
pub fn state_of(m: &v1::ServerMessage, avatar: u64) -> Option<v1::Vec3> {
    match &m.body {
        Some(S::AvatarStates(s)) => s
            .states
            .iter()
            .find(|e| e.avatar_id == avatar)
            .and_then(|e| e.transform.as_ref()?.position),
        _ => None,
    }
}

/// Moves `session` to `x,0,z` and waits until `watcher` has seen it there.
pub async fn move_and_wait(session: &mut Session, watcher: &mut Session, x: f32, z: f32) {
    session.send(state_at(x, 0.0, z)).await;
    let id = session.avatar_id();
    watcher.recv_where("the move", |m| state_of(m, id).is_some_and(|p| p.x == x && p.z == z)).await;
}

/// A minimal HTTP/1.1 GET; returns status code and body.
pub async fn http_get(server: &ServerHandle, path: &str) -> (u16, String) {
    let stream = TcpStream::connect(server.addr()).await.unwrap();
    http_request(stream, &server.addr().to_string(), path).await
}

/// The same over TLS, trusting `tls`.
pub async fn https_get(
    server: &ServerHandle,
    path: &str,
    tls: Arc<rustls::ClientConfig>,
) -> (u16, String) {
    let tcp = TcpStream::connect(server.addr()).await.unwrap();
    let name = rustls::pki_types::ServerName::try_from("localhost").unwrap();
    let stream = tokio_rustls::TlsConnector::from(tls).connect(name, tcp).await.unwrap();
    http_request(stream, "localhost", path).await
}

/// A client config that trusts exactly the certificates in `cert_pem`.
pub fn trusting(cert_pem: &Path) -> Arc<rustls::ClientConfig> {
    use rustls::pki_types::CertificateDer;
    use rustls::pki_types::pem::PemObject;
    let mut roots = rustls::RootCertStore::empty();
    for cert in CertificateDer::pem_file_iter(cert_pem).unwrap() {
        roots.add(cert.unwrap()).unwrap();
    }
    let config = rustls::ClientConfig::builder_with_provider(tv_server::tls::provider())
        .with_safe_default_protocol_versions()
        .unwrap()
        .with_root_certificates(roots)
        .with_no_client_auth();
    Arc::new(config)
}

async fn http_request<S>(mut stream: S, host: &str, path: &str) -> (u16, String)
where
    S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin,
{
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    let request = format!("GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut response = Vec::new();
    // A TLS peer may close without close_notify after the response.
    let _ = stream.read_to_end(&mut response).await;
    let text = String::from_utf8(response).unwrap();
    let (head, body) = text.split_once("\r\n\r\n").unwrap();
    let status = head.split_whitespace().nth(1).unwrap().parse().unwrap();
    (status, body.to_owned())
}
