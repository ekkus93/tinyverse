//! Command-line definition and the one-shot and interactive commands.

use std::{io::Write, path::PathBuf, time::Duration};

use clap::{Args, Parser, Subcommand, ValueEnum};
use tokio::io::AsyncBufReadExt;
use tv_proto::{
    Encoding,
    v1::{
        Appearance, Chat, ChatChannel, ChatHistoryRequest, ClientKind, Color, MoveMode, MoveTo,
        ObjectCreate, ObjectDelete, ObjectSpec, ObjectUpdate, Observe, Quat, Stop, Vec3,
        client_message::Body as CBody, server_message::Body as SBody,
    },
};

use crate::{
    bots,
    client::{Client, ClientError, ClientOptions},
    format::{Roster, describe, object_line, observation_lines},
    parse::{parse_color, parse_material, parse_rotation, parse_scale, parse_shape, parse_vec3},
    repl::{HELP, Line, default_spec, parse_line},
    smoke,
};

/// Everything worked.
pub const EXIT_OK: i32 = 0;
/// A check or command failed (including bad usage).
pub const EXIT_FAILED: i32 = 1;
/// Could not connect, or the server refused the `Hello`.
pub const EXIT_CONNECT: i32 = 2;

/// How long one-shot commands wait for a reply.
pub const REPLY_TIMEOUT: Duration = Duration::from_secs(10);

#[derive(Parser, Debug)]
#[command(
    name = "tv-cli",
    version,
    about = "Tinyverse command-line test client, bot and smoke tester",
    after_help = "Exit status: 0 success, 1 a check or command failed, 2 could not connect or was refused."
)]
pub struct Cli {
    #[command(flatten)]
    pub global: Global,
    #[command(subcommand)]
    pub command: Command,
}

#[derive(Args, Debug, Clone)]
pub struct Global {
    /// WebSocket URL of the server. A wss:// URL may end in
    /// #sha256=<hex>, the SHA-256 of the server's certificate: then exactly
    /// that certificate is accepted, whoever signed it and whatever names it
    /// holds (`tinyverse-server tls fingerprint <world-dir>` prints it).
    #[arg(long, global = true, default_value = "ws://127.0.0.1:7878/ws")]
    pub url: String,
    /// Display name. Defaults to tv-cli-XXXX, or to the stored name when a token is given.
    #[arg(long, global = true)]
    pub name: Option<String>,
    /// The world's join password.
    #[arg(long, global = true)]
    pub password: Option<String>,
    /// Session or agent token.
    #[arg(long, global = true)]
    pub token: Option<String>,
    /// Read the token from a file (surrounding whitespace is ignored).
    #[arg(long, global = true, value_name = "PATH")]
    pub token_file: Option<PathBuf>,
    /// What to announce ourselves as.
    #[arg(long, global = true, value_enum, default_value_t = Kind::Agent)]
    pub kind: Kind,
    /// Use proto3 JSON text frames instead of binary protobuf.
    #[arg(long, global = true)]
    pub json: bool,
    /// More logging; repeat for even more.
    #[arg(short, long, global = true, action = clap::ArgAction::Count)]
    pub verbose: u8,
    /// PEM file of certificates to trust for wss:// in addition to the
    /// public roots, e.g. a self-signed server's cert.pem.
    #[arg(long, global = true, value_name = "PEM")]
    pub ca_file: Option<PathBuf>,
}

/// Loads `--ca-file`, refusing it for anything but a wss:// URL.
pub fn ca_certs(g: &Global) -> Result<Vec<rustls::pki_types::CertificateDer<'static>>, Failure> {
    let Some(path) = &g.ca_file else { return Ok(Vec::new()) };
    if !g.url.to_ascii_lowercase().starts_with("wss://") {
        return Err(Failure::Failed(format!(
            "--ca-file only applies to wss:// URLs, but --url is {}",
            g.url
        )));
    }
    crate::tls::load_ca_file(path).map_err(Failure::Failed)
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Human,
    Agent,
}

#[derive(ValueEnum, Clone, Copy, Debug, PartialEq, Eq)]
pub enum Speed {
    Walk,
    Run,
    Fly,
}

impl From<Speed> for MoveMode {
    fn from(s: Speed) -> Self {
        match s {
            Speed::Walk => MoveMode::Walk,
            Speed::Run => MoveMode::Run,
            Speed::Fly => MoveMode::Fly,
        }
    }
}

#[derive(Subcommand, Debug)]
pub enum Command {
    /// Join and print every event; stdin lines are chat or /commands (/help).
    Connect {
        /// Print each message as JSON instead of a readable line.
        #[arg(long)]
        raw: bool,
    },
    /// Join, say something, wait for the echo and leave.
    Say {
        /// The text; several words are joined with spaces.
        #[arg(required = true, num_args = 1..)]
        text: Vec<String>,
        /// Say it in local chat instead of world chat.
        #[arg(long)]
        local: bool,
    },
    /// Join, Observe, print what is nearby and leave.
    Look {
        /// Radius in metres; 0 means the server default.
        #[arg(long, default_value_t = 0.0)]
        radius: f32,
    },
    /// Create an object and print it.
    Create(CreateArgs),
    /// Change properties of an object and print the result.
    Update(UpdateArgs),
    /// Delete an object.
    Delete {
        /// Object id.
        id: u64,
    },
    /// Run a bot.
    Bot {
        #[command(subcommand)]
        bot: BotCommand,
    },
    /// End-to-end check of a running server.
    Smoke(SmokeArgs),
}

#[derive(Args, Debug, Clone)]
pub struct CreateArgs {
    /// box, sphere, cylinder, cone, wedge or plane.
    #[arg(long, default_value = "box", value_parser = parse_shape_arg)]
    pub shape: i32,
    /// Centre, x,y,z.
    #[arg(long = "pos", value_parser = parse_vec3, allow_hyphen_values = true)]
    pub position: Vec3,
    /// Quaternion x,y,z,w or Euler degrees x,y,z.
    #[arg(long = "rot", value_parser = parse_rotation, allow_hyphen_values = true)]
    pub rotation: Option<Quat>,
    /// Size in metres: one number or x,y,z. Default 1.
    #[arg(long, value_parser = parse_scale, allow_hyphen_values = true)]
    pub scale: Option<Vec3>,
    /// #rrggbb[aa] or r,g,b[,a] in 0..1 (both sRGB). Default light grey.
    #[arg(long, value_parser = parse_color)]
    pub color: Option<Color>,
    /// matte, glossy, metal, glass or emissive.
    #[arg(long, default_value = "matte", value_parser = parse_material_arg)]
    pub material: i32,
    /// Optional label.
    #[arg(long = "object-name", value_name = "NAME", default_value = "")]
    pub object_name: String,
    /// Whether avatars collide with it.
    #[arg(long, default_value_t = true, action = clap::ArgAction::Set)]
    pub solid: bool,
    /// Whether only its owner (and world owners) may edit it.
    #[arg(long, default_value_t = false, action = clap::ArgAction::Set)]
    pub locked: bool,
}

#[derive(Args, Debug, Clone)]
pub struct UpdateArgs {
    /// Object id.
    pub id: u64,
    #[arg(long, value_parser = parse_shape_arg)]
    pub shape: Option<i32>,
    #[arg(long = "pos", value_parser = parse_vec3, allow_hyphen_values = true)]
    pub position: Option<Vec3>,
    #[arg(long = "rot", value_parser = parse_rotation, allow_hyphen_values = true)]
    pub rotation: Option<Quat>,
    #[arg(long, value_parser = parse_scale, allow_hyphen_values = true)]
    pub scale: Option<Vec3>,
    #[arg(long, value_parser = parse_color)]
    pub color: Option<Color>,
    #[arg(long, value_parser = parse_material_arg)]
    pub material: Option<i32>,
    /// New label; an empty string clears it.
    #[arg(long = "object-name", value_name = "NAME")]
    pub object_name: Option<String>,
    #[arg(long, action = clap::ArgAction::Set)]
    pub solid: Option<bool>,
    #[arg(long, action = clap::ArgAction::Set)]
    pub locked: Option<bool>,
}

#[derive(Subcommand, Debug, Clone)]
pub enum BotCommand {
    /// Stream AvatarState at 10 Hz around a circle centred on the spawn point.
    Circle {
        /// Circle radius in metres.
        #[arg(long, default_value_t = 5.0)]
        radius: f32,
        #[arg(long, value_enum, default_value_t = Speed::Walk)]
        speed: Speed,
        /// Stop after this many seconds (default: run until Ctrl-C).
        #[arg(long, value_name = "SECONDS")]
        duration: Option<f64>,
    },
    /// Walk to random points within 20 m of spawn, pausing and chatting.
    Wander {
        #[arg(long, value_name = "SECONDS")]
        duration: Option<f64>,
        /// Use server-side MoveTo instead of streaming AvatarState.
        #[arg(long)]
        moveto: bool,
    },
}

#[derive(Args, Debug, Clone)]
pub struct SmokeArgs {
    /// Token of a world owner, to also check set-role and kick.
    #[arg(long, value_name = "PATH")]
    pub owner_token_file: Option<PathBuf>,
    /// Skip a check by name; repeatable. Names are listed in the output.
    #[arg(long, value_name = "NAME")]
    pub skip: Vec<String>,
    /// Seconds to wait for each expected message.
    #[arg(long, default_value_t = 5.0)]
    pub timeout: f64,
}

fn parse_shape_arg(s: &str) -> Result<i32, String> {
    parse_shape(s).map(|v| v as i32)
}

fn parse_material_arg(s: &str) -> Result<i32, String> {
    parse_material(s).map(|v| v as i32)
}

/// Why a command did not succeed.
#[derive(Debug)]
pub enum Failure {
    /// Could not connect or was refused: exit 2.
    Connect(String),
    /// Anything else: exit 1.
    Failed(String),
}

impl Failure {
    pub fn exit_code(&self) -> i32 {
        match self {
            Failure::Connect(_) => EXIT_CONNECT,
            Failure::Failed(_) => EXIT_FAILED,
        }
    }
}

impl From<ClientError> for Failure {
    fn from(e: ClientError) -> Self {
        Failure::Failed(e.to_string())
    }
}

pub(crate) fn emit(out: &mut dyn Write, line: impl AsRef<str>) {
    let _ = writeln!(out, "{}", line.as_ref());
    let _ = out.flush();
}

fn random_suffix() -> String {
    format!("{:04x}", rand::random::<u16>())
}

/// A random, clearly visible body colour.
pub fn random_appearance() -> Appearance {
    let hue = rand::random::<f32>() * 6.0;
    let x = 1.0 - (hue % 2.0 - 1.0).abs();
    let (r, g, b) = match hue as u32 {
        0 => (1.0, x, 0.0),
        1 => (x, 1.0, 0.0),
        2 => (0.0, 1.0, x),
        3 => (0.0, x, 1.0),
        4 => (x, 0.0, 1.0),
        _ => (1.0, 0.0, x),
    };
    Appearance { body_color: Some(Color { r, g, b, a: 1.0 }), ..Default::default() }
}

/// Reads a token file, trimming whitespace.
pub fn read_token_file(path: &std::path::Path) -> Result<String, Failure> {
    let t = std::fs::read_to_string(path)
        .map_err(|e| Failure::Failed(format!("cannot read token file {}: {e}", path.display())))?;
    let t = t.trim().to_string();
    if t.is_empty() {
        return Err(Failure::Failed(format!("token file {} is empty", path.display())));
    }
    Ok(t)
}

/// Builds connection options from the global flags.
pub fn client_options(g: &Global) -> Result<ClientOptions, Failure> {
    if g.token.is_some() && g.token_file.is_some() {
        return Err(Failure::Failed("give --token or --token-file, not both".into()));
    }
    let token = match (&g.token, &g.token_file) {
        (Some(t), _) => t.clone(),
        (None, Some(p)) => read_token_file(p)?,
        (None, None) => String::new(),
    };
    let name = match &g.name {
        Some(n) => n.clone(),
        None if !token.is_empty() => String::new(),
        None => format!("tv-cli-{}", random_suffix()),
    };
    let mut o = ClientOptions::new(&g.url);
    o.name = name;
    o.password = g.password.clone().unwrap_or_default();
    o.token = token;
    o.kind = match g.kind {
        Kind::Human => ClientKind::Human,
        Kind::Agent => ClientKind::Agent,
    };
    o.encoding = if g.json { Encoding::Json } else { Encoding::Binary };
    o.appearance = Some(random_appearance());
    o.ca_certs = ca_certs(g)?;
    Ok(o)
}

async fn join(g: &Global) -> Result<Client, Failure> {
    let opts = client_options(g)?;
    Client::connect(opts).await.map_err(|e| Failure::Connect(e.to_string()))
}

/// Parses arguments and runs; returns the process exit code.
pub async fn run_args<I, T>(args: I, out: &mut dyn Write) -> i32
where
    I: IntoIterator<Item = T>,
    T: Into<std::ffi::OsString> + Clone,
{
    match Cli::try_parse_from(args) {
        Ok(cli) => run(cli, out).await,
        Err(e) => {
            let code = if e.use_stderr() { EXIT_FAILED } else { EXIT_OK };
            let _ = e.print();
            code
        }
    }
}

/// Runs a parsed command line; returns the process exit code. Errors are
/// printed to stderr, results to `out`.
pub async fn run(cli: Cli, out: &mut dyn Write) -> i32 {
    match execute(cli, out).await {
        Ok(()) => EXIT_OK,
        Err(f) => {
            let (Failure::Connect(m) | Failure::Failed(m)) = &f;
            eprintln!("tv-cli: {m}");
            f.exit_code()
        }
    }
}

async fn execute(cli: Cli, out: &mut dyn Write) -> Result<(), Failure> {
    let g = &cli.global;
    match &cli.command {
        Command::Connect { raw } => connect(g, *raw, out).await,
        Command::Say { text, local } => say(g, &text.join(" "), *local, out).await,
        Command::Look { radius } => look(g, *radius, out).await,
        Command::Create(a) => create(g, a, out).await,
        Command::Update(a) => update(g, a, out).await,
        Command::Delete { id } => delete(g, *id, out).await,
        Command::Bot { bot } => match bot {
            BotCommand::Circle { radius, speed, duration } => {
                if !(radius.is_finite() && *radius >= 0.5) {
                    return Err(Failure::Failed("--radius must be at least 0.5".into()));
                }
                let client = join(g).await?;
                bots::circle(client, *radius, (*speed).into(), secs(*duration)?, out).await
            }
            BotCommand::Wander { duration, moveto } => {
                let client = join(g).await?;
                bots::wander(client, secs(*duration)?, *moveto, out).await
            }
        },
        Command::Smoke(a) => {
            let opts = smoke::SmokeOptions::from_cli(g, a)?;
            let report = smoke::run(opts, out).await;
            match report.exit_code() {
                EXIT_OK => Ok(()),
                EXIT_CONNECT => Err(Failure::Connect("smoke: could not join".into())),
                _ => Err(Failure::Failed(format!("smoke: {} check(s) failed", report.failures()))),
            }
        }
    }
}

fn secs(d: Option<f64>) -> Result<Option<Duration>, Failure> {
    match d {
        None => Ok(None),
        Some(s) if s.is_finite() && s >= 0.0 => Ok(Some(Duration::from_secs_f64(s))),
        Some(s) => Err(Failure::Failed(format!("bad duration {s}"))),
    }
}

/// Prints the join summary shared by the interactive commands.
fn print_join(client: &Client, out: &mut dyn Write) {
    let w = client.welcome();
    let world = w.world.clone().unwrap_or_default();
    emit(
        out,
        format!(
            "joined '{}' as avatar {} (user {}, role {}); {} other avatars, {} objects, {} chat lines",
            world.name,
            w.avatar_id,
            w.user_id,
            crate::parse::role_name(w.role),
            client.snapshot().avatars.len(),
            client.snapshot().objects.len(),
            client.history().len()
        ),
    );
}

async fn connect(g: &Global, raw: bool, out: &mut dyn Write) -> Result<(), Failure> {
    let stdin = tokio::io::BufReader::new(tokio::io::stdin());
    connect_session(g, raw, stdin, out).await
}

/// `connect` with its command input taken from `input` instead of stdin.
/// The session ends on `/quit`, Ctrl-C, or (as a failure) when the server
/// closes the connection; end of input only stops reading commands.
pub async fn connect_session<R>(
    g: &Global,
    raw: bool,
    input: R,
    out: &mut dyn Write,
) -> Result<(), Failure>
where
    R: tokio::io::AsyncBufRead + Unpin,
{
    let mut client = join(g).await?;
    let mut roster = Roster::from_client(&client);
    if raw {
        let w = serde_json::to_string(client.welcome()).unwrap_or_default();
        emit(out, format!("{{\"welcome\":{w}}}"));
    } else {
        print_join(&client, out);
        for e in client.history() {
            emit(out, format!("  {}", crate::format::chat_line(e)));
        }
    }

    let mut stdin = Some(input.lines());
    let ctrl_c = tokio::signal::ctrl_c();
    tokio::pin!(ctrl_c);
    let result = loop {
        tokio::select! {
            m = client.recv() => match m {
                Ok(m) => {
                    roster.apply(&m);
                    if raw {
                        emit(out, serde_json::to_string(&m).unwrap_or_default());
                    } else {
                        for line in describe(&m, &roster) {
                            emit(out, line);
                        }
                    }
                    roster.forget_left(&m);
                }
                Err(e) => break Err(Failure::Failed(e.to_string())),
            },
            line = async { stdin.as_mut().expect("guarded").next_line().await }, if stdin.is_some() => {
                match line {
                    Ok(Some(l)) => match parse_line(&l) {
                        Ok(Line::Quit) => break Ok(()),
                        Ok(cmd) => {
                            for line in run_line(&mut client, &roster, cmd)? {
                                emit(out, line);
                            }
                        }
                        Err(e) => emit(out, e),
                    },
                    // stdin closed: keep printing events.
                    _ => stdin = None,
                }
            }
            _ = &mut ctrl_c => break Ok(()),
        }
    };
    if result.is_ok() {
        client.close().await;
    }
    result
}

/// Carries out one stdin command; returns lines to print locally.
fn run_line(client: &mut Client, roster: &Roster, line: Line) -> Result<Vec<String>, Failure> {
    let body = match line {
        Line::Empty | Line::Quit => return Ok(vec![]),
        Line::Help => return Ok(vec![HELP.to_string()]),
        Line::Who => return Ok(roster.who()),
        Line::WorldChat(text) => CBody::Chat(Chat { channel: ChatChannel::World as i32, text }),
        Line::LocalChat(text) => CBody::Chat(Chat { channel: ChatChannel::Local as i32, text }),
        Line::MoveTo { target, mode } => {
            CBody::MoveTo(MoveTo { target: Some(target), mode: mode as i32 })
        }
        Line::Stop => CBody::Stop(Stop {}),
        Line::Look { radius } => CBody::Observe(Observe { radius }),
        Line::Create(spec) => CBody::ObjectCreate(ObjectCreate { spec: Some(spec) }),
        Line::Move { id, position } => {
            CBody::ObjectUpdate(ObjectUpdate { id, position: Some(position), ..Default::default() })
        }
        Line::Recolor { id, color } => {
            CBody::ObjectUpdate(ObjectUpdate { id, color: Some(color), ..Default::default() })
        }
        Line::Delete { id } => CBody::ObjectDelete(ObjectDelete { id }),
        Line::History => CBody::ChatHistoryRequest(ChatHistoryRequest { since_ms: 0, limit: 0 }),
    };
    client.send(body)?;
    Ok(vec![])
}

async fn say(g: &Global, text: &str, local: bool, out: &mut dyn Write) -> Result<(), Failure> {
    let mut client = join(g).await?;
    let channel = if local { ChatChannel::Local } else { ChatChannel::World };
    let seq = client.send(CBody::Chat(Chat { channel: channel as i32, text: text.to_string() }))?;
    let me = client.avatar_id();
    let echo = client
        .wait_for(REPLY_TIMEOUT, "the echo of our chat", |m| match &m.body {
            Some(SBody::Error(_)) => m.reply_to == seq,
            Some(SBody::ChatMessage(c)) => {
                m.reply_to == seq
                    || c.entry.as_ref().is_some_and(|e| e.sender_avatar_id == me && e.text == text)
            }
            _ => false,
        })
        .await?;
    let result = match &echo.body {
        Some(SBody::ChatMessage(c)) => {
            emit(out, crate::format::chat_line(&c.entry.clone().unwrap_or_default()));
            Ok(())
        }
        Some(SBody::Error(e)) => Err(Failure::Failed(
            ClientError::Server { code: e.code, message: e.message.clone() }.to_string(),
        )),
        _ => unreachable!("filtered above"),
    };
    client.close().await;
    result
}

async fn look(g: &Global, radius: f32, out: &mut dyn Write) -> Result<(), Failure> {
    let mut client = join(g).await?;
    let reply = client.request(CBody::Observe(Observe { radius }), REPLY_TIMEOUT).await;
    let result = match reply {
        Ok(m) => match &m.body {
            Some(SBody::Observation(o)) => {
                for l in observation_lines(o) {
                    emit(out, l);
                }
                Ok(())
            }
            other => Err(Failure::Failed(format!(
                "expected Observation, got {}",
                crate::client::body_name(other)
            ))),
        },
        Err(e) => Err(e.into()),
    };
    client.close().await;
    result
}

/// The spec `create` sends for its flags.
pub fn create_spec(a: &CreateArgs) -> ObjectSpec {
    let mut spec = default_spec(tv_proto::v1::Shape::Box, a.position);
    spec.shape = a.shape;
    spec.name = a.object_name.clone();
    if let Some(r) = a.rotation {
        spec.rotation = Some(r);
    }
    if let Some(s) = a.scale {
        spec.scale = Some(s);
    }
    if let Some(c) = a.color {
        spec.color = Some(c);
    }
    spec.material = a.material;
    spec.solid = a.solid;
    spec.locked = a.locked;
    spec
}

/// The update `update` sends for its flags: only the given fields are set.
pub fn update_message(a: &UpdateArgs) -> ObjectUpdate {
    ObjectUpdate {
        id: a.id,
        name: a.object_name.clone(),
        shape: a.shape,
        position: a.position,
        rotation: a.rotation,
        scale: a.scale,
        color: a.color,
        material: a.material,
        solid: a.solid,
        locked: a.locked,
    }
}

async fn object_request(g: &Global, body: CBody, out: &mut dyn Write) -> Result<(), Failure> {
    let mut client = join(g).await?;
    let reply = client.request(body, REPLY_TIMEOUT).await;
    let result = match reply {
        Ok(m) => match &m.body {
            Some(SBody::ObjectCreated(c)) => {
                emit(out, object_line(&c.object.clone().unwrap_or_default()));
                Ok(())
            }
            Some(SBody::ObjectUpdated(u)) => {
                emit(out, object_line(&u.object.clone().unwrap_or_default()));
                Ok(())
            }
            Some(SBody::ObjectDeleted(d)) => {
                emit(out, format!("deleted object {}", d.id));
                Ok(())
            }
            Some(SBody::Ack(_)) => {
                emit(out, "ok");
                Ok(())
            }
            other => Err(Failure::Failed(format!(
                "unexpected reply {}",
                crate::client::body_name(other)
            ))),
        },
        Err(e) => Err(e.into()),
    };
    client.close().await;
    result
}

async fn create(g: &Global, a: &CreateArgs, out: &mut dyn Write) -> Result<(), Failure> {
    let body = CBody::ObjectCreate(ObjectCreate { spec: Some(create_spec(a)) });
    object_request(g, body, out).await
}

async fn update(g: &Global, a: &UpdateArgs, out: &mut dyn Write) -> Result<(), Failure> {
    object_request(g, CBody::ObjectUpdate(update_message(a)), out).await
}

async fn delete(g: &Global, id: u64, out: &mut dyn Write) -> Result<(), Failure> {
    object_request(g, CBody::ObjectDelete(ObjectDelete { id }), out).await
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(args: &[&str]) -> Cli {
        Cli::try_parse_from(std::iter::once("tv-cli").chain(args.iter().copied())).unwrap()
    }

    #[test]
    fn global_flags_anywhere() {
        let cli = parse(&["say", "hi", "there", "--local", "--json", "--url", "ws://x:1/ws"]);
        assert!(cli.global.json);
        assert_eq!(cli.global.url, "ws://x:1/ws");
        let Command::Say { text, local } = cli.command else { panic!() };
        assert_eq!(text, ["hi", "there"]);
        assert!(local);
    }

    #[test]
    fn create_flags_with_negative_vectors() {
        let cli = parse(&[
            "create",
            "--shape",
            "cone",
            "--pos",
            "-1,0.5,-2",
            "--scale",
            "2",
            "--rot",
            "0,90,0",
            "--color",
            "#ff0000",
            "--material",
            "metal",
            "--object-name",
            "c",
            "--solid",
            "false",
            "--locked",
            "true",
        ]);
        let Command::Create(a) = cli.command else { panic!() };
        let s = create_spec(&a);
        assert_eq!(s.shape, tv_proto::v1::Shape::Cone as i32);
        assert_eq!(s.position, Some(Vec3 { x: -1.0, y: 0.5, z: -2.0 }));
        assert_eq!(s.scale, Some(Vec3 { x: 2.0, y: 2.0, z: 2.0 }));
        assert!((s.rotation.unwrap().y - std::f32::consts::FRAC_1_SQRT_2).abs() < 1e-4);
        assert_eq!(s.material, tv_proto::v1::Material::Metal as i32);
        assert_eq!(s.name, "c");
        assert!(!s.solid);
        assert!(s.locked);
    }

    #[test]
    fn update_sets_only_given_fields() {
        let cli = parse(&["update", "7", "--color", "#00ff00", "--solid", "false"]);
        let Command::Update(a) = cli.command else { panic!() };
        let u = update_message(&a);
        assert_eq!(u.id, 7);
        assert_eq!(u.solid, Some(false));
        assert!(u.color.is_some());
        assert_eq!((u.name, u.shape, u.position, u.locked), (None, None, None, None));
    }

    #[test]
    fn bad_values_are_usage_errors() {
        for args in [
            &["create"][..],
            &["create", "--pos", "1,2"],
            &["create", "--pos", "0,0,0", "--shape", "cube"],
            &["update", "x"],
            &["bot", "circle", "--speed", "warp"],
            &["--kind", "robot", "connect"],
        ] {
            let r = Cli::try_parse_from(std::iter::once("tv-cli").chain(args.iter().copied()));
            assert!(r.is_err(), "{args:?} parsed");
        }
    }
}
