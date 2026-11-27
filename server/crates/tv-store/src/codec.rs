//! Conversions between rows and world types.
//!
//! Enums are stored as stable lowercase names (the same words the protocol
//! and the CLI use), never as Rust discriminants, so reordering a Rust enum
//! cannot change what a stored row means.

use rusqlite::Row;
use tv_world::{ClientKind, Color, Material, Object, ObjectSpec, Quat, Role, Shape, Vec3};

use crate::{StoreError, UserRecord};

pub(crate) fn shape_name(shape: Shape) -> &'static str {
    match shape {
        Shape::Box => "box",
        Shape::Sphere => "sphere",
        Shape::Cylinder => "cylinder",
        Shape::Cone => "cone",
        Shape::Wedge => "wedge",
        Shape::Plane => "plane",
    }
}

fn parse_shape(name: &str) -> Option<Shape> {
    Some(match name {
        "box" => Shape::Box,
        "sphere" => Shape::Sphere,
        "cylinder" => Shape::Cylinder,
        "cone" => Shape::Cone,
        "wedge" => Shape::Wedge,
        "plane" => Shape::Plane,
        _ => return None,
    })
}

pub(crate) fn material_name(material: Material) -> &'static str {
    match material {
        Material::Matte => "matte",
        Material::Glossy => "glossy",
        Material::Metal => "metal",
        Material::Glass => "glass",
        Material::Emissive => "emissive",
    }
}

fn parse_material(name: &str) -> Option<Material> {
    Some(match name {
        "matte" => Material::Matte,
        "glossy" => Material::Glossy,
        "metal" => Material::Metal,
        "glass" => Material::Glass,
        "emissive" => Material::Emissive,
        _ => return None,
    })
}

pub(crate) fn role_name(role: Role) -> &'static str {
    match role {
        Role::Visitor => "visitor",
        Role::Builder => "builder",
        Role::Owner => "owner",
    }
}

fn parse_role(name: &str) -> Option<Role> {
    Some(match name {
        "visitor" => Role::Visitor,
        "builder" => Role::Builder,
        "owner" => Role::Owner,
        _ => return None,
    })
}

pub(crate) fn kind_name(kind: ClientKind) -> &'static str {
    match kind {
        ClientKind::Human => "human",
        ClientKind::Agent => "agent",
    }
}

fn parse_kind(name: &str) -> Option<ClientKind> {
    Some(match name {
        "human" => ClientKind::Human,
        "agent" => ClientKind::Agent,
        _ => return None,
    })
}

/// Columns read by [`object_from_row`], in order.
pub(crate) const OBJECT_COLUMNS: &str = "id, owner, name, shape, \
     pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w, scale_x, scale_y, scale_z, \
     color_r, color_g, color_b, color_a, material, solid, locked";

/// Decodes one `objects` row selected with [`OBJECT_COLUMNS`].
pub(crate) fn object_from_row(row: &Row<'_>) -> Result<Object, StoreError> {
    let raw_id: i64 =
        row.get(0).map_err(|e| StoreError::Corrupt(format!("object with unreadable id: {e}")))?;
    let corrupt = |what: String| StoreError::Corrupt(format!("object {raw_id}: {what}"));
    let id = u64::try_from(raw_id)
        .ok()
        .filter(|&id| id > 0)
        .ok_or_else(|| corrupt("id is not positive".to_owned()))?;
    let text = |i: usize| row.get::<_, String>(i).map_err(|e| corrupt(e.to_string()));
    let real =
        |i: usize| row.get::<_, f64>(i).map(|v| v as f32).map_err(|e| corrupt(e.to_string()));
    let flag = |i: usize| row.get::<_, bool>(i).map_err(|e| corrupt(e.to_string()));

    let shape = text(3)?;
    let shape = parse_shape(&shape).ok_or_else(|| corrupt(format!("unknown shape {shape:?}")))?;
    let material = text(18)?;
    let material = parse_material(&material)
        .ok_or_else(|| corrupt(format!("unknown material {material:?}")))?;

    Ok(Object {
        id,
        owner: text(1)?.into(),
        spec: ObjectSpec {
            name: text(2)?,
            shape,
            position: Vec3::new(real(4)?, real(5)?, real(6)?),
            rotation: Quat::from_xyzw(real(7)?, real(8)?, real(9)?, real(10)?),
            scale: Vec3::new(real(11)?, real(12)?, real(13)?),
            color: Color::new(real(14)?, real(15)?, real(16)?, real(17)?),
            material,
            solid: flag(19)?,
            locked: flag(20)?,
        },
    })
}

/// Columns read by [`user_from_row`], in order.
pub(crate) const USER_COLUMNS: &str =
    "id, name, kind, role, created_ms, last_seen_ms, banned, token_sha256 IS NOT NULL";

/// Decodes one `users` row selected with [`USER_COLUMNS`].
pub(crate) fn user_from_row(row: &Row<'_>) -> Result<UserRecord, StoreError> {
    let id: String =
        row.get(0).map_err(|e| StoreError::Corrupt(format!("user with unreadable id: {e}")))?;
    let corrupt = |what: String| StoreError::Corrupt(format!("user {id}: {what}"));
    let kind: String = row.get(2).map_err(|e| corrupt(e.to_string()))?;
    let kind = parse_kind(&kind).ok_or_else(|| corrupt(format!("unknown kind {kind:?}")))?;
    let role: String = row.get(3).map_err(|e| corrupt(e.to_string()))?;
    let role = parse_role(&role).ok_or_else(|| corrupt(format!("unknown role {role:?}")))?;
    Ok(UserRecord {
        name: row.get(1).map_err(|e| corrupt(e.to_string()))?,
        kind,
        role,
        created_ms: row.get(4).map_err(|e| corrupt(e.to_string()))?,
        last_seen_ms: row.get(5).map_err(|e| corrupt(e.to_string()))?,
        banned: row.get(6).map_err(|e| corrupt(e.to_string()))?,
        has_token: row.get(7).map_err(|e| corrupt(e.to_string()))?,
        id: id.into(),
    })
}
