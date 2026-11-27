//! `tv-cli smoke`: an end-to-end check of a running server.
//!
//! Three connections are used: A (binary, human), B (proto3 JSON, agent) and
//! C (binary, agent, joins during `presence` and is moved far away for the
//! local chat check). With `--owner-token-file` an owner connection O and a
//! throwaway K are added for the admin checks.

use std::{io::Write, time::Duration};

use tv_proto::{
    Encoding, Frame, PROTOCOL_VERSION,
    v1::{
        Admin, AvatarState, AvatarTransform, Chat, ChatChannel, ChatHistoryRequest, ClientKind,
        Color, ErrorCode, Limits, Material, MoveMode, MoveTo, ObjectCreate, ObjectDelete,
        ObjectInfo, ObjectSpec, ObjectUpdate, Observe, Ping, Quat, Role, ServerMessage, Shape,
        admin, client_message::Body as CBody, server_message::Body as SBody,
    },
};

use crate::{
    cli::{EXIT_CONNECT, EXIT_FAILED, EXIT_OK, Failure, Global, SmokeArgs, emit, read_token_file},
    client::{Client, ClientError, ClientOptions, body_name, error_code_name, now_ms},
    format::vec,
    motion::{V3, bearing, norm180, speed_limit, yaw_of},
    parse::role_name,
};

/// Every check, in the order it runs.
pub const CHECKS: &[&str] = &[
    "join",
    "presence",
    "chat-world",
    "history",
    "avatar-state",
    "correction",
    "moveto",
    "chat-local",
    "object-create",
    "object-update",
    "not-found",
    "observe",
    "object-delete",
    "ping",
    "bad-request",
    "admin",
    "leave",
];

#[derive(Clone, Debug)]
pub struct SmokeOptions {
    pub url: String,
    pub password: String,
    /// Used for A only; B, C and K join as new users.
    pub token: String,
    pub owner_token: Option<String>,
    pub skip: Vec<String>,
    /// How long to wait for each expected message.
    pub timeout: Duration,
    /// How long to watch for something that must not arrive.
    pub quiet: Duration,
}

impl SmokeOptions {
    pub fn new(url: impl Into<String>) -> Self {
        SmokeOptions {
            url: url.into(),
            password: String::new(),
            token: String::new(),
            owner_token: None,
            skip: Vec::new(),
            timeout: Duration::from_secs(5),
            quiet: Duration::from_secs(1),
        }
    }

    pub fn from_cli(g: &Global, a: &SmokeArgs) -> Result<Self, Failure> {
        let mut o = SmokeOptions::new(&g.url);
        o.password = g.password.clone().unwrap_or_default();
        o.token = match (&g.token, &g.token_file) {
            (Some(_), Some(_)) => {
                return Err(Failure::Failed("give --token or --token-file, not both".into()));
            }
            (Some(t), None) => t.clone(),
            (None, Some(p)) => read_token_file(p)?,
            (None, None) => String::new(),
        };
        o.owner_token = a.owner_token_file.as_deref().map(read_token_file).transpose()?;
        for s in &a.skip {
            if !CHECKS.contains(&s.as_str()) {
                return Err(Failure::Failed(format!(
                    "unknown check '{s}'; checks are: {}",
                    CHECKS.join(", ")
                )));
            }
            if s == "join" {
                return Err(Failure::Failed("'join' cannot be skipped".into()));
            }
        }
        o.skip = a.skip.clone();
        if !(a.timeout.is_finite() && a.timeout > 0.0) {
            return Err(Failure::Failed("--timeout must be positive".into()));
        }
        o.timeout = Duration::from_secs_f64(a.timeout);
        Ok(o)
    }
}

#[derive(Clone, Debug, PartialEq)]
pub enum Outcome {
    Pass(String),
    Fail(String),
    Skip(String),
}

#[derive(Clone, Debug, Default)]
pub struct SmokeReport {
    pub results: Vec<(String, Outcome)>,
    /// False when A or B could not join.
    pub joined: bool,
}

impl SmokeReport {
    pub fn outcome(&self, name: &str) -> Option<&Outcome> {
        self.results.iter().find(|(n, _)| n == name).map(|(_, o)| o)
    }

    pub fn passed(&self, name: &str) -> bool {
        matches!(self.outcome(name), Some(Outcome::Pass(_)))
    }

    pub fn failures(&self) -> usize {
        self.results.iter().filter(|(_, o)| matches!(o, Outcome::Fail(_))).count()
    }

    pub fn exit_code(&self) -> i32 {
        if !self.joined {
            EXIT_CONNECT
        } else if self.failures() > 0 {
            EXIT_FAILED
        } else {
            EXIT_OK
        }
    }
}

type Check = Result<String, Outcome>;

fn fail(s: impl Into<String>) -> Outcome {
    Outcome::Fail(s.into())
}

fn skip(s: impl Into<String>) -> Outcome {
    Outcome::Skip(s.into())
}

fn secs(d: Duration) -> String {
    format!("{:.1}s", d.as_secs_f32())
}

/// Waits for a message on `c`, describing a timeout in terms of `who` and `what`.
async fn expect(
    c: &mut Client,
    who: &str,
    what: &str,
    t: Duration,
    pred: impl FnMut(&ServerMessage) -> bool,
) -> Result<ServerMessage, Outcome> {
    c.wait_for(t, what, pred).await.map_err(|e| match e {
        ClientError::Timeout(_) => {
            fail(format!("timed out after {} waiting for {who} to receive {what}", secs(t)))
        }
        e => fail(format!("{who}: {e} (while waiting for {what})")),
    })
}

/// Sends a request on `c` and waits for its reply.
async fn req(c: &mut Client, who: &str, what: &str, body: CBody, t: Duration) -> Check2 {
    c.request(body, t).await.map_err(|e| match e {
        ClientError::Timeout(_) => fail(format!(
            "timed out after {} waiting for {who}'s reply to {what} (a message with reply_to = its seq)",
            secs(t)
        )),
        ClientError::Server { code, message } => {
            fail(format!("{who}'s {what} was refused: {} {message}", error_code_name(code)))
        }
        e => fail(format!("{who}: {e} (during {what})")),
    })
}

type Check2 = Result<ServerMessage, Outcome>;

/// Sends an admin command as the owner and waits for its `Ack`. Other
/// messages with the same `reply_to` (the owner's own copy of the
/// `AvatarUpdated` a role change causes) are skipped; an `Error` fails.
async fn admin_ack(owner: &mut Client, what: &str, body: CBody, t: Duration) -> Check2 {
    let seq = owner.send(body).map_err(|e| fail(format!("the owner: {e}")))?;
    let m = expect(owner, "the owner", &format!("Ack for {what}"), t, |m| {
        m.reply_to == seq && matches!(m.body, Some(SBody::Ack(_)) | Some(SBody::Error(_)))
    })
    .await?;
    match m.body {
        Some(SBody::Error(e)) => Err(fail(format!(
            "the owner's {what} was refused: {} {}",
            error_code_name(e.code),
            e.message
        ))),
        _ => Ok(m),
    }
}

fn has_state(m: &ServerMessage, id: u64, mut f: impl FnMut(&AvatarTransform) -> bool) -> bool {
    match &m.body {
        Some(SBody::AvatarStates(s)) => {
            s.states.iter().any(|e| e.avatar_id == id && e.transform.as_ref().is_some_and(&mut f))
        }
        _ => false,
    }
}

fn pos_of(t: &AvatarTransform) -> V3 {
    t.position.into()
}

fn chat_text(m: &ServerMessage) -> Option<&tv_proto::v1::ChatEntry> {
    match &m.body {
        Some(SBody::ChatMessage(c)) => c.entry.as_ref(),
        _ => None,
    }
}

fn close_v(a: V3, b: V3, tol: f32) -> bool {
    a.dist(b) <= tol
}

struct Ctx<'o> {
    o: &'o SmokeOptions,
    tag: String,
    a: Option<Client>,
    b: Option<Client>,
    c: Option<Client>,
    limits: Limits,
    bounds: Option<(V3, V3)>,
    a_pos: V3,
    c_pos: V3,
    c_far: bool,
    world_text: Option<String>,
    object: Option<ObjectInfo>,
}

fn options(
    o: &SmokeOptions,
    who: &str,
    tag: &str,
    enc: Encoding,
    kind: ClientKind,
) -> ClientOptions {
    let mut c = ClientOptions::new(&o.url);
    c.name = format!("smoke-{who}-{tag}");
    c.password = o.password.clone();
    c.encoding = enc;
    c.kind = kind;
    c.appearance = Some(crate::cli::random_appearance());
    c.join_timeout = o.timeout.max(Duration::from_secs(5));
    c
}

fn record(report: &mut SmokeReport, out: &mut dyn Write, name: &str, r: Outcome) {
    let line = match &r {
        Outcome::Pass(d) => format!("PASS  {name:<14} {d}"),
        Outcome::Fail(d) => format!("FAIL  {name:<14} {d}"),
        Outcome::Skip(d) => format!("SKIP  {name:<14} {d}"),
    };
    emit(out, line);
    report.results.push((name.to_string(), r));
}

/// Runs every check not skipped and prints a PASS/FAIL/SKIP line for each.
pub async fn run(o: SmokeOptions, out: &mut dyn Write) -> SmokeReport {
    let mut report = SmokeReport::default();
    let tag = format!("{:04x}", rand::random::<u16>());

    // join: everything else needs A and B.
    let mut a_opts = options(&o, "a", &tag, Encoding::Binary, ClientKind::Human);
    a_opts.token = o.token.clone();
    let a = match Client::connect(a_opts).await {
        Ok(c) => c,
        Err(e) => {
            record(&mut report, out, "join", fail(format!("A (binary) could not join: {e}")));
            return report;
        }
    };
    let b = match Client::connect(options(&o, "b", &tag, Encoding::Json, ClientKind::Agent)).await {
        Ok(c) => c,
        Err(e) => {
            record(&mut report, out, "join", fail(format!("B (JSON) could not join: {e}")));
            a.close().await;
            return report;
        }
    };
    report.joined = true;
    let r = check_join(&a, &b);
    record(&mut report, out, "join", r.map_or_else(|e| e, Outcome::Pass));

    let w = a.welcome().clone();
    let limits = w.limits.unwrap_or_default();
    let bounds = w.world.as_ref().and_then(|wi| match (wi.bounds_min, wi.bounds_max) {
        (Some(lo), Some(hi)) => Some((V3::from(lo), V3::from(hi))),
        _ => None,
    });
    let a_pos = w
        .avatar
        .as_ref()
        .and_then(|x| x.transform.as_ref())
        .map(pos_of)
        .unwrap_or_else(|| w.world.as_ref().and_then(|wi| wi.spawn_point).into());
    let mut ctx = Ctx {
        o: &o,
        tag,
        a: Some(a),
        b: Some(b),
        c: None,
        limits,
        bounds,
        a_pos,
        c_pos: V3::ZERO,
        c_far: false,
        world_text: None,
        object: None,
    };

    for &name in &CHECKS[1..] {
        if o.skip.iter().any(|s| s == name) {
            record(&mut report, out, name, skip("skipped with --skip"));
            continue;
        }
        let budget = Duration::from_secs(120) + o.timeout * 10;
        let r = match tokio::time::timeout(budget, ctx.run_check(name)).await {
            Ok(Ok(detail)) => Outcome::Pass(detail),
            Ok(Err(o)) => o,
            Err(_) => fail(format!("the whole check took longer than {}", secs(budget))),
        };
        record(&mut report, out, name, r);
    }

    for c in [ctx.c.take(), ctx.b.take(), ctx.a.take()].into_iter().flatten() {
        c.close().await;
    }
    let passed = report.results.iter().filter(|(_, o)| matches!(o, Outcome::Pass(_))).count();
    let skipped = report.results.iter().filter(|(_, o)| matches!(o, Outcome::Skip(_))).count();
    emit(out, format!("smoke: {passed} passed, {} failed, {skipped} skipped", report.failures()));
    report
}

fn check_join(a: &Client, b: &Client) -> Check {
    let mut issues = Vec::new();
    for (who, c) in [("A", a), ("B", b)] {
        let w = c.welcome();
        if w.protocol_version != PROTOCOL_VERSION {
            issues.push(format!("{who}'s Welcome has protocol_version {}", w.protocol_version));
        }
        if w.avatar_id == 0 {
            issues.push(format!("{who}'s Welcome has avatar_id 0"));
        }
        if w.user_id.is_empty() {
            issues.push(format!("{who}'s Welcome has no user_id"));
        }
        if !c.snapshot().complete {
            issues.push(format!(
                "{who} got {} Snapshot chunk(s) but none with last = true before ChatHistory",
                c.snapshot().chunks
            ));
        }
        if c.snapshot().avatars.iter().any(|x| x.id == w.avatar_id) {
            issues.push(format!("{who}'s Snapshot includes its own avatar"));
        }
    }
    if a.avatar_id() == b.avatar_id() {
        issues.push("A and B got the same avatar id".into());
    }
    if !b.snapshot().avatars.iter().any(|x| x.id == a.avatar_id()) {
        issues.push(format!("B's Snapshot does not list A (avatar {})", a.avatar_id()));
    }
    if !issues.is_empty() {
        return Err(fail(issues.join("; ")));
    }
    Ok(format!(
        "A avatar {} (binary, {}), B avatar {} (JSON, {}); {} chunk(s), {} objects, {} chat lines",
        a.avatar_id(),
        role_name(a.welcome().role),
        b.avatar_id(),
        role_name(b.welcome().role),
        b.snapshot().chunks,
        b.snapshot().objects.len(),
        b.history().len()
    ))
}

impl Ctx<'_> {
    async fn run_check(&mut self, name: &str) -> Check {
        match name {
            "presence" => self.presence().await,
            "chat-world" => self.chat_world().await,
            "history" => self.history().await,
            "avatar-state" => self.avatar_state().await,
            "correction" => self.correction().await,
            "moveto" => self.moveto().await,
            "chat-local" => self.chat_local().await,
            "object-create" => self.object_create().await,
            "object-update" => self.object_update().await,
            "not-found" => self.not_found().await,
            "observe" => self.observe().await,
            "object-delete" => self.object_delete().await,
            "ping" => self.ping().await,
            "bad-request" => self.bad_request().await,
            "admin" => self.admin().await,
            "leave" => self.leave().await,
            other => Err(fail(format!("no such check {other}"))),
        }
    }

    fn t(&self) -> Duration {
        self.o.timeout
    }

    fn a(&mut self) -> Result<&mut Client, Outcome> {
        self.a.as_mut().ok_or_else(|| skip("A has left"))
    }

    fn b(&mut self) -> Result<&mut Client, Outcome> {
        self.b.as_mut().ok_or_else(|| skip("B has left"))
    }

    fn ids(&self) -> (u64, u64, u64) {
        let id = |c: &Option<Client>| c.as_ref().map(|c| c.avatar_id()).unwrap_or(0);
        (id(&self.a), id(&self.b), id(&self.c))
    }

    fn local_radius(&self) -> f32 {
        if self.limits.local_chat_radius > 0.0 { self.limits.local_chat_radius } else { 20.0 }
    }

    /// Free horizontal distance from `from` along `dir` before the bounds.
    fn room(&self, from: V3, dir: V3) -> f32 {
        let Some((lo, hi)) = self.bounds else { return 1000.0 };
        let along = |p: f32, d: f32, lo: f32, hi: f32| {
            if d > 0.0 {
                (hi - p) / d
            } else if d < 0.0 {
                (lo - p) / d
            } else {
                f32::INFINITY
            }
        };
        along(from.x, dir.x, lo.x, hi.x).min(along(from.z, dir.z, lo.z, hi.z))
    }

    /// The axis direction with the most room from `from`.
    fn roomiest(&self, from: V3) -> V3 {
        [
            V3::new(1.0, 0.0, 0.0),
            V3::new(-1.0, 0.0, 0.0),
            V3::new(0.0, 0.0, 1.0),
            V3::new(0.0, 0.0, -1.0),
        ]
        .into_iter()
        .max_by(|x, y| self.room(from, *x).total_cmp(&self.room(from, *y)))
        .expect("four directions")
    }

    async fn presence(&mut self) -> Check {
        let t = self.t();
        let (a_id, b_id, _) = self.ids();
        let b_name = format!("smoke-b-{}", self.tag);
        let m = expect(self.a()?, "A", &format!("AvatarJoined for B (avatar {b_id})"), t, |m| {
            matches!(&m.body, Some(SBody::AvatarJoined(j)) if j.avatar.as_ref().is_some_and(|x| x.id == b_id))
        })
        .await?;
        let Some(SBody::AvatarJoined(j)) = m.body else { unreachable!() };
        let info = j.avatar.unwrap_or_default();
        if info.name != b_name {
            return Err(fail(format!(
                "A's AvatarJoined for B has name '{}', expected '{b_name}'",
                info.name
            )));
        }
        if info.kind != ClientKind::Agent as i32 {
            return Err(fail(format!(
                "A's AvatarJoined for B has kind {}, expected agent",
                crate::parse::kind_name(info.kind)
            )));
        }
        if m.reply_to != 0 {
            return Err(fail(format!("A's AvatarJoined for B has reply_to {}", m.reply_to)));
        }

        let c =
            Client::connect(options(self.o, "c", &self.tag, Encoding::Binary, ClientKind::Agent))
                .await
                .map_err(|e| fail(format!("C could not join: {e}")))?;
        let c_id = c.avatar_id();
        self.c_pos = c
            .welcome()
            .avatar
            .as_ref()
            .and_then(|x| x.transform.as_ref())
            .map(pos_of)
            .unwrap_or_default();
        let lists_both =
            [a_id, b_id].iter().all(|id| c.snapshot().avatars.iter().any(|x| x.id == *id));
        self.c = Some(c);
        if !lists_both {
            return Err(fail("C's Snapshot does not list both A and B"));
        }
        let what = format!("AvatarJoined for C (avatar {c_id})");
        let joined = |m: &ServerMessage| matches!(&m.body, Some(SBody::AvatarJoined(j)) if j.avatar.as_ref().is_some_and(|x| x.id == c_id));
        expect(self.a()?, "A", &what, t, joined).await?;
        expect(self.b()?, "B", &what, t, joined).await?;
        Ok(format!("A saw B join (agent '{b_name}'); A and B saw C (avatar {c_id}) join"))
    }

    async fn chat_world(&mut self) -> Check {
        let t = self.t();
        let (a_id, _, _) = self.ids();
        let text = format!("smoke world {}", self.tag);
        let seq = self
            .a()?
            .send(CBody::Chat(Chat { channel: ChatChannel::World as i32, text: text.clone() }))
            .map_err(|e| fail(e.to_string()))?;
        let what = format!("ChatMessage '{text}'");
        let is_it = |m: &ServerMessage| chat_text(m).is_some_and(|e| e.text == text);
        let mut a_reply = 0;
        for who in ["A", "B", "C"] {
            let client = match who {
                "A" => self.a.as_mut(),
                "B" => self.b.as_mut(),
                _ => self.c.as_mut(),
            };
            let Some(client) = client else { continue };
            let m = expect(client, who, &what, t, is_it).await?;
            let e = chat_text(&m).expect("matched").clone();
            if e.channel != ChatChannel::World as i32 {
                return Err(fail(format!("{who}'s copy has channel {}", e.channel)));
            }
            if e.sender_avatar_id != a_id {
                return Err(fail(format!(
                    "{who}'s copy has sender_avatar_id {}, expected A ({a_id})",
                    e.sender_avatar_id
                )));
            }
            if who == "A" {
                a_reply = m.reply_to;
                if m.reply_to != 0 && m.reply_to != seq {
                    return Err(fail(format!(
                        "A's own copy has reply_to {}, expected {seq} or 0",
                        m.reply_to
                    )));
                }
            } else if m.reply_to != 0 {
                return Err(fail(format!("{who}'s copy has reply_to {}, expected 0", m.reply_to)));
            }
        }
        self.world_text = Some(text);
        Ok(format!("A, B and C got it (A's echo reply_to = {a_reply})"))
    }

    async fn history(&mut self) -> Check {
        let t = self.t();
        let text = self.world_text.clone().ok_or_else(|| skip("needs chat-world"))?;
        let m = req(
            self.a()?,
            "A",
            "ChatHistoryRequest",
            CBody::ChatHistoryRequest(ChatHistoryRequest { since_ms: 0, limit: 0 }),
            t,
        )
        .await?;
        let Some(SBody::ChatHistory(h)) = m.body else {
            return Err(fail(format!("expected ChatHistory, got {}", body_name(&m.body))));
        };
        if !h.entries.iter().any(|e| e.text == text) {
            return Err(fail(format!(
                "ChatHistory has {} entries but not '{text}'",
                h.entries.len()
            )));
        }
        if h.entries.windows(2).any(|w| w[0].timestamp_ms > w[1].timestamp_ms) {
            return Err(fail("ChatHistory is not oldest first"));
        }
        if let Some(e) = h.entries.iter().find(|e| e.channel != ChatChannel::World as i32) {
            return Err(fail(format!("ChatHistory contains a non-world entry: '{}'", e.text)));
        }
        Ok(format!("{} entries, includes '{text}'", h.entries.len()))
    }

    async fn avatar_state(&mut self) -> Check {
        let t = self.t();
        let quiet = self.o.quiet;
        let (a_id, _, _) = self.ids();
        let start = self.a_pos;
        let dir = self.roomiest(start);
        let speed = 1.0;
        self.a()?.drain();
        let mut pos = start;
        for i in 1..=12 {
            pos = start + dir * (speed * 0.1 * i as f32);
            let tr = AvatarTransform {
                position: Some(pos.into()),
                yaw: yaw_of(dir),
                velocity: Some((dir * speed).into()),
                move_mode: MoveMode::Walk as i32,
                animation: "walk".into(),
            };
            self.a()?
                .send(CBody::AvatarState(AvatarState { transform: Some(tr) }))
                .map_err(|e| fail(e.to_string()))?;
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
        let idle = AvatarTransform {
            position: Some(pos.into()),
            yaw: yaw_of(dir),
            velocity: Some(V3::ZERO.into()),
            move_mode: MoveMode::Walk as i32,
            animation: "idle".into(),
        };
        self.a()?
            .send(CBody::AvatarState(AvatarState { transform: Some(idle) }))
            .map_err(|e| fail(e.to_string()))?;
        self.a_pos = pos;
        let what = format!("AvatarStates with A (avatar {a_id}) at {}", vec(&Some(pos.into())));
        expect(self.b()?, "B", &what, t, |m| {
            has_state(m, a_id, |tr| close_v(pos_of(tr), pos, 0.3))
        })
        .await?;
        tokio::time::sleep(quiet).await;
        let mine = self.a()?.drain();
        if mine.iter().any(|m| has_state(m, a_id, |_| true)) {
            return Err(fail("A's own avatar appeared in the AvatarStates sent to A"));
        }
        if let Some(m) = mine.iter().find(|m| matches!(m.body, Some(SBody::AvatarCorrection(_)))) {
            let Some(SBody::AvatarCorrection(c)) = &m.body else { unreachable!() };
            return Err(fail(format!("A was corrected for a 1 m/s walk: {}", c.reason)));
        }
        Ok(format!("B saw A walk 1.2 m to {}; A never saw itself", vec(&Some(pos.into()))))
    }

    async fn correction(&mut self) -> Check {
        let t = self.t();
        let legal = self.a_pos;
        let dir = self.roomiest(legal);
        let jump = (self.room(legal, dir) - 1.0).min(50.0);
        if jump < 5.0 {
            return Err(fail("the world bounds are too small to try a teleport"));
        }
        let illegal = legal + dir * jump;
        let state = |p: V3| {
            CBody::AvatarState(AvatarState {
                transform: Some(AvatarTransform {
                    position: Some(p.into()),
                    yaw: yaw_of(dir),
                    velocity: Some(V3::ZERO.into()),
                    move_mode: MoveMode::Walk as i32,
                    animation: "idle".into(),
                }),
            })
        };
        let a = self.a()?;
        a.send(state(legal)).map_err(|e| fail(e.to_string()))?;
        tokio::time::sleep(Duration::from_millis(100)).await;
        a.send(state(illegal)).map_err(|e| fail(e.to_string()))?;
        let m = expect(
            a,
            "A",
            &format!("AvatarCorrection after a {jump:.0} m jump in 0.1 s"),
            t,
            |m| matches!(m.body, Some(SBody::AvatarCorrection(_))),
        )
        .await?;
        let Some(SBody::AvatarCorrection(c)) = m.body else { unreachable!() };
        let p: V3 = c.transform.as_ref().map(pos_of).unwrap_or_default();
        if close_v(p, illegal, 1.0) {
            return Err(fail("the correction accepts the illegal position"));
        }
        self.a_pos = p;
        Ok(format!("corrected to {} ({})", vec(&Some(p.into())), c.reason))
    }

    /// Moves C far from A and B with MoveTo and waits for it to arrive.
    async fn move_c_away(&mut self) -> Result<String, Outcome> {
        let t = self.t();
        let (_, _, c_id) = self.ids();
        if self.c.is_none() {
            return Err(skip("needs C from presence"));
        }
        let from = self.c_pos;
        let dir = self.roomiest(from);
        let dist = (self.local_radius() + 10.0).min(self.room(from, dir) - 1.0);
        let target = from + dir * dist;
        let run = speed_limit(Some(&self.limits), MoveMode::Run);
        let wait = Duration::from_secs_f32(dist / run * 2.0) + t;
        let c = self.c.as_mut().expect("checked");
        let started = tokio::time::Instant::now();
        let seq = c
            .send(CBody::MoveTo(MoveTo { target: Some(target.into()), mode: MoveMode::Run as i32 }))
            .map_err(|e| fail(e.to_string()))?;
        let what =
            format!("its own AvatarStates entry within 0.5 m of {}", vec(&Some(target.into())));
        let m = expect(c, "C", &what, wait, |m| {
            (matches!(m.body, Some(SBody::Error(_))) && m.reply_to == seq)
                || has_state(m, c_id, |tr| pos_of(tr).hdist(target) < 0.5)
        })
        .await?;
        if let Some(SBody::Error(e)) = m.body {
            return Err(fail(format!("MoveTo refused: {} {}", error_code_name(e.code), e.message)));
        }
        let took = started.elapsed();
        expect(
            self.a()?,
            "A",
            &format!("C's AvatarStates near {}", vec(&Some(target.into()))),
            t,
            |m| has_state(m, c_id, |tr| pos_of(tr).hdist(target) < 0.5),
        )
        .await?;
        self.c_pos = target;
        self.c_far = true;
        Ok(format!(
            "C ran {dist:.0} m to {} in {}; A saw it",
            vec(&Some(target.into())),
            secs(took)
        ))
    }

    async fn moveto(&mut self) -> Check {
        self.move_c_away().await
    }

    async fn chat_local(&mut self) -> Check {
        let t = self.t();
        let quiet = self.o.quiet;
        if !self.c_far {
            self.move_c_away().await.map_err(|e| match e {
                Outcome::Fail(m) => fail(format!("could not move C out of range: {m}")),
                o => o,
            })?;
        }
        let text = format!("smoke local {}", self.tag);
        self.a()?
            .send(CBody::Chat(Chat { channel: ChatChannel::Local as i32, text: text.clone() }))
            .map_err(|e| fail(e.to_string()))?;
        let what = format!("local ChatMessage '{text}'");
        let is_it = |m: &ServerMessage| chat_text(m).is_some_and(|e| e.text == text);
        expect(self.a()?, "A", &what, t, is_it).await?;
        let m = expect(self.b()?, "B", &what, t, is_it).await?;
        let e = chat_text(&m).expect("matched");
        if e.channel != ChatChannel::Local as i32 {
            return Err(fail(format!("B's copy has channel {}, expected local", e.channel)));
        }
        if m.reply_to != 0 {
            return Err(fail(format!("B's copy has reply_to {}, expected 0", m.reply_to)));
        }
        let d = self.c_pos.hdist(self.a_pos);
        let r = self.local_radius();
        let c = self.c.as_mut().expect("moved away above");
        match c.wait_for(quiet, "local chat", is_it).await {
            Ok(_) => Err(fail(format!("C, {d:.0} m away (radius {r}), heard the local chat"))),
            Err(ClientError::Timeout(_)) => {
                Ok(format!("A and B heard it; C, {d:.0} m away, did not within {}", secs(quiet)))
            }
            Err(e) => Err(fail(format!("C: {e}"))),
        }
    }

    async fn object_create(&mut self) -> Check {
        let t = self.t();
        let (a_id, _, _) = self.ids();
        let role = self.a()?.welcome().role;
        if role < Role::Builder as i32 {
            return Err(fail(format!(
                "A has role {}; the object checks need builder",
                role_name(role)
            )));
        }
        let user = self.a()?.user_id().to_string();
        let pos = self.a_pos + V3::new(2.0, 0.5, 2.0);
        let spec = ObjectSpec {
            name: format!("smoke-{}", self.tag),
            shape: Shape::Box as i32,
            position: Some(pos.into()),
            rotation: Some(Quat { x: 0.0, y: 0.0, z: 0.0, w: 1.0 }),
            scale: Some(V3::new(1.0, 1.0, 1.0).into()),
            color: Some(Color { r: 1.0, g: 0.0, b: 0.0, a: 1.0 }),
            material: Material::Matte as i32,
            solid: true,
            locked: false,
        };
        let m = req(
            self.a()?,
            "A",
            "ObjectCreate",
            CBody::ObjectCreate(ObjectCreate { spec: Some(spec.clone()) }),
            t,
        )
        .await?;
        let Some(SBody::ObjectCreated(c)) = m.body else {
            return Err(fail(format!(
                "A's reply is {}, expected ObjectCreated",
                body_name(&m.body)
            )));
        };
        let obj = c.object.clone().unwrap_or_default();
        let got = obj.spec.clone().unwrap_or_default();
        let mut issues = Vec::new();
        if obj.id == 0 {
            issues.push("object id is 0".to_string());
        }
        if obj.owner_id != user {
            issues.push(format!("owner_id '{}' is not A's user id '{user}'", obj.owner_id));
        }
        if c.by_avatar_id != a_id {
            issues.push(format!("by_avatar_id {} is not A ({a_id})", c.by_avatar_id));
        }
        if got.name != spec.name || got.shape != spec.shape {
            issues.push(format!("name/shape came back as '{}'/{}", got.name, got.shape));
        }
        if !close_v(got.position.into(), pos, 0.01) {
            issues.push(format!("position came back as {}", vec(&got.position)));
        }
        if !issues.is_empty() {
            return Err(fail(format!("A's ObjectCreated: {}", issues.join("; "))));
        }
        let id = obj.id;
        let what = format!("ObjectCreated for object {id}");
        let created = |m: &ServerMessage| matches!(&m.body, Some(SBody::ObjectCreated(c)) if c.object.as_ref().is_some_and(|o| o.id == id));
        let m = expect(self.b()?, "B", &what, t, created).await?;
        if m.reply_to != 0 {
            return Err(fail(format!("B's ObjectCreated has reply_to {}, expected 0", m.reply_to)));
        }
        if let Some(c) = self.c.as_mut() {
            let m = expect(c, "C", &what, t, created).await?;
            if m.reply_to != 0 {
                return Err(fail(format!(
                    "C's ObjectCreated has reply_to {}, expected 0",
                    m.reply_to
                )));
            }
        }
        self.object = Some(obj);
        Ok(format!("object {id}; reply_to on A's copy only"))
    }

    async fn object_update(&mut self) -> Check {
        let t = self.t();
        let obj = self.object.clone().ok_or_else(|| skip("needs object-create"))?;
        let old = obj.spec.clone().unwrap_or_default();
        let pos = V3::from(old.position) + V3::new(0.0, 0.0, 1.0);
        let blue = Color { r: 0.0, g: 0.0, b: 1.0, a: 1.0 };
        let upd = ObjectUpdate {
            id: obj.id,
            position: Some(pos.into()),
            color: Some(blue),
            ..Default::default()
        };
        let m = req(self.a()?, "A", "ObjectUpdate", CBody::ObjectUpdate(upd), t).await?;
        let Some(SBody::ObjectUpdated(u)) = m.body else {
            return Err(fail(format!(
                "A's reply is {}, expected ObjectUpdated",
                body_name(&m.body)
            )));
        };
        let check = |who: &str, o: &Option<ObjectInfo>| -> Result<ObjectInfo, Outcome> {
            let o = o.clone().unwrap_or_default();
            let s = o.spec.clone().unwrap_or_default();
            let c = s.color.unwrap_or_default();
            if !close_v(s.position.into(), pos, 0.01) {
                return Err(fail(format!(
                    "{who}'s ObjectUpdated has position {}",
                    vec(&s.position)
                )));
            }
            if (c.r, c.g, c.b) != (0.0, 0.0, 1.0) {
                return Err(fail(format!("{who}'s ObjectUpdated has colour {c:?}, expected blue")));
            }
            if s.name != old.name
                || s.shape != old.shape
                || s.scale != old.scale
                || s.solid != old.solid
            {
                return Err(fail(format!(
                    "{who}'s ObjectUpdated is not the full state: name/shape/scale/solid changed"
                )));
            }
            Ok(o)
        };
        let new = check("A", &u.object)?;
        let id = obj.id;
        let m = expect(self.b()?, "B", &format!("ObjectUpdated for object {id}"), t, |m| {
            matches!(&m.body, Some(SBody::ObjectUpdated(u)) if u.object.as_ref().is_some_and(|o| o.id == id))
        })
        .await?;
        if m.reply_to != 0 {
            return Err(fail(format!("B's ObjectUpdated has reply_to {}, expected 0", m.reply_to)));
        }
        let Some(SBody::ObjectUpdated(ub)) = m.body else { unreachable!() };
        check("B", &ub.object)?;
        self.object = Some(new);
        Ok(format!(
            "moved to {} and recoloured; both copies carry the full state",
            vec(&Some(pos.into()))
        ))
    }

    async fn not_found(&mut self) -> Check {
        let t = self.t();
        let bogus = (1u64 << 52) + rand::random::<u32>() as u64;
        let upd = ObjectUpdate { id: bogus, name: Some("nope".into()), ..Default::default() };
        match self.a()?.request(CBody::ObjectUpdate(upd), t).await {
            Err(ClientError::Server { code, message }) if code == ErrorCode::NotFound as i32 => {
                Ok(format!("NOT_FOUND: {message}"))
            }
            Err(ClientError::Server { code, message }) => {
                Err(fail(format!("expected NOT_FOUND, got {} {message}", error_code_name(code))))
            }
            Err(ClientError::Timeout(_)) => Err(fail(format!(
                "timed out after {} waiting for an Error with reply_to set",
                secs(t)
            ))),
            Err(e) => Err(fail(e.to_string())),
            Ok(m) => Err(fail(format!("expected Error NOT_FOUND, got {}", body_name(&m.body)))),
        }
    }

    async fn observe(&mut self) -> Check {
        let t = self.t();
        let (a_id, b_id, c_id) = self.ids();
        let max = self.limits.max_observe_radius;
        let want = self.local_radius() + 5.0;
        let radius = if max > 0.0 { want.min(max) } else { want };
        let a_pos = self.a_pos;
        let c_pos = self.c_pos;
        let c_far = self.c_far;
        let object = self.object.clone();
        let m = req(self.b()?, "B", "Observe", CBody::Observe(Observe { radius }), t).await?;
        let Some(SBody::Observation(o)) = m.body else {
            return Err(fail(format!("B's reply is {}, expected Observation", body_name(&m.body))));
        };
        let me = o.self_.clone().unwrap_or_default();
        if me.id != b_id {
            return Err(fail(format!("Observation.self is avatar {}, expected B ({b_id})", me.id)));
        }
        let tr = me.transform.unwrap_or_default();
        let (from, yaw) = (pos_of(&tr), tr.yaw);
        let check_one = |what: &str, at: V3, distance: f32, brg: f32| -> Result<(), Outcome> {
            let (d3, dh) = (from.dist(at), from.hdist(at));
            if (distance - d3).abs() > 0.5 && (distance - dh).abs() > 0.5 {
                return Err(fail(format!(
                    "{what} listed at distance {distance:.2} but is {d3:.2} m away"
                )));
            }
            if dh > 0.5 {
                let exp = bearing(from, yaw, at);
                if norm180(brg - exp).abs() > 5.0 {
                    return Err(fail(format!(
                        "{what} listed at bearing {brg:.1}°, expected {exp:.1}° (B at {} yaw {yaw:.0})",
                        vec(&Some(from.into()))
                    )));
                }
            }
            Ok(())
        };
        if o.avatars.iter().any(|s| s.avatar.as_ref().is_some_and(|x| x.id == b_id)) {
            return Err(fail("the observer is listed among the avatars"));
        }
        let seen_a =
            o.avatars.iter().find(|s| s.avatar.as_ref().is_some_and(|x| x.id == a_id)).ok_or_else(
                || fail(format!("A (avatar {a_id}) is not listed within {} m", o.radius)),
            )?;
        let a_at: V3 = seen_a
            .avatar
            .as_ref()
            .and_then(|x| x.transform.as_ref())
            .map(pos_of)
            .unwrap_or_default();
        if !close_v(a_at, a_pos, 1.0) {
            return Err(fail(format!(
                "A is listed at {}, expected near {}",
                vec(&Some(a_at.into())),
                vec(&Some(a_pos.into()))
            )));
        }
        check_one("A", a_at, seen_a.distance, seen_a.bearing)?;
        let mut detail = format!("A at {:.1} m {:+.0}°", seen_a.distance, seen_a.bearing);
        if let Some(obj) = &object {
            let seen = o
                .objects
                .iter()
                .find(|s| s.object.as_ref().is_some_and(|x| x.id == obj.id))
                .ok_or_else(|| fail(format!("object {} is not listed", obj.id)))?;
            let at: V3 = obj.spec.as_ref().and_then(|s| s.position).into();
            check_one("the object", at, seen.distance, seen.bearing)?;
            detail += &format!(", object at {:.1} m {:+.0}°", seen.distance, seen.bearing);
        }
        if o.avatars.windows(2).any(|w| w[0].distance > w[1].distance + 1e-3)
            || o.objects.windows(2).any(|w| w[0].distance > w[1].distance + 1e-3)
        {
            return Err(fail("the lists are not nearest first"));
        }
        if c_far && c_id != 0 && from.dist(c_pos) > o.radius + 1.0 {
            if o.avatars.iter().any(|s| s.avatar.as_ref().is_some_and(|x| x.id == c_id)) {
                return Err(fail(format!(
                    "C, {:.0} m away, is listed within radius {}",
                    from.dist(c_pos),
                    o.radius
                )));
            }
            detail += "; C (out of range) not listed";
        }
        if o.summary.trim().is_empty() {
            return Err(fail("the summary is empty"));
        }
        Ok(format!("{detail}; summary: {}", o.summary.lines().next().unwrap_or_default()))
    }

    async fn object_delete(&mut self) -> Check {
        let t = self.t();
        let (a_id, _, _) = self.ids();
        let obj = self.object.clone().ok_or_else(|| skip("needs object-create"))?;
        let id = obj.id;
        let m = req(self.a()?, "A", "ObjectDelete", CBody::ObjectDelete(ObjectDelete { id }), t)
            .await?;
        let Some(SBody::ObjectDeleted(d)) = m.body else {
            return Err(fail(format!(
                "A's reply is {}, expected ObjectDeleted",
                body_name(&m.body)
            )));
        };
        if d.id != id || d.by_avatar_id != a_id {
            return Err(fail(format!("A's ObjectDeleted says id {} by {}", d.id, d.by_avatar_id)));
        }
        let m = expect(
            self.b()?,
            "B",
            &format!("ObjectDeleted for object {id}"),
            t,
            |m| matches!(&m.body, Some(SBody::ObjectDeleted(d)) if d.id == id),
        )
        .await?;
        if m.reply_to != 0 {
            return Err(fail(format!("B's ObjectDeleted has reply_to {}, expected 0", m.reply_to)));
        }
        self.object = None;
        Ok(format!("object {id} deleted; reply_to on A's copy only"))
    }

    async fn ping(&mut self) -> Check {
        let t = self.t();
        let mut rtts = Vec::new();
        for who in ["A", "B"] {
            let c = if who == "A" { self.a()? } else { self.b()? };
            let sent = now_ms();
            let m = req(c, who, "Ping", CBody::Ping(Ping { client_time_ms: sent }), t).await?;
            let Some(SBody::Pong(p)) = m.body else {
                return Err(fail(format!(
                    "{who}'s reply is {}, expected Pong",
                    body_name(&m.body)
                )));
            };
            if p.client_time_ms != sent {
                return Err(fail(format!(
                    "{who}'s Pong echoes {} instead of {sent}",
                    p.client_time_ms
                )));
            }
            rtts.push(format!("{who} {} ms", now_ms() - sent));
        }
        Ok(rtts.join(", "))
    }

    async fn bad_request(&mut self) -> Check {
        let t = self.t();
        let b = self.b()?;
        b.send_frame(Frame::Text("{\"seq\": 4242, \"chat\": {\"text\": ".into()))
            .map_err(|e| fail(e.to_string()))?;
        let m = expect(
            b,
            "B",
            "Error BAD_REQUEST for a malformed JSON frame",
            t,
            |m| matches!(&m.body, Some(SBody::Error(e)) if e.code == ErrorCode::BadRequest as i32),
        )
        .await?;
        let Some(SBody::Error(e)) = m.body else { unreachable!() };
        let sent = now_ms();
        req(b, "B", "a Ping after the bad frame", CBody::Ping(Ping { client_time_ms: sent }), t)
            .await
            .map_err(|o| match o {
                Outcome::Fail(m) => fail(format!("the session did not survive: {m}")),
                o => o,
            })?;
        Ok(format!(
            "BAD_REQUEST (reply_to {}): {}; session still answers Ping",
            m.reply_to, e.message
        ))
    }

    async fn admin(&mut self) -> Check {
        let Some(token) = self.o.owner_token.clone() else {
            return Err(skip("no --owner-token-file"));
        };
        let (_, b_id, _) = self.ids();
        let b_user = self.b()?.user_id().to_string();
        let b_role = self.b()?.welcome().role;
        let mut oo = options(self.o, "owner", &self.tag, Encoding::Binary, ClientKind::Agent);
        oo.token = token;
        oo.name = String::new();
        let mut owner = Client::connect(oo)
            .await
            .map_err(|e| fail(format!("the owner could not join: {e}")))?;
        let r = self.admin_with(&mut owner, b_id, &b_user, b_role).await;
        owner.close().await;
        r
    }

    async fn admin_with(
        &mut self,
        owner: &mut Client,
        b_id: u64,
        b_user: &str,
        b_role: i32,
    ) -> Check {
        let t = self.t();
        if owner.welcome().role != Role::Owner as i32 {
            return Err(fail(format!(
                "the owner token joined with role {}",
                role_name(owner.welcome().role)
            )));
        }
        let set_role = |role: i32| {
            CBody::Admin(Admin {
                action: Some(admin::Action::SetRole(admin::SetRole {
                    user_id: b_user.to_string(),
                    role,
                })),
            })
        };
        admin_ack(owner, "Admin.SetRole", set_role(Role::Visitor as i32), t).await?;
        let role_is = |role: i32| {
            move |m: &ServerMessage| {
                matches!(&m.body, Some(SBody::AvatarUpdated(u))
                    if u.avatar.as_ref().is_some_and(|x| x.id == b_id && x.role == role))
            }
        };
        expect(
            self.b()?,
            "B",
            "AvatarUpdated with B's role visitor",
            t,
            role_is(Role::Visitor as i32),
        )
        .await?;
        expect(
            self.a()?,
            "A",
            "AvatarUpdated with B's role visitor",
            t,
            role_is(Role::Visitor as i32),
        )
        .await?;
        admin_ack(owner, "Admin.SetRole (restore)", set_role(b_role), t).await?;
        expect(self.b()?, "B", "AvatarUpdated restoring B's role", t, role_is(b_role)).await?;

        let mut k =
            Client::connect(options(self.o, "k", &self.tag, Encoding::Json, ClientKind::Agent))
                .await
                .map_err(|e| fail(format!("K (to be kicked) could not join: {e}")))?;
        let (k_id, k_user) = (k.avatar_id(), k.user_id().to_string());
        let kick = CBody::Admin(Admin {
            action: Some(admin::Action::Kick(admin::Kick {
                user_id: k_user,
                reason: "smoke test".into(),
            })),
        });
        admin_ack(owner, "Admin.Kick", kick, t).await?;
        let m =
            expect(&mut k, "K", "Kicked", t, |m| matches!(m.body, Some(SBody::Kicked(_)))).await?;
        let Some(SBody::Kicked(kicked)) = m.body else { unreachable!() };
        let closed = tokio::time::timeout(t, async {
            loop {
                if let Err(e) = k.recv().await {
                    break e;
                }
            }
        })
        .await;
        if closed.is_err() {
            return Err(fail(format!("K's socket stayed open {} after Kicked", secs(t))));
        }
        expect(
            self.a()?,
            "A",
            &format!("AvatarLeft for K (avatar {k_id})"),
            t,
            |m| matches!(&m.body, Some(SBody::AvatarLeft(l)) if l.avatar_id == k_id),
        )
        .await?;
        Ok(format!(
            "set B to visitor and back to {}; kicked K ('{}'), it was disconnected and left",
            role_name(b_role),
            kicked.reason
        ))
    }

    async fn leave(&mut self) -> Check {
        let t = self.t();
        let (_, b_id, c_id) = self.ids();
        let left = |id: u64| move |m: &ServerMessage| matches!(&m.body, Some(SBody::AvatarLeft(l)) if l.avatar_id == id);
        let mut done = Vec::new();
        if let Some(c) = self.c.take() {
            c.close().await;
            let what = format!("AvatarLeft for C (avatar {c_id})");
            expect(self.a()?, "A", &what, t, left(c_id)).await?;
            expect(self.b()?, "B", &what, t, left(c_id)).await?;
            done.push("C");
        }
        if let Some(b) = self.b.take() {
            b.close().await;
            expect(self.a()?, "A", &format!("AvatarLeft for B (avatar {b_id})"), t, left(b_id))
                .await?;
            done.push("B");
        }
        Ok(format!("{} left cleanly and the others saw AvatarLeft", done.join(" and ")))
    }
}
