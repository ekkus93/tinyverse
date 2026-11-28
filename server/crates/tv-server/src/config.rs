//! The world directory and its `world.toml`.
//!
//! A world is a directory holding `world.toml` (the static description, read
//! once at start), `world.db` (the store) and, after `init`, `owner.token`.
//! Every key of `world.toml` is optional; a missing key takes the default of
//! [`tv_world::WorldConfig`]. Unknown keys are refused so that a typo does not
//! silently fall back to a default.

use std::path::{Path, PathBuf};

use anyhow::{Context, bail};
use serde::{Deserialize, Serialize};
use tv_world::{Limits, Role, Vec3, WorldConfig};

/// File name of the world description inside a world directory.
pub const WORLD_TOML: &str = "world.toml";
/// File name of the world database inside a world directory.
pub const WORLD_DB: &str = "world.db";
/// File name of the owner's login token written by `init`.
pub const OWNER_TOKEN: &str = "owner.token";
/// Hard cap on `max_users` (SPEC 1).
pub const MAX_USERS_CAP: usize = 64;
/// Longest `ground` preset name.
pub const MAX_PRESET_CHARS: usize = 32;

/// The paths of one world directory.
#[derive(Clone, Debug)]
pub struct WorldDir {
    pub root: PathBuf,
}

impl WorldDir {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        WorldDir { root: root.into() }
    }
    pub fn toml(&self) -> PathBuf {
        self.root.join(WORLD_TOML)
    }
    pub fn db(&self) -> PathBuf {
        self.root.join(WORLD_DB)
    }
    pub fn owner_token(&self) -> PathBuf {
        self.root.join(OWNER_TOKEN)
    }
}

/// A role as written in `world.toml` and on the command line.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, clap::ValueEnum)]
#[serde(rename_all = "lowercase")]
pub enum RoleName {
    Visitor,
    Builder,
    Owner,
}

impl From<RoleName> for Role {
    fn from(value: RoleName) -> Self {
        match value {
            RoleName::Visitor => Role::Visitor,
            RoleName::Builder => Role::Builder,
            RoleName::Owner => Role::Owner,
        }
    }
}

impl From<Role> for RoleName {
    fn from(value: Role) -> Self {
        match value {
            Role::Visitor => RoleName::Visitor,
            Role::Builder => RoleName::Builder,
            Role::Owner => RoleName::Owner,
        }
    }
}

/// The axis-aligned box everything must stay inside.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Bounds {
    pub min: [f32; 3],
    pub max: [f32; 3],
}

/// Optional overrides for [`tv_world::Limits`]. `max_users` is set at the top
/// level of `world.toml`, not here.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct LimitOverrides {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_chat_chars: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_name_chars: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_objects: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub local_chat_radius: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub min_scale: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_scale: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub walk_speed: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub run_speed: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fly_speed: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub speed_tolerance: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub default_observe_radius: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_observe_radius: Option<f32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_observed: Option<usize>,
}

impl LimitOverrides {
    pub fn is_empty(&self) -> bool {
        *self == LimitOverrides::default()
    }

    fn apply(&self, limits: &mut Limits) {
        fn set<T: Copy>(target: &mut T, value: Option<T>) {
            if let Some(v) = value {
                *target = v;
            }
        }
        set(&mut limits.max_chat_chars, self.max_chat_chars);
        set(&mut limits.max_name_chars, self.max_name_chars);
        set(&mut limits.max_objects, self.max_objects);
        set(&mut limits.local_chat_radius, self.local_chat_radius);
        set(&mut limits.min_scale, self.min_scale);
        set(&mut limits.max_scale, self.max_scale);
        set(&mut limits.walk_speed, self.walk_speed);
        set(&mut limits.run_speed, self.run_speed);
        set(&mut limits.fly_speed, self.fly_speed);
        set(&mut limits.speed_tolerance, self.speed_tolerance);
        set(&mut limits.default_observe_radius, self.default_observe_radius);
        set(&mut limits.max_observe_radius, self.max_observe_radius);
        set(&mut limits.max_observed, self.max_observed);
    }
}

/// The contents of `world.toml`.
///
/// Plain values come before the `[bounds]` and `[limits]` tables so that the
/// struct serialises to valid TOML in field order.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct WorldFile {
    pub name: String,
    pub description: String,
    /// Argon2 PHC string of the join password. Absent means no password.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub password_hash: Option<String>,
    /// Role given to users who join for the first time without a token.
    pub default_role: RoleName,
    /// Most avatars present at once, 1..=64.
    pub max_users: usize,
    pub spawn_point: [f32; 3],
    /// Side length of the square ground plane in metres.
    pub ground_size: f32,
    /// Ground preset shipped with the client (`grass`, `sand`, `water`,
    /// `paving`); empty is the default grid.
    #[serde(skip_serializing_if = "String::is_empty")]
    pub ground: String,
    pub sky: String,
    /// Hour of the day, 0..24.
    pub time_of_day: f32,
    /// How long world chat is kept in memory; 0 disables history.
    pub chat_retention_minutes: u64,
    /// Most world chat messages kept, whatever their age.
    pub chat_history_max: usize,
    pub bounds: Bounds,
    #[serde(skip_serializing_if = "LimitOverrides::is_empty")]
    pub limits: LimitOverrides,
}

impl Default for WorldFile {
    fn default() -> Self {
        let config = WorldConfig::default();
        WorldFile {
            name: config.name,
            description: config.description,
            password_hash: None,
            default_role: RoleName::Builder,
            max_users: config.limits.max_users,
            spawn_point: config.spawn_point.to_array(),
            ground_size: config.ground_size,
            ground: config.ground,
            sky: config.sky,
            time_of_day: config.time_of_day,
            chat_retention_minutes: 180,
            chat_history_max: config.chat_history_max,
            bounds: Bounds { min: config.bounds_min.to_array(), max: config.bounds_max.to_array() },
            limits: LimitOverrides::default(),
        }
    }
}

const HEADER: &str = "\
# Tinyverse world description. Read once when the server starts.
# Every key is optional; see docs/SPEC.md section 5.1. Unknown keys are an error.
# Optional [limits] keys: max_chat_chars, max_name_chars, max_objects,
# local_chat_radius, min_scale, max_scale, walk_speed, run_speed, fly_speed,
# speed_tolerance, default_observe_radius, max_observe_radius, max_observed.

";

impl WorldFile {
    /// Parses and validates the text of a `world.toml`.
    pub fn parse(text: &str) -> anyhow::Result<WorldFile> {
        let file: WorldFile = toml::from_str(text).map_err(|e| anyhow::anyhow!("{e}"))?;
        file.validate()?;
        Ok(file)
    }

    /// Reads and validates `world.toml` from a world directory.
    pub fn load(dir: &WorldDir) -> anyhow::Result<WorldFile> {
        let path = dir.toml();
        let text = std::fs::read_to_string(&path).with_context(|| {
            format!("cannot read {} (is this a world directory?)", path.display())
        })?;
        WorldFile::parse(&text).with_context(|| format!("invalid {}", path.display()))
    }

    /// The TOML text of this file, with a short explanatory header.
    pub fn to_toml(&self) -> String {
        let body = toml::to_string(self).expect("world file always serialises");
        format!("{HEADER}{body}")
    }

    /// Checks every value for sense, naming the key that is wrong.
    pub fn validate(&self) -> anyhow::Result<()> {
        if self.name.trim().is_empty() {
            bail!("`name` must not be empty");
        }
        if !(1..=MAX_USERS_CAP).contains(&self.max_users) {
            bail!("`max_users` must be between 1 and {MAX_USERS_CAP}, not {}", self.max_users);
        }
        if let Some(hash) = &self.password_hash {
            crate::auth::validate_phc(hash)
                .map_err(|e| anyhow::anyhow!("`password_hash` is not an Argon2 PHC string: {e}"))?;
        }
        let finite = |key: &str, v: &[f32]| -> anyhow::Result<()> {
            if v.iter().all(|x| x.is_finite()) {
                Ok(())
            } else {
                bail!("`{key}` must be a finite number")
            }
        };
        finite("bounds.min", &self.bounds.min)?;
        finite("bounds.max", &self.bounds.max)?;
        finite("spawn_point", &self.spawn_point)?;
        finite("ground_size", &[self.ground_size])?;
        finite("time_of_day", &[self.time_of_day])?;
        let (min, max) = (Vec3::from(self.bounds.min), Vec3::from(self.bounds.max));
        if !min.cmplt(max).all() {
            bail!("`bounds.min` must be smaller than `bounds.max` on every axis");
        }
        let spawn = Vec3::from(self.spawn_point);
        if !(spawn.cmpge(min).all() && spawn.cmple(max).all()) {
            bail!("`spawn_point` must lie inside `bounds`");
        }
        if self.ground_size <= 0.0 {
            bail!("`ground_size` must be positive");
        }
        if self.ground.chars().count() > MAX_PRESET_CHARS
            || !self
                .ground
                .chars()
                .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-')
        {
            bail!(
                "`ground` must be a preset name of at most {MAX_PRESET_CHARS} lower-case letters, \
                 digits or hyphens"
            );
        }
        if !(0.0..=24.0).contains(&self.time_of_day) {
            bail!("`time_of_day` must be between 0 and 24");
        }
        if self.chat_retention_minutes > 7 * 24 * 60 {
            bail!("`chat_retention_minutes` must be at most one week");
        }
        let limits = self.limits().limits;
        for (key, v) in [
            ("limits.local_chat_radius", limits.local_chat_radius),
            ("limits.min_scale", limits.min_scale),
            ("limits.max_scale", limits.max_scale),
            ("limits.walk_speed", limits.walk_speed),
            ("limits.run_speed", limits.run_speed),
            ("limits.fly_speed", limits.fly_speed),
            ("limits.speed_tolerance", limits.speed_tolerance),
            ("limits.default_observe_radius", limits.default_observe_radius),
            ("limits.max_observe_radius", limits.max_observe_radius),
        ] {
            if !(v.is_finite() && v > 0.0) {
                bail!("`{key}` must be a positive number");
            }
        }
        for (key, v) in [
            ("limits.max_chat_chars", limits.max_chat_chars),
            ("limits.max_name_chars", limits.max_name_chars),
            ("limits.max_observed", limits.max_observed),
        ] {
            if v == 0 {
                bail!("`{key}` must be at least 1");
            }
        }
        if limits.min_scale > limits.max_scale {
            bail!("`limits.min_scale` must not exceed `limits.max_scale`");
        }
        if limits.default_observe_radius > limits.max_observe_radius {
            bail!("`limits.default_observe_radius` must not exceed `limits.max_observe_radius`");
        }
        Ok(())
    }

    fn limits(&self) -> WorldConfig {
        let mut config = WorldConfig::default();
        config.limits.max_users = self.max_users;
        self.limits.apply(&mut config.limits);
        config
    }

    /// The world's configuration as `tv-world` wants it.
    pub fn world_config(&self) -> WorldConfig {
        let mut config = self.limits();
        config.name = self.name.clone();
        config.description = self.description.clone();
        config.bounds_min = Vec3::from(self.bounds.min);
        config.bounds_max = Vec3::from(self.bounds.max);
        config.spawn_point = Vec3::from(self.spawn_point);
        config.ground_size = self.ground_size;
        config.ground = self.ground.clone();
        config.sky = self.sky.clone();
        config.time_of_day = self.time_of_day;
        config.chat_retention_ms = (self.chat_retention_minutes as i64) * 60_000;
        config.chat_history_max = self.chat_history_max;
        config
    }
}

/// Rewrites the top-level `password_hash` key of a `world.toml` text,
/// keeping every other line (and comment) as it is. `None` removes it.
pub fn edit_password_hash(text: &str, hash: Option<&str>) -> String {
    let mut lines: Vec<String> = text.lines().map(str::to_owned).collect();
    let first_table =
        lines.iter().position(|l| l.trim_start().starts_with('[')).unwrap_or(lines.len());
    let is_key = |l: &str| {
        let t = l.trim_start();
        t.strip_prefix("password_hash").is_some_and(|rest| rest.trim_start().starts_with('='))
    };
    let existing = lines[..first_table].iter().position(|l| is_key(l));
    let new_line = hash.map(|h| format!("password_hash = {}", toml::Value::String(h.to_owned())));
    match (existing, new_line) {
        (Some(i), Some(line)) => lines[i] = line,
        (Some(i), None) => {
            lines.remove(i);
        }
        (None, Some(line)) => {
            // Top-level keys must come before the first table.
            let mut at = first_table;
            while at > 0 && lines[at - 1].trim().is_empty() {
                at -= 1;
            }
            lines.insert(at, line);
        }
        (None, None) => {}
    }
    let mut out = lines.join("\n");
    out.push('\n');
    out
}

/// Whether `dir` already holds a world (or something `init` must not touch).
pub fn world_exists(dir: &Path) -> bool {
    [WORLD_TOML, WORLD_DB, OWNER_TOKEN].iter().any(|f| dir.join(f).exists())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_file_gives_defaults() {
        let file = WorldFile::parse("").unwrap();
        assert_eq!(file, WorldFile::default());
        let config = file.world_config();
        let default = WorldConfig::default();
        assert_eq!(config, default);
        assert_eq!(config.chat_retention_ms, 3 * 60 * 60 * 1000);
        assert_eq!(file.default_role, RoleName::Builder);
    }

    #[test]
    fn full_file_maps_onto_world_config() {
        let text = r#"
name = "Island"
description = "sand and palms"
default_role = "visitor"
max_users = 8
spawn_point = [1.0, 2.0, 3.0]
ground_size = 100
ground = "sand"
sky = "dusk"
time_of_day = 18.5
chat_retention_minutes = 10
chat_history_max = 50

[bounds]
min = [-50, -5, -50]
max = [50, 100, 50]

[limits]
max_objects = 500
walk_speed = 3.5
"#;
        let file = WorldFile::parse(text).unwrap();
        assert_eq!(file.default_role, RoleName::Visitor);
        let c = file.world_config();
        assert_eq!(c.name, "Island");
        assert_eq!(c.description, "sand and palms");
        assert_eq!(c.spawn_point, Vec3::new(1.0, 2.0, 3.0));
        assert_eq!(c.bounds_min, Vec3::new(-50.0, -5.0, -50.0));
        assert_eq!(c.bounds_max, Vec3::new(50.0, 100.0, 50.0));
        assert_eq!(c.ground_size, 100.0);
        assert_eq!(c.ground, "sand");
        assert_eq!(c.sky, "dusk");
        assert_eq!(c.time_of_day, 18.5);
        assert_eq!(c.chat_retention_ms, 600_000);
        assert_eq!(c.chat_history_max, 50);
        assert_eq!(c.limits.max_users, 8);
        assert_eq!(c.limits.max_objects, 500);
        assert_eq!(c.limits.walk_speed, 3.5);
        assert_eq!(c.limits.run_speed, Limits::default().run_speed);
    }

    #[test]
    fn unknown_keys_are_refused_with_their_name() {
        let err = WorldFile::parse("nmae = \"x\"\n").unwrap_err().to_string();
        assert!(err.contains("nmae"), "{err}");
        let err = WorldFile::parse("[limits]\nmax_users = 3\n").unwrap_err().to_string();
        assert!(err.contains("max_users"), "{err}");
        let err = WorldFile::parse("[bounds]\nmin = [0,0,0]\nmax=[1,1,1]\nmid = 1\n")
            .unwrap_err()
            .to_string();
        assert!(err.contains("mid"), "{err}");
    }

    #[test]
    fn bad_values_are_refused() {
        for (text, needle) in [
            ("max_users = 0", "max_users"),
            ("max_users = 65", "max_users"),
            ("name = \"\"", "name"),
            ("time_of_day = 25", "time_of_day"),
            ("ground = \"Deep Water\"", "ground"),
            ("spawn_point = [0, 0, 500]", "spawn_point"),
            ("[bounds]\nmin = [1,1,1]\nmax = [0,2,2]", "bounds"),
            ("password_hash = \"plain\"", "password_hash"),
            ("default_role = \"admin\"", "default_role"),
            ("[limits]\nmin_scale = 10\nmax_scale = 1", "min_scale"),
            ("[limits]\nwalk_speed = -1", "walk_speed"),
        ] {
            let err = format!("{:#}", WorldFile::parse(text).unwrap_err());
            assert!(err.contains(needle), "{text:?} gave {err}");
        }
    }

    #[test]
    fn written_file_parses_back() {
        let mut file = WorldFile {
            name: "Quote \"me\"".into(),
            description: "line\nbreak".into(),
            ..WorldFile::default()
        };
        file.limits.max_objects = Some(7);
        file.password_hash = Some(crate::auth::hash_password("pw").unwrap());
        let text = file.to_toml();
        assert!(text.starts_with("# Tinyverse"));
        assert_eq!(WorldFile::parse(&text).unwrap(), file);
    }

    #[test]
    fn password_hash_edit_keeps_the_rest() {
        let original = WorldFile::default().to_toml();
        let hash = crate::auth::hash_password("secret").unwrap();
        let with = edit_password_hash(&original, Some(&hash));
        let parsed = WorldFile::parse(&with).unwrap();
        assert_eq!(parsed.password_hash.as_deref(), Some(hash.as_str()));
        assert!(with.starts_with("# Tinyverse"));

        let other = crate::auth::hash_password("other").unwrap();
        let replaced = edit_password_hash(&with, Some(&other));
        assert_eq!(replaced.matches("password_hash").count(), 1);
        assert_eq!(WorldFile::parse(&replaced).unwrap().password_hash, Some(other));

        let cleared = edit_password_hash(&replaced, None);
        assert_eq!(WorldFile::parse(&cleared).unwrap(), WorldFile::default());
        assert!(!cleared.contains("password_hash ="));
    }
}
