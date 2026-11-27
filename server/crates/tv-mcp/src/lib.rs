//! `tv-mcp`: a Model Context Protocol server that lets an MCP-capable
//! assistant join a Tinyverse world as an agent avatar.
//!
//! The host launches it as a subprocess and speaks MCP over stdin/stdout. It
//! holds one WebSocket connection to the world ([`world::Bridge`]), keeps a
//! local model of what is there and a rolling log of chat and events
//! ([`model`]), and exposes the world as tools ([`tools`]).

mod args;
pub mod format;
pub mod model;
pub mod rpc;
pub mod tls;
pub mod tools;
pub mod world;

pub use world::{Bridge, Config};

use tokio::io::{AsyncRead, AsyncWrite};

/// Starts connecting to the world and serves MCP on `input`/`output` until
/// `input` is closed. The world connection is attempted in the background;
/// tools report why if it is not available.
pub async fn run<R, W>(config: Config, input: R, output: W) -> std::io::Result<()>
where
    R: AsyncRead + Unpin,
    W: AsyncWrite + Unpin + Send + 'static,
{
    let bridge = Bridge::new(config);
    bridge.start();
    rpc::serve(bridge, input, output).await
}
