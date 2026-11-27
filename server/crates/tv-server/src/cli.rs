//! The `tinyverse-server` command line.

use std::io::Write;
use std::net::{IpAddr, SocketAddr};
use std::path::{Path, PathBuf};

use anyhow::{Context, bail};
use clap::{Parser, Subcommand, ValueEnum};
use tv_store::{Store, UserRecord};
use tv_world::{ClientKind, UserId};

use crate::config::{RoleName, WorldDir, WorldFile, edit_password_hash, world_exists};
use crate::{Tuning, auth, now_ms};

const STORE_NOTE: &str = "These commands edit world.db directly and may be used while the server \
                          runs; a running server applies a change to a user at their next join.";

#[derive(Debug, Parser)]
#[command(name = "tinyverse-server", version, about = "Host a Tinyverse world")]
pub struct Cli {
    /// More log output (-v debug, -vv trace). RUST_LOG overrides this.
    #[arg(short, long, action = clap::ArgAction::Count, global = true)]
    pub verbose: u8,
    #[command(subcommand)]
    pub command: Cmd,
}

#[derive(Debug, Subcommand)]
pub enum Cmd {
    /// Create a new world directory with world.toml, world.db and an owner token.
    Init {
        /// The directory to create. Must not already hold a world.
        dir: PathBuf,
        /// World name (default: the directory name).
        #[arg(long)]
        name: Option<String>,
        #[arg(long)]
        description: Option<String>,
        /// Join password for visitors without a token.
        #[arg(long)]
        password: Option<String>,
    },
    /// Serve a world until SIGINT or SIGTERM.
    Run {
        dir: PathBuf,
        #[arg(long, default_value_t = tv_proto::DEFAULT_PORT)]
        port: u16,
        #[arg(long, default_value = "0.0.0.0")]
        bind: IpAddr,
    },
    /// Manage login tokens (for agents, or to invite people).
    #[command(after_help = STORE_NOTE)]
    Token {
        #[command(subcommand)]
        command: TokenCmd,
    },
    /// Manage users: roles and bans.
    #[command(after_help = STORE_NOTE)]
    User {
        #[command(subcommand)]
        command: UserCmd,
    },
    /// Show, set or clear the world's join password. Takes effect at the next start.
    Password {
        dir: PathBuf,
        /// Remove the password.
        #[arg(long, conflicts_with = "set")]
        clear: bool,
        /// Set a new password.
        #[arg(long, value_name = "PASSWORD")]
        set: Option<String>,
    },
}

#[derive(Debug, Subcommand)]
pub enum TokenCmd {
    /// Create a user and print its login token (shown only once).
    Create {
        dir: PathBuf,
        #[arg(long)]
        name: String,
        #[arg(long, value_enum, default_value_t = RoleName::Builder)]
        role: RoleName,
        #[arg(long, value_enum, default_value_t = KindName::Agent)]
        kind: KindName,
    },
    /// List users that have a login token.
    List { dir: PathBuf },
    /// Make a user's token useless. The user and their objects stay.
    Revoke { dir: PathBuf, user_id: String },
}

#[derive(Debug, Subcommand)]
pub enum UserCmd {
    /// List every user of the world.
    List { dir: PathBuf },
    /// Change a user's role.
    SetRole {
        dir: PathBuf,
        user_id: String,
        #[arg(value_enum)]
        role: RoleName,
    },
    /// Refuse a user from now on. Kick them from a running server with Admin.
    Ban {
        dir: PathBuf,
        user_id: String,
        #[arg(long, default_value = "")]
        reason: String,
    },
    /// Allow a banned user again.
    Unban { dir: PathBuf, user_id: String },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, ValueEnum)]
pub enum KindName {
    Agent,
    Human,
}

impl From<KindName> for ClientKind {
    fn from(value: KindName) -> Self {
        match value {
            KindName::Agent => ClientKind::Agent,
            KindName::Human => ClientKind::Human,
        }
    }
}

/// Sets up logging to stderr: `RUST_LOG` if set, else from `-v`.
pub fn init_logging(verbose: u8) {
    use tracing_subscriber::EnvFilter;
    let default = match verbose {
        0 => "info",
        1 => "debug",
        _ => "trace",
    };
    let filter = EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new(default));
    let _ =
        tracing_subscriber::fmt().with_env_filter(filter).with_writer(std::io::stderr).try_init();
}

/// Runs one command. Normal output goes to `out`.
pub fn execute(cli: Cli, out: &mut dyn Write) -> anyhow::Result<()> {
    match cli.command {
        Cmd::Init { dir, name, description, password } => {
            init(&dir, name, description, password, out)
        }
        Cmd::Run { dir, port, bind } => run(&dir, SocketAddr::new(bind, port), out),
        Cmd::Token { command } => match command {
            TokenCmd::Create { dir, name, role, kind } => {
                let mut store = open_store(&dir)?;
                let (user, token) = store.create_user(&name, kind.into(), role.into(), now_ms())?;
                writeln!(
                    out,
                    "created {} user {:?} with role {}",
                    kind_str(user.kind),
                    name,
                    role_str(role)
                )?;
                writeln!(out, "user id: {}", user.id)?;
                writeln!(out, "token:   {token}")?;
                writeln!(out, "This token is shown only once. Put it in Hello.token to log in.")?;
                Ok(())
            }
            TokenCmd::List { dir } => {
                let store = open_store(&dir)?;
                let users: Vec<_> = store.users()?.into_iter().filter(|u| u.has_token).collect();
                print_users(&users, out)
            }
            TokenCmd::Revoke { dir, user_id } => {
                let mut store = open_store(&dir)?;
                if !store.revoke_token(&UserId(user_id.clone()))? {
                    bail!("no user {user_id} with a token");
                }
                writeln!(out, "revoked the token of {user_id}")?;
                Ok(())
            }
        },
        Cmd::User { command } => match command {
            UserCmd::List { dir } => print_users(&open_store(&dir)?.users()?, out),
            UserCmd::SetRole { dir, user_id, role } => {
                let mut store = open_store(&dir)?;
                if !store.set_role(&UserId(user_id.clone()), role.into())? {
                    bail!("no user {user_id}");
                }
                writeln!(out, "{user_id} is now {}", role_str(role))?;
                Ok(())
            }
            UserCmd::Ban { dir, user_id, reason } => {
                let mut store = open_store(&dir)?;
                if !store.ban(&UserId(user_id.clone()), &reason)? {
                    bail!("no user {user_id}");
                }
                writeln!(out, "banned {user_id}")?;
                Ok(())
            }
            UserCmd::Unban { dir, user_id } => {
                let mut store = open_store(&dir)?;
                if !store.unban(&UserId(user_id.clone()))? {
                    bail!("no banned user {user_id}");
                }
                writeln!(out, "unbanned {user_id}")?;
                Ok(())
            }
        },
        Cmd::Password { dir, clear, set } => password(&dir, clear, set, out),
    }
}

fn open_store(dir: &Path) -> anyhow::Result<Store> {
    let dir = WorldDir::new(dir);
    if !dir.toml().exists() || !dir.db().exists() {
        bail!(
            "{} is not a world directory (no world.toml or world.db); create one with `init`",
            dir.root.display()
        );
    }
    Store::open(&dir.db()).with_context(|| format!("cannot open {}", dir.db().display()))
}

fn init(
    dir: &Path,
    name: Option<String>,
    description: Option<String>,
    password: Option<String>,
    out: &mut dyn Write,
) -> anyhow::Result<()> {
    if world_exists(dir) {
        bail!("{} already holds a world; refusing to overwrite it", dir.display());
    }
    let mut file = WorldFile::default();
    if let Some(name) = name.or_else(|| dir.file_name().map(|n| n.to_string_lossy().into_owned())) {
        file.name = name;
    }
    if let Some(description) = description {
        file.description = description;
    }
    if let Some(password) = &password {
        if password.is_empty() {
            bail!("the password must not be empty");
        }
        file.password_hash = Some(auth::hash_password(password)?);
    }
    file.validate()?;

    std::fs::create_dir_all(dir).with_context(|| format!("cannot create {}", dir.display()))?;
    let paths = WorldDir::new(dir);
    let result = (|| -> anyhow::Result<(UserRecord, String)> {
        let mut store = Store::open(&paths.db())?;
        let (owner, token) =
            store.create_user("owner", ClientKind::Human, tv_world::Role::Owner, now_ms())?;
        auth::write_secret_file(&paths.owner_token(), &format!("{token}\n"))
            .context("cannot write owner.token")?;
        write_new(&paths.toml(), &file.to_toml())?;
        Ok((owner, token))
    })();
    let (owner, _token) = match result {
        Ok(ok) => ok,
        Err(e) => {
            for p in [paths.toml(), paths.db(), paths.owner_token()] {
                let _ = std::fs::remove_file(p);
            }
            return Err(e);
        }
    };

    writeln!(out, "created world {:?} in {}", file.name, dir.display())?;
    writeln!(
        out,
        "  {}  world description (edit to change bounds, sky, limits)",
        paths.toml().display()
    )?;
    writeln!(out, "  {}  objects and users", paths.db().display())?;
    writeln!(
        out,
        "  {}  login token of the owner (user {}), mode 0600",
        paths.owner_token().display(),
        owner.id
    )?;
    if password.is_some() {
        writeln!(out, "  visitors without a token need the join password")?;
    }
    writeln!(out)?;
    writeln!(out, "run it with:  tinyverse-server run {}", dir.display())?;
    Ok(())
}

fn write_new(path: &Path, contents: &str) -> anyhow::Result<()> {
    let mut f = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .with_context(|| format!("cannot create {}", path.display()))?;
    f.write_all(contents.as_bytes())?;
    Ok(())
}

fn run(dir: &Path, bind: SocketAddr, out: &mut dyn Write) -> anyhow::Result<()> {
    let runtime = tokio::runtime::Runtime::new()?;
    runtime.block_on(async {
        let server = crate::start(dir, bind, Tuning::default()).await?;
        writeln!(out, "listening on {}", server.ws_url())?;
        out.flush()?;
        server.run_until(shutdown_signal()).await?;
        tracing::info!("stopped");
        Ok(())
    })
}

async fn shutdown_signal() {
    #[cfg(unix)]
    {
        use tokio::signal::unix::{SignalKind, signal};
        match signal(SignalKind::terminate()) {
            Ok(mut term) => {
                tokio::select! {
                    _ = tokio::signal::ctrl_c() => {}
                    _ = term.recv() => {}
                }
            }
            Err(_) => {
                let _ = tokio::signal::ctrl_c().await;
            }
        }
    }
    #[cfg(not(unix))]
    {
        let _ = tokio::signal::ctrl_c().await;
    }
    tracing::info!("shutting down");
}

fn password(
    dir: &Path,
    clear: bool,
    set: Option<String>,
    out: &mut dyn Write,
) -> anyhow::Result<()> {
    let paths = WorldDir::new(dir);
    let current = WorldFile::load(&paths)?;
    let hash = match (clear, set) {
        (false, None) => {
            let state = if current.password_hash.is_some() { "has a" } else { "has no" };
            writeln!(out, "the world {state} join password")?;
            return Ok(());
        }
        (true, _) => None,
        (false, Some(pw)) => {
            if pw.is_empty() {
                bail!("the password must not be empty; use --clear to remove it");
            }
            Some(auth::hash_password(&pw)?)
        }
    };
    let text = std::fs::read_to_string(paths.toml())?;
    let edited = edit_password_hash(&text, hash.as_deref());
    WorldFile::parse(&edited).context("internal error: edited world.toml does not parse")?;
    let tmp = paths.root.join(".world.toml.tmp");
    std::fs::write(&tmp, edited)?;
    std::fs::rename(&tmp, paths.toml())?;
    let what = if hash.is_some() { "set" } else { "cleared" };
    writeln!(out, "join password {what}; restart the server to apply it")?;
    Ok(())
}

fn role_str(role: RoleName) -> &'static str {
    match role {
        RoleName::Visitor => "visitor",
        RoleName::Builder => "builder",
        RoleName::Owner => "owner",
    }
}

fn kind_str(kind: ClientKind) -> &'static str {
    match kind {
        ClientKind::Human => "human",
        ClientKind::Agent => "agent",
    }
}

/// "5s ago", "3m ago", "2h ago", "4d ago", or "never".
pub fn ago(then_ms: i64, now_ms: i64) -> String {
    if then_ms <= 0 {
        return "never".into();
    }
    let s = (now_ms - then_ms).max(0) / 1000;
    match s {
        0..60 => format!("{s}s ago"),
        60..3600 => format!("{}m ago", s / 60),
        3600..86400 => format!("{}h ago", s / 3600),
        _ => format!("{}d ago", s / 86400),
    }
}

fn print_users(users: &[UserRecord], out: &mut dyn Write) -> anyhow::Result<()> {
    if users.is_empty() {
        writeln!(out, "no users")?;
        return Ok(());
    }
    let now = now_ms();
    writeln!(
        out,
        "{:<36}  {:<20}  {:<5}  {:<7}  {:<5}  {:<9}  status",
        "id", "name", "kind", "role", "token", "last seen"
    )?;
    for u in users {
        let status = match &u.banned {
            None => "ok".to_owned(),
            Some(r) if r.is_empty() => "banned".to_owned(),
            Some(r) => format!("banned: {r}"),
        };
        writeln!(
            out,
            "{:<36}  {:<20}  {:<5}  {:<7}  {:<5}  {:<9}  {status}",
            u.id.as_str(),
            u.name,
            kind_str(u.kind),
            role_str(u.role.into()),
            if u.has_token { "yes" } else { "no" },
            ago(u.last_seen_ms, now),
        )?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use clap::CommandFactory;

    use super::*;

    #[test]
    fn cli_definition_is_valid() {
        Cli::command().debug_assert();
    }

    #[test]
    fn parses_the_documented_commands() {
        let parse = |args: &[&str]| Cli::try_parse_from(args).map(|c| c.command);
        match parse(&["tinyverse-server", "run", "w"]).unwrap() {
            Cmd::Run { port, bind, .. } => {
                assert_eq!(port, 7878);
                assert_eq!(bind.to_string(), "0.0.0.0");
            }
            other => panic!("{other:?}"),
        }
        match parse(&["tinyverse-server", "token", "create", "w", "--name", "scout"]).unwrap() {
            Cmd::Token { command: TokenCmd::Create { role, kind, name, .. } } => {
                assert_eq!(
                    (role, kind, name.as_str()),
                    (RoleName::Builder, KindName::Agent, "scout")
                );
            }
            other => panic!("{other:?}"),
        }
        assert!(
            parse(&["tinyverse-server", "token", "create", "w", "--name", "s", "--role", "admin"])
                .is_err()
        );
        assert!(parse(&["tinyverse-server", "password", "w", "--clear", "--set", "x"]).is_err());
        match parse(&["tinyverse-server", "user", "set-role", "w", "u1", "visitor"]).unwrap() {
            Cmd::User { command: UserCmd::SetRole { role, user_id, .. } } => {
                assert_eq!((role, user_id.as_str()), (RoleName::Visitor, "u1"));
            }
            other => panic!("{other:?}"),
        }
        assert!(matches!(
            parse(&["tinyverse-server", "-vv", "user", "ban", "w", "u1", "--reason", "spam"]),
            Ok(Cmd::User { command: UserCmd::Ban { .. } })
        ));
    }

    #[test]
    fn help_mentions_next_join() {
        let mut cmd = Cli::command();
        let user = cmd.find_subcommand_mut("user").unwrap();
        let help = user.render_long_help().to_string();
        assert!(help.contains("next join"), "{help}");
    }

    #[test]
    fn ago_formats() {
        assert_eq!(ago(0, 1000), "never");
        assert_eq!(ago(1000, 6000), "5s ago");
        assert_eq!(ago(1, 1 + 180_000), "3m ago");
        assert_eq!(ago(1, 1 + 7_200_000), "2h ago");
        assert_eq!(ago(1, 1 + 4 * 86_400_000), "4d ago");
    }

    #[test]
    fn password_command_edits_world_toml() {
        let dir = tempfile::tempdir().unwrap();
        let paths = WorldDir::new(dir.path());
        std::fs::write(paths.toml(), WorldFile::default().to_toml()).unwrap();
        let mut out = Vec::new();
        password(dir.path(), false, None, &mut out).unwrap();
        assert!(String::from_utf8_lossy(&out).contains("has no"));

        password(dir.path(), false, Some("pw".into()), &mut out).unwrap();
        let file = WorldFile::load(&paths).unwrap();
        assert!(auth::verify_password("pw", file.password_hash.as_deref().unwrap()));

        out.clear();
        password(dir.path(), false, None, &mut out).unwrap();
        assert!(String::from_utf8_lossy(&out).contains("has a"));

        password(dir.path(), true, None, &mut out).unwrap();
        assert_eq!(WorldFile::load(&paths).unwrap().password_hash, None);
        assert!(password(dir.path(), false, Some(String::new()), &mut out).is_err());
    }

    #[test]
    fn init_refuses_an_existing_world() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("world.toml"), "").unwrap();
        let err = init(dir.path(), None, None, None, &mut Vec::new()).unwrap_err();
        assert!(err.to_string().contains("refusing"), "{err}");
    }

    #[test]
    fn store_commands_need_a_world() {
        let dir = tempfile::tempdir().unwrap();
        let err = open_store(dir.path()).err().unwrap();
        assert!(err.to_string().contains("init"), "{err}");
    }
}
