//! The MCP tools: their schemas, argument checks, the protocol messages they
//! send and the text they return.

use std::time::Duration;

use serde_json::{Value, json};
use tokio::time::Instant;
use tv_proto::v1::{
    ChatChannel, Material, MoveMode, ObjectInfo, ObjectSpec, Quat, ServerMessage, Shape, Vec3,
    client_message::Body as C, server_message::Body as S,
};

use crate::args::Args;
use crate::format::{self, num, vec3};
use crate::model::{LogLine, Session};
use crate::world::{Bridge, Pending, Reply, error_text};

/// What a tool call returns to the assistant.
#[derive(Debug, Clone, PartialEq)]
pub struct ToolResult {
    pub text: String,
    pub is_error: bool,
}

impl ToolResult {
    fn error(text: impl Into<String>) -> Self {
        ToolResult { text: text.into(), is_error: true }
    }
}

type Outcome = Result<String, String>;

const COORDS: &str = "Coordinates are metres in a left-handed, Y-up frame (as in Unity): \
    y is height and the ground is at y = 0; seen from above, +x is to the right of +z. \
    Headings and yaw are degrees clockwise seen from above: 0 faces +z, 90 faces +x.";

/// The server ends a `MoveTo` by snapping to the target with zero velocity.
/// Stopped within this distance (metres) of the target counts as arrived.
const ARRIVE_TOLERANCE: f32 = 0.5;
/// Closer than this counts as already there, moving or not (the server sends
/// no state at all for a move to the point the avatar stands on).
const AT_TARGET: f32 = 0.01;
const MAX_WAIT_S: f32 = 120.0;

const SHAPES: &[&str] = &["box", "sphere", "cylinder", "cone", "wedge", "plane"];
const MATERIALS: &[&str] = &["matte", "glossy", "metal", "glass", "emissive"];
const OBJECT_FIELDS: &[&str] = &[
    "shape",
    "position",
    "scale",
    "rotation_euler_deg",
    "color",
    "colour",
    "material",
    "name",
    "solid",
    "locked",
];

fn vec3_schema(desc: &str) -> Value {
    json!({
        "type": "object",
        "description": desc,
        "properties": {
            "x": {"type": "number"},
            "y": {"type": "number"},
            "z": {"type": "number"}
        },
        "required": ["x", "y", "z"],
        "additionalProperties": false
    })
}

fn id_schema(desc: &str) -> Value {
    json!({"type": ["integer", "string"], "description": desc})
}

fn object_properties() -> serde_json::Map<String, Value> {
    let mut p = serde_json::Map::new();
    p.insert(
        "shape".into(),
        json!({
            "type": "string",
            "enum": SHAPES,
            "description": "Primitive shape. Each fits a 1 m cube centred on `position` before \
                scaling. cylinder: axis along y. cone: apex at +y. wedge: a box cut along its \
                diagonal, full height at -z sloping to zero at +z. plane: a flat 1x1 m quad in \
                the xz plane facing +y (its y scale is ignored)."
        }),
    );
    p.insert(
        "position".into(),
        vec3_schema(
            "Centre of the object in metres. A 1 m box resting on the ground has y = 0.5 \
             (y = scale.y / 2 in general).",
        ),
    );
    p.insert(
        "scale".into(),
        json!({
            "description": "Size per axis in metres (width x, height y, depth z), each 0.01..64 \
                unless the world says otherwise. A single number means the same on every axis. \
                Default 1.",
            "anyOf": [
                {"type": "number"},
                vec3_schema("Size per axis in metres.")
            ]
        }),
    );
    p.insert(
        "rotation_euler_deg".into(),
        vec3_schema(
            "Rotation as Euler angles in degrees, applied as in Unity's Quaternion.Euler: \
             z (roll) first, then x (pitch), then y (yaw), about the world axes. \
             {\"x\":0,\"y\":90,\"z\":0} turns the object clockwise seen from above so that \
             its +z face points to +x. Default: no rotation. Absolute, not added to the \
             current rotation.",
        ),
    );
    p.insert(
        "color".into(),
        json!({
            "type": "string",
            "description": format!(
                "Colour as \"#rrggbb\" or \"#rrggbbaa\" (sRGB, like CSS), or a name: {}. \
                 Default white.",
                format::colour_names()
            )
        }),
    );
    p.insert(
        "material".into(),
        json!({"type": "string", "enum": MATERIALS, "description": "Surface preset. Default matte."}),
    );
    p.insert(
        "name".into(),
        json!({"type": "string", "description": "Optional label, at most 64 characters."}),
    );
    p.insert(
        "solid".into(),
        json!({"type": "boolean", "description": "Avatars collide with it. Default true."}),
    );
    p.insert(
        "locked".into(),
        json!({
            "type": "boolean",
            "description": "Only you (and world owners) may edit or delete it. Default false."
        }),
    );
    p
}

/// The tool list, in a fixed order.
pub fn definitions() -> Vec<Value> {
    let empty = json!({"type": "object", "properties": {}, "additionalProperties": false});

    let create_props = object_properties();
    let mut edit_props = serde_json::Map::new();
    edit_props
        .insert("id".into(), id_schema("Id of the object to change (from look or list_objects)."));
    for (k, v) in object_properties() {
        edit_props.insert(k, v);
    }
    if let Some(Value::Object(name)) = edit_props.get_mut("name") {
        name.insert(
            "description".into(),
            json!("New label, at most 64 characters; an empty string removes the label."),
        );
    }

    vec![
        json!({
            "name": "look",
            "title": "Look around",
            "description": format!(
                "Describe what is around your avatar: a short summary, then every nearby avatar \
                 and object (nearest first) with id, position, distance and bearing (degrees \
                 relative to where you face: 0 ahead, positive to the right, negative to the \
                 left). Call this first when you arrive and whenever you need to know what is \
                 near you. {COORDS}"
            ),
            "inputSchema": {
                "type": "object",
                "properties": {
                    "radius": {
                        "type": "number",
                        "minimum": 0,
                        "description": "How far to look, in metres. Omit for the server default; \
                            the server caps it (see world_info)."
                    }
                },
                "additionalProperties": false
            },
            "annotations": {"readOnlyHint": true}
        }),
        json!({
            "name": "move_to",
            "title": "Walk, run or fly to a point",
            "description": format!(
                "Move your avatar in a straight line to a point (obstacles are ignored). Walking \
                 and running ignore y and keep your current height, so after flying, fly back \
                 down (mode fly, y = 0) before walking; flying moves in 3D. With \
                 wait=true (the default) this returns when you arrive, stop, or the timeout \
                 passes, and reports where you ended up. With wait=false it returns at once \
                 while the avatar keeps moving. Typical speeds: walk 4 m/s, run 8 m/s, fly \
                 16 m/s. {COORDS}"
            ),
            "inputSchema": {
                "type": "object",
                "properties": {
                    "x": {"type": "number", "description": "Target x in metres."},
                    "y": {"type": "number", "description": "Target height in metres; only used when flying. Default: current height."},
                    "z": {"type": "number", "description": "Target z in metres."},
                    "mode": {"type": "string", "enum": ["walk", "run", "fly"], "description": "Default walk."},
                    "wait": {"type": "boolean", "description": "Wait until arrival. Default true."},
                    "timeout_s": {"type": "number", "exclusiveMinimum": 0, "maximum": MAX_WAIT_S,
                        "description": "With wait, give up waiting after this many seconds (the avatar keeps moving). Default: enough for the distance, at most 120."}
                },
                "required": ["x", "z"],
                "additionalProperties": false
            }
        }),
        json!({
            "name": "stop",
            "title": "Stop moving",
            "description": "Cancel a move_to in progress; the avatar stops where it is.",
            "inputSchema": empty.clone()
        }),
        json!({
            "name": "say",
            "title": "Say something",
            "description": "Say something in chat. `local` (default) is heard by avatars within \
                about 20 m of you; `world` is heard by everyone in the world. At most 500 \
                characters. Replies arrive as chat; use wait_for_chat or recent_chat to read them.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "text": {"type": "string", "minLength": 1, "description": "What to say."},
                    "channel": {"type": "string", "enum": ["local", "world"], "description": "Default local."}
                },
                "required": ["text"],
                "additionalProperties": false
            }
        }),
        json!({
            "name": "recent_chat",
            "title": "Read recent chat",
            "description": "Read chat heard since you joined, oldest first, with UTC times, \
                channel and speaker; your own lines are marked (you) and AI agents (agent). \
                Also lists notable events (people joining or leaving, objects others created, \
                changed or deleted) unless include_events is false. With since_last=true \
                (default) only lines not already returned by recent_chat or wait_for_chat.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "since_last": {"type": "boolean", "description": "Only lines not returned before. Default true."},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 300, "description": "At most this many lines, the newest kept. Default 50."},
                    "include_events": {"type": "boolean", "description": "Include join/leave/object events. Default true."}
                },
                "additionalProperties": false
            },
            "annotations": {"readOnlyHint": true}
        }),
        json!({
            "name": "wait_for_chat",
            "title": "Wait for someone to speak",
            "description": "Block until someone else says something you can hear, or the timeout \
                passes, then return the chat lines (and events) not yet returned to you. \
                Returns at once if there are already unread lines from others. Use this to \
                wait for a reply after `say`.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "timeout_s": {"type": "number", "exclusiveMinimum": 0, "maximum": MAX_WAIT_S, "description": "Default 30, at most 120."}
                },
                "additionalProperties": false
            },
            "annotations": {"readOnlyHint": true}
        }),
        json!({
            "name": "who",
            "title": "Who is here",
            "description": "List everyone present in the world (you first) with name, kind \
                (human or agent), role, avatar id, position, and distance and bearing from you.",
            "inputSchema": empty.clone(),
            "annotations": {"readOnlyHint": true}
        }),
        json!({
            "name": "create_object",
            "title": "Build an object",
            "description": format!(
                "Create a primitive object (box, sphere, cylinder, cone, wedge or plane) and \
                 return it with its id. Needs the builder role. To place something in front of \
                 you, use your position and heading from look or who. {COORDS}"
            ),
            "inputSchema": {
                "type": "object",
                "properties": create_props,
                "required": ["shape", "position"],
                "additionalProperties": false
            }
        }),
        json!({
            "name": "edit_object",
            "title": "Change an object",
            "description": format!(
                "Change some properties of an existing object; give its id and only the fields \
                 to change. You may edit your own objects and unlocked objects of others \
                 (owners may edit anything). {COORDS}"
            ),
            "inputSchema": {
                "type": "object",
                "properties": edit_props,
                "required": ["id"],
                "additionalProperties": false
            }
        }),
        json!({
            "name": "delete_object",
            "title": "Delete an object",
            "description": "Remove an object from the world by id (from look or list_objects). \
                You may delete your own objects and unlocked objects of others. Cannot be undone.",
            "inputSchema": {
                "type": "object",
                "properties": {"id": id_schema("Id of the object to delete.")},
                "required": ["id"],
                "additionalProperties": false
            },
            "annotations": {"destructiveHint": true}
        }),
        json!({
            "name": "list_objects",
            "title": "List objects",
            "description": "List objects in the world from your local copy, nearest first, \
                with id, shape, name, position, size, rotation, colour, material, flags and \
                owner. Cheaper than look for finding your own builds.",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "radius": {"type": "number", "exclusiveMinimum": 0, "description": "Only objects within this many metres of you. Default: anywhere."},
                    "owner": {"type": "string", "enum": ["me", "anyone"], "description": "`me` for objects you created. Default anyone."},
                    "limit": {"type": "integer", "minimum": 1, "maximum": 500, "description": "Default 50."}
                },
                "additionalProperties": false
            },
            "annotations": {"readOnlyHint": true}
        }),
        json!({
            "name": "world_info",
            "title": "World information",
            "description": format!(
                "The world's name, description, bounds (everything must stay inside), spawn \
                 point and ground size, your role and what it allows, your position, and the \
                 server's limits (chat length, object sizes, speeds, look radius). {COORDS}"
            ),
            "inputSchema": empty,
            "annotations": {"readOnlyHint": true}
        }),
    ]
}

/// Runs a tool. Returns `None` if there is no tool of that name.
pub async fn call(bridge: &Bridge, name: &str, arguments: Option<&Value>) -> Option<ToolResult> {
    // Check arguments before anything touches the network.
    let allowed: &[&str] = match name {
        "look" => &["radius"],
        "move_to" => &["x", "y", "z", "mode", "wait", "timeout_s"],
        "stop" | "who" | "world_info" => &[],
        "say" => &["text", "channel"],
        "recent_chat" => &["since_last", "limit", "include_events"],
        "wait_for_chat" => &["timeout_s"],
        "create_object" => OBJECT_FIELDS,
        "edit_object" => &[
            "id",
            "shape",
            "position",
            "scale",
            "rotation_euler_deg",
            "color",
            "colour",
            "material",
            "name",
            "solid",
            "locked",
        ],
        "delete_object" => &["id"],
        "list_objects" => &["radius", "owner", "limit"],
        _ => return None,
    };
    let args = match Args::new(name, arguments, allowed) {
        Ok(a) => a,
        Err(e) => return Some(ToolResult::error(e)),
    };
    let checked = match name {
        "look" => look_args(&args).map(Call::Look),
        "move_to" => move_args(&args).map(Call::MoveTo),
        "stop" => Ok(Call::Stop),
        "say" => say_args(&args).map(Call::Say),
        "recent_chat" => recent_args(&args).map(Call::Recent),
        "wait_for_chat" => args.seconds("timeout_s", MAX_WAIT_S).map(Call::Wait),
        "who" => Ok(Call::Who),
        "create_object" => spec_args(&args, true).map(Call::Create),
        "edit_object" => edit_args(&args).map(Call::Edit),
        "delete_object" => {
            args.id("id").and_then(|id| id.ok_or_else(|| args.missing("id"))).map(Call::Delete)
        }
        "list_objects" => list_args(&args).map(Call::List),
        "world_info" => Ok(Call::Info),
        _ => unreachable!("filtered above"),
    };
    let call = match checked {
        Ok(c) => c,
        Err(e) => return Some(ToolResult::error(e)),
    };

    let note = match bridge.ensure_connected().await {
        Ok(n) => n,
        Err(e) => return Some(ToolResult::error(e)),
    };
    let outcome = match call {
        Call::Look(r) => look(bridge, r).await,
        Call::MoveTo(m) => move_to(bridge, m).await,
        Call::Stop => stop(bridge).await,
        Call::Say(s) => say(bridge, s).await,
        Call::Recent(r) => Ok(recent_chat(bridge, r)),
        Call::Wait(t) => Ok(wait_for_chat(bridge, t).await),
        Call::Who => with_session(bridge, who),
        Call::Create(s) => create(bridge, s).await,
        Call::Edit(e) => edit(bridge, e).await,
        Call::Delete(id) => delete(bridge, id).await,
        Call::List(l) => with_session(bridge, |s| list_objects(s, &l)),
        Call::Info => with_session(bridge, world_info),
    };
    let (mut text, is_error) = match outcome {
        Ok(t) => (t, false),
        Err(t) => (t, true),
    };
    if let Some(n) = note {
        text = format!("{n}\n{text}");
    }
    Some(ToolResult { text, is_error })
}

enum Call {
    Look(Option<f32>),
    MoveTo(MoveArgs),
    Stop,
    Say(SayArgs),
    Recent(RecentArgs),
    Wait(Option<f32>),
    Who,
    Create(SpecArgs),
    Edit(EditArgs),
    Delete(u64),
    List(ListArgs),
    Info,
}

fn with_session(bridge: &Bridge, f: impl FnOnce(&Session) -> Outcome) -> Outcome {
    let st = bridge.lock();
    match &st.session {
        Some(s) => f(s),
        None => Err(not_connected()),
    }
}

fn not_connected() -> String {
    "The connection to the world dropped just now; try again in a moment.".into()
}

/// Turns a reply into an error if it is a server `Error`.
fn refused(msg: &ServerMessage, what: &str) -> Option<String> {
    error_text(msg).map(|e| format!("The world refused to {what}: {e}"))
}

/// Waits for the reply to a request whose success reply carries nothing we
/// need (`Ack`, or the echo of our own chat line); fails on `Error`.
async fn confirm(bridge: &Bridge, pending: &mut Pending, what: &str) -> Result<(), String> {
    let limit = bridge.config().reply_timeout;
    match pending.wait(limit).await {
        Reply::Message(msg) => match refused(&msg, what) {
            Some(e) => Err(e),
            None => Ok(()),
        },
        Reply::TimedOut => {
            Err(format!("The world did not answer within {} s.", limit.as_secs_f32()))
        }
        Reply::ConnectionLost => {
            Err("The connection to the world was lost before it answered.".into())
        }
    }
}

// ---------------------------------------------------------------- look

fn look_args(a: &Args) -> Result<Option<f32>, String> {
    match a.f32("radius")? {
        Some(r) if r < 0.0 => Err(a.err("`radius` must be 0 or more metres")),
        r => Ok(r),
    }
}

async fn look(bridge: &Bridge, radius: Option<f32>) -> Outcome {
    let msg =
        bridge.request(C::Observe(tv_proto::v1::Observe { radius: radius.unwrap_or(0.0) })).await?;
    if let Some(e) = refused(&msg, "describe the surroundings") {
        return Err(e);
    }
    let Some(S::Observation(obs)) = msg.body else {
        return Err("The world answered look with something other than an Observation.".into());
    };
    let st = bridge.lock();
    let session = st.session.as_ref();
    let owner =
        |id: &str| session.and_then(|s| s.name_of_user(id)).unwrap_or_else(|| short_user(id));
    let mut out = String::new();
    if !obs.summary.trim().is_empty() {
        out.push_str(obs.summary.trim());
        out.push_str("\n\n");
    }
    if let Some(me) = &obs.self_ {
        let t = me.transform.clone().unwrap_or_default();
        out.push_str(&format!(
            "You: {} (avatar {}) at {}, facing {}°.\n",
            me.name,
            me.id,
            vec3(t.position.as_ref()),
            num(t.yaw)
        ));
    }
    out.push_str(&format!("Avatars within {} m ({}):\n", num(obs.radius), obs.avatars.len()));
    if obs.avatars.is_empty() {
        out.push_str("- none\n");
    }
    for seen in &obs.avatars {
        let a = seen.avatar.clone().unwrap_or_default();
        out.push_str(&format!(
            "- {} ({}, {}), avatar {}: {}, at {}\n",
            a.name,
            format::kind_name(a.kind),
            format::role_name(a.role),
            a.id,
            format::where_is(seen.distance, seen.bearing),
            vec3(a.transform.as_ref().and_then(|t| t.position.as_ref()))
        ));
    }
    out.push_str(&format!("Objects within {} m ({}):\n", num(obs.radius), obs.objects.len()));
    if obs.objects.is_empty() {
        out.push_str("- none\n");
    }
    for seen in &obs.objects {
        let o = seen.object.clone().unwrap_or_default();
        out.push_str(&format!(
            "- {} — {}\n",
            describe_object(&o, &owner),
            format::where_is(seen.distance, seen.bearing)
        ));
    }
    if obs.truncated {
        out.push_str("(The lists were cut short because a lot is nearby; look with a smaller radius for detail.)\n");
    }
    Ok(out.trim_end().to_string())
}

fn short_user(id: &str) -> String {
    if id.is_empty() { "unknown".into() } else { format!("user {}", &id[..id.len().min(8)]) }
}

fn describe_object(o: &ObjectInfo, owner: &dyn Fn(&str) -> String) -> String {
    let s = o.spec.clone().unwrap_or_default();
    let mut parts = vec![format!(
        "object {}{}: {} at {}, size {}",
        o.id,
        if s.name.is_empty() { String::new() } else { format!(" {:?}", s.name) },
        format::shape_name(s.shape),
        vec3(s.position.as_ref()),
        vec3(s.scale.as_ref()),
    )];
    if !format::is_identity(s.rotation.as_ref()) {
        let (x, y, z) = format::quat_to_euler(&s.rotation.unwrap_or_default());
        parts.push(format!("rotation ({}, {}, {})°", num(x), num(y), num(z)));
    }
    parts.push(format!("colour {}", format::colour_hex(s.color.as_ref())));
    parts.push(format::material_name(s.material));
    parts.push(if s.solid { "solid".into() } else { "not solid".into() });
    if s.locked {
        parts.push("locked".into());
    }
    parts.push(format!("owner {}", owner(&o.owner_id)));
    parts.join(", ")
}

// ---------------------------------------------------------------- moving

struct MoveArgs {
    x: f32,
    y: Option<f32>,
    z: f32,
    mode: MoveMode,
    wait: bool,
    timeout: Option<f32>,
}

fn move_args(a: &Args) -> Result<MoveArgs, String> {
    let x = a.f32("x")?.ok_or_else(|| a.missing("x"))?;
    let z = a.f32("z")?.ok_or_else(|| a.missing("z"))?;
    let mode = match a.choice("mode", &["walk", "run", "fly"])?.as_deref() {
        None | Some("walk") => MoveMode::Walk,
        Some("run") => MoveMode::Run,
        _ => MoveMode::Fly,
    };
    Ok(MoveArgs {
        x,
        y: a.f32("y")?,
        z,
        mode,
        wait: a.bool("wait")?.unwrap_or(true),
        timeout: a.seconds("timeout_s", MAX_WAIT_S)?,
    })
}

fn bounds(s: &Session) -> Option<(Vec3, Vec3)> {
    let (lo, hi) = (s.world.bounds_min?, s.world.bounds_max?);
    (hi.x > lo.x && hi.y > lo.y && hi.z > lo.z).then_some((lo, hi))
}

fn outside(s: &Session, p: &Vec3, check_y: bool) -> Option<String> {
    let (lo, hi) = bounds(s)?;
    let out = p.x < lo.x
        || p.x > hi.x
        || p.z < lo.z
        || p.z > hi.z
        || (check_y && (p.y < lo.y || p.y > hi.y));
    out.then(|| {
        format!(
            "{} is outside the world bounds, which run from {} to {}; pick a point inside",
            vec3(Some(p)),
            vec3(Some(&lo)),
            vec3(Some(&hi))
        )
    })
}

fn speed(s: &Session, mode: MoveMode) -> f32 {
    let l = &s.limits;
    let v = match mode {
        MoveMode::Run => l.run_speed,
        MoveMode::Fly => l.fly_speed,
        _ => l.walk_speed,
    };
    if v > 0.0 {
        v
    } else {
        match mode {
            MoveMode::Run => 8.0,
            MoveMode::Fly => 16.0,
            _ => 4.0,
        }
    }
}

async fn move_to(bridge: &Bridge, m: MoveArgs) -> Outcome {
    let fly = m.mode == MoveMode::Fly;
    let (target, start_updates, start_pos, timeout) = {
        let st = bridge.lock();
        let s = st.session.as_ref().ok_or_else(not_connected)?;
        let here = s.my_position();
        let target = Vec3 { x: m.x, y: m.y.unwrap_or(here.y), z: m.z };
        if let Some(e) = outside(s, &target, fly) {
            return Err(format!("move_to: {e}."));
        }
        let dist = if fly {
            format::distance(&here, &target)
        } else {
            format::distance_xz(&here, &target)
        };
        let default = (dist / speed(s, m.mode) * 1.5 + 3.0).clamp(5.0, MAX_WAIT_S);
        (target, s.me_updates, here, m.timeout.unwrap_or(default))
    };
    let mut changed = bridge.subscribe();
    let mut pending = bridge
        .send(C::MoveTo(tv_proto::v1::MoveTo { target: Some(target), mode: m.mode as i32 }))?;
    let mode = format::mode_name(m.mode as i32);
    let target_text =
        if fly { vec3(Some(&target)) } else { format!("({}, {})", num(target.x), num(target.z)) };

    if !m.wait {
        confirm(bridge, &mut pending, "move there").await?;
        return Ok(format!(
            "Started to {mode} from {} towards {target_text}. Not waiting: use who or look to \
             check progress, stop to cancel.",
            vec3(Some(&start_pos))
        ));
    }

    let deadline = Instant::now() + Duration::from_secs_f32(timeout);
    let mut reply_done = false;
    loop {
        {
            let st = bridge.lock();
            let Some(s) = st.session.as_ref() else {
                return Err("The connection to the world was lost while moving.".into());
            };
            let t = s.my_transform();
            let pos = t.position.unwrap_or_default();
            let left = if fly {
                format::distance(&pos, &target)
            } else {
                format::distance_xz(&pos, &target)
            };
            let speed_now =
                t.velocity.map(|v| (v.x * v.x + v.y * v.y + v.z * v.z).sqrt()).unwrap_or(0.0);
            let stopped = s.me_updates > start_updates && speed_now < 0.05;
            if left <= AT_TARGET || (stopped && left <= ARRIVE_TOLERANCE) {
                return Ok(format!("Arrived at {} ({mode}).", vec3(Some(&pos))));
            }
            if stopped {
                return Ok(format!(
                    "Stopped at {}, {} m short of {target_text}. The server ended the move early \
                     (for example at the world's edge, or another command cancelled it).",
                    vec3(Some(&pos)),
                    num(left)
                ));
            }
        }
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            let st = bridge.lock();
            let pos = st.session.as_ref().map(|s| s.my_position()).unwrap_or_default();
            let left = if fly {
                format::distance(&pos, &target)
            } else {
                format::distance_xz(&pos, &target)
            };
            return Ok(format!(
                "Not there yet after {} s: now at {}, {} m from {target_text}. The avatar is still \
                 on its way; call move_to again to keep waiting, or stop to cancel.",
                num(timeout),
                vec3(Some(&pos)),
                num(left)
            ));
        }
        tokio::select! {
            r = pending.wait(remaining), if !reply_done => {
                reply_done = true;
                if let Reply::Message(msg) = r
                    && let Some(e) = refused(&msg, "move there") {
                        return Err(e);
                    }
            }
            r = changed.changed() => {
                if r.is_err() {
                    return Err(not_connected());
                }
            }
            _ = tokio::time::sleep_until(deadline) => {}
        }
    }
}

async fn stop(bridge: &Bridge) -> Outcome {
    let mut pending = bridge.send(C::Stop(tv_proto::v1::Stop {}))?;
    confirm(bridge, &mut pending, "stop").await?;
    with_session(bridge, |s| Ok(format!("Stopped at {}.", vec3(Some(&s.my_position())))))
}

// ---------------------------------------------------------------- chat

struct SayArgs {
    text: String,
    channel: ChatChannel,
}

fn say_args(a: &Args) -> Result<SayArgs, String> {
    let text = a.string("text")?.ok_or_else(|| a.missing("text"))?;
    let text = text.trim().to_string();
    if text.is_empty() {
        return Err(a.err("`text` is empty; say something"));
    }
    let channel = match a.choice("channel", &["local", "world"])?.as_deref() {
        Some("world") => ChatChannel::World,
        _ => ChatChannel::Local,
    };
    Ok(SayArgs { text, channel })
}

async fn say(bridge: &Bridge, a: SayArgs) -> Outcome {
    let (max, radius) = {
        let st = bridge.lock();
        let s = st.session.as_ref().ok_or_else(not_connected)?;
        let max = if s.limits.max_chat_chars > 0 { s.limits.max_chat_chars } else { 500 };
        let r = if s.limits.local_chat_radius > 0.0 { s.limits.local_chat_radius } else { 20.0 };
        (max as usize, r)
    };
    let len = a.text.chars().count();
    if len > max {
        return Err(format!(
            "say: `text` is {len} characters; the world allows at most {max}. Shorten it or \
             split it into several messages."
        ));
    }
    let mut pending = bridge
        .send(C::Chat(tv_proto::v1::Chat { channel: a.channel as i32, text: a.text.clone() }))?;
    // The server answers with our own ChatMessage (reply_to set) or an Error.
    confirm(bridge, &mut pending, "pass on your message").await?;
    Ok(match a.channel {
        ChatChannel::World => format!("You said to the whole world: {}", a.text),
        _ => format!("You said (heard within {} m): {}", num(radius), a.text),
    })
}

struct RecentArgs {
    since_last: bool,
    limit: usize,
    events: bool,
}

fn recent_args(a: &Args) -> Result<RecentArgs, String> {
    Ok(RecentArgs {
        since_last: a.bool("since_last")?.unwrap_or(true),
        limit: a.u32("limit", 1, 300)?.unwrap_or(50) as usize,
        events: a.bool("include_events")?.unwrap_or(true),
    })
}

/// Renders log lines after `after`, keeping the newest `limit`, and moves the cursor.
fn take_lines(bridge: &Bridge, after: u64, limit: usize, events: bool) -> (Vec<String>, usize) {
    let mut st = bridge.lock();
    let lines = st.log.lines_after(after, events);
    let skipped = lines.len().saturating_sub(limit);
    let kept: Vec<&LogLine> = lines.iter().skip(skipped).collect();
    let rendered = kept.iter().map(|l| l.render()).collect();
    // Everything up to the newest line shown counts as returned. With events
    // included that is everything in the log.
    let last = if events { st.log.latest() } else { kept.last().map(|l| l.n()).unwrap_or(0) };
    drop(kept);
    drop(lines);
    if last > st.log.cursor {
        st.log.cursor = last;
    }
    (rendered, skipped)
}

fn recent_chat(bridge: &Bridge, a: RecentArgs) -> String {
    let after = if a.since_last { bridge.lock().log.cursor } else { 0 };
    let (lines, skipped) = take_lines(bridge, after, a.limit, a.events);
    if lines.is_empty() {
        return if a.since_last {
            "No new chat since you last checked.".into()
        } else {
            "No chat heard yet.".into()
        };
    }
    let mut out = if a.since_last {
        format!("{} new line(s) since you last checked, oldest first:\n", lines.len())
    } else {
        format!("Last {} line(s), oldest first:\n", lines.len())
    };
    if skipped > 0 {
        out.push_str(&format!("({skipped} older line(s) not shown; raise limit to see more)\n"));
    }
    out.push_str(&lines.join("\n"));
    out
}

async fn wait_for_chat(bridge: &Bridge, timeout: Option<f32>) -> String {
    let timeout = timeout.unwrap_or(30.0);
    let deadline = Instant::now() + Duration::from_secs_f32(timeout);
    let mut changed = bridge.subscribe();
    let heard_at_start = bridge.lock().log.heard;
    let mut timed_out = false;
    loop {
        {
            let st = bridge.lock();
            if st.log.heard > heard_at_start || st.log.unread_from_others() {
                break;
            }
        }
        tokio::select! {
            r = changed.changed() => if r.is_err() { break },
            _ = tokio::time::sleep_until(deadline) => { timed_out = true; break }
        }
    }
    let after = bridge.lock().log.cursor;
    let (lines, skipped) = take_lines(bridge, after, 300, true);
    let mut out = if timed_out {
        format!("No one said anything you could hear in {} s.", num(timeout))
    } else {
        "New chat:".to_string()
    };
    if !lines.is_empty() {
        if timed_out {
            out.push_str(" Other new lines:");
        }
        if skipped > 0 {
            out.push_str(&format!("\n({skipped} older line(s) not shown)"));
        }
        out.push('\n');
        out.push_str(&lines.join("\n"));
    }
    if !bridge.is_connected() {
        out.push_str("\n(Not connected to the world at the moment; it will reconnect.)");
    }
    out
}

// ---------------------------------------------------------------- who / info

fn who(s: &Session) -> Outcome {
    let me = s.my_transform();
    let here = me.position.unwrap_or_default();
    let mut others: Vec<_> = s
        .avatars
        .values()
        .map(|a| {
            let p = a.transform.as_ref().and_then(|t| t.position).unwrap_or_default();
            (format::distance(&here, &p), a, p)
        })
        .collect();
    others.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.id.cmp(&b.1.id)));
    let mut out = format!("{} present, including you:\n", others.len() + 1);
    out.push_str(&format!(
        "- you: {} ({}, {}), avatar {}, at {}, facing {}°\n",
        s.me.name,
        format::kind_name(s.me.kind),
        format::role_name(s.role),
        s.avatar_id,
        vec3(Some(&here)),
        num(me.yaw)
    ));
    for (d, a, p) in others {
        out.push_str(&format!(
            "- {} ({}, {}), avatar {}: {}, at {}\n",
            a.name,
            format::kind_name(a.kind),
            format::role_name(a.role),
            a.id,
            format::where_is(d, format::bearing(&here, me.yaw, &p)),
            vec3(Some(&p))
        ));
    }
    Ok(out.trim_end().to_string())
}

fn world_info(s: &Session) -> Outcome {
    let w = &s.world;
    let l = &s.limits;
    let mut out = format!("World: {}\n", if w.name.is_empty() { "(unnamed)" } else { &w.name });
    if !w.description.is_empty() {
        out.push_str(&format!("Description: {}\n", w.description));
    }
    match bounds(s) {
        Some((lo, hi)) => out.push_str(&format!(
            "Bounds: {} to {} (everything must stay inside)\n",
            vec3(Some(&lo)),
            vec3(Some(&hi))
        )),
        None => out.push_str("Bounds: not given\n"),
    }
    out.push_str(&format!("Spawn point: {}\n", vec3(w.spawn_point.as_ref())));
    if w.ground_size > 0.0 {
        out.push_str(&format!(
            "Ground: flat, {0} x {0} m, centred on (0, 0, 0) at y = 0\n",
            num(w.ground_size)
        ));
    }
    if !w.sky.is_empty() || w.time_of_day > 0.0 {
        out.push_str(&format!(
            "Sky: {}, time of day {}h\n",
            if w.sky.is_empty() { "default" } else { &w.sky },
            num(w.time_of_day)
        ));
    }
    out.push_str(&format!(
        "{}\nYour user id: {}\nYour role: {} — {}\n",
        s.describe_self(),
        s.user_id,
        format::role_name(s.role),
        match tv_proto::v1::Role::try_from(s.role).unwrap_or_default() {
            tv_proto::v1::Role::Owner => "you may do everything: move, chat, look, build, edit or delete any object, moderate",
            tv_proto::v1::Role::Builder => "you may move, chat, look, create objects, and edit or delete your own objects and unlocked objects of others",
            _ => "you may move, chat and look, but not build",
        }
    ));
    let or = |v: f32, d: f32| if v > 0.0 { v } else { d };
    let ou = |v: u32, d: u32| if v > 0 { v } else { d };
    out.push_str(&format!(
        "Limits: chat {} characters, local chat heard within {} m, names {} characters, \
         object size {}..{} m per axis, at most {} objects in the world, speeds walk {} / run {} / \
         fly {} m/s, look radius up to {} m\n",
        ou(l.max_chat_chars, 500),
        num(or(l.local_chat_radius, 20.0)),
        ou(l.max_name_chars, 64),
        num(or(l.min_scale, 0.01)),
        num(or(l.max_scale, 64.0)),
        ou(l.max_objects, 10_000),
        num(or(l.walk_speed, 4.0)),
        num(or(l.run_speed, 8.0)),
        num(or(l.fly_speed, 16.0)),
        num(l.max_observe_radius),
    ));
    out.push_str(&format!(
        "People present: {}; objects known: {}{}\n",
        s.avatars.len() + 1,
        s.objects.len(),
        if s.objects_dropped { " (more exist; local copy is full)" } else { "" }
    ));
    if !s.server_version.is_empty() {
        out.push_str(&format!("Server version: {}\n", s.server_version));
    }
    Ok(out.trim_end().to_string())
}

fn list_objects(s: &Session, a: &ListArgs) -> Outcome {
    let here = s.my_position();
    let yaw = s.my_transform().yaw;
    let mut found: Vec<(f32, &ObjectInfo)> = s
        .objects
        .values()
        .filter(|o| !a.mine || o.owner_id == s.user_id)
        .map(|o| {
            let p = o.spec.as_ref().and_then(|x| x.position).unwrap_or_default();
            (format::distance(&here, &p), o)
        })
        .filter(|(d, _)| a.radius.is_none_or(|r| *d <= r))
        .collect();
    found.sort_by(|x, y| x.0.total_cmp(&y.0).then(x.1.id.cmp(&y.1.id)));
    let total = found.len();
    let whose = if a.mine { "of yours " } else { "" };
    let range = a.radius.map_or(String::new(), |r| format!(" within {} m", num(r)));
    if total == 0 {
        return Ok(format!("No objects {whose}found{range}."));
    }
    let owner = |id: &str| s.name_of_user(id).unwrap_or_else(|| short_user(id));
    let mut out = format!("{total} object(s) {whose}found{range}");
    if total > a.limit {
        out.push_str(&format!(", showing the nearest {}", a.limit));
    }
    out.push_str(":\n");
    for (d, o) in found.into_iter().take(a.limit) {
        let p = o.spec.as_ref().and_then(|x| x.position).unwrap_or_default();
        out.push_str(&format!(
            "- {} — {}\n",
            describe_object(o, &owner),
            format::where_is(d, format::bearing(&here, yaw, &p))
        ));
    }
    if s.objects_dropped {
        out.push_str("(The world has more objects than the local copy holds; some are missing.)\n");
    }
    Ok(out.trim_end().to_string())
}

struct ListArgs {
    radius: Option<f32>,
    mine: bool,
    limit: usize,
}

fn list_args(a: &Args) -> Result<ListArgs, String> {
    let radius = match a.f32("radius")? {
        Some(r) if r <= 0.0 => return Err(a.err("`radius` must be more than 0 metres")),
        r => r,
    };
    Ok(ListArgs {
        radius,
        mine: a.choice("owner", &["me", "anyone"])?.as_deref() == Some("me"),
        limit: a.u32("limit", 1, 500)?.unwrap_or(50) as usize,
    })
}

// ---------------------------------------------------------------- objects

/// Object fields as given by the model; validated against limits later.
#[derive(Default)]
struct SpecArgs {
    shape: Option<Shape>,
    position: Option<Vec3>,
    scale: Option<Vec3>,
    rotation: Option<Quat>,
    color: Option<tv_proto::v1::Color>,
    material: Option<Material>,
    name: Option<String>,
    solid: Option<bool>,
    locked: Option<bool>,
}

impl SpecArgs {
    fn is_empty(&self) -> bool {
        self.shape.is_none()
            && self.position.is_none()
            && self.scale.is_none()
            && self.rotation.is_none()
            && self.color.is_none()
            && self.material.is_none()
            && self.name.is_none()
            && self.solid.is_none()
            && self.locked.is_none()
    }
}

fn spec_args(a: &Args, create: bool) -> Result<SpecArgs, String> {
    let shape = a.choice("shape", SHAPES)?.map(|s| match s.as_str() {
        "box" => Shape::Box,
        "sphere" => Shape::Sphere,
        "cylinder" => Shape::Cylinder,
        "cone" => Shape::Cone,
        "wedge" => Shape::Wedge,
        _ => Shape::Plane,
    });
    let material = a.choice("material", MATERIALS)?.map(|m| match m.as_str() {
        "matte" => Material::Matte,
        "glossy" => Material::Glossy,
        "metal" => Material::Metal,
        "glass" => Material::Glass,
        _ => Material::Emissive,
    });
    if a.has("color") && a.has("colour") {
        return Err(a.err("give `color` or `colour`, not both"));
    }
    let colour = match a.string("color")? {
        Some(c) => Some(c),
        None => a.string("colour")?,
    };
    let color = colour.map(|c| format::parse_colour(&c).map_err(|e| a.err(e))).transpose()?;
    let rotation = a.vec3("rotation_euler_deg")?.map(|e| format::euler_to_quat(e.x, e.y, e.z));
    let spec = SpecArgs {
        shape,
        position: a.vec3("position")?,
        scale: a.scale("scale")?,
        rotation,
        color,
        material,
        name: a.string("name")?.map(|n| n.trim().to_string()),
        solid: a.bool("solid")?,
        locked: a.bool("locked")?,
    };
    if create {
        if spec.shape.is_none() {
            return Err(a.err(format!("`shape` is required: one of {}", SHAPES.join(", "))));
        }
        if spec.position.is_none() {
            return Err(a.missing("position"));
        }
    }
    Ok(spec)
}

/// Checks a spec against the world's limits and the own role.
fn check_spec(s: &Session, spec: &SpecArgs, tool: &str) -> Result<(), String> {
    if !s.can_build() {
        return Err(format!(
            "{tool}: your role is {}, which may not build or edit objects. Ask a world owner \
             for the builder role.",
            format::role_name(s.role)
        ));
    }
    let l = &s.limits;
    let (min, max) = (
        if l.min_scale > 0.0 { l.min_scale } else { 0.01 },
        if l.max_scale > 0.0 { l.max_scale } else { 64.0 },
    );
    if let Some(sc) = &spec.scale {
        for (axis, v) in [("x", sc.x), ("y", sc.y), ("z", sc.z)] {
            if v < min || v > max {
                return Err(format!(
                    "{tool}: scale.{axis} is {v} m; each axis must be from {} to {} m",
                    num(min),
                    num(max)
                ));
            }
        }
    }
    if let Some(p) = &spec.position
        && let Some(e) = outside(s, p, true)
    {
        return Err(format!("{tool}: position {e}"));
    }
    if let Some(n) = &spec.name {
        let max = if l.max_name_chars > 0 { l.max_name_chars } else { 64 } as usize;
        if n.chars().count() > max {
            return Err(format!("{tool}: `name` is longer than {max} characters"));
        }
    }
    Ok(())
}

async fn create(bridge: &Bridge, spec: SpecArgs) -> Outcome {
    with_session(bridge, |s| check_spec(s, &spec, "create_object").map(|_| String::new()))?;
    let full = ObjectSpec {
        name: spec.name.unwrap_or_default(),
        shape: spec.shape.unwrap_or(Shape::Box) as i32,
        position: spec.position,
        rotation: Some(spec.rotation.unwrap_or(Quat { x: 0.0, y: 0.0, z: 0.0, w: 1.0 })),
        scale: Some(spec.scale.unwrap_or(Vec3 { x: 1.0, y: 1.0, z: 1.0 })),
        color: Some(spec.color.unwrap_or(tv_proto::v1::Color { r: 1.0, g: 1.0, b: 1.0, a: 1.0 })),
        material: spec.material.unwrap_or(Material::Matte) as i32,
        solid: spec.solid.unwrap_or(true),
        locked: spec.locked.unwrap_or(false),
    };
    let msg =
        bridge.request(C::ObjectCreate(tv_proto::v1::ObjectCreate { spec: Some(full) })).await?;
    if let Some(e) = refused(&msg, "create the object") {
        return Err(e);
    }
    match msg.body {
        Some(S::ObjectCreated(c)) => {
            let o = c.object.unwrap_or_default();
            Ok(format!("Created {}.", render_own(bridge, &o)))
        }
        _ => Ok("The world accepted the object but did not say its id; use list_objects owner=me."
            .into()),
    }
}

fn render_own(bridge: &Bridge, o: &ObjectInfo) -> String {
    let st = bridge.lock();
    let s = st.session.as_ref();
    describe_object(o, &|id: &str| {
        s.and_then(|s| s.name_of_user(id)).unwrap_or_else(|| short_user(id))
    })
}

struct EditArgs {
    id: u64,
    spec: SpecArgs,
}

fn edit_args(a: &Args) -> Result<EditArgs, String> {
    let id = a.id("id")?.ok_or_else(|| a.missing("id"))?;
    let spec = spec_args(a, false)?;
    if spec.is_empty() {
        return Err(a.err(
            "nothing to change; give at least one of shape, position, scale, \
             rotation_euler_deg, color, material, name, solid, locked",
        ));
    }
    Ok(EditArgs { id, spec })
}

async fn edit(bridge: &Bridge, e: EditArgs) -> Outcome {
    with_session(bridge, |s| check_spec(s, &e.spec, "edit_object").map(|_| String::new()))?;
    let spec = e.spec;
    let update = tv_proto::v1::ObjectUpdate {
        id: e.id,
        name: spec.name,
        shape: spec.shape.map(|v| v as i32),
        position: spec.position,
        rotation: spec.rotation,
        scale: spec.scale,
        color: spec.color,
        material: spec.material.map(|v| v as i32),
        solid: spec.solid,
        locked: spec.locked,
    };
    let msg = bridge.request(C::ObjectUpdate(update)).await?;
    if let Some(err) = refused(&msg, &format!("change object {}", e.id)) {
        return Err(err);
    }
    match msg.body {
        Some(S::ObjectUpdated(u)) => {
            let o = u.object.unwrap_or_default();
            Ok(format!("Updated {}.", render_own(bridge, &o)))
        }
        _ => Ok(format!("The world accepted the change to object {}.", e.id)),
    }
}

async fn delete(bridge: &Bridge, id: u64) -> Outcome {
    let msg = bridge.request(C::ObjectDelete(tv_proto::v1::ObjectDelete { id })).await?;
    if let Some(e) = refused(&msg, &format!("delete object {id}")) {
        return Err(e);
    }
    Ok(format!("Deleted object {id}."))
}
