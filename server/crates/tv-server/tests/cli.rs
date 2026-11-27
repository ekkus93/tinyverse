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

fn run(args: &[&str]) -> Output {
    Command::new(BIN).args(args).env_remove("RUST_LOG").output().unwrap()
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
