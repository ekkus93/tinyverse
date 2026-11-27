//! `tv-mcp` command line: an MCP stdio server that joins a Tinyverse world.
//! stdout carries only MCP messages; all logging goes to stderr.

use std::path::PathBuf;
use std::process::ExitCode;

use clap::{ArgAction, Parser};
use tracing_subscriber::EnvFilter;

/// Lets an MCP-capable assistant join a Tinyverse world as an agent avatar.
/// Speaks MCP on stdin/stdout; logs to stderr.
#[derive(Parser, Debug)]
#[command(name = "tv-mcp", version)]
struct Cli {
    /// WebSocket URL of the world.
    #[arg(long, default_value = "ws://127.0.0.1:7878/ws")]
    url: String,
    /// Agent token from `tinyverse-server token create`. Also read from the
    /// TINYVERSE_TOKEN environment variable.
    #[arg(long, conflicts_with = "token_file")]
    token: Option<String>,
    /// File holding the agent token (surrounding whitespace is ignored).
    #[arg(long)]
    token_file: Option<PathBuf>,
    /// Display name of the avatar. Optional with a token for an existing user.
    #[arg(long)]
    name: Option<String>,
    /// The world's join password, if it has one and no token is used. Also
    /// read from TINYVERSE_PASSWORD.
    #[arg(long)]
    password: Option<String>,
    /// More logging on stderr; repeat for more.
    #[arg(short, long, action = ArgAction::Count)]
    verbose: u8,
}

fn main() -> ExitCode {
    let cli = Cli::parse();

    let filter = match std::env::var("RUST_LOG") {
        Ok(f) if !f.is_empty() => EnvFilter::new(f),
        _ => EnvFilter::new(match cli.verbose {
            0 => "warn,tv_mcp=info",
            1 => "info,tv_mcp=debug",
            _ => "debug,tv_mcp=trace",
        }),
    };
    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_ansi(false)
        .with_env_filter(filter)
        .init();

    let env =
        |k: &str| std::env::var(k).ok().map(|v| v.trim().to_string()).filter(|v| !v.is_empty());
    let token = match (&cli.token, &cli.token_file) {
        (Some(t), _) => Some(t.trim().to_string()),
        (None, Some(path)) => match std::fs::read_to_string(path) {
            Ok(t) if !t.trim().is_empty() => Some(t.trim().to_string()),
            Ok(_) => {
                eprintln!("tv-mcp: token file {} is empty", path.display());
                return ExitCode::from(2);
            }
            Err(e) => {
                eprintln!("tv-mcp: cannot read token file {}: {e}", path.display());
                return ExitCode::from(2);
            }
        },
        (None, None) => env("TINYVERSE_TOKEN"),
    };

    let mut config = tv_mcp::Config::new(cli.url);
    config.token = token.filter(|t| !t.is_empty());
    config.name = cli.name.filter(|n| !n.trim().is_empty());
    config.password = cli.password.or_else(|| env("TINYVERSE_PASSWORD"));

    let runtime = match tokio::runtime::Runtime::new() {
        Ok(r) => r,
        Err(e) => {
            eprintln!("tv-mcp: cannot start the async runtime: {e}");
            return ExitCode::FAILURE;
        }
    };
    tracing::info!("tv-mcp {} starting; world {}", env!("CARGO_PKG_VERSION"), config.url);
    let result = runtime.block_on(tv_mcp::run(config, tokio::io::stdin(), tokio::io::stdout()));
    // Do not wait for background tasks (reconnects, pings) once stdin is closed.
    runtime.shutdown_background();
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("tv-mcp: {e}");
            ExitCode::FAILURE
        }
    }
}
