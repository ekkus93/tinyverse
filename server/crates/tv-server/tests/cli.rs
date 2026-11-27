//! The `tinyverse-server` binary: init, token, user, password and run.

mod common;

use std::io::{BufRead, BufReader};
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::time::{Duration, Instant};

use tv_server::config::{WorldDir, WorldFile};
use tv_store::Store;
use tv_world::{ClientKind, Role, UserId};

const BIN: &str = env!("CARGO_BIN_EXE_tinyverse-server");

fn command() -> Command {
    let mut cmd = Command::new(BIN);
    cmd.env_remove("RUST_LOG").env_remove("TINYVERSE_PASSWORD");
    cmd
}

fn run(args: &[&str]) -> Output {
    command().args(args).output().unwrap()
}

/// Runs with `input` on stdin and extra environment.
fn run_with(args: &[&str], input: &str, env: &[(&str, &str)]) -> Output {
    use std::io::Write;
    let mut child = command()
        .args(args)
        .envs(env.iter().copied())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child.stdin.take().unwrap().write_all(input.as_bytes()).unwrap();
    child.wait_with_output().unwrap()
}

/// Starts `run` and returns the child, the URL from its `listening on`
/// line, and its stdout (still open).
fn spawn_run(
    dir: &Path,
    extra: &[&str],
) -> (std::process::Child, String, BufReader<std::process::ChildStdout>) {
    let (child, url, stdout, _) = spawn_run_with_lines(dir, extra);
    (child, url, stdout)
}

/// Like [`spawn_run`], also returning the stdout lines before `listening on`.
// The child is returned for the caller to stop and wait for.
#[allow(clippy::zombie_processes)]
fn spawn_run_with_lines(
    dir: &Path,
    extra: &[&str],
) -> (std::process::Child, String, BufReader<std::process::ChildStdout>, Vec<String>) {
    let mut child = command()
        .args(["run", s(dir), "--port", "0", "--bind", "127.0.0.1"])
        .args(extra)
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let mut stdout = BufReader::new(child.stdout.take().unwrap());
    let mut before = Vec::new();
    loop {
        let mut line = String::new();
        if stdout.read_line(&mut line).unwrap_or(0) == 0 {
            let _ = child.kill();
            let _ = child.wait();
            panic!("no `listening on` line: {before:?}");
        }
        if let Some(url) = line.trim().strip_prefix("listening on ") {
            return (child, url.to_owned(), stdout, before);
        }
        before.push(line.trim().to_owned());
    }
}

fn sigterm_and_wait(child: &mut std::process::Child) -> std::process::ExitStatus {
    let status = Command::new("kill").args(["-TERM", &child.id().to_string()]).status().unwrap();
    assert!(status.success());
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        if let Some(status) = child.try_wait().unwrap() {
            return status;
        }
        assert!(Instant::now() < deadline, "server did not stop after SIGTERM");
        std::thread::sleep(Duration::from_millis(20));
    }
}

fn ok(args: &[&str]) -> String {
    let out = run(args);
    assert!(
        out.status.success(),
        "{args:?} failed: {}\n{}",
        String::from_utf8_lossy(&out.stdout),
        String::from_utf8_lossy(&out.stderr)
    );
    String::from_utf8(out.stdout).unwrap()
}

fn fails(args: &[&str]) -> String {
    let out = run(args);
    assert!(!out.status.success(), "{args:?} should fail");
    String::from_utf8(out.stderr).unwrap()
}

fn s(p: &Path) -> &str {
    p.to_str().unwrap()
}

fn store(dir: &Path) -> Store {
    Store::open(&WorldDir::new(dir).db()).unwrap()
}

#[test]
fn init_creates_a_world_and_refuses_to_overwrite_it() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("home");
    let out = ok(&["init", s(&dir), "--name", "Home", "--description", "my place"]);
    assert!(out.contains("tinyverse-server run"), "{out}");

    let paths = WorldDir::new(&dir);
    let file = WorldFile::load(&paths).unwrap();
    assert_eq!(file.name, "Home");
    assert_eq!(file.description, "my place");
    assert_eq!(file.password_hash, None);
    assert!(paths.db().exists());

    let token = std::fs::read_to_string(paths.owner_token()).unwrap();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let mode = std::fs::metadata(paths.owner_token()).unwrap().permissions().mode();
        assert_eq!(mode & 0o777, 0o600);
    }
    let owner = store(&dir).user_by_token(token.trim()).unwrap().expect("owner token works");
    assert_eq!(owner.role, Role::Owner);
    assert_eq!(store(&dir).users().unwrap().len(), 1);

    let err = fails(&["init", s(&dir)]);
    assert!(err.contains("refusing"), "{err}");
    assert_eq!(std::fs::read_to_string(paths.owner_token()).unwrap(), token);
}

#[test]
fn init_with_password() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("locked");
    ok(&["init", s(&dir), "--password", "sesame"]);
    let file = WorldFile::load(&WorldDir::new(&dir)).unwrap();
    assert_eq!(file.name, "locked", "name defaults to the directory name");
    assert!(tv_server::auth::verify_password("sesame", file.password_hash.as_deref().unwrap()));
}

#[test]
fn tokens() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("w");
    ok(&["init", s(&dir)]);

    let out = ok(&["token", "create", s(&dir), "--name", "scout"]);
    let token = out
        .lines()
        .find_map(|l| l.strip_prefix("token:"))
        .expect("prints the token")
        .trim()
        .to_owned();
    let user = store(&dir).user_by_token(&token).unwrap().unwrap();
    assert_eq!(
        (user.name.as_str(), user.kind, user.role),
        ("scout", ClientKind::Agent, Role::Builder)
    );

    let out = ok(&[
        "token",
        "create",
        s(&dir),
        "--name",
        "friend",
        "--role",
        "visitor",
        "--kind",
        "human",
    ]);
    let token2 = out.lines().find_map(|l| l.strip_prefix("token:")).unwrap().trim().to_owned();
    let friend = store(&dir).user_by_token(&token2).unwrap().unwrap();
    assert_eq!((friend.kind, friend.role), (ClientKind::Human, Role::Visitor));

    let list = ok(&["token", "list", s(&dir)]);
    assert!(list.contains("scout") && list.contains("friend") && list.contains(user.id.as_str()));

    ok(&["token", "revoke", s(&dir), user.id.as_str()]);
    assert!(store(&dir).user_by_token(&token).unwrap().is_none());
    let list = ok(&["token", "list", s(&dir)]);
    assert!(!list.contains("scout"), "{list}");
    let err = fails(&["token", "revoke", s(&dir), user.id.as_str()]);
    assert!(err.contains(user.id.as_str()), "{err}");

    assert!(
        fails(&["token", "create", s(&dir), "--name", "x", "--role", "admin"]).contains("admin")
    );
}

#[test]
fn users() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("w");
    ok(&["init", s(&dir)]);
    let (user, _) = store(&dir).create_user("guest", ClientKind::Human, Role::Builder, 1).unwrap();
    let id = user.id.as_str();

    let list = ok(&["user", "list", s(&dir)]);
    assert!(list.contains("guest") && list.contains("owner") && list.contains(id), "{list}");

    ok(&["user", "set-role", s(&dir), id, "visitor"]);
    assert_eq!(store(&dir).user(&UserId(id.into())).unwrap().unwrap().role, Role::Visitor);

    ok(&["user", "ban", s(&dir), id, "--reason", "spam"]);
    assert_eq!(
        store(&dir).user(&UserId(id.into())).unwrap().unwrap().banned.as_deref(),
        Some("spam")
    );
    assert!(ok(&["user", "list", s(&dir)]).contains("banned: spam"));

    ok(&["user", "unban", s(&dir), id]);
    assert_eq!(store(&dir).user(&UserId(id.into())).unwrap().unwrap().banned, None);
    fails(&["user", "unban", s(&dir), id]);
    fails(&["user", "set-role", s(&dir), "no-such-user", "owner"]);

    let help = ok(&["user", "--help"]);
    assert!(help.contains("next join"), "{help}");
}

#[test]
fn password() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("w");
    ok(&["init", s(&dir)]);
    assert!(ok(&["password", s(&dir)]).contains("has no"));
    ok(&["password", s(&dir), "--set", "pw"]);
    let file = WorldFile::load(&WorldDir::new(&dir)).unwrap();
    assert!(tv_server::auth::verify_password("pw", file.password_hash.as_deref().unwrap()));
    ok(&["password", s(&dir), "--clear"]);
    assert_eq!(WorldFile::load(&WorldDir::new(&dir)).unwrap().password_hash, None);
}

#[test]
fn commands_on_a_missing_world_fail_clearly() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("nothing");
    assert!(fails(&["user", "list", s(&dir)]).contains("init"));
    assert!(fails(&["run", s(&dir), "--port", "0"]).contains("world.toml"));
}

#[test]
fn bad_world_toml_is_reported() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("w");
    ok(&["init", s(&dir)]);
    let path = WorldDir::new(&dir).toml();
    let mut text = std::fs::read_to_string(&path).unwrap();
    text.insert_str(0, "colour = \"red\"\n");
    std::fs::write(&path, text).unwrap();
    let err = fails(&["run", s(&dir), "--port", "0", "--bind", "127.0.0.1"]);
    assert!(err.contains("colour"), "{err}");
}

#[test]
fn run_serves_until_sigterm() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("w");
    ok(&["init", s(&dir)]);
    let mut child = Command::new(BIN)
        .args(["run", s(&dir), "--port", "0", "--bind", "127.0.0.1"])
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .unwrap();
    let mut line = String::new();
    BufReader::new(child.stdout.take().unwrap()).read_line(&mut line).unwrap();
    let url = line.trim().strip_prefix("listening on ").unwrap_or_else(|| panic!("{line:?}"));
    assert!(url.starts_with("ws://127.0.0.1:") && url.ends_with("/ws"), "{url}");
    let addr = url.trim_start_matches("ws://").trim_end_matches("/ws").to_owned();

    // It answers /health.
    {
        use std::io::{Read, Write};
        let mut stream = std::net::TcpStream::connect(&addr).unwrap();
        write!(stream, "GET /health HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n").unwrap();
        let mut response = String::new();
        stream.read_to_string(&mut response).unwrap();
        assert!(response.starts_with("HTTP/1.1 200"), "{response}");
        assert!(response.ends_with("ok"), "{response}");
    }

    let status = Command::new("kill").args(["-TERM", &child.id().to_string()]).status().unwrap();
    assert!(status.success());
    let deadline = Instant::now() + Duration::from_secs(10);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        assert!(Instant::now() < deadline, "server did not stop after SIGTERM");
        std::thread::sleep(Duration::from_millis(50));
    };
    assert!(status.success(), "exit status {status:?}");
}
fn verify(dir: &Path, password: &str) -> bool {
    let file = WorldFile::load(&WorldDir::new(dir)).unwrap();
    tv_server::auth::verify_password(password, file.password_hash.as_deref().unwrap())
}

#[test]
fn passwords_from_stdin_and_the_environment() {
    let tmp = tempfile::tempdir().unwrap();

    let a = tmp.path().join("stdin");
    let out = run_with(&["init", s(&a), "--password-stdin"], "piped secret\n", &[]);
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    assert!(verify(&a, "piped secret"));

    let b = tmp.path().join("env");
    let out = run_with(&["init", s(&b)], "", &[("TINYVERSE_PASSWORD", "env secret")]);
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    assert!(verify(&b, "env secret"));

    // Empty is an error, and nothing is created.
    let c = tmp.path().join("empty");
    let out = run_with(&["init", s(&c), "--password-stdin"], "\n", &[]);
    assert!(!out.status.success());
    assert!(String::from_utf8_lossy(&out.stderr).contains("empty"));
    let out = run_with(&["init", s(&c)], "", &[("TINYVERSE_PASSWORD", "")]);
    assert!(!out.status.success());
    assert!(!c.exists());

    let out = run_with(&["password", s(&a), "--set-stdin"], "new one\r\n", &[]);
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    assert!(verify(&a, "new one"));
    let out =
        run_with(&["password", s(&a), "--set-from-env"], "", &[("TINYVERSE_PASSWORD", "from env")]);
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    assert!(verify(&a, "from env"));
    let out = run_with(&["password", s(&a), "--set-from-env"], "", &[]);
    assert!(!out.status.success());
    assert!(String::from_utf8_lossy(&out.stderr).contains("TINYVERSE_PASSWORD"));
    assert!(verify(&a, "from env"), "a failed attempt changes nothing");

    let help = ok(&["init", "--help"]);
    assert!(help.contains("process list") && help.contains("--password-stdin"), "{help}");
    let help = ok(&["password", "--help"]);
    assert!(help.contains("process list") && help.contains("--set-from-env"), "{help}");
}

/// The process that launched the server dies: the read ends of its stdout
/// and stderr go away while it keeps running, then it gets SIGTERM. It must
/// still save the world and exit 0.
#[tokio::test]
async fn survives_dead_stdout_and_stderr() {
    use common::{Client, box_at, hello, join_client};
    use tv_proto::v1::{self, client_message::Body as C, server_message::Body as S};

    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("w");
    ok(&["init", s(&dir)]);
    let mut child = command()
        .args(["run", s(&dir), "--port", "0", "--bind", "127.0.0.1"])
        // Debug logging, so plenty is written after the pipes are gone.
        .env("RUST_LOG", "debug")
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut stdout = BufReader::new(child.stdout.take().unwrap());
    let mut line = String::new();
    stdout.read_line(&mut line).unwrap();
    let url = line.trim().strip_prefix("listening on ").unwrap().to_owned();
    // Close both read ends.
    drop(stdout);
    drop(child.stderr.take());

    let client = Client::connect_url(&url, tv_proto::Encoding::Json, None).await.unwrap();
    let mut session = join_client(client, hello("survivor")).await;
    let seq =
        session.send(C::ObjectCreate(v1::ObjectCreate { spec: Some(box_at(1.0, 0.5, 1.0)) })).await;
    let msg = session
        .recv_where("ObjectCreated", |m| {
            m.reply_to == seq && matches!(m.body, Some(S::ObjectCreated(_)))
        })
        .await;
    let Some(S::ObjectCreated(created)) = msg.body else { unreachable!() };
    let id = created.object.unwrap().id;
    session.client.close().await;

    let status = tokio::task::spawn_blocking(move || sigterm_and_wait(&mut child)).await.unwrap();
    assert!(status.success(), "exit status {status:?}");
    let loaded = store(&dir).load_world().unwrap();
    assert_eq!(loaded.objects.iter().map(|o| o.id).collect::<Vec<_>>(), vec![id]);
}

#[test]
fn run_self_signed_creates_a_certificate_and_prints_its_pin() {
    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("w");
    ok(&["init", s(&dir)]);
    let (mut child, url, mut stdout, before) = spawn_run_with_lines(&dir, &["--self-signed"]);
    assert!(url.starts_with("wss://127.0.0.1:"), "{url}");
    assert!(dir.join("cert.pem").exists() && dir.join("key.pem").exists());
    let fingerprint = ok(&["tls", "fingerprint", s(&dir)]);
    let line = fingerprint.lines().next().unwrap().to_owned();
    assert_eq!(before, std::slice::from_ref(&line), "the certificate line comes first");
    let hex = line.strip_prefix("tls certificate sha256=").unwrap();
    assert_eq!(hex.len(), 64);
    let mut join = String::new();
    stdout.read_line(&mut join).unwrap();
    assert_eq!(join.trim(), format!("join with {url}#sha256={hex}"));
    assert!(sigterm_and_wait(&mut child).success());

    // The next start keeps the certificate, with or without the flag.
    for extra in [&["--self-signed"][..], &[]] {
        let (mut child, _, _, before) = spawn_run_with_lines(&dir, extra);
        assert_eq!(before, std::slice::from_ref(&line));
        assert!(sigterm_and_wait(&mut child).success());
    }
    // No world, no certificate.
    let err = fails(&["run", s(&tmp.path().join("nope")), "--self-signed", "--port", "0"]);
    assert!(err.contains("init"), "{err}");
    assert!(!tmp.path().join("nope").exists());
}

#[tokio::test]
async fn run_serves_tls_from_the_world_directory() {
    use common::{Client, hello, join_client, trusting};

    let tmp = tempfile::tempdir().unwrap();
    let dir = tmp.path().join("w");
    ok(&["init", s(&dir)]);
    assert!(fails(&["tls", "self-signed", s(&tmp.path().join("nope"))]).contains("init"));

    let out = ok(&["tls", "self-signed", s(&dir), "--host", "localhost", "--host", "127.0.0.1"]);
    let printed = out
        .lines()
        .find_map(|l| l.strip_prefix("SHA-256 fingerprint: "))
        .unwrap_or_else(|| panic!("{out}"))
        .to_owned();
    let cert = dir.join("cert.pem");
    let key = dir.join("key.pem");
    {
        use rustls::pki_types::CertificateDer;
        use rustls::pki_types::pem::PemObject;
        let der = CertificateDer::from_pem_file(&cert).unwrap();
        assert_eq!(printed, tv_server::tls::fingerprint(der.as_ref()));
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let mode = std::fs::metadata(&key).unwrap().permissions().mode();
        assert_eq!(mode & 0o777, 0o600);
        // The certificate is public: readable by others, and said so.
        let mode = std::fs::metadata(&cert).unwrap().permissions().mode();
        assert_eq!(mode & 0o044, 0o044, "cert.pem mode {mode:o}");
    }
    assert!(out.contains("cert.pem (certificate, public)"), "{out}");
    assert!(out.contains("key.pem (private key, mode 0600)"), "{out}");
    let help = ok(&["tls", "self-signed", "--help"]);
    assert!(help.contains("world-readable") && help.contains("mode 0600"), "{help}");
    assert!(fails(&["tls", "self-signed", s(&dir)]).contains("--force"));

    // cert.pem and key.pem are picked up without flags.
    let (mut child, url, _stdout) = spawn_run(&dir, &[]);
    assert!(url.starts_with("wss://127.0.0.1:") && url.ends_with("/ws"), "{url}");
    let client =
        Client::connect_url(&url, tv_proto::Encoding::Json, Some(trusting(&cert))).await.unwrap();
    let session = join_client(client, hello("secure")).await;
    session.client.close().await;
    let status = tokio::task::spawn_blocking(move || sigterm_and_wait(&mut child)).await.unwrap();
    assert!(status.success());

    // --no-tls refuses them.
    let (mut child, url, _stdout) = spawn_run(&dir, &["--no-tls"]);
    assert!(url.starts_with("ws://"), "{url}");
    assert!(sigterm_and_wait(&mut child).success());

    // Explicit flags point anywhere.
    let elsewhere = tmp.path().join("certs");
    std::fs::create_dir(&elsewhere).unwrap();
    std::fs::rename(&cert, elsewhere.join("c.pem")).unwrap();
    std::fs::rename(&key, elsewhere.join("k.pem")).unwrap();
    let (c, k) = (elsewhere.join("c.pem"), elsewhere.join("k.pem"));
    let (mut child, url, _stdout) = spawn_run(&dir, &["--tls-cert", s(&c), "--tls-key", s(&k)]);
    assert!(url.starts_with("wss://"), "{url}");
    assert!(sigterm_and_wait(&mut child).success());

    // Half a pair in the world directory is a clear error.
    std::fs::copy(&c, &cert).unwrap();
    let err = fails(&["run", s(&dir), "--port", "0", "--bind", "127.0.0.1"]);
    assert!(err.contains("key.pem") && err.contains("--no-tls"), "{err}");
    // A mismatched explicit pair too.
    let other = tmp.path().join("other");
    ok(&["init", s(&other)]);
    ok(&["tls", "self-signed", s(&other)]);
    let other_key = other.join("key.pem");
    let err =
        fails(&["run", s(&dir), "--port", "0", "--tls-cert", s(&c), "--tls-key", s(&other_key)]);
    assert!(err.contains("do not form a usable pair"), "{err}");
}
