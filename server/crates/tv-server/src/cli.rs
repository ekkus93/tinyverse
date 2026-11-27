//! The `tinyverse-server` command line.

use std::ffi::OsString;
use std::io::{BufRead, IsTerminal, Write};
use std::net::{IpAddr, SocketAddr};
use std::path::{Path, PathBuf};

use anyhow::{Context, bail};
use clap::{Parser, Subcommand, ValueEnum};
use tv_store::{Store, UserRecord};
use tv_world::{ClientKind, UserId};

use crate::config::{RoleName, WorldDir, WorldFile, edit_password_hash, world_exists};
use crate::tls::{self, TlsFiles};
use crate::{Tuning, auth, now_ms};

/// The environment variable `init` and `password --set-from-env` read the
/// join password from.
pub const PASSWORD_ENV: &str = "TINYVERSE_PASSWORD";

const RUN_NOTE: &str = "Prints `listening on ws://<addr>/ws` (or `wss://` with TLS) on stdout once \
                        it accepts connections. TLS is used when --tls-cert and --tls-key are \
                        given, or when the world directory holds cert.pem and key.pem (see \
                        `tinyverse-server tls self-signed`, or pass --self-signed); --no-tls \
                        refuses it. With TLS, `tls certificate sha256=<hex>` comes first, and \
                        `join with wss://<address>:<port>/ws#sha256=<hex>` lines follow: \
                        addresses with the certificate pinned, for guests to paste.";

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
        /// Join password for visitors without a token. Visible to other local
        /// users in the process list; prefer --password-stdin or the
        /// TINYVERSE_PASSWORD environment variable, which is used when neither
        /// flag is given.
        #[arg(long, value_name = "PASSWORD", conflicts_with = "password_stdin")]
        password: Option<String>,
        /// Read the join password from the first line of stdin.
        #[arg(long)]
        password_stdin: bool,
    },
    /// Serve a world until SIGINT or SIGTERM, then save it and exit 0.
    #[command(after_help = RUN_NOTE)]
    Run {
        dir: PathBuf,
        #[arg(long, default_value_t = tv_proto::DEFAULT_PORT)]
        port: u16,
        #[arg(long, default_value = "0.0.0.0")]
        bind: IpAddr,
        /// PEM certificate chain to serve wss:// and HTTPS with (needs --tls-key).
        #[arg(long, value_name = "PEM", requires = "tls_key", conflicts_with = "no_tls")]
        tls_cert: Option<PathBuf>,
        /// PEM private key of --tls-cert.
        #[arg(long, value_name = "PEM", requires = "tls_cert", conflicts_with = "no_tls")]
        tls_key: Option<PathBuf>,
        /// Serve plain ws:// even if the world directory holds cert.pem and key.pem.
        #[arg(long)]
        no_tls: bool,
        /// Serve wss:// with the world directory's cert.pem and key.pem,
        /// creating a self-signed pair first if there is none.
        #[arg(long, conflicts_with_all = ["no_tls", "tls_cert"])]
        self_signed: bool,
    },
    /// Manage the world's TLS certificate.
    Tls {
        #[command(subcommand)]
        command: TlsCmd,
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
    #[command(group = clap::ArgGroup::new("action").multiple(false))]
    Password {
        dir: PathBuf,
        /// Remove the password.
        #[arg(long, group = "action")]
        clear: bool,
        /// Set a new password. Visible to other local users in the process
        /// list; prefer --set-stdin or --set-from-env.
        #[arg(long, value_name = "PASSWORD", group = "action")]
        set: Option<String>,
        /// Set the password to the first line of stdin.
        #[arg(long, group = "action")]
        set_stdin: bool,
        /// Set the password to the value of TINYVERSE_PASSWORD.
        #[arg(long, group = "action")]
        set_from_env: bool,
    },
}

#[derive(Debug, Subcommand)]
pub enum TlsCmd {
    /// Write a self-signed cert.pem (public, world-readable) and key.pem
    /// (private, mode 0600) into the world directory; `run` then serves wss://
    /// with them. It is valid for localhost, 127.0.0.1, ::1 and this
    /// machine's current network addresses. Guests trust it by its SHA-256
    /// fingerprint: give them the `wss://…#sha256=<hex>` address `run` prints.
    SelfSigned {
        dir: PathBuf,
        /// A further DNS name or IP address the certificate is valid for.
        /// Repeat for several.
        #[arg(long = "host", value_name = "NAME_OR_IP")]
        hosts: Vec<String>,
        /// Replace an existing cert.pem and key.pem.
        #[arg(long)]
        force: bool,
    },
    /// Print the SHA-256 fingerprint of the world's cert.pem: first as
    /// `tls certificate sha256=<hex>` (the pin guests put after `#` in the
    /// address), then as colon-separated hex.
    Fingerprint {
        dir: PathBuf,
        /// A certificate file other than the world directory's cert.pem.
        #[arg(long, value_name = "PEM")]
        cert: Option<PathBuf>,
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
    // A dead stderr (the process that launched us crashed) must never panic:
    // by default tracing-subscriber reports a failed write with `eprintln!`,
    // which panics on a broken pipe, so turn that off and swallow errors.
    let _ = tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_writer(|| QuietStderr)
        .with_ansi(std::io::stderr().is_terminal())
        .log_internal_errors(false)
        .try_init();
}

/// Standard error that reports every write as done, so a closed pipe is
/// silently ignored instead of failing (and maybe panicking) the caller.
pub struct QuietStderr;

impl Write for QuietStderr {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        let _ = std::io::stderr().write_all(buf);
        Ok(buf.len())
    }
    fn flush(&mut self) -> std::io::Result<()> {
        let _ = std::io::stderr().flush();
        Ok(())
    }
}

/// Where commands read passwords from besides their arguments.
pub struct Inputs<'a> {
    pub stdin: &'a mut dyn BufRead,
    /// The value of `TINYVERSE_PASSWORD`, if set.
    pub env_password: Option<OsString>,
}

/// Where a password comes from.
enum Source {
    Arg(String),
    Stdin,
    Env,
}

fn read_password(source: Source, inputs: &mut Inputs<'_>) -> anyhow::Result<String> {
    let password = match source {
        Source::Arg(pw) => pw,
        Source::Stdin => read_line(inputs.stdin)?,
        Source::Env => match inputs.env_password.take() {
            None => bail!("{PASSWORD_ENV} is not set"),
            Some(value) => value
                .into_string()
                .map_err(|_| anyhow::anyhow!("{PASSWORD_ENV} is not valid UTF-8"))?,
        },
    };
    if password.is_empty() {
        bail!("the password must not be empty");
    }
    Ok(password)
}

/// One line of input without its line ending.
pub fn read_line(input: &mut dyn BufRead) -> anyhow::Result<String> {
    let mut line = String::new();
    if input.read_line(&mut line).context("cannot read the password from stdin")? == 0 {
        bail!("no password on stdin");
    }
    if line.ends_with('\n') {
        line.pop();
        if line.ends_with('\r') {
            line.pop();
        }
    }
    Ok(line)
}

/// Runs one command with the process's stdin and environment. Normal output
/// goes to `out`.
pub fn execute(cli: Cli, out: &mut dyn Write) -> anyhow::Result<()> {
    let stdin = std::io::stdin();
    let mut lock = stdin.lock();
    let mut inputs = Inputs { stdin: &mut lock, env_password: std::env::var_os(PASSWORD_ENV) };
    execute_with(cli, &mut inputs, out)
}

/// Runs one command, reading passwords from `inputs` only.
pub fn execute_with(cli: Cli, inputs: &mut Inputs<'_>, out: &mut dyn Write) -> anyhow::Result<()> {
    match cli.command {
        Cmd::Init { dir, name, description, password, password_stdin } => {
            let source = match (password, password_stdin) {
                (Some(pw), _) => Some(Source::Arg(pw)),
                (None, true) => Some(Source::Stdin),
                (None, false) => inputs.env_password.is_some().then_some(Source::Env),
            };
            let password = source.map(|s| read_password(s, inputs)).transpose()?;
            init(&dir, name, description, password, out)
        }
        Cmd::Run { dir, port, bind, tls_cert, tls_key, no_tls, self_signed } => {
            let tls = match (tls_cert, tls_key) {
                (Some(cert), Some(key)) => Some(TlsFiles { cert, key }),
                _ if no_tls => None,
                _ => match tls_in_dir(&dir)? {
                    None if self_signed => {
                        require_world(&dir)?;
                        let made = tls::write_self_signed(&dir, &[], false)?;
                        tracing::info!(
                            cert = %made.files.cert.display(),
                            names = %made.hosts.join(", "),
                            "created a self-signed certificate"
                        );
                        Some(made.files)
                    }
                    found => found,
                },
            };
            run(&dir, SocketAddr::new(bind, port), tls, out)
        }
        Cmd::Tls { command: TlsCmd::SelfSigned { dir, hosts, force } } => {
            require_world(&dir)?;
            let made = tls::write_self_signed(&dir, &hosts, force)?;
            writeln!(out, "wrote {} (certificate, public)", made.files.cert.display())?;
            writeln!(out, "wrote {} (private key, mode 0600)", made.files.key.display())?;
            writeln!(out, "valid for: {}", made.hosts.join(", "))?;
            writeln!(out, "{}", tls::certificate_line(&made.pin))?;
            writeln!(out, "SHA-256 fingerprint: {}", made.fingerprint)?;
            writeln!(
                out,
                "`run` now serves wss://. Guests trust this certificate by its fingerprint: give \
                 them the wss://…#{} address that `run` prints.",
                made.pin
            )?;
            Ok(())
        }
        Cmd::Tls { command: TlsCmd::Fingerprint { dir, cert } } => {
            let cert = match cert {
                Some(c) => c,
                None => {
                    require_world(&dir)?;
                    let path = TlsFiles::in_dir(&dir).cert;
                    if !path.exists() {
                        bail!(
                            "{} has no certificate; make one with `tinyverse-server tls \
                             self-signed {}`",
                            dir.display(),
                            dir.display()
                        );
                    }
                    path
                }
            };
            let leaf = tls::read_leaf(&cert)?;
            let pin = tls::Pin::of(leaf.as_ref());
            writeln!(out, "{}", tls::certificate_line(&pin))?;
            writeln!(out, "SHA-256 fingerprint: {}", pin.colons())?;
            Ok(())
        }
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
        Cmd::Password { dir, clear, set, set_stdin, set_from_env } => {
            let source = match (set, set_stdin, set_from_env) {
                (Some(pw), _, _) => Some(Source::Arg(pw)),
                (None, true, _) => Some(Source::Stdin),
                (None, false, true) => Some(Source::Env),
                (None, false, false) => None,
            };
            let new = source.map(|s| read_password(s, inputs)).transpose()?;
            password(&dir, clear, new, out)
        }
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

fn require_world(dir: &Path) -> anyhow::Result<()> {
    if !WorldDir::new(dir).toml().exists() {
        bail!("{} is not a world directory; create one with `init`", dir.display());
    }
    Ok(())
}

/// The addresses to give guests for a server bound to `bind` on `port`,
/// with the certificate pinned: every LAN address for a wildcard bind (then
/// 127.0.0.1), else the bind address itself.
pub fn join_urls(bind: SocketAddr, pin: &tls::Pin, lan: &[IpAddr]) -> Vec<String> {
    let url = |ip: IpAddr| {
        let base = format!("wss://{}{}", SocketAddr::new(ip, bind.port()), tv_proto::WS_PATH);
        tv_tls::pinned_url(&base, pin)
    };
    if bind.ip().is_unspecified() {
        let mut urls: Vec<String> = lan
            .iter()
            .filter(|ip| ip.is_ipv4() || bind.ip().is_ipv6())
            .map(|ip| url(*ip))
            .collect();
        urls.push(url(IpAddr::from([127, 0, 0, 1])));
        urls
    } else {
        vec![url(bind.ip())]
    }
}

/// `cert.pem` and `key.pem` of the world directory if both exist; an error
/// if only one does.
fn tls_in_dir(dir: &Path) -> anyhow::Result<Option<TlsFiles>> {
    let files = TlsFiles::in_dir(dir);
    match (files.cert.exists(), files.key.exists()) {
        (true, true) => Ok(Some(files)),
        (false, false) => Ok(None),
        (true, false) => bail!(
            "{} exists but {} does not; add the key, remove the certificate, or pass --no-tls",
            files.cert.display(),
            files.key.display()
        ),
        (false, true) => bail!(
            "{} exists but {} does not; add the certificate, remove the key, or pass --no-tls",
            files.key.display(),
            files.cert.display()
        ),
    }
}

fn run(
    dir: &Path,
    bind: SocketAddr,
    tls: Option<TlsFiles>,
    out: &mut dyn Write,
) -> anyhow::Result<()> {
    let runtime = tokio::runtime::Runtime::new()?;
    runtime.block_on(async {
        let server = crate::start_with(dir, bind, Tuning::default(), tls.as_ref()).await?;
        let mut lines = Vec::new();
        if let (Some(files), Some(fingerprint)) = (&tls, server.tls_fingerprint()) {
            tracing::info!(cert = %files.cert.display(), %fingerprint, "serving TLS");
        }
        let pin = server.tls_pin();
        // Before the ready line, so that whoever waits for it has the pin.
        if let Some(pin) = &pin {
            lines.push(tls::certificate_line(pin));
        }
        lines.push(format!("listening on {}", server.ws_url()));
        if let Some(pin) = &pin {
            let urls = join_urls(server.addr(), pin, &tls::lan_addresses());
            lines.extend(urls.iter().map(|u| format!("join with {u}")));
        }
        // Whoever reads stdout may already be gone; that must not stop the
        // server, which still has to save the world on SIGTERM.
        let printed = lines.iter().try_for_each(|l| writeln!(out, "{l}"));
        if printed.and_then(|()| out.flush()).is_err() {
            tracing::warn!("cannot write to stdout");
        }
        tracing::info!("listening on {}", server.ws_url());
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

    fn exec(args: &[&str], stdin: &str, env: Option<&str>) -> anyhow::Result<String> {
        let cli = Cli::try_parse_from(args)?;
        let mut input = stdin.as_bytes();
        let mut inputs = Inputs { stdin: &mut input, env_password: env.map(OsString::from) };
        let mut out = Vec::new();
        execute_with(cli, &mut inputs, &mut out)?;
        Ok(String::from_utf8(out).unwrap())
    }

    fn hash_of(dir: &Path) -> Option<String> {
        WorldFile::load(&WorldDir::new(dir)).unwrap().password_hash
    }

    #[test]
    fn read_line_strips_one_line_ending() {
        for (input, want) in
            [("pw\n", "pw"), ("pw\r\n", "pw"), ("pw", "pw"), ("a b \nrest", "a b ")]
        {
            assert_eq!(read_line(&mut input.as_bytes()).unwrap(), want, "{input:?}");
        }
        assert!(read_line(&mut "".as_bytes()).unwrap_err().to_string().contains("no password"));
        assert_eq!(read_line(&mut "\n".as_bytes()).unwrap(), "");
    }

    #[test]
    fn init_reads_the_password_from_stdin_env_or_flag() {
        let tmp = tempfile::tempdir().unwrap();
        let s = |p: &Path| p.to_str().unwrap().to_owned();

        let a = tmp.path().join("stdin");
        exec(&["t", "init", &s(&a), "--password-stdin"], "from stdin\n", Some("ignored")).unwrap();
        assert!(auth::verify_password("from stdin", &hash_of(&a).unwrap()));

        let b = tmp.path().join("env");
        exec(&["t", "init", &s(&b)], "", Some("from env")).unwrap();
        assert!(auth::verify_password("from env", &hash_of(&b).unwrap()));

        let c = tmp.path().join("flag");
        exec(&["t", "init", &s(&c), "--password", "flag"], "", Some("ignored")).unwrap();
        assert!(auth::verify_password("flag", &hash_of(&c).unwrap()));

        let d = tmp.path().join("none");
        exec(&["t", "init", &s(&d)], "", None).unwrap();
        assert_eq!(hash_of(&d), None);

        // Empty passwords are errors, not "no password", and create nothing.
        for (args, stdin, env) in [
            (vec!["--password-stdin"], "\n", None),
            (vec!["--password-stdin"], "", None),
            (vec![], "", Some("")),
            (vec!["--password", ""], "", None),
        ] {
            let e = tmp.path().join("empty");
            let mut all = vec!["t".to_owned(), "init".to_owned(), s(&e)];
            all.extend(args.iter().map(|a| a.to_string()));
            let all: Vec<&str> = all.iter().map(String::as_str).collect();
            let err = exec(&all, stdin, env).unwrap_err().to_string();
            assert!(err.contains("password"), "{all:?}: {err}");
            assert!(!e.exists(), "{all:?} created the world anyway");
        }
        assert!(
            exec(&["t", "init", &s(&d), "--password", "x", "--password-stdin"], "", None).is_err()
        );
    }

    #[test]
    fn password_command_reads_stdin_and_env() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(WorldDir::new(dir.path()).toml(), WorldFile::default().to_toml()).unwrap();
        let d = dir.path().to_str().unwrap();
        exec(&["t", "password", d, "--set-stdin"], "piped\n", None).unwrap();
        assert!(auth::verify_password("piped", &hash_of(dir.path()).unwrap()));
        exec(&["t", "password", d, "--set-from-env"], "", Some("env pw")).unwrap();
        assert!(auth::verify_password("env pw", &hash_of(dir.path()).unwrap()));

        let before = hash_of(dir.path());
        for (args, stdin, env, needle) in [
            (vec!["--set-stdin"], "\n", None, "empty"),
            (vec!["--set-from-env"], "", Some(""), "empty"),
            (vec!["--set-from-env"], "", None, PASSWORD_ENV),
        ] {
            let mut all = vec!["t", "password", d];
            all.extend(args);
            let err = exec(&all, stdin, env).unwrap_err().to_string();
            assert!(err.contains(needle), "{all:?}: {err}");
        }
        assert_eq!(hash_of(dir.path()), before, "failed attempts change nothing");
        assert!(exec(&["t", "password", d, "--set-stdin", "--clear"], "x\n", None).is_err());
        assert!(exec(&["t", "password", d, "--set-from-env", "--set", "x"], "", None).is_err());
    }

    #[test]
    fn help_warns_about_the_process_list() {
        let mut cmd = Cli::command();
        let init = cmd.find_subcommand_mut("init").unwrap().render_long_help().to_string();
        assert!(init.contains("process list") && init.contains("TINYVERSE_PASSWORD"), "{init}");
        let mut cmd = Cli::command();
        let pw = cmd.find_subcommand_mut("password").unwrap().render_long_help().to_string();
        assert!(pw.contains("process list"), "{pw}");
    }

    #[test]
    fn run_tls_flags() {
        let parse = |args: &[&str]| Cli::try_parse_from(args).map(|c| c.command);
        assert!(parse(&["t", "run", "w", "--tls-cert", "c.pem"]).is_err(), "needs --tls-key");
        assert!(parse(&["t", "run", "w", "--tls-key", "k.pem"]).is_err(), "needs --tls-cert");
        assert!(
            parse(&["t", "run", "w", "--tls-cert", "c", "--tls-key", "k", "--no-tls"]).is_err()
        );
        assert!(matches!(
            parse(&["t", "run", "w", "--tls-cert", "c", "--tls-key", "k"]),
            Ok(Cmd::Run { tls_cert: Some(_), tls_key: Some(_), no_tls: false, .. })
        ));
        match parse(&["t", "tls", "self-signed", "w", "--host", "a.lan", "--host", "10.0.0.2"]) {
            Ok(Cmd::Tls { command: TlsCmd::SelfSigned { hosts, force: false, .. } }) => {
                assert_eq!(hosts, vec!["a.lan", "10.0.0.2"]);
            }
            other => panic!("{other:?}"),
        }
        assert!(matches!(
            parse(&["t", "run", "w", "--self-signed"]),
            Ok(Cmd::Run { self_signed: true, no_tls: false, .. })
        ));
        assert!(parse(&["t", "run", "w", "--self-signed", "--no-tls"]).is_err());
        assert!(
            parse(&["t", "run", "w", "--self-signed", "--tls-cert", "c", "--tls-key", "k"])
                .is_err()
        );
        assert!(matches!(
            parse(&["t", "tls", "fingerprint", "w"]),
            Ok(Cmd::Tls { command: TlsCmd::Fingerprint { cert: None, .. } })
        ));
    }

    #[test]
    fn join_urls_pin_every_reachable_address() {
        let pin = tls::Pin::of(b"abc");
        let hex = pin.hex();
        let lan: Vec<IpAddr> = vec!["192.168.1.20".parse().unwrap(), "fd00::5".parse().unwrap()];
        let any: SocketAddr = "0.0.0.0:7878".parse().unwrap();
        assert_eq!(
            join_urls(any, &pin, &lan),
            [
                format!("wss://192.168.1.20:7878/ws#sha256={hex}"),
                format!("wss://127.0.0.1:7878/ws#sha256={hex}"),
            ]
        );
        let any6: SocketAddr = "[::]:9000".parse().unwrap();
        assert_eq!(join_urls(any6, &pin, &lan).len(), 3);
        assert!(join_urls(any6, &pin, &lan)[1].starts_with("wss://[fd00::5]:9000/ws#sha256="));
        let one: SocketAddr = "10.0.0.2:7878".parse().unwrap();
        assert_eq!(join_urls(one, &pin, &lan), [format!("wss://10.0.0.2:7878/ws#sha256={hex}")]);
    }

    #[test]
    fn tls_fingerprint_prints_the_pin_of_the_world_certificate() {
        let dir = tempfile::tempdir().unwrap();
        let d = dir.path().to_str().unwrap();
        let err = exec(&["t", "tls", "fingerprint", d], "", None).unwrap_err().to_string();
        assert!(err.contains("not a world directory"), "{err}");
        exec(&["t", "init", d], "", None).unwrap();
        let err = exec(&["t", "tls", "fingerprint", d], "", None).unwrap_err().to_string();
        assert!(err.contains("tls self-signed"), "{err}");
        let made = exec(&["t", "tls", "self-signed", d], "", None).unwrap();
        let printed = exec(&["t", "tls", "fingerprint", d], "", None).unwrap();
        let first = printed.lines().next().unwrap();
        assert!(first.starts_with("tls certificate sha256="), "{printed}");
        assert_eq!(first.len(), "tls certificate sha256=".len() + 64, "{printed}");
        assert!(made.contains(first), "self-signed prints the same line:\n{made}\n{printed}");
        let setup = tls::load(&TlsFiles::in_dir(dir.path())).unwrap();
        assert_eq!(first, tls::certificate_line(&setup.pin));
        assert!(printed.contains(&format!("SHA-256 fingerprint: {}", setup.fingerprint)));
        let cert = dir.path().join("cert.pem");
        let other = exec(
            &["t", "tls", "fingerprint", "/nonexistent", "--cert", cert.to_str().unwrap()],
            "",
            None,
        )
        .unwrap();
        assert_eq!(other, printed);
    }

    #[test]
    fn tls_files_in_the_world_directory() {
        let dir = tempfile::tempdir().unwrap();
        assert_eq!(tls_in_dir(dir.path()).unwrap(), None);
        std::fs::write(dir.path().join("cert.pem"), "x").unwrap();
        let err = tls_in_dir(dir.path()).unwrap_err().to_string();
        assert!(err.contains("key.pem") && err.contains("--no-tls"), "{err}");
        std::fs::write(dir.path().join("key.pem"), "x").unwrap();
        assert_eq!(tls_in_dir(dir.path()).unwrap(), Some(TlsFiles::in_dir(dir.path())));
        std::fs::remove_file(dir.path().join("cert.pem")).unwrap();
        assert!(tls_in_dir(dir.path()).unwrap_err().to_string().contains("cert.pem"));
    }

    #[test]
    fn quiet_stderr_never_fails() {
        let mut w = QuietStderr;
        assert_eq!(w.write(b"").unwrap(), 0);
        w.flush().unwrap();
    }
}
