//! Parsing of the small value syntaxes used on the command line and in
//! `connect`'s slash commands: vectors, rotations, colours and enum names.

use tv_proto::v1::{ClientKind, Color, Material, MoveMode, Quat, Role, Shape, Vec3};

/// Parses a finite number.
pub fn parse_f32(s: &str) -> Result<f32, String> {
    let v: f32 = s.trim().parse().map_err(|_| format!("'{s}' is not a number"))?;
    if v.is_finite() { Ok(v) } else { Err(format!("'{s}' is not a finite number")) }
}

/// Splits `"1,2,3"`, `"1 2 3"` or `"1, 2, 3"` into numbers.
pub fn parse_numbers(s: &str) -> Result<Vec<f32>, String> {
    s.split(|c: char| c == ',' || c.is_whitespace())
        .filter(|p| !p.is_empty())
        .map(parse_f32)
        .collect()
}

/// A point or direction: exactly three numbers.
pub fn parse_vec3(s: &str) -> Result<Vec3, String> {
    match parse_numbers(s)?.as_slice() {
        [x, y, z] => Ok(Vec3 { x: *x, y: *y, z: *z }),
        _ => Err(format!("'{s}' is not a vector; expected three numbers like 1,0,-2.5")),
    }
}

/// A scale: one number (uniform) or three.
pub fn parse_scale(s: &str) -> Result<Vec3, String> {
    match parse_numbers(s)?.as_slice() {
        [v] => Ok(Vec3 { x: *v, y: *v, z: *v }),
        [x, y, z] => Ok(Vec3 { x: *x, y: *y, z: *z }),
        _ => Err(format!("'{s}' is not a scale; expected one number or three like 1,2,0.5")),
    }
}

/// A rotation: four numbers are a quaternion `x,y,z,w`; three numbers are
/// Euler angles in degrees, composed the way Unity's `Quaternion.Euler` does
/// (Z first, then X, then Y).
pub fn parse_rotation(s: &str) -> Result<Quat, String> {
    match parse_numbers(s)?.as_slice() {
        [x, y, z, w] => Ok(Quat { x: *x, y: *y, z: *z, w: *w }),
        [x, y, z] => Ok(euler_degrees(*x, *y, *z)),
        _ => Err(format!(
            "'{s}' is not a rotation; expected a quaternion x,y,z,w or Euler degrees x,y,z"
        )),
    }
}

/// Unity's `Quaternion.Euler(x, y, z)`: q = qy * qx * qz.
pub fn euler_degrees(x: f32, y: f32, z: f32) -> Quat {
    let axis = |ax: f32, ay: f32, az: f32, deg: f32| {
        let (s, c) = (deg.to_radians() / 2.0).sin_cos();
        Quat { x: ax * s, y: ay * s, z: az * s, w: c }
    };
    let qx = axis(1.0, 0.0, 0.0, x);
    let qy = axis(0.0, 1.0, 0.0, y);
    let qz = axis(0.0, 0.0, 1.0, z);
    mul(mul(qy, qx), qz)
}

fn mul(a: Quat, b: Quat) -> Quat {
    Quat {
        w: a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
        x: a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
        y: a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
        z: a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
    }
}

/// sRGB-encoded 8-bit channel to a linear 0..1 value.
pub fn srgb_to_linear(c: u8) -> f32 {
    let c = c as f32 / 255.0;
    if c <= 0.04045 { c / 12.92 } else { ((c + 0.055) / 1.055).powf(2.4) }
}

/// Linear 0..1 value to an sRGB-encoded 8-bit channel.
pub fn linear_to_srgb(v: f32) -> u8 {
    let v = v.clamp(0.0, 1.0);
    let s = if v <= 0.003_130_8 { v * 12.92 } else { 1.055 * v.powf(1.0 / 2.4) - 0.055 };
    (s * 255.0).round() as u8
}

/// A colour. `#rrggbb` and `#rrggbbaa` are sRGB hex as in any colour picker
/// and are converted to the protocol's linear 0..1 values (alpha is not
/// converted). `r,g,b` or `r,g,b,a` are taken as linear 0..1 values as is.
pub fn parse_color(s: &str) -> Result<Color, String> {
    let t = s.trim();
    if let Some(hex) = t.strip_prefix('#') {
        let bad = || format!("'{s}' is not a colour; expected #rrggbb or #rrggbbaa");
        if !(hex.len() == 6 || hex.len() == 8) || !hex.chars().all(|c| c.is_ascii_hexdigit()) {
            return Err(bad());
        }
        let byte = |i: usize| u8::from_str_radix(&hex[i..i + 2], 16).map_err(|_| bad());
        let a = if hex.len() == 8 { byte(6)? as f32 / 255.0 } else { 1.0 };
        return Ok(Color {
            r: srgb_to_linear(byte(0)?),
            g: srgb_to_linear(byte(2)?),
            b: srgb_to_linear(byte(4)?),
            a,
        });
    }
    let n = parse_numbers(t)
        .map_err(|_| format!("'{s}' is not a colour; expected #rrggbb or r,g,b[,a] in 0..1"))?;
    let (r, g, b, a) = match n.as_slice() {
        [r, g, b] => (*r, *g, *b, 1.0),
        [r, g, b, a] => (*r, *g, *b, *a),
        _ => return Err(format!("'{s}' is not a colour; expected #rrggbb or r,g,b[,a] in 0..1")),
    };
    if [r, g, b, a].iter().any(|v| !(0.0..=1.0).contains(v)) {
        return Err(format!("'{s}': colour channels must be within 0..1"));
    }
    Ok(Color { r, g, b, a })
}

/// The `#rrggbb` (or `#rrggbbaa` when not opaque) form of a linear colour.
pub fn color_hex(c: &Color) -> String {
    let rgb = format!(
        "#{:02x}{:02x}{:02x}",
        linear_to_srgb(c.r),
        linear_to_srgb(c.g),
        linear_to_srgb(c.b)
    );
    if (c.a - 1.0).abs() < 0.002 {
        rgb
    } else {
        format!("{rgb}{:02x}", (c.a.clamp(0.0, 1.0) * 255.0).round() as u8)
    }
}

/// Lower-case short name of a protocol enum value: `SHAPE_BOX` -> `box`.
pub fn short_name(full: &str, prefix: &str) -> String {
    full.strip_prefix(prefix).unwrap_or(full).to_ascii_lowercase()
}

fn parse_enum<T>(
    s: &str,
    prefix: &str,
    what: &str,
    from: impl Fn(&str) -> Option<T>,
    choices: &str,
) -> Result<T, String> {
    let upper = s.trim().to_ascii_uppercase();
    let full = if upper.starts_with(prefix) { upper } else { format!("{prefix}{upper}") };
    match from(&full) {
        Some(v) if !full.ends_with("UNSPECIFIED") => Ok(v),
        _ => Err(format!("'{s}' is not a {what}; expected one of {choices}")),
    }
}

pub fn parse_shape(s: &str) -> Result<Shape, String> {
    parse_enum(
        s,
        "SHAPE_",
        "shape",
        Shape::from_str_name,
        "box, sphere, cylinder, cone, wedge, plane",
    )
}

pub fn parse_material(s: &str) -> Result<Material, String> {
    parse_enum(
        s,
        "MATERIAL_",
        "material",
        Material::from_str_name,
        "matte, glossy, metal, glass, emissive",
    )
}

pub fn parse_role(s: &str) -> Result<Role, String> {
    parse_enum(s, "ROLE_", "role", Role::from_str_name, "visitor, builder, owner")
}

pub fn parse_move_mode(s: &str) -> Result<MoveMode, String> {
    parse_enum(s, "MOVE_MODE_", "move mode", MoveMode::from_str_name, "walk, run, fly")
}

pub fn shape_name(v: i32) -> String {
    Shape::try_from(v).map(|s| short_name(s.as_str_name(), "SHAPE_")).unwrap_or(format!("shape{v}"))
}

pub fn material_name(v: i32) -> String {
    Material::try_from(v)
        .map(|s| short_name(s.as_str_name(), "MATERIAL_"))
        .unwrap_or(format!("material{v}"))
}

pub fn role_name(v: i32) -> String {
    Role::try_from(v).map(|s| short_name(s.as_str_name(), "ROLE_")).unwrap_or(format!("role{v}"))
}

pub fn kind_name(v: i32) -> String {
    ClientKind::try_from(v)
        .map(|s| short_name(s.as_str_name(), "CLIENT_KIND_"))
        .unwrap_or(format!("kind{v}"))
}

pub fn mode_name(v: i32) -> String {
    MoveMode::try_from(v)
        .map(|s| short_name(s.as_str_name(), "MOVE_MODE_"))
        .unwrap_or(format!("mode{v}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() < 1e-4
    }

    #[test]
    fn vectors_accept_commas_and_spaces_and_negatives() {
        for s in ["1,-2.5,3", "1 -2.5 3", " 1, -2.5, 3 "] {
            let v = parse_vec3(s).unwrap();
            assert_eq!((v.x, v.y, v.z), (1.0, -2.5, 3.0), "{s}");
        }
        assert!(parse_vec3("1,2").is_err());
        assert!(parse_vec3("1,2,3,4").is_err());
        assert!(parse_vec3("1,x,3").is_err());
        assert!(parse_vec3("1,inf,3").is_err());
        assert!(parse_vec3("NaN,0,0").is_err());
    }

    #[test]
    fn scale_is_uniform_or_per_axis() {
        let s = parse_scale("2").unwrap();
        assert_eq!((s.x, s.y, s.z), (2.0, 2.0, 2.0));
        let s = parse_scale("1,2,0.5").unwrap();
        assert_eq!((s.x, s.y, s.z), (1.0, 2.0, 0.5));
        assert!(parse_scale("1,2").is_err());
    }

    #[test]
    fn rotation_quaternion_or_unity_euler() {
        let q = parse_rotation("0,0.6,0,0.8").unwrap();
        assert_eq!((q.x, q.y, q.z, q.w), (0.0, 0.6, 0.0, 0.8));
        let q = parse_rotation("0,90,0").unwrap();
        assert!(
            close(q.x, 0.0)
                && close(q.y, std::f32::consts::FRAC_1_SQRT_2)
                && close(q.z, 0.0)
                && close(q.w, std::f32::consts::FRAC_1_SQRT_2)
        );
        let q = parse_rotation("0,0,0").unwrap();
        assert!(close(q.w, 1.0) && close(q.x, 0.0));
        // Unity: Quaternion.Euler(90, 90, 0) == (0.5, 0.5, -0.5, 0.5).
        let q = euler_degrees(90.0, 90.0, 0.0);
        assert!(close(q.x, 0.5) && close(q.y, 0.5) && close(q.z, -0.5) && close(q.w, 0.5), "{q:?}");
        assert!(parse_rotation("1,2").is_err());
    }

    #[test]
    fn hex_colours_are_converted_to_linear() {
        let c = parse_color("#ff0000").unwrap();
        assert_eq!((c.r, c.g, c.b, c.a), (1.0, 0.0, 0.0, 1.0));
        let c = parse_color("#808080").unwrap();
        assert!(close(c.r, 0.21586), "{c:?}");
        let c = parse_color("#FFFFFF80").unwrap();
        assert!(close(c.a, 128.0 / 255.0));
        for bad in ["#ff00", "#gg0000", "ff0000", "#ff00000", "red"] {
            assert!(parse_color(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn float_colours_are_linear_and_bounded() {
        let c = parse_color("0.5,0.25,1").unwrap();
        assert_eq!((c.r, c.g, c.b, c.a), (0.5, 0.25, 1.0, 1.0));
        let c = parse_color("0,0,0,0.5").unwrap();
        assert_eq!(c.a, 0.5);
        assert!(parse_color("1.5,0,0").is_err());
        assert!(parse_color("1,0").is_err());
    }

    #[test]
    fn hex_round_trips() {
        for hex in ["#ff8000", "#000000", "#ffffff", "#123456", "#10203040"] {
            assert_eq!(color_hex(&parse_color(hex).unwrap()), hex);
        }
    }

    #[test]
    fn enum_names() {
        assert_eq!(parse_shape("box").unwrap(), Shape::Box);
        assert_eq!(parse_shape("CYLINDER").unwrap(), Shape::Cylinder);
        assert_eq!(parse_shape("SHAPE_WEDGE").unwrap(), Shape::Wedge);
        assert!(parse_shape("unspecified").is_err());
        assert!(parse_shape("cube").is_err());
        assert_eq!(parse_material("glass").unwrap(), Material::Glass);
        assert_eq!(parse_role("owner").unwrap(), Role::Owner);
        assert_eq!(parse_move_mode("fly").unwrap(), MoveMode::Fly);
        assert_eq!(shape_name(Shape::Plane as i32), "plane");
        assert_eq!(role_name(99), "role99");
    }
}
