use std::io::Write;

use clap::Parser;
use tv_server::cli::{Cli, execute, init_logging};

fn main() -> std::process::ExitCode {
    // Rust's runtime ignores SIGPIPE, so writing to a closed stdout or stderr
    // fails with EPIPE instead of killing the process. Nothing below may
    // panic on such a failure: `println!`/`eprintln!` would, so they are not
    // used; logs go through a writer that ignores errors.
    let cli = Cli::parse();
    init_logging(cli.verbose);
    let mut stdout = std::io::stdout();
    match execute(cli, &mut stdout) {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(e) => {
            let _ = writeln!(std::io::stderr(), "error: {e:#}");
            std::process::ExitCode::FAILURE
        }
    }
}
