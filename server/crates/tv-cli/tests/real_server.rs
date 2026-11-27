//! `smoke` and the TLS failure cases against the real `tinyverse-server`
//! binary, if it has been built into the same cargo target directory
//! (`cargo build -p tv-server`). The server is not a Cargo dependency of this
//! crate, so the tests skip with a message when the binary is missing.

mod common;

use std::{
    io::{BufRead, BufReader},
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::mpsc,
    time::{Duration, Instant},
};

use tv_cli::{
    Client, ClientError, ClientOptions,
    cli::{EXIT_CONNECT, EXIT_OK},
    smoke::{self, CHECKS, SmokeOptions},
    tls::load_ca_file,
};

/// `target/<profile>/tinyverse-server`, next to `target/<profile>/deps/<this test>`.
fn server_binary() -> Option<PathBuf> {
    let exe = std::env::current_exe().ok()?;
    let profile_dir = exe.parent()?.parent()?;
    let bin = profile_dir.join(format!("tinyverse-server{}", std::env::consts::EXE_SUFFIX));
    if bin.is_file() {
        Some(bin)
    } else {
        eprintln!(
            "SKIPPED: tinyverse-server is not built in {}; run `cargo build -p tv-server` \
             first to check tv-cli against the real server",
            profile_dir.display()
        );
        None
    }
}

/// A running server on a fresh world. Dropping it stops the server with
/// SIGTERM (then SIGKILL as a fallback) and removes the world, even when the
/// test panics.
struct Server {
    child: Child,
    dir: PathBuf,
    world: PathBuf,
    url: String,
}

impl Server {
    /// Initialises a world in a temp directory (with a self-signed
    /// certificate when `tls`) and runs it on a free local port.
    fn start(bin: &Path, tls: bool) -> Server {
        let nanos = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap();
        let dir = std::env::temp_dir().join(format!(
            "tv-cli-real-{}-{}",
            std::process::id(),
            nanos.as_nanos()
        ));
        let world = dir.join("world");
        std::fs::create_dir_all(&dir).unwrap();
        let ok = |args: &[&std::ffi::OsStr]| {
            let out = Command::new(bin).args(args).output().unwrap();
            assert!(out.status.success(), "{args:?}: {}", String::from_utf8_lossy(&out.stderr));
        };
        ok(&["init".as_ref(), world.as_os_str()]);
        if tls {
            ok(&["tls".as_ref(), "self-signed".as_ref(), world.as_os_str()]);
        }
        let mut child = Command::new(bin)
            .arg("run")
            .arg(&world)
            .args(["--bind", "127.0.0.1", "--port", "0"])
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        // stdout announces `listening on ws[s]://...`; keep draining it after
        // that so the server never writes into a closed pipe.
        let stdout = child.stdout.take().unwrap();
        let (tx, rx) = mpsc::channel();
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines().map_while(Result::ok) {
                if let Some(url) = line.strip_prefix("listening on ") {
                    let _ = tx.send(url.to_string());
                }
            }
        });
        let mut server = Server { child, dir, world, url: String::new() };
        server.url = rx
            .recv_timeout(Duration::from_secs(20))
            .expect("the server did not report its address");
        let scheme = if tls { "wss://" } else { "ws://" };
        assert!(server.url.starts_with(scheme), "{tls} world listens on {}", server.url);
        server
    }

    fn cert(&self) -> PathBuf {
        self.world.join("cert.pem")
    }

    /// The certificate's pin, from `tinyverse-server tls fingerprint`.
    fn pin(&self, bin: &Path) -> String {
        let out = Command::new(bin).args(["tls", "fingerprint"]).arg(&self.world).output().unwrap();
        let out = String::from_utf8(out.stdout).unwrap();
        let hex = out.lines().find_map(|l| l.strip_prefix("tls certificate sha256="));
        hex.unwrap_or_else(|| panic!("no pin in {out:?}")).to_owned()
    }

    fn owner_token(&self) -> String {
        std::fs::read_to_string(self.world.join("owner.token")).unwrap().trim().into()
    }

    /// host:port of the server.
    fn addr(&self) -> &str {
        let rest = self.url.split_once("://").unwrap().1;
        rest.split('/').next().unwrap()
    }
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

async fn smoke_all_pass(server: &Server, ca: Option<&Path>) {
    let mut opts = SmokeOptions::new(&server.url);
    opts.owner_token = Some(server.owner_token());
    if let Some(ca) = ca {
        opts.ca_certs = load_ca_file(ca).unwrap();
    }
    let mut out = Vec::new();
    let report = smoke::run(opts, &mut out).await;
    let out = String::from_utf8(out).unwrap();
    println!("{out}");
    for name in CHECKS {
        assert!(report.passed(name), "{name} did not pass against {}:\n{out}", server.url);
    }
    assert_eq!(report.exit_code(), EXIT_OK);
}

/// The reason a library connection fails, for checking the message.
async fn connect_error(url: &str, ca: Option<&Path>) -> String {
    let mut o = ClientOptions::new(url);
    o.name = "tls-probe".into();
    if let Some(ca) = ca {
        o.ca_certs = load_ca_file(ca).unwrap();
    }
    match Client::connect(o).await {
        Ok(_) => panic!("{url} should not have connected"),
        Err(e @ ClientError::Connect { .. }) => e.to_string(),
        Err(e) => panic!("{url}: expected a connect error, got {e:?}"),
    }
}

#[tokio::test]
async fn smoke_passes_against_the_real_server() {
    let Some(bin) = server_binary() else { return };
    let server = Server::start(&bin, false);
    smoke_all_pass(&server, None).await;
}

#[tokio::test]
async fn smoke_passes_over_wss_with_a_ca_file() {
    let Some(bin) = server_binary() else { return };
    let server = Server::start(&bin, true);
    smoke_all_pass(&server, Some(&server.cert())).await;

    // The CLI too, in both encodings.
    let cert = server.cert();
    for json in [false, true] {
        let mut args = vec!["--url", &server.url, "--ca-file", cert.to_str().unwrap()];
        if json {
            args.push("--json");
        }
        args.extend(["say", "hello over tls"]);
        let (code, out) = common::run_cli(&args).await;
        assert_eq!(code, EXIT_OK, "{args:?}");
        assert!(out.contains("hello over tls"), "{out}");
    }
}

#[tokio::test]
async fn a_pinned_url_joins_a_self_signed_world_without_a_ca_file() {
    let Some(bin) = server_binary() else { return };
    let tls = Server::start(&bin, true);
    let other = Server::start(&bin, true);
    let pinned = format!("{}#sha256={}", tls.url, tls.pin(&bin));

    // The whole smoke run, pinned and nothing else.
    let mut opts = SmokeOptions::new(&pinned);
    opts.owner_token = Some(tls.owner_token());
    let mut out = Vec::new();
    let report = smoke::run(opts, &mut out).await;
    assert_eq!(report.exit_code(), EXIT_OK, "{}", String::from_utf8_lossy(&out));

    // The real binary, with a CA file that would not vouch for the server:
    // the pin decides.
    let other_cert = other.cert();
    let out = Command::new(env!("CARGO_BIN_EXE_tv-cli"))
        .args(["--url", &pinned, "--ca-file", other_cert.to_str().unwrap(), "say", "pinned hi"])
        .output()
        .unwrap();
    assert_eq!(out.status.code(), Some(EXIT_OK), "{}", String::from_utf8_lossy(&out.stderr));
    assert!(String::from_utf8_lossy(&out.stdout).contains("pinned hi"));

    // Another world's pin: refused with exit 2, naming both fingerprints.
    let wrong = format!("{}#sha256={}", tls.url, other.pin(&bin));
    let why = connect_error(&wrong, None).await;
    assert!(why.contains("does not match the pin"), "{why}");
    assert!(why.contains(&tls.pin(&bin)) && why.contains(&other.pin(&bin)), "{why}");
    let out = Command::new(env!("CARGO_BIN_EXE_tv-cli"))
        .args(["--url", &wrong, "say", "x"])
        .output()
        .unwrap();
    assert_eq!(out.status.code(), Some(EXIT_CONNECT));
    assert!(String::from_utf8_lossy(&out.stderr).contains("does not match the pin"));

    // Malformed pins and pins on ws:// are refused before connecting.
    for (url, want) in [
        (format!("{}#sha256=12", tls.url), "64 hex digits"),
        (format!("ws://{}/ws#sha256={}", tls.addr(), tls.pin(&bin)), "only applies to wss://"),
        (format!("{}#other", tls.url), "#sha256="),
    ] {
        let why = connect_error(&url, None).await;
        assert!(why.contains(want), "{url}: {why}");
    }
}

#[tokio::test]
async fn tls_mismatches_fail_readably_with_exit_2() {
    let Some(bin) = server_binary() else { return };
    let tls = Server::start(&bin, true);
    let plain = Server::start(&bin, false);

    // Self-signed server, no --ca-file: refused, naming the fingerprint and
    // the pinned URL to use once the host confirms it.
    let why = connect_error(&tls.url, None).await;
    assert!(why.contains("not trusted") && why.contains("--ca-file"), "{why}");
    assert!(why.contains(&format!("{}#sha256={}", tls.url, tls.pin(&bin))), "{why}");
    assert_eq!(common::run_cli(&["--url", &tls.url, "say", "x"]).await.0, EXIT_CONNECT);

    // ws:// to the TLS port.
    let url = format!("ws://{}/ws", tls.addr());
    let why = connect_error(&url, None).await;
    assert!(why.contains("use wss://"), "{why}");
    assert_eq!(common::run_cli(&["--url", &url, "say", "x"]).await.0, EXIT_CONNECT);

    // wss:// to the plain port, with and without a CA file.
    let url = format!("wss://{}/ws", plain.addr());
    for ca in [None, Some(tls.cert())] {
        let why = connect_error(&url, ca.as_deref()).await;
        assert!(why.contains("TLS handshake failed") && why.contains("try ws://"), "{why}");
    }
    assert_eq!(common::run_cli(&["--url", &url, "say", "x"]).await.0, EXIT_CONNECT);

    // A CA file that did not sign the server's certificate.
    let other = Server::start(&bin, true);
    let why = connect_error(&tls.url, Some(&other.cert())).await;
    assert!(why.contains("signed neither by a certificate in --ca-file"), "{why}");
}
