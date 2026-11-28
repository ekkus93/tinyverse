//! A compact in-process fake of the Tinyverse server: enough of the protocol
//! for `tv-cli smoke` to pass, with switches to make it misbehave.

use std::{
    collections::BTreeMap,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

use futures_util::{SinkExt, StreamExt};
use tokio::{
    net::{TcpListener, TcpStream},
    sync::mpsc,
};
use tokio_tungstenite::{accept_async, tungstenite::Message};
use tv_proto::{
    Encoding, Frame, PROTOCOL_VERSION, decode, encode,
    v1::{
        Ack, AvatarCorrection, AvatarInfo, AvatarJoined, AvatarLeft, AvatarStates, AvatarTransform,
        AvatarUpdated, ChatChannel, ChatEntry, ChatHistory, ChatMessage, ClientMessage, Error,
        ErrorCode, Kicked, Limits, MoveMode, ObjectCreated, ObjectDeleted, ObjectInfo,
        ObjectUpdated, Observation, Pong, Role, ServerMessage, Snapshot, Vec3, Welcome, WorldInfo,
        admin::Action, avatar_states::Entry, client_message::Body as C, observation,
        server_message::Body as S,
    },
};

/// Ways to make the fake world break the protocol.
#[derive(Clone, Copy, Debug, Default)]
pub struct Quirks {
    /// Send each client its own avatar in AvatarStates.
    pub leak_own_states: bool,
    /// Put the actor's reply_to on everyone's copy of an event.
    pub reply_to_everyone: bool,
}

pub const OWNER_TOKEN: &str = "owner-token";
const LOCAL_RADIUS: f32 = 5.0;
const MAX_OBSERVE: f32 = 50.0;

enum Out {
    Msg(Box<ServerMessage>),
    Close,
}

struct Av {
    info: AvatarInfo,
    tx: mpsc::UnboundedSender<Out>,
    last_state: Instant,
    moveto: Option<(Vec3, f32)>,
    dirty: bool,
    server_moved: bool,
}

struct World {
    next_id: u64,
    avatars: BTreeMap<u64, Av>,
    objects: BTreeMap<u64, ObjectInfo>,
    history: Vec<ChatEntry>,
    quirks: Quirks,
}

fn now_ms() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis() as i64
}

fn d3(a: Vec3, b: Vec3) -> f32 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt()
}

fn pos(t: &Option<AvatarTransform>) -> Vec3 {
    t.as_ref().and_then(|t| t.position).unwrap_or_default()
}

fn speed(mode: i32) -> f32 {
    match MoveMode::try_from(mode) {
        Ok(MoveMode::Run) => 8.0,
        Ok(MoveMode::Fly) => 16.0,
        _ => 4.0,
    }
}

/// Bearing in the observer's own frame: forward (sin y, cos y), right (cos y, -sin y).
fn bearing_of(from: Vec3, yaw: f32, to: Vec3) -> f32 {
    let (dx, dz) = (to.x - from.x, to.z - from.z);
    let (s, c) = yaw.to_radians().sin_cos();
    let fwd = dx * s + dz * c;
    let right = dx * c - dz * s;
    right.atan2(fwd).to_degrees()
}

fn err(code: ErrorCode, message: &str) -> S {
    S::Error(Error { code: code as i32, message: message.into() })
}

impl World {
    fn send(&self, to: u64, reply_to: u32, body: S) {
        if let Some(a) = self.avatars.get(&to) {
            let _ = a.tx.send(Out::Msg(Box::new(ServerMessage { reply_to, body: Some(body) })));
        }
    }

    /// Sends `body` to every avatar `filter` accepts; the actor's copy
    /// carries `seq`.
    fn broadcast(&self, actor: u64, seq: u32, body: S, filter: impl Fn(&Av) -> bool) {
        for (id, a) in &self.avatars {
            if !filter(a) {
                continue;
            }
            let reply_to = if *id == actor || self.quirks.reply_to_everyone { seq } else { 0 };
            let _ =
                a.tx.send(Out::Msg(Box::new(ServerMessage { reply_to, body: Some(body.clone()) })));
        }
    }

    fn on_message(&mut self, me: u64, frame: &Frame) {
        let msg: ClientMessage = match decode(frame) {
            Ok(m) => m,
            Err(e) => return self.send(me, 0, err(ErrorCode::BadRequest, &e.to_string())),
        };
        let seq = msg.seq;
        let Some(body) = msg.body else {
            return self.send(me, seq, err(ErrorCode::BadRequest, "no body"));
        };
        let role = self.avatars[&me].info.role;
        match body {
            C::Hello(_) => self.send(me, seq, err(ErrorCode::BadRequest, "already joined")),
            C::AvatarState(s) => {
                let t = s.transform.unwrap_or_default();
                let a = self.avatars.get_mut(&me).unwrap();
                let now = Instant::now();
                let dt = (now - a.last_state).as_secs_f32();
                a.last_state = now;
                let moved = d3(pos(&a.info.transform), t.position.unwrap_or_default());
                if moved > speed(t.move_mode) * 1.5 * dt + 0.5 {
                    let back = a.info.transform.clone();
                    self.send(
                        me,
                        0,
                        S::AvatarCorrection(AvatarCorrection {
                            transform: back,
                            reason: "too fast".into(),
                        }),
                    );
                } else {
                    a.info.transform = Some(t);
                    a.moveto = None;
                    a.dirty = true;
                }
            }
            C::MoveTo(m) => {
                let a = self.avatars.get_mut(&me).unwrap();
                let mut target = m.target.unwrap_or_default();
                if m.mode != MoveMode::Fly as i32 {
                    target.y = pos(&a.info.transform).y;
                }
                a.moveto = Some((target, speed(m.mode)));
            }
            C::Stop(_) => self.avatars.get_mut(&me).unwrap().moveto = None,
            C::Chat(c) => {
                if c.text.is_empty() {
                    return self.send(me, seq, err(ErrorCode::InvalidArgument, "empty"));
                }
                let a = &self.avatars[&me].info;
                let entry = ChatEntry {
                    sender_avatar_id: me,
                    sender_user_id: a.user_id.clone(),
                    sender_name: a.name.clone(),
                    sender_kind: a.kind,
                    channel: c.channel,
                    text: c.text,
                    timestamp_ms: now_ms(),
                };
                let at = pos(&a.transform);
                let body = S::ChatMessage(ChatMessage { entry: Some(entry.clone()) });
                if c.channel == ChatChannel::Local as i32 {
                    self.broadcast(me, seq, body, |o| {
                        d3(pos(&o.info.transform), at) <= LOCAL_RADIUS
                    });
                } else {
                    self.history.push(entry);
                    self.broadcast(me, seq, body, |_| true);
                }
            }
            C::ChatHistoryRequest(r) => {
                let mut entries: Vec<ChatEntry> =
                    self.history.iter().filter(|e| e.timestamp_ms > r.since_ms).cloned().collect();
                if r.limit > 0 && entries.len() > r.limit as usize {
                    entries.drain(..entries.len() - r.limit as usize);
                }
                self.send(me, seq, S::ChatHistory(ChatHistory { entries }));
            }
            C::ObjectCreate(c) => {
                if role < Role::Builder as i32 {
                    return self.send(me, seq, err(ErrorCode::PermissionDenied, "visitor"));
                }
                let Some(spec) = c.spec else {
                    return self.send(me, seq, err(ErrorCode::InvalidArgument, "no spec"));
                };
                self.next_id += 1;
                let id = self.next_id;
                let owner_id = self.avatars[&me].info.user_id.clone();
                let obj = ObjectInfo { id, owner_id, spec: Some(spec) };
                self.objects.insert(id, obj.clone());
                let body = S::ObjectCreated(ObjectCreated { object: Some(obj), by_avatar_id: me });
                self.broadcast(me, seq, body, |_| true);
            }
            C::ObjectUpdate(u) => {
                let Some(obj) = self.objects.get_mut(&u.id) else {
                    return self.send(me, seq, err(ErrorCode::NotFound, "no such object"));
                };
                let s = obj.spec.get_or_insert_default();
                if let Some(v) = u.name {
                    s.name = v;
                }
                if let Some(v) = u.shape {
                    s.shape = v;
                }
                if u.position.is_some() {
                    s.position = u.position;
                }
                if u.rotation.is_some() {
                    s.rotation = u.rotation;
                }
                if u.scale.is_some() {
                    s.scale = u.scale;
                }
                if u.color.is_some() {
                    s.color = u.color;
                }
                if let Some(v) = u.material {
                    s.material = v;
                }
                if let Some(v) = u.solid {
                    s.solid = v;
                }
                if let Some(v) = u.locked {
                    s.locked = v;
                }
                let body =
                    S::ObjectUpdated(ObjectUpdated { object: Some(obj.clone()), by_avatar_id: me });
                self.broadcast(me, seq, body, |_| true);
            }
            C::ObjectDelete(d) => {
                if self.objects.remove(&d.id).is_none() {
                    return self.send(me, seq, err(ErrorCode::NotFound, "no such object"));
                }
                let body = S::ObjectDeleted(ObjectDeleted { id: d.id, by_avatar_id: me });
                self.broadcast(me, seq, body, |_| true);
            }
            C::Observe(o) => {
                let radius = if o.radius <= 0.0 { 20.0 } else { o.radius.min(MAX_OBSERVE) };
                let me_info = self.avatars[&me].info.clone();
                let from = pos(&me_info.transform);
                let yaw = me_info.transform.as_ref().map(|t| t.yaw).unwrap_or(0.0);
                let mut avatars: Vec<observation::SeenAvatar> = self
                    .avatars
                    .values()
                    .filter(|a| a.info.id != me)
                    .map(|a| {
                        let p = pos(&a.info.transform);
                        observation::SeenAvatar {
                            avatar: Some(a.info.clone()),
                            distance: d3(from, p),
                            bearing: bearing_of(from, yaw, p),
                        }
                    })
                    .filter(|s| s.distance <= radius)
                    .collect();
                avatars.sort_by(|a, b| a.distance.total_cmp(&b.distance));
                let mut objects: Vec<observation::SeenObject> = self
                    .objects
                    .values()
                    .map(|o| {
                        let p = o.spec.as_ref().and_then(|s| s.position).unwrap_or_default();
                        observation::SeenObject {
                            object: Some(o.clone()),
                            distance: d3(from, p),
                            bearing: bearing_of(from, yaw, p),
                        }
                    })
                    .filter(|s| s.distance <= radius)
                    .collect();
                objects.sort_by(|a, b| a.distance.total_cmp(&b.distance));
                let summary =
                    format!("{} avatars and {} objects nearby.", avatars.len(), objects.len());
                let obs = Observation {
                    self_: Some(me_info),
                    avatars,
                    objects,
                    summary,
                    truncated: false,
                    radius,
                };
                self.send(me, seq, S::Observation(obs));
            }
            C::SetAppearance(s) => {
                let a = self.avatars.get_mut(&me).unwrap();
                a.info.appearance = s.appearance;
                let info = a.info.clone();
                self.broadcast(
                    me,
                    seq,
                    S::AvatarUpdated(AvatarUpdated { avatar: Some(info) }),
                    |_| true,
                );
            }
            C::Admin(a) => {
                if role != Role::Owner as i32 {
                    return self.send(me, seq, err(ErrorCode::PermissionDenied, "owners only"));
                }
                let find = |w: &World, user: &str| {
                    w.avatars.iter().find(|(_, a)| a.info.user_id == user).map(|(id, _)| *id)
                };
                match a.action {
                    Some(Action::SetRole(r)) => {
                        let Some(id) = find(self, &r.user_id) else {
                            return self.send(me, seq, err(ErrorCode::NotFound, "no such user"));
                        };
                        let t = self.avatars.get_mut(&id).unwrap();
                        t.info.role = r.role;
                        let info = t.info.clone();
                        self.broadcast(
                            0,
                            0,
                            S::AvatarUpdated(AvatarUpdated { avatar: Some(info) }),
                            |_| true,
                        );
                        self.send(me, seq, S::Ack(Ack {}));
                    }
                    Some(Action::Kick(k)) => {
                        let Some(id) = find(self, &k.user_id) else {
                            return self.send(me, seq, err(ErrorCode::NotFound, "no such user"));
                        };
                        self.send(id, 0, S::Kicked(Kicked { reason: k.reason }));
                        let _ = self.avatars[&id].tx.send(Out::Close);
                        self.send(me, seq, S::Ack(Ack {}));
                    }
                    _ => self.send(me, seq, S::Ack(Ack {})),
                }
            }
            C::Ping(p) => self.send(
                me,
                seq,
                S::Pong(Pong { client_time_ms: p.client_time_ms, server_time_ms: now_ms() }),
            ),
        }
    }

    fn tick(&mut self, dt: f32) {
        for a in self.avatars.values_mut() {
            let Some((target, speed)) = a.moveto else { continue };
            let t = a.info.transform.get_or_insert_default();
            let p = t.position.unwrap_or_default();
            let d = d3(p, target);
            let step = speed * dt;
            if d <= step {
                t.position = Some(target);
                t.velocity = Some(Vec3::default());
                t.animation = "idle".into();
                a.moveto = None;
            } else {
                let k = step / d;
                let dir = Vec3 {
                    x: (target.x - p.x) / d,
                    y: (target.y - p.y) / d,
                    z: (target.z - p.z) / d,
                };
                t.position = Some(Vec3 {
                    x: p.x + (target.x - p.x) * k,
                    y: p.y + (target.y - p.y) * k,
                    z: p.z + (target.z - p.z) * k,
                });
                t.velocity = Some(Vec3 { x: dir.x * speed, y: dir.y * speed, z: dir.z * speed });
                t.yaw = dir.x.atan2(dir.z).to_degrees();
                t.animation = "run".into();
            }
            a.dirty = true;
            a.server_moved = true;
        }
        let entries: Vec<(u64, bool, Entry)> = self
            .avatars
            .iter()
            .filter(|(_, a)| a.dirty)
            .map(|(id, a)| {
                (*id, a.server_moved, Entry { avatar_id: *id, transform: a.info.transform.clone() })
            })
            .collect();
        if !entries.is_empty() {
            for (id, a) in &self.avatars {
                let states: Vec<Entry> = entries
                    .iter()
                    .filter(|(e, moved, _)| e != id || *moved || self.quirks.leak_own_states)
                    .map(|(_, _, e)| e.clone())
                    .collect();
                if !states.is_empty() {
                    let body = S::AvatarStates(AvatarStates { server_time_ms: now_ms(), states });
                    let _ = a
                        .tx
                        .send(Out::Msg(Box::new(ServerMessage { reply_to: 0, body: Some(body) })));
                }
            }
        }
        for a in self.avatars.values_mut() {
            a.dirty = false;
            a.server_moved = false;
        }
    }
}

/// Starts the fake world on a random port and returns its URL.
pub async fn start(quirks: Quirks) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("ws://{}/ws", listener.local_addr().unwrap());
    let world = Arc::new(Mutex::new(World {
        next_id: 100,
        avatars: BTreeMap::new(),
        objects: BTreeMap::new(),
        history: Vec::new(),
        quirks,
    }));
    let w = world.clone();
    tokio::spawn(async move {
        let mut iv = tokio::time::interval(Duration::from_millis(50));
        loop {
            iv.tick().await;
            w.lock().unwrap().tick(0.05);
        }
    });
    tokio::spawn(async move {
        while let Ok((stream, _)) = listener.accept().await {
            tokio::spawn(connection(world.clone(), stream));
        }
    });
    url
}

fn to_ws(m: &ServerMessage, enc: Encoding) -> Message {
    match encode(m, enc) {
        Frame::Binary(b) => Message::Binary(b.into()),
        Frame::Text(t) => Message::Text(t.into()),
    }
}

async fn connection(world: Arc<Mutex<World>>, stream: TcpStream) {
    let Ok(ws) = accept_async(stream).await else { return };
    let (mut sink, mut src) = ws.split();
    let next_frame = async |src: &mut futures_util::stream::SplitStream<_>| -> Option<Frame> {
        loop {
            match src.next().await {
                Some(Ok(Message::Binary(b))) => return Some(Frame::Binary(b.to_vec())),
                Some(Ok(Message::Text(t))) => return Some(Frame::Text(t.as_str().to_owned())),
                Some(Ok(Message::Close(_))) | None | Some(Err(_)) => return None,
                Some(Ok(_)) => continue,
            }
        }
    };
    let Some(first) = next_frame(&mut src).await else { return };
    let enc = first.encoding();
    let hello = match decode::<ClientMessage>(&first) {
        Ok(ClientMessage { seq, body: Some(C::Hello(h)) }) => (seq, h),
        _ => {
            let m = ServerMessage {
                reply_to: 0,
                body: Some(err(ErrorCode::BadRequest, "Hello first")),
            };
            let _ = sink.send(to_ws(&m, enc)).await;
            let _ = sink.close().await;
            return;
        }
    };
    let (seq, h) = hello;
    if h.protocol_version != PROTOCOL_VERSION {
        let m = ServerMessage {
            reply_to: seq,
            body: Some(err(ErrorCode::UnsupportedVersion, "v1 only")),
        };
        let _ = sink.send(to_ws(&m, enc)).await;
        let _ = sink.close().await;
        return;
    }

    let (tx, mut rx) = mpsc::unbounded_channel::<Out>();
    tokio::spawn(async move {
        while let Some(o) = rx.recv().await {
            match o {
                Out::Msg(m) => {
                    if sink.send(to_ws(&m, enc)).await.is_err() {
                        return;
                    }
                }
                Out::Close => {
                    let _ = sink.close().await;
                    return;
                }
            }
        }
    });

    let me = {
        let mut w = world.lock().unwrap();
        w.next_id += 1;
        let id = w.next_id;
        let owner = h.token == OWNER_TOKEN;
        let info = AvatarInfo {
            id,
            user_id: if owner { "owner-user".into() } else { format!("user-{id}") },
            name: if h.name.is_empty() { format!("user{id}") } else { h.name.clone() },
            kind: h.kind,
            role: if owner { Role::Owner as i32 } else { Role::Builder as i32 },
            appearance: h.appearance,
            transform: Some(AvatarTransform {
                position: Some(Vec3::default()),
                yaw: 0.0,
                velocity: Some(Vec3::default()),
                move_mode: MoveMode::Walk as i32,
                animation: "idle".into(),
            }),
        };
        let welcome = Welcome {
            protocol_version: PROTOCOL_VERSION,
            user_id: info.user_id.clone(),
            avatar_id: id,
            role: info.role,
            session_token: format!("session-{id}"),
            world: Some(WorldInfo {
                name: "Fake".into(),
                description: String::new(),
                bounds_min: Some(Vec3 { x: -100.0, y: -10.0, z: -100.0 }),
                bounds_max: Some(Vec3 { x: 100.0, y: 100.0, z: 100.0 }),
                spawn_point: Some(Vec3::default()),
                ground_size: 200.0,
                sky: String::new(),
                time_of_day: 12.0,
                ground: String::new(),
            }),
            limits: Some(Limits {
                max_message_bytes: 65536,
                max_chat_chars: 500,
                max_name_chars: 64,
                max_objects: 1000,
                local_chat_radius: LOCAL_RADIUS,
                min_scale: 0.01,
                max_scale: 64.0,
                walk_speed: 4.0,
                run_speed: 8.0,
                fly_speed: 16.0,
                max_observe_radius: MAX_OBSERVE,
            }),
            tick_rate_hz: 20,
            server_time_ms: now_ms(),
            server_version: "fake".into(),
            avatar: Some(info.clone()),
        };
        let send = |reply_to: u32, body: S| {
            let _ = tx.send(Out::Msg(Box::new(ServerMessage { reply_to, body: Some(body) })));
        };
        send(seq, S::Welcome(welcome));
        // Two chunks: avatars, then objects.
        let others: Vec<AvatarInfo> = w.avatars.values().map(|a| a.info.clone()).collect();
        send(0, S::Snapshot(Snapshot { avatars: others, objects: vec![], last: false }));
        let objects: Vec<ObjectInfo> = w.objects.values().cloned().collect();
        send(0, S::Snapshot(Snapshot { avatars: vec![], objects, last: true }));
        send(0, S::ChatHistory(ChatHistory { entries: w.history.clone() }));
        w.broadcast(id, 0, S::AvatarJoined(AvatarJoined { avatar: Some(info.clone()) }), |_| true);
        w.avatars.insert(
            id,
            Av {
                info,
                tx: tx.clone(),
                last_state: Instant::now(),
                moveto: None,
                dirty: false,
                server_moved: false,
            },
        );
        id
    };

    while let Some(frame) = next_frame(&mut src).await {
        world.lock().unwrap().on_message(me, &frame);
    }
    let mut w = world.lock().unwrap();
    w.avatars.remove(&me);
    w.broadcast(0, 0, S::AvatarLeft(AvatarLeft { avatar_id: me }), |_| true);
    let _ = tx.send(Out::Close);
}
