//! Conversion between protocol messages (`tv_proto::v1`) and world types
//! (`tv_world`).
//!
//! Inbound rules: a sub-message the command cannot do without (a position, a
//! target, a spec, a transform) is `INVALID_ARGUMENT` naming the field. Other
//! missing sub-messages take a neutral value: an object's colour white, an
//! appearance colour all-zero ("not set; the client uses its default"),
//! rotation identity (also for an all-zero quaternion), scale one, velocity
//! zero. Unspecified
//! enums take their documented default (walk, local, matte, human); an
//! unspecified shape on create or role in `SetRole` is refused, and so is any
//! enum number the schema does not define.

use tv_proto::v1::{self, client_message::Body as C, server_message::Body as S};
use tv_world as w;
use tv_world::{Command, EventKind, WorldConfig, WorldError};

use crate::ratelimit::MessageType;

/// Largest inbound frame accepted, in bytes (SPEC 4.1).
pub const MAX_MESSAGE_BYTES: usize = 64 * 1024;
/// How often the world task ticks and sends `AvatarStates`.
pub const TICK_RATE_HZ: u32 = 20;
/// Most objects in one `Snapshot` chunk.
pub const SNAPSHOT_CHUNK: usize = 100;
/// The server's version string, sent in `Welcome` and `/info`.
pub const SERVER_VERSION: &str = env!("CARGO_PKG_VERSION");

/// A refused request: an error code and a message fit to show the sender.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Refusal {
    pub code: v1::ErrorCode,
    pub message: String,
}

impl Refusal {
    pub fn new(code: v1::ErrorCode, message: impl Into<String>) -> Self {
        Refusal { code, message: message.into() }
    }

    pub fn bad_request(message: impl Into<String>) -> Self {
        Refusal::new(v1::ErrorCode::BadRequest, message)
    }

    pub fn invalid(message: impl Into<String>) -> Self {
        Refusal::new(v1::ErrorCode::InvalidArgument, message)
    }

    pub fn internal(message: impl Into<String>) -> Self {
        Refusal::new(v1::ErrorCode::Internal, message)
    }

    fn missing(field: &str) -> Self {
        Refusal::invalid(format!("`{field}` is required"))
    }

    /// The `Error` body that carries this refusal.
    pub fn body(&self) -> S {
        S::Error(v1::Error { code: self.code as i32, message: self.message.clone() })
    }
}

impl std::fmt::Display for Refusal {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.code.as_str_name(), self.message)
    }
}

/// The protocol code for a world error.
pub fn error_code(error: &WorldError) -> v1::ErrorCode {
    match error {
        WorldError::NotJoined => v1::ErrorCode::BadRequest,
        WorldError::WorldFull => v1::ErrorCode::WorldFull,
        WorldError::PermissionDenied(_) => v1::ErrorCode::PermissionDenied,
        WorldError::NotFound(_) => v1::ErrorCode::NotFound,
        WorldError::InvalidArgument(_) => v1::ErrorCode::InvalidArgument,
        WorldError::OutOfBounds(_) => v1::ErrorCode::OutOfBounds,
        WorldError::LimitExceeded(_) => v1::ErrorCode::LimitExceeded,
    }
}

impl From<&WorldError> for Refusal {
    fn from(error: &WorldError) -> Self {
        Refusal::new(error_code(error), error.to_string())
    }
}

impl From<WorldError> for Refusal {
    fn from(error: WorldError) -> Self {
        Refusal::from(&error)
    }
}

// ---------------------------------------------------------------- inbound

/// The rate-limit class of a client message body; `None` for `Hello`.
pub fn message_type(body: &C) -> Option<MessageType> {
    Some(match body {
        C::Hello(_) => return None,
        C::AvatarState(_) => MessageType::AvatarState,
        C::MoveTo(_) => MessageType::MoveTo,
        C::Stop(_) => MessageType::Stop,
        C::Chat(_) => MessageType::Chat,
        C::ChatHistoryRequest(_) => MessageType::ChatHistoryRequest,
        C::ObjectCreate(_) => MessageType::ObjectCreate,
        C::ObjectUpdate(_) => MessageType::ObjectUpdate,
        C::ObjectDelete(_) => MessageType::ObjectDelete,
        C::Observe(_) => MessageType::Observe,
        C::SetAppearance(_) => MessageType::SetAppearance,
        C::Admin(_) => MessageType::Admin,
        C::Ping(_) => MessageType::Ping,
    })
}

pub fn vec3_from(v: &v1::Vec3) -> w::Vec3 {
    w::Vec3::new(v.x, v.y, v.z)
}

fn required_vec3(v: Option<&v1::Vec3>, field: &str) -> Result<w::Vec3, Refusal> {
    v.map(vec3_from).ok_or_else(|| Refusal::missing(field))
}

/// A rotation; an all-zero quaternion is identity.
pub fn quat_from(q: &v1::Quat) -> w::Quat {
    if q.x == 0.0 && q.y == 0.0 && q.z == 0.0 && q.w == 0.0 {
        w::Quat::IDENTITY
    } else {
        w::Quat::from_xyzw(q.x, q.y, q.z, q.w)
    }
}

pub fn color_from(c: Option<&v1::Color>) -> w::Color {
    c.map_or(w::Color::WHITE, |c| w::Color::new(c.r, c.g, c.b, c.a))
}

/// The colour that means "not chosen; the client uses its default".
pub const UNSET_COLOR: w::Color = w::Color::new(0.0, 0.0, 0.0, 0.0);

/// An appearance colour: a missing one is [`UNSET_COLOR`], not white, so
/// clients can tell "not chosen" from "white".
pub fn appearance_color_from(c: Option<&v1::Color>) -> w::Color {
    c.map_or(UNSET_COLOR, |c| w::Color::new(c.r, c.g, c.b, c.a))
}

/// An appearance; a missing one has every colour unset and empty presets.
pub fn appearance_from(a: Option<&v1::Appearance>) -> w::Appearance {
    let default = v1::Appearance::default();
    let a = a.unwrap_or(&default);
    w::Appearance {
        body_color: appearance_color_from(a.body_color.as_ref()),
        body_preset: a.body_preset.clone(),
        skin_color: appearance_color_from(a.skin_color.as_ref()),
        hair_color: appearance_color_from(a.hair_color.as_ref()),
        hair_style: a.hair_style.clone(),
        top_color: appearance_color_from(a.top_color.as_ref()),
        bottom_color: appearance_color_from(a.bottom_color.as_ref()),
    }
}

fn unknown_enum(field: &str, name: &str, value: i32) -> Refusal {
    Refusal::invalid(format!("`{field}`: {value} is not a valid {name}"))
}

pub fn client_kind_from(value: i32, field: &str) -> Result<w::ClientKind, Refusal> {
    match v1::ClientKind::try_from(value) {
        Ok(v1::ClientKind::Unspecified | v1::ClientKind::Human) => Ok(w::ClientKind::Human),
        Ok(v1::ClientKind::Agent) => Ok(w::ClientKind::Agent),
        Err(_) => Err(unknown_enum(field, "ClientKind", value)),
    }
}

pub fn move_mode_from(value: i32, field: &str) -> Result<w::MoveMode, Refusal> {
    match v1::MoveMode::try_from(value) {
        Ok(v1::MoveMode::Unspecified | v1::MoveMode::Walk) => Ok(w::MoveMode::Walk),
        Ok(v1::MoveMode::Run) => Ok(w::MoveMode::Run),
        Ok(v1::MoveMode::Fly) => Ok(w::MoveMode::Fly),
        Err(_) => Err(unknown_enum(field, "MoveMode", value)),
    }
}

pub fn chat_channel_from(value: i32, field: &str) -> Result<w::ChatChannel, Refusal> {
    match v1::ChatChannel::try_from(value) {
        Ok(v1::ChatChannel::Unspecified | v1::ChatChannel::Local) => Ok(w::ChatChannel::Local),
        Ok(v1::ChatChannel::World) => Ok(w::ChatChannel::World),
        Err(_) => Err(unknown_enum(field, "ChatChannel", value)),
    }
}

pub fn material_from(value: i32, field: &str) -> Result<w::Material, Refusal> {
    match v1::Material::try_from(value) {
        Ok(v1::Material::Unspecified | v1::Material::Matte) => Ok(w::Material::Matte),
        Ok(v1::Material::Glossy) => Ok(w::Material::Glossy),
        Ok(v1::Material::Metal) => Ok(w::Material::Metal),
        Ok(v1::Material::Glass) => Ok(w::Material::Glass),
        Ok(v1::Material::Emissive) => Ok(w::Material::Emissive),
        Err(_) => Err(unknown_enum(field, "Material", value)),
    }
}

/// A shape; `SHAPE_UNSPECIFIED` is refused because there is no sensible default.
pub fn shape_from(value: i32, field: &str) -> Result<w::Shape, Refusal> {
    match v1::Shape::try_from(value) {
        Ok(v1::Shape::Unspecified) => Err(Refusal::invalid(format!(
            "`{field}` is required (SHAPE_BOX, SHAPE_SPHERE, SHAPE_CYLINDER, SHAPE_CONE, \
             SHAPE_WEDGE or SHAPE_PLANE)"
        ))),
        Ok(v1::Shape::Box) => Ok(w::Shape::Box),
        Ok(v1::Shape::Sphere) => Ok(w::Shape::Sphere),
        Ok(v1::Shape::Cylinder) => Ok(w::Shape::Cylinder),
        Ok(v1::Shape::Cone) => Ok(w::Shape::Cone),
        Ok(v1::Shape::Wedge) => Ok(w::Shape::Wedge),
        Ok(v1::Shape::Plane) => Ok(w::Shape::Plane),
        Err(_) => Err(unknown_enum(field, "Shape", value)),
    }
}

/// A role; `ROLE_UNSPECIFIED` is refused.
pub fn role_from(value: i32, field: &str) -> Result<w::Role, Refusal> {
    match v1::Role::try_from(value) {
        Ok(v1::Role::Unspecified) => Err(Refusal::invalid(format!(
            "`{field}` is required (ROLE_VISITOR, ROLE_BUILDER or ROLE_OWNER)"
        ))),
        Ok(v1::Role::Visitor) => Ok(w::Role::Visitor),
        Ok(v1::Role::Builder) => Ok(w::Role::Builder),
        Ok(v1::Role::Owner) => Ok(w::Role::Owner),
        Err(_) => Err(unknown_enum(field, "Role", value)),
    }
}

pub fn transform_from(
    t: Option<&v1::AvatarTransform>,
    field: &str,
) -> Result<w::AvatarTransform, Refusal> {
    let t = t.ok_or_else(|| Refusal::missing(field))?;
    Ok(w::AvatarTransform {
        position: required_vec3(t.position.as_ref(), &format!("{field}.position"))?,
        yaw: t.yaw,
        velocity: t.velocity.as_ref().map_or(w::Vec3::ZERO, vec3_from),
        move_mode: move_mode_from(t.move_mode, &format!("{field}.move_mode"))?,
        animation: t.animation.clone(),
    })
}

pub fn spec_from(s: Option<&v1::ObjectSpec>, field: &str) -> Result<w::ObjectSpec, Refusal> {
    let s = s.ok_or_else(|| Refusal::missing(field))?;
    Ok(w::ObjectSpec {
        name: s.name.clone(),
        shape: shape_from(s.shape, &format!("{field}.shape"))?,
        position: required_vec3(s.position.as_ref(), &format!("{field}.position"))?,
        rotation: s.rotation.as_ref().map_or(w::Quat::IDENTITY, quat_from),
        scale: s.scale.as_ref().map_or(w::Vec3::ONE, vec3_from),
        color: color_from(s.color.as_ref()),
        material: material_from(s.material, &format!("{field}.material"))?,
        solid: s.solid,
        locked: s.locked,
    })
}

/// The patch of an `ObjectUpdate`: absent fields stay unchanged.
pub fn patch_from(u: &v1::ObjectUpdate) -> Result<w::ObjectPatch, Refusal> {
    Ok(w::ObjectPatch {
        name: u.name.clone(),
        shape: u.shape.map(|s| shape_from(s, "object_update.shape")).transpose()?,
        position: u.position.as_ref().map(vec3_from),
        rotation: u.rotation.as_ref().map(quat_from),
        scale: u.scale.as_ref().map(vec3_from),
        color: u.color.as_ref().map(|c| color_from(Some(c))),
        material: u.material.map(|m| material_from(m, "object_update.material")).transpose()?,
        solid: u.solid,
        locked: u.locked,
    })
}

fn user_id(id: &str, field: &str) -> Result<w::UserId, Refusal> {
    if id.trim().is_empty() { Err(Refusal::missing(field)) } else { Ok(w::UserId(id.to_owned())) }
}

/// The world command for a client message body. `Hello` and `Ping` are not
/// commands; they are answered by the session and give `BAD_REQUEST` here.
pub fn command_from(body: &C) -> Result<Command, Refusal> {
    Ok(match body {
        C::Hello(_) => return Err(Refusal::bad_request("Hello may only be sent once")),
        C::Ping(_) => return Err(Refusal::bad_request("Ping is not a world command")),
        C::AvatarState(s) => {
            Command::AvatarState(transform_from(s.transform.as_ref(), "avatar_state.transform")?)
        }
        C::MoveTo(m) => Command::MoveTo {
            target: required_vec3(m.target.as_ref(), "move_to.target")?,
            mode: move_mode_from(m.mode, "move_to.mode")?,
        },
        C::Stop(_) => Command::Stop,
        C::Chat(c) => Command::Chat {
            channel: chat_channel_from(c.channel, "chat.channel")?,
            text: c.text.clone(),
        },
        C::ChatHistoryRequest(r) => {
            Command::ChatHistory { since_ms: r.since_ms, limit: r.limit as usize }
        }
        C::ObjectCreate(c) => {
            Command::ObjectCreate(spec_from(c.spec.as_ref(), "object_create.spec")?)
        }
        C::ObjectUpdate(u) => Command::ObjectUpdate { id: u.id, patch: patch_from(u)? },
        C::ObjectDelete(d) => Command::ObjectDelete { id: d.id },
        C::Observe(o) => Command::Observe { radius: o.radius },
        C::SetAppearance(s) => {
            let appearance = s.appearance.as_ref();
            if appearance.is_none() {
                return Err(Refusal::missing("set_appearance.appearance"));
            }
            Command::SetAppearance(appearance_from(appearance))
        }
        C::Admin(a) => {
            use v1::admin::Action;
            match a.action.as_ref() {
                None => {
                    return Err(Refusal::invalid(
                        "`admin` needs one of `kick`, `ban`, `unban` or `set_role`",
                    ));
                }
                Some(Action::Kick(k)) => Command::Kick {
                    user_id: user_id(&k.user_id, "admin.kick.user_id")?,
                    reason: k.reason.clone(),
                },
                Some(Action::Ban(b)) => Command::Ban {
                    user_id: user_id(&b.user_id, "admin.ban.user_id")?,
                    reason: b.reason.clone(),
                },
                Some(Action::Unban(u)) => {
                    Command::Unban { user_id: user_id(&u.user_id, "admin.unban.user_id")? }
                }
                Some(Action::SetRole(s)) => Command::SetRole {
                    user_id: user_id(&s.user_id, "admin.set_role.user_id")?,
                    role: role_from(s.role, "admin.set_role.role")?,
                },
            }
        }
    })
}

// ---------------------------------------------------------------- outbound

pub fn vec3_to(v: w::Vec3) -> v1::Vec3 {
    v1::Vec3 { x: v.x, y: v.y, z: v.z }
}

pub fn quat_to(q: w::Quat) -> v1::Quat {
    v1::Quat { x: q.x, y: q.y, z: q.z, w: q.w }
}

pub fn color_to(c: w::Color) -> v1::Color {
    v1::Color { r: c.r, g: c.g, b: c.b, a: c.a }
}

pub fn client_kind_to(k: w::ClientKind) -> v1::ClientKind {
    match k {
        w::ClientKind::Human => v1::ClientKind::Human,
        w::ClientKind::Agent => v1::ClientKind::Agent,
    }
}

pub fn role_to(r: w::Role) -> v1::Role {
    match r {
        w::Role::Visitor => v1::Role::Visitor,
        w::Role::Builder => v1::Role::Builder,
        w::Role::Owner => v1::Role::Owner,
    }
}

pub fn move_mode_to(m: w::MoveMode) -> v1::MoveMode {
    match m {
        w::MoveMode::Walk => v1::MoveMode::Walk,
        w::MoveMode::Run => v1::MoveMode::Run,
        w::MoveMode::Fly => v1::MoveMode::Fly,
    }
}

pub fn chat_channel_to(c: w::ChatChannel) -> v1::ChatChannel {
    match c {
        w::ChatChannel::Local => v1::ChatChannel::Local,
        w::ChatChannel::World => v1::ChatChannel::World,
    }
}

pub fn shape_to(s: w::Shape) -> v1::Shape {
    match s {
        w::Shape::Box => v1::Shape::Box,
        w::Shape::Sphere => v1::Shape::Sphere,
        w::Shape::Cylinder => v1::Shape::Cylinder,
        w::Shape::Cone => v1::Shape::Cone,
        w::Shape::Wedge => v1::Shape::Wedge,
        w::Shape::Plane => v1::Shape::Plane,
    }
}

pub fn material_to(m: w::Material) -> v1::Material {
    match m {
        w::Material::Matte => v1::Material::Matte,
        w::Material::Glossy => v1::Material::Glossy,
        w::Material::Metal => v1::Material::Metal,
        w::Material::Glass => v1::Material::Glass,
        w::Material::Emissive => v1::Material::Emissive,
    }
}

pub fn appearance_to(a: &w::Appearance) -> v1::Appearance {
    v1::Appearance {
        body_color: Some(color_to(a.body_color)),
        body_preset: a.body_preset.clone(),
        skin_color: Some(color_to(a.skin_color)),
        hair_color: Some(color_to(a.hair_color)),
        hair_style: a.hair_style.clone(),
        top_color: Some(color_to(a.top_color)),
        bottom_color: Some(color_to(a.bottom_color)),
    }
}

pub fn transform_to(t: &w::AvatarTransform) -> v1::AvatarTransform {
    v1::AvatarTransform {
        position: Some(vec3_to(t.position)),
        yaw: t.yaw,
        velocity: Some(vec3_to(t.velocity)),
        move_mode: move_mode_to(t.move_mode) as i32,
        animation: t.animation.clone(),
    }
}

pub fn avatar_to(a: &w::Avatar) -> v1::AvatarInfo {
    v1::AvatarInfo {
        id: a.id,
        user_id: a.user_id.0.clone(),
        name: a.name.clone(),
        kind: client_kind_to(a.kind) as i32,
        role: role_to(a.role) as i32,
        appearance: Some(appearance_to(&a.appearance)),
        transform: Some(transform_to(&a.transform)),
    }
}

pub fn spec_to(s: &w::ObjectSpec) -> v1::ObjectSpec {
    v1::ObjectSpec {
        name: s.name.clone(),
        shape: shape_to(s.shape) as i32,
        position: Some(vec3_to(s.position)),
        rotation: Some(quat_to(s.rotation)),
        scale: Some(vec3_to(s.scale)),
        color: Some(color_to(s.color)),
        material: material_to(s.material) as i32,
        solid: s.solid,
        locked: s.locked,
    }
}

pub fn object_to(o: &w::Object) -> v1::ObjectInfo {
    v1::ObjectInfo { id: o.id, owner_id: o.owner.0.clone(), spec: Some(spec_to(&o.spec)) }
}

pub fn chat_entry_to(e: &w::ChatEntry) -> v1::ChatEntry {
    v1::ChatEntry {
        sender_avatar_id: e.sender_avatar,
        sender_user_id: e.sender_user.0.clone(),
        sender_name: e.sender_name.clone(),
        sender_kind: client_kind_to(e.sender_kind) as i32,
        channel: chat_channel_to(e.channel) as i32,
        text: e.text.clone(),
        timestamp_ms: e.timestamp_ms,
    }
}

pub fn observation_to(o: &w::Observation) -> v1::Observation {
    v1::Observation {
        self_: Some(avatar_to(&o.observer)),
        avatars: o
            .avatars
            .iter()
            .map(|s| v1::observation::SeenAvatar {
                avatar: Some(avatar_to(&s.item)),
                distance: s.distance,
                bearing: s.bearing,
            })
            .collect(),
        objects: o
            .objects
            .iter()
            .map(|s| v1::observation::SeenObject {
                object: Some(object_to(&s.item)),
                distance: s.distance,
                bearing: s.bearing,
            })
            .collect(),
        summary: o.summary.clone(),
        truncated: o.truncated,
        radius: o.radius,
    }
}

pub fn world_info(c: &WorldConfig) -> v1::WorldInfo {
    v1::WorldInfo {
        name: c.name.clone(),
        description: c.description.clone(),
        bounds_min: Some(vec3_to(c.bounds_min)),
        bounds_max: Some(vec3_to(c.bounds_max)),
        spawn_point: Some(vec3_to(c.spawn_point)),
        ground_size: c.ground_size,
        sky: c.sky.clone(),
        time_of_day: c.time_of_day,
    }
}

fn clamp_u32(v: usize) -> u32 {
    u32::try_from(v).unwrap_or(u32::MAX)
}

pub fn limits_to(c: &WorldConfig) -> v1::Limits {
    let l = &c.limits;
    v1::Limits {
        max_message_bytes: clamp_u32(MAX_MESSAGE_BYTES),
        max_chat_chars: clamp_u32(l.max_chat_chars),
        max_name_chars: clamp_u32(l.max_name_chars),
        max_objects: clamp_u32(l.max_objects),
        local_chat_radius: l.local_chat_radius,
        min_scale: l.min_scale,
        max_scale: l.max_scale,
        walk_speed: l.walk_speed,
        run_speed: l.run_speed,
        fly_speed: l.fly_speed,
        max_observe_radius: l.max_observe_radius,
    }
}

pub fn welcome(
    config: &WorldConfig,
    avatar: &w::Avatar,
    session_token: String,
    now_ms: i64,
) -> v1::Welcome {
    v1::Welcome {
        protocol_version: tv_proto::PROTOCOL_VERSION,
        user_id: avatar.user_id.0.clone(),
        avatar_id: avatar.id,
        role: role_to(avatar.role) as i32,
        session_token,
        world: Some(world_info(config)),
        limits: Some(limits_to(config)),
        tick_rate_hz: TICK_RATE_HZ,
        server_time_ms: now_ms,
        server_version: SERVER_VERSION.to_owned(),
        avatar: Some(avatar_to(avatar)),
    }
}

/// Splits the join-time world into `Snapshot` chunks: all avatars in the
/// first, at most [`SNAPSHOT_CHUNK`] objects in each, `last` on the final
/// one. There is always at least one chunk.
pub fn snapshot_chunks(
    avatars: Vec<v1::AvatarInfo>,
    objects: Vec<v1::ObjectInfo>,
) -> Vec<v1::Snapshot> {
    let mut chunks = Vec::with_capacity(objects.len() / SNAPSHOT_CHUNK + 1);
    let mut avatars = Some(avatars);
    let mut objects = objects.into_iter().peekable();
    loop {
        let batch: Vec<_> = objects.by_ref().take(SNAPSHOT_CHUNK).collect();
        let last = objects.peek().is_none();
        chunks.push(v1::Snapshot {
            avatars: avatars.take().unwrap_or_default(),
            objects: batch,
            last,
        });
        if last {
            return chunks;
        }
    }
}

/// The `AvatarStates` body for one recipient. Leaves out `exclude`'s own
/// entry unless the server is driving it; `None` if nothing is left.
pub fn avatar_states_to(
    updates: &[w::AvatarStateUpdate],
    exclude: Option<w::EntityId>,
    now_ms: i64,
) -> Option<S> {
    let states: Vec<_> = updates
        .iter()
        .filter(|u| u.server_driven || Some(u.id) != exclude)
        .map(|u| v1::avatar_states::Entry {
            avatar_id: u.id,
            transform: Some(transform_to(&u.transform)),
        })
        .collect();
    if states.is_empty() {
        None
    } else {
        Some(S::AvatarStates(v1::AvatarStates { server_time_ms: now_ms, states }))
    }
}

/// The server message body for an event, or `None` for host-only events.
/// `AvatarStates` here carries every entry; use [`avatar_states_to`] per
/// recipient.
pub fn event_body(kind: &EventKind, now_ms: i64) -> Option<S> {
    Some(match kind {
        EventKind::AvatarJoined(a) => {
            S::AvatarJoined(v1::AvatarJoined { avatar: Some(avatar_to(a)) })
        }
        EventKind::AvatarLeft { id } => S::AvatarLeft(v1::AvatarLeft { avatar_id: *id }),
        EventKind::AvatarStates(updates) => return avatar_states_to(updates, None, now_ms),
        EventKind::AvatarCorrection { transform, reason } => {
            S::AvatarCorrection(v1::AvatarCorrection {
                transform: Some(transform_to(transform)),
                reason: reason.clone(),
            })
        }
        EventKind::AvatarUpdated(a) => {
            S::AvatarUpdated(v1::AvatarUpdated { avatar: Some(avatar_to(a)) })
        }
        EventKind::Chat(e) => S::ChatMessage(v1::ChatMessage { entry: Some(chat_entry_to(e)) }),
        EventKind::ChatHistory(entries) => {
            S::ChatHistory(v1::ChatHistory { entries: entries.iter().map(chat_entry_to).collect() })
        }
        EventKind::ObjectCreated { object, by } => S::ObjectCreated(v1::ObjectCreated {
            object: Some(object_to(object)),
            by_avatar_id: *by,
        }),
        EventKind::ObjectUpdated { object, by } => S::ObjectUpdated(v1::ObjectUpdated {
            object: Some(object_to(object)),
            by_avatar_id: *by,
        }),
        EventKind::ObjectDeleted { id, by } => {
            S::ObjectDeleted(v1::ObjectDeleted { id: *id, by_avatar_id: *by })
        }
        EventKind::Observation(o) => S::Observation(observation_to(o)),
        EventKind::Kicked { reason } => S::Kicked(v1::Kicked { reason: reason.clone() }),
        EventKind::Ack => S::Ack(v1::Ack {}),
        EventKind::UserBanned { .. }
        | EventKind::UserUnbanned { .. }
        | EventKind::UserRoleChanged { .. } => return None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use tv_world::{Audience, AvatarStateUpdate, Event};

    fn avatar(id: u64) -> w::Avatar {
        w::Avatar {
            id,
            user_id: w::UserId(format!("user-{id}")),
            name: format!("A{id}"),
            kind: w::ClientKind::Agent,
            role: w::Role::Builder,
            appearance: w::Appearance {
                body_color: w::Color::new(0.1, 0.2, 0.3, 1.0),
                body_preset: "p".into(),
                ..Default::default()
            },
            transform: w::AvatarTransform {
                position: w::Vec3::new(1.0, 2.0, 3.0),
                yaw: 45.0,
                velocity: w::Vec3::new(0.5, 0.0, 0.0),
                move_mode: w::MoveMode::Run,
                animation: "run".into(),
            },
        }
    }

    fn object(id: u64) -> w::Object {
        w::Object {
            id,
            owner: w::UserId("owner".into()),
            spec: w::ObjectSpec {
                name: "door".into(),
                shape: w::Shape::Wedge,
                position: w::Vec3::new(1.0, 2.0, 3.0),
                rotation: w::Quat::from_xyzw(0.0, 0.6, 0.0, 0.8),
                scale: w::Vec3::new(2.0, 1.0, 0.5),
                color: w::Color::new(1.0, 0.0, 0.0, 0.5),
                material: w::Material::Glass,
                solid: false,
                locked: true,
            },
        }
    }

    fn v(x: f32, y: f32, z: f32) -> v1::Vec3 {
        v1::Vec3 { x, y, z }
    }

    #[test]
    fn every_world_error_has_a_code() {
        let cases = [
            (WorldError::NotJoined, v1::ErrorCode::BadRequest),
            (WorldError::WorldFull, v1::ErrorCode::WorldFull),
            (WorldError::PermissionDenied("x".into()), v1::ErrorCode::PermissionDenied),
            (WorldError::NotFound("x".into()), v1::ErrorCode::NotFound),
            (WorldError::InvalidArgument("x".into()), v1::ErrorCode::InvalidArgument),
            (WorldError::OutOfBounds("x".into()), v1::ErrorCode::OutOfBounds),
            (WorldError::LimitExceeded("x".into()), v1::ErrorCode::LimitExceeded),
        ];
        for (err, code) in cases {
            let refusal = Refusal::from(&err);
            assert_eq!(refusal.code, code);
            assert_eq!(refusal.message, err.to_string());
            assert_eq!(
                refusal.body(),
                S::Error(v1::Error { code: code as i32, message: err.to_string() })
            );
        }
    }

    #[test]
    fn message_types() {
        assert_eq!(message_type(&C::Hello(Default::default())), None);
        assert_eq!(message_type(&C::Chat(Default::default())), Some(MessageType::Chat));
        assert_eq!(message_type(&C::Ping(Default::default())), Some(MessageType::Ping));
        assert_eq!(
            message_type(&C::ObjectUpdate(Default::default())),
            Some(MessageType::ObjectUpdate)
        );
    }

    #[test]
    fn avatar_state_defaults_and_required_fields() {
        let body = C::AvatarState(v1::AvatarState {
            transform: Some(v1::AvatarTransform {
                position: Some(v(1.0, 0.0, 2.0)),
                yaw: 90.0,
                velocity: None,
                move_mode: 0,
                animation: "idle".into(),
            }),
        });
        let Command::AvatarState(t) = command_from(&body).unwrap() else { panic!() };
        assert_eq!(t.position, w::Vec3::new(1.0, 0.0, 2.0));
        assert_eq!(t.velocity, w::Vec3::ZERO);
        assert_eq!(t.move_mode, w::MoveMode::Walk);
        assert_eq!(t.yaw, 90.0);
        assert_eq!(t.animation, "idle");

        let err = command_from(&C::AvatarState(v1::AvatarState { transform: None })).unwrap_err();
        assert_eq!(err.code, v1::ErrorCode::InvalidArgument);
        assert!(err.message.contains("avatar_state.transform"), "{}", err.message);

        let err = command_from(&C::AvatarState(v1::AvatarState {
            transform: Some(v1::AvatarTransform::default()),
        }))
        .unwrap_err();
        assert!(err.message.contains("avatar_state.transform.position"), "{}", err.message);

        let err = command_from(&C::AvatarState(v1::AvatarState {
            transform: Some(v1::AvatarTransform {
                position: Some(v(0.0, 0.0, 0.0)),
                move_mode: 9,
                ..Default::default()
            }),
        }))
        .unwrap_err();
        assert_eq!(err.code, v1::ErrorCode::InvalidArgument);
        assert!(err.message.contains("move_mode") && err.message.contains('9'));
    }

    #[test]
    fn move_to_stop_chat_history_observe() {
        let c = command_from(&C::MoveTo(v1::MoveTo { target: Some(v(1.0, 2.0, 3.0)), mode: 3 }))
            .unwrap();
        assert_eq!(
            c,
            Command::MoveTo { target: w::Vec3::new(1.0, 2.0, 3.0), mode: w::MoveMode::Fly }
        );
        let c = command_from(&C::MoveTo(v1::MoveTo { target: Some(v(1.0, 2.0, 3.0)), mode: 0 }))
            .unwrap();
        assert!(matches!(c, Command::MoveTo { mode: w::MoveMode::Walk, .. }));
        let err = command_from(&C::MoveTo(v1::MoveTo { target: None, mode: 1 })).unwrap_err();
        assert!(err.message.contains("move_to.target"));

        assert_eq!(command_from(&C::Stop(v1::Stop {})).unwrap(), Command::Stop);

        let c = command_from(&C::Chat(v1::Chat { channel: 0, text: "hi".into() })).unwrap();
        assert_eq!(c, Command::Chat { channel: w::ChatChannel::Local, text: "hi".into() });
        let c = command_from(&C::Chat(v1::Chat { channel: 2, text: "yo".into() })).unwrap();
        assert_eq!(c, Command::Chat { channel: w::ChatChannel::World, text: "yo".into() });
        assert!(command_from(&C::Chat(v1::Chat { channel: 7, text: "x".into() })).is_err());

        let c =
            command_from(&C::ChatHistoryRequest(v1::ChatHistoryRequest { since_ms: 5, limit: 3 }))
                .unwrap();
        assert_eq!(c, Command::ChatHistory { since_ms: 5, limit: 3 });

        let c = command_from(&C::Observe(v1::Observe { radius: 12.5 })).unwrap();
        assert_eq!(c, Command::Observe { radius: 12.5 });
    }

    #[test]
    fn object_create_defaults() {
        let spec = v1::ObjectSpec {
            name: "n".into(),
            shape: v1::Shape::Sphere as i32,
            position: Some(v(1.0, 2.0, 3.0)),
            ..Default::default()
        };
        let c =
            command_from(&C::ObjectCreate(v1::ObjectCreate { spec: Some(spec.clone()) })).unwrap();
        let Command::ObjectCreate(s) = c else { panic!() };
        assert_eq!(s.shape, w::Shape::Sphere);
        assert_eq!(s.rotation, w::Quat::IDENTITY);
        assert_eq!(s.scale, w::Vec3::ONE);
        assert_eq!(s.color, w::Color::WHITE);
        assert_eq!(s.material, w::Material::Matte);
        assert!(!s.solid && !s.locked);

        // All-zero quaternion is identity; a real one is kept.
        let with_rot = v1::ObjectSpec {
            rotation: Some(v1::Quat { x: 0.0, y: 0.0, z: 0.0, w: 0.0 }),
            ..spec.clone()
        };
        let Command::ObjectCreate(s) =
            command_from(&C::ObjectCreate(v1::ObjectCreate { spec: Some(with_rot) })).unwrap()
        else {
            panic!()
        };
        assert_eq!(s.rotation, w::Quat::IDENTITY);

        for (bad, needle) in [
            (v1::ObjectSpec { shape: 0, ..spec.clone() }, "object_create.spec.shape"),
            (v1::ObjectSpec { shape: 42, ..spec.clone() }, "object_create.spec.shape"),
            (v1::ObjectSpec { position: None, ..spec.clone() }, "object_create.spec.position"),
            (v1::ObjectSpec { material: 99, ..spec.clone() }, "object_create.spec.material"),
        ] {
            let err =
                command_from(&C::ObjectCreate(v1::ObjectCreate { spec: Some(bad) })).unwrap_err();
            assert_eq!(err.code, v1::ErrorCode::InvalidArgument);
            assert!(err.message.contains(needle), "{}", err.message);
        }
        let err = command_from(&C::ObjectCreate(v1::ObjectCreate { spec: None })).unwrap_err();
        assert!(err.message.contains("object_create.spec"));
    }

    #[test]
    fn object_update_patch() {
        let u = v1::ObjectUpdate {
            id: 7,
            name: Some("x".into()),
            shape: None,
            position: Some(v(1.0, 1.0, 1.0)),
            rotation: Some(v1::Quat::default()),
            scale: None,
            color: Some(v1::Color { r: 0.0, g: 1.0, b: 0.0, a: 1.0 }),
            material: Some(v1::Material::Metal as i32),
            solid: Some(true),
            locked: None,
        };
        let Command::ObjectUpdate { id, patch } =
            command_from(&C::ObjectUpdate(u.clone())).unwrap()
        else {
            panic!()
        };
        assert_eq!(id, 7);
        assert_eq!(
            patch,
            w::ObjectPatch {
                name: Some("x".into()),
                shape: None,
                position: Some(w::Vec3::ONE),
                rotation: Some(w::Quat::IDENTITY),
                scale: None,
                color: Some(w::Color::new(0.0, 1.0, 0.0, 1.0)),
                material: Some(w::Material::Metal),
                solid: Some(true),
                locked: None,
            }
        );
        let err =
            command_from(&C::ObjectUpdate(v1::ObjectUpdate { shape: Some(0), ..u })).unwrap_err();
        assert!(err.message.contains("object_update.shape"));
        let Command::ObjectUpdate { patch, .. } =
            command_from(&C::ObjectUpdate(v1::ObjectUpdate { id: 1, ..Default::default() }))
                .unwrap()
        else {
            panic!()
        };
        assert_eq!(patch, w::ObjectPatch::default());
        assert_eq!(
            command_from(&C::ObjectDelete(v1::ObjectDelete { id: 9 })).unwrap(),
            Command::ObjectDelete { id: 9 }
        );
    }

    #[test]
    fn unset_appearance_colours_are_all_zero() {
        let grey = v1::Color { r: 0.5, g: 0.5, b: 0.5, a: 1.0 };
        let a = appearance_from(Some(&v1::Appearance {
            body_color: Some(grey),
            hair_style: "bob".into(),
            ..Default::default()
        }));
        assert_eq!(a.body_color, w::Color::new(0.5, 0.5, 0.5, 1.0));
        for c in [a.skin_color, a.hair_color, a.top_color, a.bottom_color] {
            assert_eq!(c, UNSET_COLOR);
        }
        assert_eq!(a.hair_style, "bob");

        // No appearance at all: every colour unset.
        let none = appearance_from(None);
        for c in
            [none.body_color, none.skin_color, none.hair_color, none.top_color, none.bottom_color]
        {
            assert_eq!(c, UNSET_COLOR);
        }

        // All-zero goes out as all-zero, present on the wire.
        let out = appearance_to(&a);
        let zero = v1::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 };
        assert_eq!(out.body_color, Some(grey));
        assert_eq!(out.skin_color, Some(zero));
        assert_eq!(out.bottom_color, Some(zero));
        let msg = v1::ServerMessage {
            reply_to: 0,
            body: Some(S::AvatarUpdated(v1::AvatarUpdated {
                avatar: Some(v1::AvatarInfo { appearance: Some(out), ..Default::default() }),
            })),
        };
        let tv_proto::Frame::Text(json) = tv_proto::encode(&msg, tv_proto::Encoding::Json) else {
            unreachable!()
        };
        let value: serde_json::Value = serde_json::from_str(&json).unwrap();
        let skin = &value["avatarUpdated"]["avatar"]["appearance"]["skinColor"];
        assert_eq!(*skin, serde_json::json!({"r": 0.0, "g": 0.0, "b": 0.0, "a": 0.0}));

        let err =
            command_from(&C::SetAppearance(v1::SetAppearance { appearance: None })).unwrap_err();
        assert!(err.message.contains("set_appearance.appearance"));
        let Ok(Command::SetAppearance(set)) = command_from(&C::SetAppearance(v1::SetAppearance {
            appearance: Some(Default::default()),
        })) else {
            panic!()
        };
        assert_eq!(set.top_color, UNSET_COLOR);
    }

    #[test]
    fn object_colour_still_defaults_to_white() {
        let spec = v1::ObjectSpec {
            shape: v1::Shape::Box as i32,
            position: Some(v(0.0, 0.0, 0.0)),
            color: None,
            ..Default::default()
        };
        assert_eq!(spec_from(Some(&spec), "spec").unwrap().color, w::Color::WHITE);
        let patch = patch_from(&v1::ObjectUpdate { id: 1, ..Default::default() }).unwrap();
        assert_eq!(patch.color, None, "a missing colour in an update leaves it unchanged");
    }

    #[test]
    fn the_world_keeps_unset_colours() {
        let mut world = w::World::new(WorldConfig::default(), Vec::new(), 1);
        let partial = appearance_from(Some(&v1::Appearance {
            body_color: Some(v1::Color { r: 0.2, g: 0.4, b: 0.6, a: 1.0 }),
            ..Default::default()
        }));
        let (id, _) = world
            .join(
                w::JoinRequest {
                    user_id: w::UserId("u".into()),
                    name: "Ada".into(),
                    kind: w::ClientKind::Human,
                    role: w::Role::Builder,
                    appearance: partial.clone(),
                },
                1,
            )
            .unwrap();
        let stored = &world.avatar(id).unwrap().appearance;
        assert_eq!(stored, &partial);
        assert_eq!(stored.skin_color, UNSET_COLOR);

        let events = world
            .apply(id, Command::SetAppearance(appearance_from(Some(&Default::default()))), 2)
            .unwrap();
        let Some(EventKind::AvatarUpdated(a)) = events.first().map(|e| &e.kind) else {
            panic!("{events:?}")
        };
        assert_eq!(a.appearance.body_color, UNSET_COLOR);
        let sent = avatar_to(a).appearance.unwrap();
        assert_eq!(sent.body_color, Some(v1::Color { r: 0.0, g: 0.0, b: 0.0, a: 0.0 }));
    }

    #[test]
    fn admin_commands() {
        use v1::admin::{Action, Ban, Kick, SetRole, Unban};
        let admin = |a| C::Admin(v1::Admin { action: Some(a) });
        assert_eq!(
            command_from(&admin(Action::Kick(Kick { user_id: "u".into(), reason: "r".into() })))
                .unwrap(),
            Command::Kick { user_id: "u".into(), reason: "r".into() }
        );
        assert_eq!(
            command_from(&admin(Action::Ban(Ban { user_id: "u".into(), reason: "r".into() })))
                .unwrap(),
            Command::Ban { user_id: "u".into(), reason: "r".into() }
        );
        assert_eq!(
            command_from(&admin(Action::Unban(Unban { user_id: "u".into() }))).unwrap(),
            Command::Unban { user_id: "u".into() }
        );
        assert_eq!(
            command_from(&admin(Action::SetRole(SetRole { user_id: "u".into(), role: 1 })))
                .unwrap(),
            Command::SetRole { user_id: "u".into(), role: w::Role::Visitor }
        );
        for bad in [
            admin(Action::SetRole(SetRole { user_id: "u".into(), role: 0 })),
            admin(Action::SetRole(SetRole { user_id: "u".into(), role: 8 })),
            admin(Action::Kick(Kick { user_id: "".into(), reason: "".into() })),
            C::Admin(v1::Admin { action: None }),
        ] {
            let err = command_from(&bad).unwrap_err();
            assert_eq!(err.code, v1::ErrorCode::InvalidArgument, "{bad:?}");
        }
    }

    #[test]
    fn hello_and_ping_are_not_commands() {
        assert_eq!(
            command_from(&C::Hello(Default::default())).unwrap_err().code,
            v1::ErrorCode::BadRequest
        );
        assert!(command_from(&C::Ping(Default::default())).is_err());
    }

    #[test]
    fn client_kind() {
        assert_eq!(client_kind_from(0, "k").unwrap(), w::ClientKind::Human);
        assert_eq!(client_kind_from(1, "k").unwrap(), w::ClientKind::Human);
        assert_eq!(client_kind_from(2, "k").unwrap(), w::ClientKind::Agent);
        assert!(client_kind_from(3, "k").is_err());
    }

    #[test]
    fn outbound_round_trips_inbound() {
        let o = object(5);
        let proto = object_to(&o);
        assert_eq!(proto.id, 5);
        assert_eq!(proto.owner_id, "owner");
        let back = spec_from(proto.spec.as_ref(), "spec").unwrap();
        assert_eq!(back, o.spec);

        let a = avatar(3);
        let info = avatar_to(&a);
        assert_eq!(info.id, 3);
        assert_eq!(info.kind, v1::ClientKind::Agent as i32);
        assert_eq!(info.role, v1::Role::Builder as i32);
        assert_eq!(appearance_from(info.appearance.as_ref()), a.appearance);
        assert_eq!(transform_from(info.transform.as_ref(), "t").unwrap(), a.transform);
    }

    #[test]
    fn welcome_from_config() {
        let config = WorldConfig::default();
        let w = welcome(&config, &avatar(4), "tok".into(), 1234);
        assert_eq!(w.protocol_version, tv_proto::PROTOCOL_VERSION);
        assert_eq!(w.avatar_id, 4);
        assert_eq!(w.user_id, "user-4");
        assert_eq!(w.session_token, "tok");
        assert_eq!(w.tick_rate_hz, 20);
        assert_eq!(w.server_time_ms, 1234);
        assert_eq!(w.server_version, SERVER_VERSION);
        let info = w.world.unwrap();
        assert_eq!(info.name, config.name);
        assert_eq!(info.bounds_max, Some(vec3_to(config.bounds_max)));
        assert_eq!(info.sky, config.sky);
        let limits = w.limits.unwrap();
        assert_eq!(limits.max_message_bytes, 65536);
        assert_eq!(limits.max_chat_chars, 500);
        assert_eq!(limits.max_objects, 10_000);
        assert_eq!(limits.local_chat_radius, 20.0);
        assert_eq!(limits.fly_speed, 16.0);
        assert_eq!(limits.max_observe_radius, 100.0);
    }

    #[test]
    fn snapshot_chunking() {
        let avatars: Vec<_> = (1..=3).map(|i| avatar_to(&avatar(i))).collect();
        let objects: Vec<_> = (10..260).map(|i| object_to(&object(i))).collect();

        let chunks = snapshot_chunks(avatars.clone(), objects.clone());
        assert_eq!(chunks.len(), 3);
        assert_eq!(chunks[0].avatars.len(), 3);
        assert!(chunks[1].avatars.is_empty() && chunks[2].avatars.is_empty());
        assert_eq!(chunks.iter().map(|c| c.objects.len()).collect::<Vec<_>>(), vec![100, 100, 50]);
        assert_eq!(chunks.iter().map(|c| c.last).collect::<Vec<_>>(), vec![false, false, true]);
        let all: Vec<_> = chunks.iter().flat_map(|c| c.objects.clone()).collect();
        assert_eq!(all, objects);

        let empty = snapshot_chunks(Vec::new(), Vec::new());
        assert_eq!(empty, vec![v1::Snapshot { avatars: vec![], objects: vec![], last: true }]);

        let exact = snapshot_chunks(avatars, objects[..100].to_vec());
        assert_eq!(exact.len(), 1);
        assert!(exact[0].last);
    }

    #[test]
    fn avatar_states_filtering() {
        let t = w::AvatarTransform::default();
        let updates = vec![
            AvatarStateUpdate { id: 1, transform: t.clone(), server_driven: false },
            AvatarStateUpdate { id: 2, transform: t.clone(), server_driven: true },
        ];
        let ids = |b: Option<S>| match b {
            Some(S::AvatarStates(s)) => {
                assert_eq!(s.server_time_ms, 99);
                s.states.iter().map(|e| e.avatar_id).collect::<Vec<_>>()
            }
            None => vec![],
            other => panic!("{other:?}"),
        };
        assert_eq!(ids(avatar_states_to(&updates, None, 99)), vec![1, 2]);
        assert_eq!(ids(avatar_states_to(&updates, Some(1), 99)), vec![2]);
        assert_eq!(ids(avatar_states_to(&updates, Some(2), 99)), vec![1, 2]);
        assert_eq!(ids(avatar_states_to(&updates, Some(3), 99)), vec![1, 2]);
        assert!(avatar_states_to(&updates[..1], Some(1), 99).is_none());
    }

    #[test]
    fn every_event_kind_converts() {
        let a = avatar(1);
        let o = object(2);
        let entry = w::ChatEntry {
            sender_avatar: 1,
            sender_user: "u".into(),
            sender_name: "A".into(),
            sender_kind: w::ClientKind::Human,
            channel: w::ChatChannel::World,
            text: "hi".into(),
            timestamp_ms: 5,
        };
        let observation = w::Observation {
            observer: a.clone(),
            avatars: vec![w::Seen { item: a.clone(), distance: 1.0, bearing: -10.0 }],
            objects: vec![w::Seen { item: o.clone(), distance: 2.0, bearing: 20.0 }],
            summary: "s".into(),
            truncated: true,
            radius: 30.0,
        };
        let kinds = vec![
            EventKind::AvatarJoined(a.clone()),
            EventKind::AvatarLeft { id: 1 },
            EventKind::AvatarStates(vec![AvatarStateUpdate {
                id: 1,
                transform: a.transform.clone(),
                server_driven: false,
            }]),
            EventKind::AvatarCorrection { transform: a.transform.clone(), reason: "fast".into() },
            EventKind::AvatarUpdated(a.clone()),
            EventKind::Chat(entry.clone()),
            EventKind::ChatHistory(vec![entry.clone()]),
            EventKind::ObjectCreated { object: o.clone(), by: 1 },
            EventKind::ObjectUpdated { object: o.clone(), by: 1 },
            EventKind::ObjectDeleted { id: 2, by: 1 },
            EventKind::Observation(Box::new(observation)),
            EventKind::Kicked { reason: "bye".into() },
            EventKind::Ack,
        ];
        for kind in &kinds {
            let body = event_body(kind, 0).unwrap_or_else(|| panic!("{kind:?}"));
            // Every body encodes in both encodings.
            let msg = v1::ServerMessage { reply_to: 0, body: Some(body) };
            for enc in [tv_proto::Encoding::Binary, tv_proto::Encoding::Json] {
                let frame = tv_proto::encode(&msg, enc);
                let back: v1::ServerMessage = tv_proto::decode(&frame).unwrap();
                assert_eq!(back, msg);
            }
        }
        match event_body(&kinds[6], 0) {
            Some(S::ChatHistory(h)) => {
                assert_eq!(h.entries[0].channel, v1::ChatChannel::World as i32)
            }
            other => panic!("{other:?}"),
        }
        match event_body(&kinds[10], 0) {
            Some(S::Observation(o)) => {
                assert_eq!(o.self_.unwrap().id, 1);
                assert_eq!(o.avatars[0].bearing, -10.0);
                assert_eq!(o.objects[0].object.as_ref().unwrap().id, 2);
                assert!(o.truncated);
            }
            other => panic!("{other:?}"),
        }
        for host in [
            EventKind::UserBanned { user_id: "u".into(), reason: "r".into() },
            EventKind::UserUnbanned { user_id: "u".into() },
            EventKind::UserRoleChanged { user_id: "u".into(), role: w::Role::Owner },
        ] {
            assert!(event_body(&host, 0).is_none());
            let _ = Event::new(Audience::Host, host);
        }
    }
}
