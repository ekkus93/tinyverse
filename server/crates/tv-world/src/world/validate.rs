//! Argument checks and normalisation shared by the command handlers.

use glam::{Quat, Vec3};

use crate::error::WorldError;
use crate::types::{Appearance, Color, ObjectSpec, Shape, WorldConfig};

/// Longest animation hint, body preset or hair style kept, in characters.
pub(super) const MAX_TAG_CHARS: usize = 32;

pub(super) fn invalid(message: impl Into<String>) -> WorldError {
    WorldError::InvalidArgument(message.into())
}

pub(super) fn finite(value: f32, what: &str) -> Result<(), WorldError> {
    if value.is_finite() { Ok(()) } else { Err(invalid(format!("{what} must be a finite number"))) }
}

pub(super) fn finite_vec(value: Vec3, what: &str) -> Result<(), WorldError> {
    if value.is_finite() { Ok(()) } else { Err(invalid(format!("{what} must be finite"))) }
}

pub(super) fn finite_quat(value: Quat, what: &str) -> Result<(), WorldError> {
    if value.is_finite() { Ok(()) } else { Err(invalid(format!("{what} must be finite"))) }
}

/// Rejects non-finite channels and clamps the rest into 0..=1.
pub(super) fn color(value: Color, what: &str) -> Result<Color, WorldError> {
    for channel in [value.r, value.g, value.b, value.a] {
        finite(channel, what)?;
    }
    Ok(Color::new(
        value.r.clamp(0.0, 1.0),
        value.g.clamp(0.0, 1.0),
        value.b.clamp(0.0, 1.0),
        value.a.clamp(0.0, 1.0),
    ))
}

/// The first `max` characters of `text`.
pub(super) fn truncate_chars(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

pub(super) fn appearance(value: Appearance) -> Result<Appearance, WorldError> {
    Ok(Appearance {
        body_color: color(value.body_color, "body colour")?,
        body_preset: truncate_chars(&value.body_preset, MAX_TAG_CHARS),
        skin_color: color(value.skin_color, "skin colour")?,
        hair_color: color(value.hair_color, "hair colour")?,
        hair_style: truncate_chars(&value.hair_style, MAX_TAG_CHARS),
        top_color: color(value.top_color, "top colour")?,
        bottom_color: color(value.bottom_color, "bottom colour")?,
    })
}

/// A trimmed display name of 1..=`max` characters.
pub(super) fn display_name(name: &str, max: usize) -> Result<String, WorldError> {
    let name = name.trim();
    let chars = name.chars().count();
    if chars == 0 {
        return Err(invalid("the name must not be empty"));
    }
    if chars > max {
        return Err(invalid(format!("the name is longer than {max} characters")));
    }
    Ok(name.to_owned())
}

pub(super) fn in_bounds(config: &WorldConfig, point: Vec3) -> bool {
    point.cmpge(config.bounds_min).all() && point.cmple(config.bounds_max).all()
}

pub(super) fn check_bounds(
    config: &WorldConfig,
    point: Vec3,
    what: &str,
) -> Result<(), WorldError> {
    if in_bounds(config, point) {
        Ok(())
    } else {
        Err(WorldError::OutOfBounds(format!(
            "{what} ({}, {}, {}) is outside the world",
            point.x, point.y, point.z
        )))
    }
}

/// A unit quaternion; a (near) zero-length one becomes identity.
pub(super) fn normalize_rotation(rotation: Quat) -> Quat {
    let length = rotation.length();
    if length < 1e-6 { Quat::IDENTITY } else { rotation / length }
}

/// Checks an object spec against the world's rules and returns its
/// normalised form (trimmed name, unit rotation, clamped colour, clamped
/// plane thickness).
pub(super) fn object_spec(
    config: &WorldConfig,
    spec: ObjectSpec,
) -> Result<ObjectSpec, WorldError> {
    let limits = &config.limits;
    finite_vec(spec.position, "position")?;
    finite_quat(spec.rotation, "rotation")?;
    finite_vec(spec.scale, "scale")?;
    let color = color(spec.color, "colour")?;

    let name = spec.name.trim().to_owned();
    if name.chars().count() > limits.max_name_chars {
        return Err(invalid(format!(
            "the object name is longer than {} characters",
            limits.max_name_chars
        )));
    }

    let mut scale = spec.scale;
    if spec.shape == Shape::Plane {
        // A plane has no thickness; its y scale is meaningless, so keep it in
        // range rather than refuse it.
        scale.y = scale.y.clamp(limits.min_scale, limits.max_scale);
    }
    for (axis, value) in [("x", scale.x), ("y", scale.y), ("z", scale.z)] {
        if !(limits.min_scale..=limits.max_scale).contains(&value) {
            return Err(invalid(format!(
                "scale {axis} = {value} is outside {}..{} m",
                limits.min_scale, limits.max_scale
            )));
        }
    }

    check_bounds(config, spec.position, "the position")?;

    Ok(ObjectSpec { name, rotation: normalize_rotation(spec.rotation), scale, color, ..spec })
}
