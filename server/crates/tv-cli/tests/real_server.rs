//! `smoke` against the real `tinyverse-server` binary, if it has been built
//! into the same cargo target directory (`cargo build -p tv-server`). The
//! server is not a Cargo dependency of this crate, so the test skips with a
//! message when the binary is missing.

use std::{
    io::{BufRead, BufReader},
    path::PathBuf,
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};

use tv_cli::{
    cli::EXIT_OK,
    smoke::{self, CHECKS, SmokeOptions},
};

/// `target/<profile>/tinyverse-server`, next to `target/<profile>/deps/<this test>`.
fn server_binary() -> Option<PathBuf> {
    let exe = std::env::current_exe().ok()?;
    let profile_dir = exe.parent()?.parent()?;
    let bin = profile_dir.join(format!("tinyverse-server{}", std::env::consts::EXE_SUFFIX));
    bin.is_file().then_some(bin)
}

/// Stops the server with SIGTERM (then SIGKILL as a fallback) and removes
/// the world directory, even when the test panics.
struct Server {
    child: Child,
    dir: PathBuf,
}

impl Drop for Server {
    fn drop(&mut self) {
        let pid = self.child.id().to_string();
        let _ = Command::new("kill").args(["-TERM", &pid]).status();
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            match self.child.try_wait() {
                Ok(Some(_)) => break,
                Ok(None) if Instant::now() < deadline => {
                    std::thread::sleep(Duration::from_millis(50))
                }
                _ => {
                    let _ = self.child.kill();
                    let _ = self.child.wait();
                    break;
                }
            }
        }
        let _ = std::fs::remove_dir_all(&self.dir);
    }
}

#[tokio::test]
async fn smoke_passes_against_the_real_server() {
    let Some(bin) = server_binary() else {
        eprintln!(
            "SKIPPED: tinyverse-server is not built in this target directory; \
             run `cargo build -p tv-server` first to check smoke against the real server"
        );
        return;
    };
    let nanos = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap();
    let dir = std::env::temp_dir().join(format!(
        "tv-cli-real-{}-{}",
        std::process::id(),
        nanos.as_nanos()
    ));
    let world = dir.join("world");
    std::fs::create_dir_all(&dir).unwrap();

    let init = Command::new(&bin).arg("init").arg(&world).output().unwrap();
    assert!(init.status.success(), "init failed: {}", String::from_utf8_lossy(&init.stderr));

    let child = Command::new(&bin)
        .arg("run")
        .arg(&world)
        .args(["--bind", "127.0.0.1", "--port", "0"])
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let mut server = Server { child, dir };

    // The first stdout line is `listening on ws://...`.
    let stdout = server.child.stdout.take().unwrap();
    let url = tokio::task::spawn_blocking(move || {
        let mut lines = BufReader::new(stdout).lines();
        lines.find_map(|l| l.ok()?.strip_prefix("listening on ").map(str::to_string))
    });
    let url = tokio::time::timeout(Duration::from_secs(20), url)
        .await
        .expect("the server did not report its address")
        .unwrap()
        .expect("the server exited before listening");

    let mut opts = SmokeOptions::new(&url);
    opts.owner_token =
        Some(std::fs::read_to_string(world.join("owner.token")).unwrap().trim().into());
    let mut out = Vec::new();
    let report = smoke::run(opts, &mut out).await;
    let out = String::from_utf8(out).unwrap();
    println!("{out}");
    for name in CHECKS {
        assert!(report.passed(name), "{name} did not pass against {}:\n{out}", bin.display());
    }
    assert_eq!(report.exit_code(), EXIT_OK);
    drop(server);
}
