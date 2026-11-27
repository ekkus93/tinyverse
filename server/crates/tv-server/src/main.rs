use clap::Parser;
use tv_server::cli::{Cli, execute, init_logging};

fn main() -> std::process::ExitCode {
    let cli = Cli::parse();
    init_logging(cli.verbose);
    let mut stdout = std::io::stdout();
    match execute(cli, &mut stdout) {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("error: {e:#}");
            std::process::ExitCode::FAILURE
        }
    }
}
