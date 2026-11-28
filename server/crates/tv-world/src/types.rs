//! Plain data types of the world. They mirror the protocol messages in
//! `proto/tinyverse/v1/common.proto` but use native Rust and `glam` types.
//!
//! Conventions: metres and seconds; left-handed, Y up; yaw in degrees,
//! clockwise seen from above, 0 = facing +Z; timestamps in milliseconds since
//! the Unix epoch.

use glam::{Quat, Vec3};

/// Identifies an avatar or an object. Assigned by the world, starting at 1,
/// never reused. 0 is never a valid id.
pub type EntityId = u64;

/// The stable identity of a user in this world: a UUID string.
#[derive(Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct UserId(pub String);

impl UserId {
    /// The id as a string slice.
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl std::fmt::Display for UserId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl From<&str> for UserId {
    fn from(value: &str) -> Self {
        UserId(value.to_owned())
    }
}

impl From<String> for UserId {
    fn from(value: String) -> Self {
        UserId(value)
    }
}

/// What sits behind a connection.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ClientKind {
    Human,
    Agent,
}

/// What a user may do. Ordered: each role includes the ones before it, so
/// `role >= Role::Builder` is the test for "may build".
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Role {
    /// Move, chat, observe.
    Visitor,
    /// Also create objects; edit and delete own objects and unlocked objects of others.
    Builder,
    /// Everything, plus edit any object, kick, ban, change roles.
    Owner,
}

/// How an avatar is moving. Sets the speed limit.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum MoveMode {
    #[default]
    Walk,
    Run,
    Fly,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ChatChannel {
    /// Heard within [`Limits::local_chat_radius`] of the speaker. Never retained.
    Local,
    /// Heard by everyone. Retained in memory for [`WorldConfig::chat_retention_ms`].
    World,
}

/// Primitive shapes. Each fits a 1 m cube centred on the origin before scaling.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum Shape {
    #[default]
    Box,
    Sphere,
    Cylinder,
    Cone,
    Wedge,
    Plane,
}

/// Surface presets.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum Material {
    #[default]
    Matte,
    Glossy,
    Metal,
    Glass,
    Emissive,
}

/// An RGBA colour, each channel 0..1.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Color {
    pub r: f32,
    pub g: f32,
    pub b: f32,
    pub a: f32,
}

impl Color {
    pub const WHITE: Color = Color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 };

    pub const fn new(r: f32, g: f32, b: f32, a: f32) -> Self {
        Color { r, g, b, a }
    }
}

impl Default for Color {
    fn default() -> Self {
        Color::WHITE
    }
}

/// How an avatar looks.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Appearance {
    pub body_color: Color,
    pub body_preset: String,
    pub skin_color: Color,
    pub hair_color: Color,
    pub hair_style: String,
    pub top_color: Color,
    pub bottom_color: Color,
}

/// Where an avatar is and how it is moving.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct AvatarTransform {
    /// Position of the avatar's feet.
    pub position: Vec3,
    /// Heading in degrees, clockwise seen from above, 0 = facing +Z.
    pub yaw: f32,
    /// Metres per second, world space.
    pub velocity: Vec3,
    pub move_mode: MoveMode,
    /// Free-form animation hint such as "idle" or "walk".
    pub animation: String,
}

/// An avatar that is present in the world.
#[derive(Clone, Debug, PartialEq)]
pub struct Avatar {
    pub id: EntityId,
    pub user_id: UserId,
    pub name: String,
    pub kind: ClientKind,
    pub role: Role,
    pub appearance: Appearance,
    pub transform: AvatarTransform,
}

/// The editable properties of a primitive object.
#[derive(Clone, Debug, PartialEq)]
pub struct ObjectSpec {
    pub name: String,
    pub shape: Shape,
    /// Centre of the object.
    pub position: Vec3,
    pub rotation: Quat,
    /// Size per axis in metres.
    pub scale: Vec3,
    pub color: Color,
    pub material: Material,
    /// Avatars collide with it.
    pub solid: bool,
    /// Only its owner and world owners may edit or delete it.
    pub locked: bool,
}

impl Default for ObjectSpec {
    fn default() -> Self {
        ObjectSpec {
            name: String::new(),
            shape: Shape::Box,
            position: Vec3::ZERO,
            rotation: Quat::IDENTITY,
            scale: Vec3::ONE,
            color: Color::WHITE,
            material: Material::Matte,
            solid: true,
            locked: false,
        }
    }
}

/// An object that exists in the world.
#[derive(Clone, Debug, PartialEq)]
pub struct Object {
    pub id: EntityId,
    /// The user who created it.
    pub owner: UserId,
    pub spec: ObjectSpec,
}

/// A partial change to an object. `None` leaves the property as it is.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ObjectPatch {
    pub name: Option<String>,
    pub shape: Option<Shape>,
    pub position: Option<Vec3>,
    pub rotation: Option<Quat>,
    pub scale: Option<Vec3>,
    pub color: Option<Color>,
    pub material: Option<Material>,
    pub solid: Option<bool>,
    pub locked: Option<bool>,
}

/// One line of chat.
#[derive(Clone, Debug, PartialEq)]
pub struct ChatEntry {
    pub sender_avatar: EntityId,
    pub sender_user: UserId,
    pub sender_name: String,
    pub sender_kind: ClientKind,
    pub channel: ChatChannel,
    pub text: String,
    pub timestamp_ms: i64,
}

/// Something seen by an observer.
#[derive(Clone, Debug, PartialEq)]
pub struct Seen<T> {
    pub item: T,
    /// Metres from the observer.
    pub distance: f32,
    /// Degrees relative to the observer's heading: 0 = straight ahead,
    /// positive = to the right, range -180..=180.
    pub bearing: f32,
}

/// What is around an avatar, nearest first.
#[derive(Clone, Debug, PartialEq)]
pub struct Observation {
    pub observer: Avatar,
    pub avatars: Vec<Seen<Avatar>>,
    pub objects: Vec<Seen<Object>>,
    /// A short plain-text description, ready for a language model's context.
    pub summary: String,
    /// True if the lists were cut short because too much is nearby.
    pub truncated: bool,
    /// The radius actually used, in metres.
    pub radius: f32,
}

/// The limits the world enforces.
#[derive(Clone, Debug, PartialEq)]
pub struct Limits {
    /// Most avatars present at once.
    pub max_users: usize,
    pub max_chat_chars: usize,
    /// Longest avatar or object name, in characters.
    pub max_name_chars: usize,
    pub max_objects: usize,
    /// How far local chat carries, in metres.
    pub local_chat_radius: f32,
    /// Smallest and largest object size per axis, in metres.
    pub min_scale: f32,
    pub max_scale: f32,
    /// Speed limits in metres per second.
    pub walk_speed: f32,
    pub run_speed: f32,
    pub fly_speed: f32,
    /// Reported movement may exceed the speed limit by this factor before it
    /// is corrected, to absorb network jitter. 1.5 means 50 % slack.
    pub speed_tolerance: f32,
    /// Radius used by `Observe` when none is given, and the largest accepted.
    pub default_observe_radius: f32,
    pub max_observe_radius: f32,
    /// Most avatars and most objects listed in one observation.
    pub max_observed: usize,
}

impl Default for Limits {
    fn default() -> Self {
        Limits {
            max_users: 16,
            max_chat_chars: 500,
            max_name_chars: 64,
            max_objects: 10_000,
            local_chat_radius: 20.0,
            min_scale: 0.01,
            max_scale: 64.0,
            walk_speed: 4.0,
            run_speed: 8.0,
            fly_speed: 16.0,
            speed_tolerance: 1.5,
            default_observe_radius: 30.0,
            max_observe_radius: 100.0,
            max_observed: 50,
        }
    }
}

/// The static description of a world. Loaded from `world.toml` by the server.
#[derive(Clone, Debug, PartialEq)]
pub struct WorldConfig {
    pub name: String,
    pub description: String,
    /// Axis-aligned box that every avatar and object centre must stay inside.
    pub bounds_min: Vec3,
    pub bounds_max: Vec3,
    /// Where new avatars appear.
    pub spawn_point: Vec3,
    /// Side length of the square ground plane, centred on the origin at y = 0.
    pub ground_size: f32,
    /// Name of a ground preset shipped with the client; empty means the default.
    pub ground: String,
    /// Name of a sky preset shipped with the client.
    pub sky: String,
    /// Hour of the day, 0..24.
    pub time_of_day: f32,
    pub limits: Limits,
    /// How long world chat is kept, in milliseconds. 0 disables history.
    pub chat_retention_ms: i64,
    /// Most world chat messages kept, whatever their age.
    pub chat_history_max: usize,
}

impl Default for WorldConfig {
    fn default() -> Self {
        WorldConfig {
            name: "Tinyverse".to_owned(),
            description: String::new(),
            bounds_min: Vec3::new(-128.0, -16.0, -128.0),
            bounds_max: Vec3::new(128.0, 256.0, 128.0),
            spawn_point: Vec3::ZERO,
            ground_size: 256.0,
            ground: String::new(),
            sky: "day".to_owned(),
            time_of_day: 12.0,
            limits: Limits::default(),
            chat_retention_ms: 3 * 60 * 60 * 1000,
            chat_history_max: 1000,
        }
    }
}
