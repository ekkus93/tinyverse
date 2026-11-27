//! The `tv-cli` binary. See `tv-cli --help`.

use clap::Parser;
use tracing_subscriber::EnvFilter;
use tv_cli::cli::{Cli, EXIT_FAILED, EXIT_OK, run};

#[tokio::main]
async fn main() {
    let cli = match Cli::try_parse() {
        Ok(cli) => cli,
        Err(e) => {
            let _ = e.print();
            // Usage errors are command failures (1); 2 is reserved for
            // "could not connect or was refused".
            std::process::exit(if e.use_stderr() { EXIT_FAILED } else { EXIT_OK });
        }
    };
    let level = match cli.global.verbose {
        0 => "warn",
        1 => "tv_cli=debug,info",
        _ => "tv_cli=trace,debug",
    };
    tracing_subscriber::fmt()
        .with_env_filter(
            EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new(level)),
        )
        .with_writer(std::io::stderr)
        .init();
    let code = run(cli, &mut std::io::stdout()).await;
    std::process::exit(code);
}
