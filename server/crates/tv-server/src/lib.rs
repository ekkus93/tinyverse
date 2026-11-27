//! The Tinyverse host server.
//!
//! [`start`] opens a world directory and serves it: `GET /ws` (the protocol),
//! `GET /info` and `GET /health`. One task ([`hub`]) owns the world; one task
//! per connection ([`session`]) speaks the protocol; the store runs on its
//! own thread ([`store`]). The binary `tinyverse-server` wraps this in a CLI
//! ([`cli`]).
//!
//! Tests start a server in-process on port 0:
//!
//! ```no_run
//! # async fn demo(dir: &std::path::Path) -> anyhow::Result<()> {
//! let server = tv_server::start(dir, "127.0.0.1:0".parse()?, Default::default()).await?;
//! println!("{}", server.ws_url());
//! server.shutdown().await?;
//! # Ok(()) }
//! ```

pub mod auth;
pub mod cli;
pub mod config;
pub mod convert;
pub mod hub;
pub mod persist;
pub mod ratelimit;
pub mod session;
pub mod store;

use std::net::SocketAddr;
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use anyhow::Context;
use axum::Router;
use axum::extract::{State, WebSocketUpgrade};
use axum::response::{IntoResponse, Response};
use axum::routing::get;
use serde::Serialize;
use tokio::sync::{mpsc, watch};
use tokio::task::JoinHandle;

use crate::config::{WorldDir, WorldFile};
use crate::hub::{Hub, HubTiming};
use crate::session::{SessionCtx, SessionTiming};
use crate::store::StoreHandle;

/// Milliseconds since the Unix epoch.
pub fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// Frames larger than this are cut off by the WebSocket layer itself; frames
/// between [`convert::MAX_MESSAGE_BYTES`] and this get a proper `Error`.
const WS_HARD_LIMIT: usize = 1024 * 1024;

/// Capacity of the channel from sessions to the world task.
const HUB_QUEUE: usize = 4096;

/// Timing and capacity knobs; the defaults are the protocol's. Tests shorten
/// them.
#[derive(Clone, Debug, Default)]
pub struct Tuning {
    pub session: SessionTiming,
    pub hub: HubTiming,
}

/// A running server.
pub struct ServerHandle {
    addr: SocketAddr,
    shutdown: watch::Sender<bool>,
    task: JoinHandle<anyhow::Result<()>>,
}

impl ServerHandle {
    /// The address it listens on.
    pub fn addr(&self) -> SocketAddr {
        self.addr
    }

    /// `ws://<addr>/ws`.
    pub fn ws_url(&self) -> String {
        format!("ws://{}{}", self.addr, tv_proto::WS_PATH)
    }

    /// `http://<addr>`.
    pub fn http_url(&self) -> String {
        format!("http://{}", self.addr)
    }

    /// Stops accepting connections, closes every session, writes the world
    /// to disk and waits for all of it.
    pub async fn shutdown(self) -> anyhow::Result<()> {
        let _ = self.shutdown.send(true);
        self.task.await.context("server task panicked")?
    }

    /// Serves until `signal` completes (or the server fails), then shuts
    /// down cleanly.
    pub async fn run_until(mut self, signal: impl Future<Output = ()>) -> anyhow::Result<()> {
        tokio::select! {
            () = signal => self.shutdown().await,
            result = &mut self.task => result.context("server task panicked")?,
        }
    }
}

struct App {
    info: InfoStatic,
    users: Arc<AtomicUsize>,
    session: Arc<SessionCtx>,
}

struct InfoStatic {
    name: String,
    description: String,
    max_users: usize,
    password_required: bool,
}

/// The body of `GET /info`.
#[derive(Clone, Debug, Serialize, serde::Deserialize, PartialEq)]
pub struct Info {
    pub name: String,
    pub description: String,
    pub users: usize,
    pub max_users: usize,
    pub protocol_version: u32,
    pub server_version: String,
    pub password_required: bool,
}

/// Opens the world in `dir` and starts serving it on `bind` (port 0 picks a
/// free port; see [`ServerHandle::addr`]).
pub async fn start(dir: &Path, bind: SocketAddr, tuning: Tuning) -> anyhow::Result<ServerHandle> {
    let dir = WorldDir::new(dir);
    let file = WorldFile::load(&dir)?;
    let config = file.world_config();

    let db = dir.db();
    let store = tokio::task::spawn_blocking(move || tv_store::Store::open(&db))
        .await?
        .with_context(|| format!("cannot open {}", dir.db().display()))?;
    let (store, _store_thread) = StoreHandle::spawn(store);
    let loaded = store.call(|s| s.load_world()).await?.context("cannot load the world")?;
    tracing::info!(
        objects = loaded.objects.len(),
        next_entity_id = loaded.next_entity_id,
        "world loaded"
    );
    let saved_next_id = loaded.next_entity_id;
    let world = tv_world::World::new(config.clone(), loaded.objects, loaded.next_entity_id);

    let listener = tokio::net::TcpListener::bind(bind)
        .await
        .with_context(|| format!("cannot listen on {bind}"))?;
    let addr = listener.local_addr()?;

    let (shutdown_tx, shutdown_rx) = watch::channel(false);
    let (hub_tx, hub_rx) = mpsc::channel(HUB_QUEUE);
    let users = Arc::new(AtomicUsize::new(0));
    let hub = Hub::new(world, saved_next_id, store.clone(), users.clone());
    let hub_task = tokio::spawn(hub.run(hub_rx, shutdown_rx.clone(), tuning.hub.clone()));

    let session = Arc::new(SessionCtx {
        hub: hub_tx,
        store,
        password_hash: file.password_hash.clone(),
        default_role: file.default_role.into(),
        max_users: config.limits.max_users,
        max_name_chars: config.limits.max_name_chars,
        users: users.clone(),
        timing: tuning.session.clone(),
        shutdown: shutdown_rx.clone(),
    });
    let app = Arc::new(App {
        info: InfoStatic {
            name: config.name.clone(),
            description: config.description.clone(),
            max_users: config.limits.max_users,
            password_required: file.password_hash.is_some(),
        },
        users,
        session,
    });
    let router = Router::new()
        .route(tv_proto::WS_PATH, get(ws_handler))
        .route("/info", get(info_handler))
        .route("/health", get(|| async { "ok" }))
        .with_state(app);

    let mut serve_shutdown = shutdown_rx;
    let task = tokio::spawn(async move {
        let served = axum::serve(listener, router)
            .with_graceful_shutdown(async move {
                let _ = serve_shutdown.wait_for(|stop| *stop).await;
            })
            .await;
        // The hub stops on the same signal and saves the world.
        hub_task.await.context("world task panicked")?;
        served.context("HTTP server failed")
    });

    Ok(ServerHandle { addr, shutdown: shutdown_tx, task })
}

async fn ws_handler(State(app): State<Arc<App>>, upgrade: WebSocketUpgrade) -> Response {
    let ctx = app.session.clone();
    upgrade
        .max_message_size(WS_HARD_LIMIT)
        .max_frame_size(WS_HARD_LIMIT)
        .on_upgrade(move |socket| session::run(socket, ctx))
}

async fn info_handler(State(app): State<Arc<App>>) -> impl IntoResponse {
    axum::Json(Info {
        name: app.info.name.clone(),
        description: app.info.description.clone(),
        users: app.users.load(Ordering::Relaxed),
        max_users: app.info.max_users,
        protocol_version: tv_proto::PROTOCOL_VERSION,
        server_version: convert::SERVER_VERSION.to_owned(),
        password_required: app.info.password_required,
    })
}
