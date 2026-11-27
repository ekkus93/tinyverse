//! The bridge's local picture of the world, kept up to date from the snapshot
//! and live events, plus the rolling log of chat and notable events that the
//! assistant reads between turns.

use std::collections::{HashMap, VecDeque};

use tv_proto::v1::{
    AvatarInfo, AvatarTransform, ChatEntry, ClientKind, Limits, ObjectInfo, Role, ServerMessage,
    Vec3, Welcome, WorldInfo, server_message::Body,
};

use crate::format::{self, num, vec3};

/// Chat lines kept in the rolling buffer.
pub const CHAT_CAPACITY: usize = 300;
/// Notable events kept in the rolling buffer.
pub const EVENT_CAPACITY: usize = 200;
/// Objects kept in the local model. The default world limit is 10,000.
pub const OBJECT_CAPACITY: usize = 20_000;

/// Everything known about the current session with the world.
#[derive(Debug)]
pub struct Session {
    pub user_id: String,
    pub avatar_id: u64,
    pub role: i32,
    pub world: WorldInfo,
    pub limits: Limits,
    pub server_version: String,
    pub tick_rate_hz: u32,
    /// The own avatar, transform included.
    pub me: AvatarInfo,
    /// Counts updates of the own transform, so waiters can tell that the
    /// server has moved the avatar since they started.
    pub me_updates: u64,
    /// Other avatars present.
    pub avatars: HashMap<u64, AvatarInfo>,
    pub objects: HashMap<u64, ObjectInfo>,
    /// Objects dropped because the model was full.
    pub objects_dropped: bool,
    pub snapshot_done: bool,
    /// The ChatHistory that follows the snapshot on join has arrived.
    pub history_done: bool,
}

impl Session {
    pub fn from_welcome(w: Welcome) -> Self {
        let mut me = w.avatar.unwrap_or_default();
        if me.id == 0 {
            me.id = w.avatar_id;
        }
        if me.user_id.is_empty() {
            me.user_id = w.user_id.clone();
        }
        me.role = w.role;
        Session {
            user_id: w.user_id,
            avatar_id: w.avatar_id,
            role: w.role,
            world: w.world.unwrap_or_default(),
            limits: w.limits.unwrap_or_default(),
            server_version: w.server_version,
            tick_rate_hz: w.tick_rate_hz,
            me,
            me_updates: 0,
            avatars: HashMap::new(),
            objects: HashMap::new(),
            objects_dropped: false,
            snapshot_done: false,
            history_done: false,
        }
    }

    /// The join sequence (snapshot, then chat history) is complete.
    pub fn is_ready(&self) -> bool {
        self.snapshot_done && self.history_done
    }

    pub fn my_transform(&self) -> AvatarTransform {
        self.me.transform.clone().unwrap_or_default()
    }

    pub fn my_position(&self) -> Vec3 {
        self.my_transform().position.unwrap_or_default()
    }

    /// Display name of a user id, if any avatar present belongs to it.
    pub fn name_of_user(&self, user_id: &str) -> Option<String> {
        if user_id == self.user_id {
            return Some("you".into());
        }
        self.avatars.values().find(|a| a.user_id == user_id).map(|a| a.name.clone())
    }

    fn name_of_avatar(&self, id: u64) -> String {
        if id == self.avatar_id {
            return "you".into();
        }
        match self.avatars.get(&id) {
            Some(a) => a.name.clone(),
            None if id == 0 => "the server".into(),
            None => format!("avatar {id}"),
        }
    }

    fn insert_object(&mut self, obj: ObjectInfo) {
        if self.objects.len() >= OBJECT_CAPACITY && !self.objects.contains_key(&obj.id) {
            self.objects_dropped = true;
            return;
        }
        self.objects.insert(obj.id, obj);
    }

    /// Applies one server message to the model and returns notable events
    /// (text) and chat lines for the log.
    pub fn apply(&mut self, msg: &ServerMessage) -> Vec<Note> {
        let mut notes = Vec::new();
        let Some(body) = &msg.body else { return notes };
        match body {
            Body::Snapshot(s) => {
                for a in &s.avatars {
                    if a.id != self.avatar_id {
                        self.avatars.insert(a.id, a.clone());
                    }
                }
                for o in &s.objects {
                    self.insert_object(o.clone());
                }
                if s.last {
                    self.snapshot_done = true;
                }
            }
            Body::AvatarJoined(j) => {
                if let Some(a) = &j.avatar
                    && a.id != self.avatar_id
                {
                    notes.push(Note::Event(format!(
                        "{} joined ({}, {})",
                        a.name,
                        format::kind_name(a.kind),
                        format::role_name(a.role)
                    )));
                    self.avatars.insert(a.id, a.clone());
                }
            }
            Body::AvatarLeft(l) => {
                if let Some(a) = self.avatars.remove(&l.avatar_id) {
                    notes.push(Note::Event(format!("{} left", a.name)));
                }
            }
            Body::AvatarStates(states) => {
                for e in &states.states {
                    if e.avatar_id == self.avatar_id {
                        self.me.transform = e.transform.clone();
                        self.me_updates += 1;
                    } else if let Some(a) = self.avatars.get_mut(&e.avatar_id) {
                        a.transform = e.transform.clone();
                    }
                }
            }
            Body::AvatarCorrection(c) => {
                self.me.transform = c.transform.clone();
                self.me_updates += 1;
                notes.push(Note::Event(format!(
                    "the server moved you to {}{}",
                    vec3(c.transform.as_ref().and_then(|t| t.position.as_ref())),
                    if c.reason.is_empty() { String::new() } else { format!(": {}", c.reason) }
                )));
            }
            Body::AvatarUpdated(u) => {
                if let Some(a) = &u.avatar {
                    if a.id == self.avatar_id {
                        let transform = self.me.transform.clone();
                        self.me = a.clone();
                        if self.me.transform.is_none() {
                            self.me.transform = transform;
                        }
                        if a.role != self.role {
                            notes.push(Note::Event(format!(
                                "your role is now {}",
                                format::role_name(a.role)
                            )));
                            self.role = a.role;
                        }
                    } else {
                        let transform =
                            self.avatars.get(&a.id).and_then(|old| old.transform.clone());
                        let mut a = a.clone();
                        if a.transform.is_none() {
                            a.transform = transform;
                        }
                        self.avatars.insert(a.id, a);
                    }
                }
            }
            Body::ChatMessage(m) => {
                if let Some(e) = &m.entry {
                    notes.push(Note::Chat(e.clone(), true));
                }
            }
            Body::ChatHistory(h) => {
                self.history_done = true;
                for e in &h.entries {
                    notes.push(Note::Chat(e.clone(), false));
                }
            }
            Body::ObjectCreated(c) => {
                if let Some(o) = &c.object {
                    if c.by_avatar_id != self.avatar_id {
                        notes.push(Note::Event(format!(
                            "{} created object {} ({}{}) at {}",
                            self.name_of_avatar(c.by_avatar_id),
                            o.id,
                            format::shape_name(o.spec.as_ref().map_or(0, |s| s.shape)),
                            o.spec
                                .as_ref()
                                .filter(|s| !s.name.is_empty())
                                .map_or(String::new(), |s| format!(" {:?}", s.name)),
                            vec3(o.spec.as_ref().and_then(|s| s.position.as_ref()))
                        )));
                    }
                    self.insert_object(o.clone());
                }
            }
            Body::ObjectUpdated(u) => {
                if let Some(o) = &u.object {
                    if u.by_avatar_id != self.avatar_id {
                        notes.push(Note::ObjectEvent(
                            o.id,
                            u.by_avatar_id,
                            format!(
                                "{} changed object {}{}",
                                self.name_of_avatar(u.by_avatar_id),
                                o.id,
                                o.spec
                                    .as_ref()
                                    .filter(|s| !s.name.is_empty())
                                    .map_or(String::new(), |s| format!(" {:?}", s.name)),
                            ),
                        ));
                    }
                    self.insert_object(o.clone());
                }
            }
            Body::ObjectDeleted(d) => {
                let old = self.objects.remove(&d.id);
                if d.by_avatar_id != self.avatar_id {
                    let label = old
                        .and_then(|o| o.spec)
                        .filter(|s| !s.name.is_empty())
                        .map_or(String::new(), |s| format!(" {:?}", s.name));
                    notes.push(Note::Event(format!(
                        "{} deleted object {}{label}",
                        self.name_of_avatar(d.by_avatar_id),
                        d.id
                    )));
                }
            }
            Body::Kicked(k) => {
                notes.push(Note::Event(format!("you were kicked: {}", k.reason)));
            }
            Body::Error(e) if msg.reply_to == 0 => {
                notes.push(Note::Event(format!(
                    "the server reported {}: {}",
                    format::error_code_name(e.code),
                    e.message
                )));
            }
            _ => {}
        }
        notes
    }

    /// Distance from the own avatar to a point.
    pub fn distance_to(&self, p: &Vec3) -> f32 {
        format::distance(&self.my_position(), p)
    }

    pub fn is_agent(kind: i32) -> bool {
        kind == ClientKind::Agent as i32
    }

    pub fn can_build(&self) -> bool {
        self.role >= Role::Builder as i32
    }

    pub fn describe_self(&self) -> String {
        let t = self.my_transform();
        format!(
            "You are {} (avatar {}, role {}) at {}, facing {}°{}.",
            self.me.name,
            self.avatar_id,
            format::role_name(self.role),
            vec3(t.position.as_ref()),
            num(t.yaw),
            if t.velocity.map(|v| v.x.abs() + v.y.abs() + v.z.abs() > 0.01).unwrap_or(false) {
                format!(", moving ({})", format::mode_name(t.move_mode))
            } else {
                String::new()
            }
        )
    }
}

/// Something worth remembering produced by applying a server message.
#[derive(Debug)]
pub enum Note {
    /// A chat line; the flag is true for live chat, false for history.
    Chat(ChatEntry, bool),
    Event(String),
    /// An update of an object by someone else: object id, actor avatar id,
    /// text. A run of these from one actor collapses into one log line.
    ObjectEvent(u64, u64, String),
}

#[derive(Clone, Debug)]
pub struct ChatLine {
    pub n: u64,
    pub entry: ChatEntry,
    /// Said by this bridge's own user.
    pub own: bool,
}

#[derive(Clone, Debug)]
pub struct EventLine {
    pub n: u64,
    pub ts_ms: i64,
    pub text: String,
    /// For coalescing a stream of updates to one object by one avatar.
    pub object: Option<(u64, u64)>,
}

/// One rendered line of the log, chat or event.
pub enum LogLine<'a> {
    Chat(&'a ChatLine),
    Event(&'a EventLine),
}

impl LogLine<'_> {
    pub fn n(&self) -> u64 {
        match self {
            LogLine::Chat(c) => c.n,
            LogLine::Event(e) => e.n,
        }
    }

    pub fn render(&self) -> String {
        match self {
            LogLine::Chat(c) => {
                let e = &c.entry;
                let who = if c.own {
                    format!("{} (you)", e.sender_name)
                } else if Session::is_agent(e.sender_kind) {
                    format!("{} (agent)", e.sender_name)
                } else {
                    e.sender_name.clone()
                };
                format!(
                    "[{}] [{}] {who}: {}",
                    format::clock(e.timestamp_ms),
                    format::channel_name(e.channel),
                    e.text
                )
            }
            LogLine::Event(e) => format!("[{}] * {}", format::clock(e.ts_ms), e.text),
        }
    }
}

/// The rolling buffer of chat and events, with the read cursor used by
/// `recent_chat` and `wait_for_chat`.
#[derive(Debug, Default)]
pub struct Log {
    next: u64,
    pub chat: VecDeque<ChatLine>,
    pub events: VecDeque<EventLine>,
    /// Everything with `n <= cursor` has been returned to the assistant.
    pub cursor: u64,
    /// Counts live chat lines from others, to wake `wait_for_chat`.
    pub heard: u64,
}

impl Log {
    fn bump(&mut self) -> u64 {
        self.next += 1;
        self.next
    }

    pub fn push_chat(&mut self, entry: ChatEntry, live: bool, own_user: &str) {
        let own = !own_user.is_empty() && entry.sender_user_id == own_user;
        if !live {
            // History is resent on every join; skip lines already held.
            let dup = self.chat.iter().any(|c| {
                c.entry.timestamp_ms == entry.timestamp_ms
                    && c.entry.sender_user_id == entry.sender_user_id
                    && c.entry.text == entry.text
            });
            if dup {
                return;
            }
        }
        let n = self.bump();
        if live && !own {
            self.heard += 1;
        }
        self.chat.push_back(ChatLine { n, entry, own });
        if self.chat.len() > CHAT_CAPACITY {
            self.chat.pop_front();
        }
    }

    pub fn push_event(&mut self, text: String, object: Option<(u64, u64)>) {
        let ts_ms = format::now_ms();
        // Someone dragging an object sends ~10 updates a second; keep one line.
        if let (Some(key), Some(last)) = (object, self.events.back_mut())
            && last.object == Some(key)
            && last.n > self.cursor
            && ts_ms - last.ts_ms < 5_000
        {
            last.ts_ms = ts_ms;
            last.text = text;
            return;
        }
        let n = self.bump();
        self.events.push_back(EventLine { n, ts_ms, text, object });
        if self.events.len() > EVENT_CAPACITY {
            self.events.pop_front();
        }
    }

    pub fn record(&mut self, notes: Vec<Note>, own_user: &str) {
        for note in notes {
            match note {
                Note::Chat(e, live) => self.push_chat(e, live, own_user),
                Note::Event(t) => self.push_event(t, None),
                Note::ObjectEvent(id, by, t) => self.push_event(t, Some((id, by))),
            }
        }
    }

    /// Lines with `n > after`, oldest first, chat and (optionally) events merged.
    pub fn lines_after(&self, after: u64, events: bool) -> Vec<LogLine<'_>> {
        let mut out: Vec<LogLine<'_>> =
            self.chat.iter().filter(|c| c.n > after).map(LogLine::Chat).collect();
        if events {
            out.extend(self.events.iter().filter(|e| e.n > after).map(LogLine::Event));
        }
        out.sort_by_key(|l| l.n());
        out
    }

    /// Whether there is unread chat from someone else.
    pub fn unread_from_others(&self) -> bool {
        self.chat.iter().any(|c| c.n > self.cursor && !c.own)
    }

    pub fn latest(&self) -> u64 {
        self.next
    }
}
