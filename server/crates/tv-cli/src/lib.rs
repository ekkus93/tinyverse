//! `tv-cli`: a command-line test client, bot and end-to-end smoke tester for
//! the Tinyverse protocol.
//!
//! The library half holds the async [`Client`](client::Client) and the
//! pieces the binary is built from, so tests can drive them directly.

pub mod bots;
pub mod cli;
pub mod client;
pub mod format;
pub mod motion;
pub mod parse;
pub mod repl;
pub mod smoke;

pub use client::{Client, ClientError, ClientOptions};
