//! Conversions and text formatting shared by the tools: colours, Euler
//! angles, distances, bearings and timestamps.

use tv_proto::v1::{Color, Quat, Vec3};

/// Formats a float with at most two decimals and no trailing zeros.
pub fn num(x: f32) -> String {
    if !x.is_finite() {
        return x.to_string();
    }
    let s = format!("{x:.2}");
    let s = s.trim_end_matches('0').trim_end_matches('.');
    if s == "-0" { "0".to_string() } else { s.to_string() }
}

/// `(x, y, z)` with short numbers.
pub fn vec3(v: Option<&Vec3>) -> String {
    let v = v.copied().unwrap_or_default();
    format!("({}, {}, {})", num(v.x), num(v.y), num(v.z))
}

/// Euclidean distance between two points.
pub fn distance(a: &Vec3, b: &Vec3) -> f32 {
    let (dx, dy, dz) = (b.x - a.x, b.y - a.y, b.z - a.z);
    (dx * dx + dy * dy + dz * dz).sqrt()
}

/// Distance between two points ignoring height.
pub fn distance_xz(a: &Vec3, b: &Vec3) -> f32 {
    let (dx, dz) = (b.x - a.x, b.z - a.z);
    (dx * dx + dz * dz).sqrt()
}

/// Wraps an angle in degrees into -180..180.
pub fn wrap_deg(a: f32) -> f32 {
    let mut a = a % 360.0;
    if a > 180.0 {
        a -= 360.0;
    } else if a <= -180.0 {
        a += 360.0;
    }
    a
}

/// Bearing of `to` as seen from `from` facing `yaw` degrees: 0 straight ahead,
/// positive to the right (clockwise seen from above), -180..180. Matches the
/// convention of `Observation`.
pub fn bearing(from: &Vec3, yaw: f32, to: &Vec3) -> f32 {
    let (dx, dz) = (to.x - from.x, to.z - from.z);
    if dx.abs() < 1e-4 && dz.abs() < 1e-4 {
        return 0.0;
    }
    // Yaw 0 faces +Z and 90 faces +X, so the heading of a direction is atan2(x, z).
    wrap_deg(dx.atan2(dz).to_degrees() - yaw)
}

/// "12.3 m, 40° right" style description of where something is.
pub fn where_is(dist: f32, bearing: f32) -> String {
    let b = bearing.round();
    let dir = if b.abs() <= 10.0 {
        "ahead".to_string()
    } else if b.abs() >= 170.0 {
        "behind".to_string()
    } else if b > 0.0 {
        format!("{}° right", b as i32)
    } else {
        format!("{}° left", (-b) as i32)
    };
    format!("{} m, {dir}", num(dist))
}

/// Converts Euler angles in degrees to a quaternion, using Unity's order:
/// rotate about Z (roll) first, then X (pitch), then Y (yaw), all about the
/// world axes. Equivalent to Unity's `Quaternion.Euler(x, y, z)`. A positive Y
/// angle turns clockwise seen from above (+Z towards +X), like avatar yaw.
pub fn euler_to_quat(x_deg: f32, y_deg: f32, z_deg: f32) -> Quat {
    let half = |d: f32| (d.to_radians() * 0.5).sin_cos();
    let (sx, cx) = half(x_deg);
    let (sy, cy) = half(y_deg);
    let (sz, cz) = half(z_deg);
    // q = qy * qx * qz (Hamilton product), quaternions as (x, y, z, w).
    Quat {
        x: cy * sx * cz + sy * cx * sz,
        y: sy * cx * cz - cy * sx * sz,
        z: cy * cx * sz - sy * sx * cz,
        w: cy * cx * cz + sy * sx * sz,
    }
}

/// The inverse of [`euler_to_quat`], for display. Angles in degrees.
pub fn quat_to_euler(q: &Quat) -> (f32, f32, f32) {
    let (mut x, mut y, mut z, mut w) = (q.x, q.y, q.z, q.w);
    let n = (x * x + y * y + z * z + w * w).sqrt();
    if n < 1e-6 {
        return (0.0, 0.0, 0.0);
    }
    (x, y, z, w) = (x / n, y / n, z / n, w / n);
    // Rotation matrix entries needed for the Y-X-Z decomposition.
    let m12 = 2.0 * (y * z - w * x);
    let pitch = (-m12).clamp(-1.0, 1.0).asin();
    let (yaw, roll);
    if m12.abs() < 0.9999 {
        let m02 = 2.0 * (x * z + w * y);
        let m22 = 1.0 - 2.0 * (x * x + y * y);
        let m10 = 2.0 * (x * y + w * z);
        let m11 = 1.0 - 2.0 * (x * x + z * z);
        yaw = m02.atan2(m22);
        roll = m10.atan2(m11);
    } else {
        // Gimbal lock: fold everything into yaw. With roll = 0,
        // m00 = cos(yaw) and m01 = sin(yaw) * sin(pitch), sin(pitch) = -m12.
        let m01 = 2.0 * (x * y - w * z);
        let m00 = 1.0 - 2.0 * (y * y + z * z);
        yaw = (m01 * -m12.signum()).atan2(m00);
        roll = 0.0;
    }
    (pitch.to_degrees(), yaw.to_degrees(), roll.to_degrees())
}

/// True for an all-zero quaternion (treated as identity by the protocol) or identity.
pub fn is_identity(q: Option<&Quat>) -> bool {
    match q {
        None => true,
        Some(q) => {
            (q.x == 0.0 && q.y == 0.0 && q.z == 0.0)
                && (q.w == 0.0 || (q.w.abs() - 1.0).abs() < 1e-6)
        }
    }
}

const NAMED_COLOURS: &[(&str, &str)] = &[
    ("white", "#ffffff"),
    ("black", "#000000"),
    ("gray", "#808080"),
    ("grey", "#808080"),
    ("lightgray", "#d3d3d3"),
    ("lightgrey", "#d3d3d3"),
    ("darkgray", "#404040"),
    ("darkgrey", "#404040"),
    ("red", "#ff0000"),
    ("darkred", "#8b0000"),
    ("green", "#008000"),
    ("lime", "#00ff00"),
    ("darkgreen", "#006400"),
    ("blue", "#0000ff"),
    ("navy", "#000080"),
    ("skyblue", "#87ceeb"),
    ("lightblue", "#add8e6"),
    ("yellow", "#ffff00"),
    ("gold", "#ffd700"),
    ("orange", "#ffa500"),
    ("purple", "#800080"),
    ("violet", "#ee82ee"),
    ("pink", "#ffc0cb"),
    ("brown", "#8b4513"),
    ("tan", "#d2b48c"),
    ("beige", "#f5f5dc"),
    ("cyan", "#00ffff"),
    ("teal", "#008080"),
    ("magenta", "#ff00ff"),
];

/// Names accepted by [`parse_colour`], for error messages and tool descriptions.
pub fn colour_names() -> String {
    NAMED_COLOURS.iter().map(|(n, _)| *n).collect::<Vec<_>>().join(", ")
}

fn srgb_to_linear(c: f32) -> f32 {
    if c <= 0.04045 { c / 12.92 } else { ((c + 0.055) / 1.055).powf(2.4) }
}

fn linear_to_srgb(c: f32) -> f32 {
    let c = c.clamp(0.0, 1.0);
    if c <= 0.003_130_8 { c * 12.92 } else { 1.055 * c.powf(1.0 / 2.4) - 0.055 }
}

/// Parses `#rgb`, `#rrggbb`, `#rrggbbaa` (sRGB, as in CSS) or a colour name
/// into the protocol's linear 0..1 colour.
pub fn parse_colour(s: &str) -> Result<Color, String> {
    let t = s.trim().to_ascii_lowercase().replace([' ', '_', '-'], "");
    let hex = match NAMED_COLOURS.iter().find(|(n, _)| *n == t) {
        Some((_, h)) => h.to_string(),
        None => t,
    };
    let digits = hex.strip_prefix('#').unwrap_or(&hex);
    let bad = || {
        format!(
            "colour {s:?} is not understood; use \"#rrggbb\" (e.g. \"#ff8800\"), \
             \"#rrggbbaa\", or one of: {}",
            colour_names()
        )
    };
    if !digits.chars().all(|c| c.is_ascii_hexdigit()) {
        return Err(bad());
    }
    let expanded: String = match digits.len() {
        3 => digits.chars().flat_map(|c| [c, c]).collect(),
        6 | 8 => digits.to_string(),
        _ => return Err(bad()),
    };
    let byte = |i: usize| u8::from_str_radix(&expanded[i..i + 2], 16).map_err(|_| bad());
    let to = |b: u8| srgb_to_linear(b as f32 / 255.0);
    Ok(Color {
        r: to(byte(0)?),
        g: to(byte(2)?),
        b: to(byte(4)?),
        a: if expanded.len() == 8 { byte(6)? as f32 / 255.0 } else { 1.0 },
    })
}

/// Formats a linear colour as sRGB `#rrggbb` (plus alpha when not opaque).
pub fn colour_hex(c: Option<&Color>) -> String {
    let c = c.copied().unwrap_or(Color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 });
    let to = |x: f32| (linear_to_srgb(x) * 255.0).round() as u8;
    let mut s = format!("#{:02x}{:02x}{:02x}", to(c.r), to(c.g), to(c.b));
    if c.a < 0.999 {
        s.push_str(&format!("{:02x}", (c.a.clamp(0.0, 1.0) * 255.0).round() as u8));
    }
    s
}

/// `HH:MM:SS` (UTC) of a Unix millisecond timestamp.
pub fn clock(ms: i64) -> String {
    let secs = ms.div_euclid(1000).rem_euclid(86_400);
    format!("{:02}:{:02}:{:02}", secs / 3600, (secs / 60) % 60, secs % 60)
}

/// Milliseconds since the Unix epoch, now.
pub fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// `ERROR_CODE_NOT_FOUND`-style name of an error code number.
pub fn error_code_name(code: i32) -> String {
    tv_proto::v1::ErrorCode::try_from(code)
        .map(|c| c.as_str_name().to_string())
        .unwrap_or_else(|_| format!("ERROR_CODE_{code}"))
}

/// Lower-case short name of an enum value, e.g. `SHAPE_BOX` → `box`.
pub fn short(name: &str, prefix: &str) -> String {
    name.strip_prefix(prefix).unwrap_or(name).to_ascii_lowercase()
}

pub fn role_name(role: i32) -> String {
    tv_proto::v1::Role::try_from(role)
        .map(|r| short(r.as_str_name(), "ROLE_"))
        .unwrap_or_else(|_| "unknown".into())
}

pub fn kind_name(kind: i32) -> String {
    tv_proto::v1::ClientKind::try_from(kind)
        .map(|k| short(k.as_str_name(), "CLIENT_KIND_"))
        .unwrap_or_else(|_| "unknown".into())
}

pub fn shape_name(shape: i32) -> String {
    tv_proto::v1::Shape::try_from(shape)
        .map(|s| short(s.as_str_name(), "SHAPE_"))
        .unwrap_or_else(|_| "unknown".into())
}

pub fn material_name(m: i32) -> String {
    tv_proto::v1::Material::try_from(m)
        .map(|s| short(s.as_str_name(), "MATERIAL_"))
        .unwrap_or_else(|_| "unknown".into())
}

pub fn mode_name(m: i32) -> String {
    tv_proto::v1::MoveMode::try_from(m)
        .map(|s| short(s.as_str_name(), "MOVE_MODE_"))
        .unwrap_or_else(|_| "unknown".into())
}

pub fn channel_name(c: i32) -> String {
    tv_proto::v1::ChatChannel::try_from(c)
        .map(|s| short(s.as_str_name(), "CHAT_CHANNEL_"))
        .unwrap_or_else(|_| "unknown".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn close(a: f32, b: f32) -> bool {
        (a - b).abs() < 1e-3
    }

    #[test]
    fn euler_matches_unity() {
        // Quaternion.Euler(0, 90, 0) = (0, 0.7071, 0, 0.7071)
        let q = euler_to_quat(0.0, 90.0, 0.0);
        assert!(
            close(q.x, 0.0)
                && close(q.y, std::f32::consts::FRAC_1_SQRT_2)
                && close(q.z, 0.0)
                && close(q.w, std::f32::consts::FRAC_1_SQRT_2)
        );
        // Quaternion.Euler(90, 0, 0) = (0.7071, 0, 0, 0.7071)
        let q = euler_to_quat(90.0, 0.0, 0.0);
        assert!(
            close(q.x, std::f32::consts::FRAC_1_SQRT_2)
                && close(q.w, std::f32::consts::FRAC_1_SQRT_2)
        );
        // Quaternion.Euler(30, 45, 60) = (0.3919, 0.2005, 0.3604, 0.8224)
        let q = euler_to_quat(30.0, 45.0, 60.0);
        assert!(close(q.x, 0.39190) && close(q.y, 0.20056), "{q:?}");
        assert!(close(q.z, 0.36042) && close(q.w, 0.82236), "{q:?}");
        let (x, y, z) = quat_to_euler(&q);
        assert!(close(x, 30.0) && close(y, 45.0) && close(z, 60.0), "{x} {y} {z}");
    }

    #[test]
    fn bearings_follow_the_yaw_convention() {
        let o = Vec3::default();
        let east = Vec3 { x: 5.0, y: 0.0, z: 0.0 };
        assert!(close(bearing(&o, 0.0, &east), 90.0));
        assert!(close(bearing(&o, 90.0, &east), 0.0));
        let north = Vec3 { x: 0.0, y: 0.0, z: 5.0 };
        assert!(close(bearing(&o, 90.0, &north), -90.0));
    }

    #[test]
    fn colours_parse_and_print() {
        let c = parse_colour("#ff8000").unwrap();
        assert!(close(c.r, 1.0) && close(c.b, 0.0) && close(c.a, 1.0));
        assert!(close(c.g, 0.21586), "sRGB 0x80 is linear 0.216, got {}", c.g);
        assert_eq!(colour_hex(Some(&c)), "#ff8000");
        assert_eq!(colour_hex(Some(&parse_colour("Sky Blue").unwrap())), "#87ceeb");
        assert_eq!(colour_hex(Some(&parse_colour("#f00").unwrap())), "#ff0000");
        assert_eq!(colour_hex(Some(&parse_colour("#00ff0080").unwrap())), "#00ff0080");
        assert!(parse_colour("#12345").is_err());
        assert!(parse_colour("chartreuse-ish").unwrap_err().contains("#rrggbb"));
    }

    #[test]
    fn numbers_are_short() {
        assert_eq!(num(1.0), "1");
        assert_eq!(num(1.25), "1.25");
        assert_eq!(num(-0.001), "0");
        assert_eq!(num(1.23456), "1.23");
        assert_eq!(clock(3_723_000), "01:02:03");
    }
}
