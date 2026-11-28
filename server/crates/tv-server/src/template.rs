//! World templates: ready-made places a new world can start from.
//!
//! A template is a TOML file under `crates/tv-server/templates/`, compiled
//! into the binary. It holds a title and description (for `template list`),
//! a `[world]` table with defaults for `world.toml`, and a list of
//! `[[object]]` primitives that `init --template` creates, owned by the
//! world's owner. Objects are checked by the same rules `tv-world` enforces
//! (by actually creating them in a scratch world), so a template that loads
//! is a template the server would have accepted object by object.
//!
//! The format of one object (every key but `shape` and the position is
//! optional):
//!
//! ```toml
//! [[object]]
//! name = "table"                 # up to 64 characters
//! shape = "box"                  # box, sphere, cylinder, cone, wedge, plane
//! position = [0, 0.75, 2]        # centre, metres; or `at = [[x, y, z], ...]`
//! rotation = [0, 90, 0]          # Euler degrees, applied z, x, y (Unity's order)
//! scale = [1.6, 0.05, 0.9]       # metres per axis, 0.01..64
//! color = "#8a6a4a"              # sRGB hex, optionally with alpha (#rrggbbaa)
//! material = "matte"             # matte, glossy, metal, glass, emissive
//! solid = true                   # avatars collide (default true)
//! locked = true                  # only the owner may edit (default true)
//! repeat = 5                     # copies, each `step` further on (default 1)
//! step = [2, 0, 0]
//! ```
//!
//! `at` lists several positions for one otherwise identical object (trees,
//! posts); `repeat` and `step` apply to each of them.

use anyhow::{Context, bail};
use glam::{EulerRot, Quat, Vec3};
use serde::Deserialize;
use tv_world::{
    Appearance, ClientKind, Color, Command, EntityId, JoinRequest, Material, Object, ObjectSpec,
    Role, Shape, UserId, World, WorldConfig,
};

use crate::config::{Bounds, WorldFile};

/// The template `init` uses when none is named: today's empty plane.
pub const DEFAULT_TEMPLATE: &str = "empty";

/// Most objects one template may hold. Far below the world's own limit, so a
/// templated world leaves plenty of room to build.
pub const MAX_TEMPLATE_OBJECTS: usize = 1000;

/// The built-in templates: file name (the template's name) and contents.
const BUILT_IN: &[(&str, &str)] = &[
    ("empty", include_str!("../templates/empty.toml")),
    ("room", include_str!("../templates/room.toml")),
    ("house", include_str!("../templates/house.toml")),
    ("town", include_str!("../templates/town.toml")),
    ("island", include_str!("../templates/island.toml")),
];

/// A loaded, validated template.
#[derive(Clone, Debug, PartialEq)]
pub struct Template {
    /// The name `--template` takes, such as `island`.
    pub name: String,
    /// A short display name, such as "Island".
    pub title: String,
    /// One or two sentences for people choosing a template.
    pub description: String,
    /// `world.toml` as this template starts it (name and description are
    /// defaults that `init --name` and `--description` override).
    pub world: WorldFile,
    /// The objects, normalised the way the world stores them.
    pub objects: Vec<ObjectSpec>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct TemplateFile {
    title: String,
    description: String,
    #[serde(default)]
    world: WorldDefaults,
    #[serde(default, rename = "object")]
    objects: Vec<ObjectEntry>,
}

/// The keys of `world.toml` a template may set.
#[derive(Debug, Default, Deserialize)]
#[serde(deny_unknown_fields)]
struct WorldDefaults {
    name: Option<String>,
    description: Option<String>,
    spawn_point: Option<[f32; 3]>,
    ground_size: Option<f32>,
    ground: Option<String>,
    sky: Option<String>,
    time_of_day: Option<f32>,
    bounds: Option<Bounds>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct ObjectEntry {
    #[serde(default)]
    name: String,
    shape: String,
    position: Option<[f32; 3]>,
    at: Option<Vec<[f32; 3]>>,
    #[serde(default)]
    rotation: [f32; 3],
    #[serde(default = "one")]
    scale: [f32; 3],
    #[serde(default = "white")]
    color: String,
    #[serde(default = "matte")]
    material: String,
    #[serde(default = "yes")]
    solid: bool,
    #[serde(default = "yes")]
    locked: bool,
    #[serde(default = "single")]
    repeat: u32,
    #[serde(default)]
    step: [f32; 3],
}

fn one() -> [f32; 3] {
    [1.0; 3]
}
fn white() -> String {
    "#ffffff".to_owned()
}
fn matte() -> String {
    "matte".to_owned()
}
fn yes() -> bool {
    true
}
fn single() -> u32 {
    1
}

/// The names of the built-in templates, in the order `template list` shows them.
pub fn names() -> Vec<&'static str> {
    BUILT_IN.iter().map(|(name, _)| *name).collect()
}

/// Every built-in template, loaded and validated.
pub fn all() -> anyhow::Result<Vec<Template>> {
    BUILT_IN.iter().map(|(name, text)| parse(name, text)).collect()
}

/// The built-in template called `name`.
pub fn find(name: &str) -> anyhow::Result<Template> {
    match BUILT_IN.iter().find(|(n, _)| *n == name) {
        Some((n, text)) => parse(n, text),
        None => bail!("there is no template {name:?}; choose one of: {}", names().join(", ")),
    }
}

/// Parses and validates one template.
pub fn parse(name: &str, text: &str) -> anyhow::Result<Template> {
    let file: TemplateFile =
        toml::from_str(text).map_err(|e| anyhow::anyhow!("template {name}: {e}"))?;
    load(name, file).with_context(|| format!("template {name}"))
}

fn load(name: &str, file: TemplateFile) -> anyhow::Result<Template> {
    if file.title.trim().is_empty() {
        bail!("`title` must not be empty");
    }
    let d = file.world;
    let mut world = WorldFile::default();
    if let Some(v) = d.name {
        world.name = v;
    }
    if let Some(v) = d.description {
        world.description = v;
    }
    if let Some(v) = d.spawn_point {
        world.spawn_point = v;
    }
    if let Some(v) = d.ground_size {
        world.ground_size = v;
    }
    if let Some(v) = d.ground {
        world.ground = v;
    }
    if let Some(v) = d.sky {
        world.sky = v;
    }
    if let Some(v) = d.time_of_day {
        world.time_of_day = v;
    }
    if let Some(v) = d.bounds {
        world.bounds = v;
    }
    world.validate().context("[world]")?;

    let mut specs = Vec::new();
    for (i, entry) in file.objects.into_iter().enumerate() {
        let label = if entry.name.is_empty() {
            format!("object {}", i + 1)
        } else {
            format!("object {} ({:?})", i + 1, entry.name)
        };
        expand(entry, &mut specs).with_context(|| label.clone())?;
        if specs.len() > MAX_TEMPLATE_OBJECTS {
            bail!("more than {MAX_TEMPLATE_OBJECTS} objects");
        }
    }

    // The world's own rules, applied by creating every object in a scratch world.
    let config = world.world_config();
    if specs.len() >= config.limits.max_objects {
        bail!(
            "{} objects do not fit the world's limit of {}",
            specs.len(),
            config.limits.max_objects
        );
    }
    let owner = UserId::from("template-check");
    let (objects, _) = seed_specs(&config, &owner, &specs)?;
    Ok(Template {
        name: name.to_owned(),
        title: file.title.trim().to_owned(),
        description: file.description.trim().to_owned(),
        world,
        objects: objects.into_iter().map(|o| o.spec).collect(),
    })
}

fn expand(entry: ObjectEntry, out: &mut Vec<ObjectSpec>) -> anyhow::Result<()> {
    let positions = match (entry.position, entry.at) {
        (Some(p), None) => vec![p],
        (None, Some(list)) if !list.is_empty() => list,
        (None, Some(_)) => bail!("`at` is empty"),
        (Some(_), Some(_)) => bail!("give either `position` or `at`, not both"),
        (None, None) => bail!("`position` (or `at`) is missing"),
    };
    if entry.repeat == 0 {
        bail!("`repeat` must be at least 1");
    }
    let [rx, ry, rz] = entry.rotation.map(f32::to_radians);
    let template = ObjectSpec {
        name: entry.name,
        shape: shape(&entry.shape)?,
        position: Vec3::ZERO,
        // Unity's Euler order: z first, then x, then y.
        rotation: Quat::from_euler(EulerRot::YXZ, ry, rx, rz),
        scale: Vec3::from(entry.scale),
        color: color(&entry.color)?,
        material: material(&entry.material)?,
        solid: entry.solid,
        locked: entry.locked,
    };
    let step = Vec3::from(entry.step);
    for p in positions {
        for n in 0..entry.repeat {
            out.push(ObjectSpec { position: Vec3::from(p) + step * n as f32, ..template.clone() });
            if out.len() > MAX_TEMPLATE_OBJECTS {
                return Ok(());
            }
        }
    }
    Ok(())
}

fn shape(name: &str) -> anyhow::Result<Shape> {
    Ok(match name {
        "box" => Shape::Box,
        "sphere" => Shape::Sphere,
        "cylinder" => Shape::Cylinder,
        "cone" => Shape::Cone,
        "wedge" => Shape::Wedge,
        "plane" => Shape::Plane,
        other => {
            bail!("unknown shape {other:?} (box, sphere, cylinder, cone, wedge, plane)")
        }
    })
}

fn material(name: &str) -> anyhow::Result<Material> {
    Ok(match name {
        "matte" => Material::Matte,
        "glossy" => Material::Glossy,
        "metal" => Material::Metal,
        "glass" => Material::Glass,
        "emissive" => Material::Emissive,
        other => bail!("unknown material {other:?} (matte, glossy, metal, glass, emissive)"),
    })
}

/// `#rrggbb` or `#rrggbbaa`, sRGB.
fn color(text: &str) -> anyhow::Result<Color> {
    let hex = text.strip_prefix('#').unwrap_or(text);
    if !(hex.len() == 6 || hex.len() == 8) || !hex.bytes().all(|b| b.is_ascii_hexdigit()) {
        bail!("colour {text:?} is not #rrggbb or #rrggbbaa");
    }
    let channel = |i: usize| -> f32 {
        f32::from(u8::from_str_radix(&hex[i..i + 2], 16).unwrap_or(255)) / 255.0
    };
    let a = if hex.len() == 8 { channel(6) } else { 1.0 };
    Ok(Color::new(channel(0), channel(2), channel(4), a))
}

/// Creates `specs` in a fresh world with `config`, as `owner`, by the world's
/// own rules. Returns the objects and the first free entity id.
fn seed_specs(
    config: &WorldConfig,
    owner: &UserId,
    specs: &[ObjectSpec],
) -> anyhow::Result<(Vec<Object>, EntityId)> {
    let mut world = World::new(config.clone(), Vec::new(), 1);
    let (avatar, _) = world
        .join(
            JoinRequest {
                user_id: owner.clone(),
                name: "owner".to_owned(),
                kind: ClientKind::Human,
                role: Role::Owner,
                appearance: Appearance::default(),
            },
            0,
        )
        .map_err(|e| anyhow::anyhow!("{e}"))?;
    for (i, spec) in specs.iter().enumerate() {
        world.apply(avatar, Command::ObjectCreate(spec.clone()), 0).map_err(|e| {
            let name = if spec.name.is_empty() { "unnamed" } else { spec.name.as_str() };
            anyhow::anyhow!("object {} after expansion ({name}): {e}", i + 1)
        })?;
    }
    let objects = world.objects().into_iter().cloned().collect();
    Ok((objects, world.next_entity_id()))
}

impl Template {
    /// The template's objects for a new world with `config`, owned by
    /// `owner`, and the first entity id after them.
    pub fn seed(
        &self,
        config: &WorldConfig,
        owner: &UserId,
    ) -> anyhow::Result<(Vec<Object>, EntityId)> {
        seed_specs(config, owner, &self.objects)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_built_in_template_loads_and_validates() {
        let all = all().unwrap();
        assert_eq!(all.len(), BUILT_IN.len());
        for t in &all {
            assert!(!t.description.is_empty(), "{} has no description", t.name);
            let config = t.world.world_config();
            assert!(
                t.objects.len() * 10 <= config.limits.max_objects,
                "{} has {} objects; keep templates well under the limit of {}",
                t.name,
                t.objects.len(),
                config.limits.max_objects
            );
            let (objects, next) = t.seed(&config, &UserId::from("owner")).unwrap();
            assert_eq!(objects.len(), t.objects.len());
            assert!(objects.iter().all(|o| o.owner.as_str() == "owner"));
            assert!(
                objects.iter().all(|o| o.spec.locked),
                "{}: template objects are locked",
                t.name
            );
            assert!(next > objects.iter().map(|o| o.id).max().unwrap_or(0));
            for o in &objects {
                assert!(!o.spec.name.is_empty(), "{}: every object has a name", t.name);
            }
        }
    }

    #[test]
    fn empty_is_the_default_and_changes_nothing() {
        assert_eq!(DEFAULT_TEMPLATE, "empty");
        let t = find("empty").unwrap();
        assert!(t.objects.is_empty());
        assert_eq!(t.world, WorldFile::default());
    }

    #[test]
    fn places_have_things_in_them() {
        for name in ["room", "house", "town", "island"] {
            let t = find(name).unwrap();
            assert!(t.objects.len() >= 20, "{name}: only {} objects", t.objects.len());
            // The spawn point is on the ground and not inside a solid object.
            let spawn = Vec3::from(t.world.spawn_point);
            assert!(spawn.y.abs() < 0.05, "{name}: spawn at height {}", spawn.y);
            for o in t.objects.iter().filter(|o| o.solid) {
                let local = o.rotation.inverse() * (spawn + Vec3::Y * 0.9 - o.position);
                let half = o.scale * 0.5 + Vec3::splat(0.3);
                assert!(
                    !(local.abs().cmplt(half).all()),
                    "{name}: the spawn point is inside {:?}",
                    o.name
                );
            }
        }
    }

    #[test]
    fn unknown_template_names_the_choices() {
        let err = find("castle").unwrap_err().to_string();
        assert!(err.contains("castle") && err.contains("island"), "{err}");
    }

    fn one_object(object: &str) -> anyhow::Result<Template> {
        parse("t", &format!("title = \"T\"\ndescription = \"d\"\n[[object]]\n{object}\n"))
    }

    #[test]
    fn object_keys_are_parsed() {
        let t = one_object(
            "name = \"post\"\nshape = \"cylinder\"\nat = [[0, 0.5, 0], [4, 0.5, 0]]\n\
             repeat = 3\nstep = [0, 0, 2]\nrotation = [0, 90, 0]\nscale = [0.1, 1, 0.1]\n\
             color = \"#ff800080\"\nmaterial = \"glass\"\nsolid = false",
        )
        .unwrap();
        assert_eq!(t.objects.len(), 6);
        let o = &t.objects[2];
        assert_eq!(o.position, Vec3::new(0.0, 0.5, 4.0));
        assert_eq!(t.objects[3].position, Vec3::new(4.0, 0.5, 0.0));
        assert_eq!(o.shape, Shape::Cylinder);
        assert_eq!(o.material, Material::Glass);
        assert!(!o.solid && o.locked);
        assert!((o.color.r - 1.0).abs() < 1e-6 && (o.color.a - 128.0 / 255.0).abs() < 1e-6);
        // 90 degrees about y turns +z into +x.
        assert!((o.rotation * Vec3::Z - Vec3::X).length() < 1e-5);
    }

    #[test]
    fn euler_order_matches_unity() {
        // Unity applies z, then x, then y: Euler(90, 90, 0) maps +y to +x...
        let t = one_object(
            "name = \"w\"\nshape = \"box\"\nposition = [0, 1, 0]\nrotation = [90, 90, 0]",
        )
        .unwrap();
        let q = t.objects[0].rotation;
        assert!((q * Vec3::Y - Vec3::X).length() < 1e-5, "{:?}", q * Vec3::Y);
        // ...and +z to -y.
        assert!((q * Vec3::Z - Vec3::NEG_Y).length() < 1e-5, "{:?}", q * Vec3::Z);
    }

    #[test]
    fn bad_templates_are_refused_with_a_reason() {
        for (object, needle) in [
            ("shape = \"torus\"\nposition = [0, 0, 0]", "torus"),
            ("shape = \"box\"", "position"),
            ("shape = \"box\"\nposition = [0, 0, 0]\nat = [[1, 1, 1]]", "either"),
            ("shape = \"box\"\nposition = [0, 0, 0]\nscale = [100, 1, 1]", "scale"),
            ("shape = \"box\"\nposition = [0, 0, 500]", "outside"),
            ("shape = \"box\"\nposition = [0, 0, 0]\ncolor = \"red\"", "colour"),
            ("shape = \"box\"\nposition = [0, 0, 0]\nmaterial = \"wood\"", "wood"),
            ("shape = \"box\"\nposition = [0, 0, 0]\nrepeat = 0", "repeat"),
            ("shape = \"box\"\nposition = [0, 0, 0]\nrepeat = 5000", "1000"),
            ("shape = \"box\"\nposition = [0, 0, 0]\ncolour = \"#fff\"", "colour"),
        ] {
            let err = format!("{:#}", one_object(object).unwrap_err());
            assert!(err.contains(needle), "{object:?} gave {err}");
        }
        let long = format!("name = \"{}\"\nshape = \"box\"\nposition = [0, 0, 0]", "x".repeat(65));
        assert!(format!("{:#}", one_object(&long).unwrap_err()).contains("64"));
        let err =
            parse("t", "title = \"T\"\ndescription = \"\"\n[world]\nspawn_point = [0, 0, 999]\n")
                .unwrap_err();
        assert!(format!("{err:#}").contains("spawn_point"), "{err:#}");
        assert!(parse("t", "title = \"\"\ndescription = \"d\"\n").is_err());
    }
}
