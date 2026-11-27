//! Turning server messages into readable lines, and tracking who and what is
//! present so the lines can use names instead of ids.

use std::collections::BTreeMap;

use tv_proto::v1::{
    AvatarInfo, AvatarTransform, ChatChannel, ChatEntry, ObjectInfo, Observation, ServerMessage,
    Vec3, server_message::Body as SBody,
};

use crate::{
    client::{Client, error_code_name},
    parse::{color_hex, kind_name, material_name, mode_name, role_name, shape_name},
};

/// Formats a vector as `(1.00, 0.00, -2.50)`.
pub fn vec(v: &Option<Vec3>) -> String {
    let v = v.unwrap_or_default();
    format!("({:.2}, {:.2}, {:.2})", v.x, v.y, v.z)
}

/// The avatars and objects this client knows about.
#[derive(Clone, Debug, Default)]
pub struct Roster {
    pub self_id: u64,
    pub avatars: BTreeMap<u64, AvatarInfo>,
    pub objects: BTreeMap<u64, ObjectInfo>,
}

impl Roster {
    /// Starts from the join-time state of a client.
    pub fn from_client(client: &Client) -> Self {
        let mut r = Roster { self_id: client.avatar_id(), ..Default::default() };
        if let Some(me) = &client.welcome().avatar {
            r.avatars.insert(client.avatar_id(), me.clone());
        }
        for a in &client.snapshot().avatars {
            r.avatars.insert(a.id, a.clone());
        }
        for o in &client.snapshot().objects {
            r.objects.insert(o.id, o.clone());
        }
        r
    }

    /// The display name of an avatar, or `avatar <id>` when unknown.
    pub fn name(&self, id: u64) -> String {
        match self.avatars.get(&id) {
            Some(a) if !a.name.is_empty() => a.name.clone(),
            _ => format!("avatar {id}"),
        }
    }

    /// Updates the roster from a server message.
    pub fn apply(&mut self, msg: &ServerMessage) {
        match &msg.body {
            Some(SBody::Snapshot(s)) => {
                for a in &s.avatars {
                    self.avatars.insert(a.id, a.clone());
                }
                for o in &s.objects {
                    self.objects.insert(o.id, o.clone());
                }
            }
            Some(SBody::AvatarJoined(j)) => {
                if let Some(a) = &j.avatar {
                    self.avatars.insert(a.id, a.clone());
                }
            }
            Some(SBody::AvatarUpdated(u)) => {
                if let Some(a) = &u.avatar {
                    self.avatars.insert(a.id, a.clone());
                }
            }
            // AvatarLeft: the name is kept for the "left" line and removed by
            // `forget_left` after printing.
            Some(SBody::AvatarStates(s)) => {
                for e in &s.states {
                    if let Some(a) = self.avatars.get_mut(&e.avatar_id) {
                        a.transform = e.transform.clone();
                    }
                }
            }
            Some(SBody::AvatarCorrection(c)) => {
                let id = self.self_id;
                if let Some(a) = self.avatars.get_mut(&id) {
                    a.transform = c.transform.clone();
                }
            }
            Some(SBody::ObjectCreated(c)) => {
                if let Some(o) = &c.object {
                    self.objects.insert(o.id, o.clone());
                }
            }
            Some(SBody::ObjectUpdated(u)) => {
                if let Some(o) = &u.object {
                    self.objects.insert(o.id, o.clone());
                }
            }
            Some(SBody::ObjectDeleted(d)) => {
                self.objects.remove(&d.id);
            }
            _ => {}
        }
    }

    /// Removes avatars that a message says have left. Call after printing.
    pub fn forget_left(&mut self, msg: &ServerMessage) {
        if let Some(SBody::AvatarLeft(l)) = &msg.body {
            self.avatars.remove(&l.avatar_id);
        }
    }

    /// One line per present avatar, for `/who`.
    pub fn who(&self) -> Vec<String> {
        self.avatars
            .values()
            .map(|a| {
                let me = if a.id == self.self_id { " (you)" } else { "" };
                format!("{}{me}", avatar_line(a))
            })
            .collect()
    }
}

/// `Ada (avatar 3, human, builder) at (1.00, 0.00, -2.50)`.
pub fn avatar_line(a: &AvatarInfo) -> String {
    let pos = a.transform.as_ref().map(|t| vec(&t.position)).unwrap_or_else(|| "?".into());
    format!(
        "{} (avatar {}, {}, {}, user {}) at {pos}",
        a.name,
        a.id,
        kind_name(a.kind),
        role_name(a.role),
        a.user_id
    )
}

/// `object 7 box "door" at (..) rot (..) scale (..) #ff0000 glossy solid locked owner u`.
pub fn object_line(o: &ObjectInfo) -> String {
    let s = o.spec.clone().unwrap_or_default();
    let mut line = format!("object {} {}", o.id, shape_name(s.shape));
    if !s.name.is_empty() {
        line += &format!(" \"{}\"", s.name);
    }
    let r = s.rotation.unwrap_or_default();
    line += &format!(
        " at {} rot ({:.3}, {:.3}, {:.3}, {:.3}) scale {} {} {}",
        vec(&s.position),
        r.x,
        r.y,
        r.z,
        r.w,
        vec(&s.scale),
        s.color.map(|c| color_hex(&c)).unwrap_or_else(|| "nocolor".into()),
        material_name(s.material),
    );
    if s.solid {
        line += " solid";
    }
    if s.locked {
        line += " locked";
    }
    line += &format!(" owner {}", o.owner_id);
    line
}

fn transform_line(t: &Option<AvatarTransform>) -> String {
    let t = t.clone().unwrap_or_default();
    let mut s = format!("{} yaw {:.0} {}", vec(&t.position), t.yaw, mode_name(t.move_mode));
    if !t.animation.is_empty() {
        s += &format!(" \"{}\"", t.animation);
    }
    s
}

/// `[world] Ada (agent): hello`.
pub fn chat_line(e: &ChatEntry) -> String {
    let channel = match ChatChannel::try_from(e.channel) {
        Ok(ChatChannel::Local) => "local",
        Ok(ChatChannel::World) => "world",
        _ => "?",
    };
    format!("[{channel}] {} ({}): {}", e.sender_name, kind_name(e.sender_kind), e.text)
}

/// The lines for an `Observation`: summary first, then the lists.
pub fn observation_lines(o: &Observation) -> Vec<String> {
    let mut lines = vec![format!(
        "observation within {:.1} m{}: {}",
        o.radius,
        if o.truncated { " (truncated)" } else { "" },
        o.summary
    )];
    for a in &o.avatars {
        if let Some(info) = &a.avatar {
            lines.push(format!(
                "  avatar {:6.1} m {:+5.0}°  {}",
                a.distance,
                a.bearing,
                avatar_line(info)
            ));
        }
    }
    for s in &o.objects {
        if let Some(obj) = &s.object {
            lines.push(format!(
                "  object {:6.1} m {:+5.0}°  {}",
                s.distance,
                s.bearing,
                object_line(obj)
            ));
        }
    }
    lines
}

/// The readable form of a server message. Most messages are one line; chat
/// history and observations are one line per entry.
pub fn describe(msg: &ServerMessage, roster: &Roster) -> Vec<String> {
    let reply =
        if msg.reply_to != 0 { format!(" [reply to {}]", msg.reply_to) } else { String::new() };
    let one = |s: String| vec![format!("{s}{reply}")];
    let Some(body) = &msg.body else { return one("(empty message)".into()) };
    match body {
        SBody::Welcome(w) => one(format!(
            "welcome: avatar {} user {} role {}",
            w.avatar_id,
            w.user_id,
            role_name(w.role)
        )),
        SBody::Snapshot(s) => one(format!(
            "snapshot: {} avatars, {} objects{}",
            s.avatars.len(),
            s.objects.len(),
            if s.last { " (last)" } else { "" }
        )),
        SBody::AvatarJoined(j) => match &j.avatar {
            Some(a) => one(format!("+ joined: {}", avatar_line(a))),
            None => one("+ joined: (no avatar info)".into()),
        },
        SBody::AvatarLeft(l) => {
            one(format!("- left: {} (avatar {})", roster.name(l.avatar_id), l.avatar_id))
        }
        SBody::AvatarStates(s) => {
            let parts: Vec<String> = s
                .states
                .iter()
                .map(|e| format!("{} {}", roster.name(e.avatar_id), transform_line(&e.transform)))
                .collect();
            one(format!("states: {}", parts.join("; ")))
        }
        SBody::AvatarCorrection(c) => {
            one(format!("! correction: {} ({})", transform_line(&c.transform), c.reason))
        }
        SBody::AvatarUpdated(u) => match &u.avatar {
            Some(a) => one(format!("~ updated: {}", avatar_line(a))),
            None => one("~ updated: (no avatar info)".into()),
        },
        SBody::ChatMessage(c) => match &c.entry {
            Some(e) => one(chat_line(e)),
            None => one("(empty chat)".into()),
        },
        SBody::ChatHistory(h) => {
            let mut lines = vec![format!("chat history: {} messages{reply}", h.entries.len())];
            lines.extend(h.entries.iter().map(|e| format!("  {}", chat_line(e))));
            lines
        }
        SBody::ObjectCreated(c) => one(format!(
            "+ {} (by {})",
            c.object.as_ref().map(object_line).unwrap_or_default(),
            roster.name(c.by_avatar_id)
        )),
        SBody::ObjectUpdated(u) => one(format!(
            "~ {} (by {})",
            u.object.as_ref().map(object_line).unwrap_or_default(),
            roster.name(u.by_avatar_id)
        )),
        SBody::ObjectDeleted(d) => {
            one(format!("- object {} deleted (by {})", d.id, roster.name(d.by_avatar_id)))
        }
        SBody::Observation(o) => {
            let mut lines = observation_lines(o);
            lines[0] += &reply;
            lines
        }
        SBody::Error(e) => one(format!("error {}: {}", error_code_name(e.code), e.message)),
        SBody::Kicked(k) => one(format!("kicked: {}", k.reason)),
        SBody::Pong(p) => {
            let rtt = crate::client::now_ms() - p.client_time_ms;
            one(format!("pong: round trip {rtt} ms"))
        }
        SBody::Ack(_) => one("ok".into()),
    }
}
