//! `Observe`: what is around an avatar, and a plain-text summary of it for a
//! language model.
//!
//! Compass convention for the summary: +Z is north and +X is east, so yaw 0
//! faces north and yaw 90 faces east.

use std::fmt::Write as _;

use glam::Vec3;

use super::World;
use super::movement::heading_of;
use super::validate::{self, invalid};
use crate::error::WorldError;
use crate::event::{Audience, Event, EventKind};
use crate::types::{Avatar, ClientKind, Color, EntityId, Object, Observation, Seen, Shape};

/// Things closer than this horizontally are described as above, below or here
/// rather than by bearing.
const OVERHEAD_M: f32 = 0.5;

/// Degrees relative to `yaw` at which `to` lies as seen from `from`:
/// 0 = straight ahead, positive = to the right, -180..=180. Something directly
/// above or below is straight ahead.
pub(super) fn bearing(from: Vec3, yaw: f32, to: Vec3) -> f32 {
    match heading_of(to - from) {
        Some(heading) => wrap_degrees(heading - yaw),
        None => 0.0,
    }
}

fn wrap_degrees(angle: f32) -> f32 {
    let wrapped = (angle + 180.0).rem_euclid(360.0) - 180.0;
    if wrapped <= -180.0 { 180.0 } else { wrapped }
}

/// Sorts `seen` nearest first (ties by id), cuts it to `max` and reports
/// whether anything was cut.
fn nearest<T>(mut seen: Vec<(EntityId, Seen<T>)>, max: usize) -> (Vec<Seen<T>>, bool) {
    seen.sort_by(|a, b| a.1.distance.total_cmp(&b.1.distance).then(a.0.cmp(&b.0)));
    let truncated = seen.len() > max;
    seen.truncate(max);
    (seen.into_iter().map(|(_, s)| s).collect(), truncated)
}

impl World {
    pub(super) fn observe(&self, actor: EntityId, radius: f32) -> Result<Vec<Event>, WorldError> {
        validate::finite(radius, "radius")?;
        if radius < 0.0 {
            return Err(invalid("the radius must not be negative"));
        }
        let limits = &self.config.limits;
        let radius = if radius == 0.0 { limits.default_observe_radius } else { radius }
            .min(limits.max_observe_radius);

        let observer = self.actor(actor)?.clone();
        let origin = observer.transform.position;
        let yaw = observer.transform.yaw;
        let seen = |position: Vec3| {
            let distance = position.distance(origin);
            (distance <= radius).then(|| (distance, bearing(origin, yaw, position)))
        };

        let avatars = self
            .avatars
            .values()
            .map(|p| &p.avatar)
            .filter(|a| a.id != actor)
            .filter_map(|a| {
                seen(a.transform.position)
                    .map(|(distance, bearing)| (a.id, Seen { item: a.clone(), distance, bearing }))
            })
            .collect();
        // Measure everything first and clone only what will be listed.
        let mut object_refs: Vec<(EntityId, f32, f32, &Object)> = self
            .objects
            .values()
            .filter_map(|o| seen(o.spec.position).map(|(d, b)| (o.id, d, b, o)))
            .collect();
        object_refs.sort_by(|a, b| a.1.total_cmp(&b.1).then(a.0.cmp(&b.0)));
        let objects_truncated = object_refs.len() > limits.max_observed;
        object_refs.truncate(limits.max_observed);
        let objects: Vec<Seen<Object>> = object_refs
            .into_iter()
            .map(|(_, distance, bearing, o)| Seen { item: o.clone(), distance, bearing })
            .collect();

        let (avatars, avatars_truncated) = nearest(avatars, limits.max_observed);
        let truncated = avatars_truncated || objects_truncated;
        let summary = summarize(&observer, &avatars, &objects, truncated, radius);
        Ok(vec![Event::new(
            Audience::Only(actor),
            EventKind::Observation(Box::new(Observation {
                observer,
                avatars,
                objects,
                summary,
                truncated,
                radius,
            })),
        )])
    }
}

fn summarize(
    observer: &Avatar,
    avatars: &[Seen<Avatar>],
    objects: &[Seen<Object>],
    truncated: bool,
    radius: f32,
) -> String {
    let t = &observer.transform;
    let mut out = format!(
        "You are {} at ({}, {}, {}), facing {}.",
        clean(&observer.name),
        one_decimal(t.position.x),
        one_decimal(t.position.y),
        one_decimal(t.position.z),
        compass(t.yaw)
    );
    let r = compact(radius);

    if avatars.is_empty() {
        let _ = write!(out, "\nNo one else within {r} m.");
    } else {
        let _ = write!(out, "\nPeople within {r} m:");
        for (i, seen) in avatars.iter().enumerate() {
            let a = &seen.item;
            let kind = match a.kind {
                ClientKind::Human => "human",
                ClientKind::Agent => "agent",
            };
            let _ = write!(
                out,
                "{} {} ({kind}), {} m {}",
                if i == 0 { "" } else { ";" },
                clean(&a.name),
                one_decimal(seen.distance),
                direction(observer, a.transform.position, seen.bearing)
            );
        }
        out.push('.');
    }

    if objects.is_empty() {
        let _ = write!(out, "\nNo objects within {r} m.");
    } else {
        let _ = write!(out, "\nObjects within {r} m:");
        for (i, seen) in objects.iter().enumerate() {
            let o = &seen.item;
            let s = &o.spec;
            let _ = write!(
                out,
                "{} {} {}",
                if i == 0 { "" } else { ";" },
                color_words(s.color),
                shape_word(s.shape)
            );
            if !s.name.is_empty() {
                let _ = write!(out, " \"{}\"", clean(&s.name));
            }
            let _ = write!(
                out,
                " {}x{}x{} m, {} m {}, id {}",
                compact(s.scale.x),
                compact(s.scale.y),
                compact(s.scale.z),
                one_decimal(seen.distance),
                direction(observer, s.position, seen.bearing),
                o.id
            );
        }
        out.push('.');
    }

    if truncated {
        out.push_str("\nMore is nearby than listed.");
    }
    out
}

/// One of eight compass points for a yaw (0 = north = +Z, 90 = east = +X).
fn compass(yaw: f32) -> &'static str {
    const POINTS: [&str; 8] =
        ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
    let index = (yaw.rem_euclid(360.0) / 45.0).round() as usize % 8;
    POINTS[index]
}

/// A rough direction relative to the observer, from the bearing, or above,
/// below or here for things (almost) straight up or down.
fn direction(observer: &Avatar, position: Vec3, bearing: f32) -> &'static str {
    let delta = position - observer.transform.position;
    if delta.x.hypot(delta.z) < OVERHEAD_M {
        return if delta.y >= OVERHEAD_M {
            "above"
        } else if delta.y <= -OVERHEAD_M {
            "below"
        } else {
            "here"
        };
    }
    const SECTORS: [&str; 8] = [
        "ahead",
        "ahead-right",
        "right",
        "behind-right",
        "behind",
        "behind-left",
        "left",
        "ahead-left",
    ];
    let index = (bearing.rem_euclid(360.0) / 45.0).round() as usize % 8;
    SECTORS[index]
}

fn shape_word(shape: Shape) -> &'static str {
    match shape {
        Shape::Box => "box",
        Shape::Sphere => "sphere",
        Shape::Cylinder => "cylinder",
        Shape::Cone => "cone",
        Shape::Wedge => "wedge",
        Shape::Plane => "plane",
    }
}

/// A colour in one or two plain words.
fn color_words(color: Color) -> String {
    let (r, g, b) = (color.r, color.g, color.b);
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let chroma = max - min;
    let saturation = if max > 0.0 { chroma / max } else { 0.0 };

    let name = if max < 0.15 {
        "black"
    } else if saturation < 0.2 {
        if max > 0.85 { "white" } else { "grey" }
    } else {
        let hue = if max == r {
            60.0 * ((g - b) / chroma).rem_euclid(6.0)
        } else if max == g {
            60.0 * ((b - r) / chroma + 2.0)
        } else {
            60.0 * ((r - g) / chroma + 4.0)
        };
        match hue {
            h if h < 15.0 => "red",
            h if h < 45.0 => {
                if max < 0.6 {
                    "brown"
                } else {
                    "orange"
                }
            }
            h if h < 70.0 => "yellow",
            h if h < 165.0 => "green",
            h if h < 195.0 => "cyan",
            h if h < 255.0 => "blue",
            h if h < 290.0 => "purple",
            h if h < 335.0 => "pink",
            _ => "red",
        }
    };
    if color.a < 0.5 { format!("translucent {name}") } else { name.to_owned() }
}

/// One decimal place, without a negative zero.
fn one_decimal(value: f32) -> String {
    let rounded = (value * 10.0).round() / 10.0;
    format!("{:.1}", if rounded == 0.0 { 0.0 } else { rounded })
}

/// At most two decimals, trailing zeros removed.
fn compact(value: f32) -> String {
    let text = format!("{value:.2}");
    let text = text.trim_end_matches('0').trim_end_matches('.');
    if text == "-0" { "0".to_owned() } else { text.to_owned() }
}

/// A name made safe to embed in one line of the summary.
fn clean(text: &str) -> String {
    text.chars().map(|c| if c.is_control() { ' ' } else { c }).collect()
}
