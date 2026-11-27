"""The greeter's actions, as tools for Claude and as plain calls for the
rule-based mode. `build_*` functions are pure: they turn tool input into one
protocol message (body name, payload). `ToolBox` sends them and waits."""

from __future__ import annotations

import asyncio
from typing import Any, Awaitable, Callable

from . import protocol
from .connection import Connection, ServerError
from .world import WorldState, horizontal_distance, point_near

MAX_OBJECTS_PER_REQUEST = 25

TOOL_SCHEMAS: list[dict[str, Any]] = [
    {
        "name": "look",
        "description": "Describe the people and objects around you, nearest first, with distance, "
                       "direction and object ids.",
        "input_schema": {
            "type": "object",
            "properties": {"radius": {"type": "number", "description": "Metres, default 30, max 100."}},
            "additionalProperties": False,
        },
    },
    {
        "name": "move_to",
        "description": "Walk to a person (by name) or to a point on the ground. Returns when you arrive. "
                       "Walking to a person stops about 1.5 m in front of them.",
        "input_schema": {
            "type": "object",
            "properties": {
                "person": {"type": "string", "description": "Display name of someone present."},
                "x": {"type": "number"},
                "z": {"type": "number"},
                "run": {"type": "boolean", "description": "Run (8 m/s) instead of walk (4 m/s)."},
            },
            "additionalProperties": False,
        },
    },
    {
        "name": "say",
        "description": "Say something in local chat right now, before your final reply (for example "
                       "'On my way!' before walking). Your final reply is spoken anyway.",
        "input_schema": {
            "type": "object",
            "properties": {"text": {"type": "string"}},
            "required": ["text"],
            "additionalProperties": False,
        },
    },
    {
        "name": "create_object",
        "description": "Create a primitive object. Coordinates are metres, Y up, ground at y=0; "
                       "position is the centre, so a 1 m box resting on the ground has y=0.5.",
        "input_schema": {
            "type": "object",
            "properties": {
                "shape": {"type": "string", "enum": list(protocol.SHAPES)},
                "x": {"type": "number"},
                "y": {"type": "number"},
                "z": {"type": "number"},
                "scale": {"description": "Metres: one number, or {x, y, z}. Each 0.01..64.",
                          "anyOf": [{"type": "number"},
                                    {"type": "object",
                                     "properties": {"x": {"type": "number"}, "y": {"type": "number"},
                                                    "z": {"type": "number"}}}]},
                "color": {"type": "string", "description": "A name (red, blue, ...) or #rrggbb."},
                "material": {"type": "string", "enum": list(protocol.MATERIALS)},
                "yaw_deg": {"type": "number", "description": "Turn clockwise seen from above, degrees."},
                "name": {"type": "string", "description": "Optional label, up to 64 characters."},
            },
            "required": ["shape", "x", "y", "z"],
            "additionalProperties": False,
        },
    },
    {
        "name": "edit_object",
        "description": "Change some properties of an object you created. Only the given fields change.",
        "input_schema": {
            "type": "object",
            "properties": {
                "id": {"type": "string"},
                "shape": {"type": "string", "enum": list(protocol.SHAPES)},
                "x": {"type": "number"},
                "y": {"type": "number"},
                "z": {"type": "number"},
                "scale": {"anyOf": [{"type": "number"},
                                    {"type": "object",
                                     "properties": {"x": {"type": "number"}, "y": {"type": "number"},
                                                    "z": {"type": "number"}}}]},
                "color": {"type": "string"},
                "material": {"type": "string", "enum": list(protocol.MATERIALS)},
                "yaw_deg": {"type": "number"},
                "name": {"type": "string"},
            },
            "required": ["id"],
            "additionalProperties": False,
        },
    },
    {
        "name": "delete_object",
        "description": "Delete an object you created.",
        "input_schema": {
            "type": "object",
            "properties": {"id": {"type": "string"}},
            "required": ["id"],
            "additionalProperties": False,
        },
    },
]


class ToolError(Exception):
    """A tool call that cannot be carried out; the text is shown to the model."""


# ------------------------------------------------------------- pure mapping

def build_look(args: dict[str, Any]) -> tuple[str, dict[str, Any]]:
    radius = float(args.get("radius", 0) or 0)
    if radius < 0:
        raise ToolError("radius must not be negative")
    return "observe", {"radius": radius}


def build_move_to(args: dict[str, Any], state: WorldState) -> tuple[str, dict[str, Any]]:
    """MoveTo for a person or a point. Walking keeps the current height."""
    if state.me is None:
        raise ToolError("not in the world")
    me = state.me.position
    if args.get("person"):
        who = state.find_by_name(str(args["person"]))
        if who is None:
            raise ToolError(f"nobody called {args['person']!r} is here")
        target = point_near(who.position, me, gap=1.5)
    elif "x" in args and "z" in args:
        target = (float(args["x"]), me[1], float(args["z"]))
    else:
        raise ToolError("give either person or both x and z")
    mode = "MOVE_MODE_RUN" if args.get("run") else "MOVE_MODE_WALK"
    return "moveTo", {"target": protocol.vec3(*target), "mode": mode}


def build_say(args: dict[str, Any], channel: str = "CHAT_CHANNEL_LOCAL") -> tuple[str, dict[str, Any]]:
    text = " ".join(str(args.get("text", "")).split())
    if not text:
        raise ToolError("text is empty")
    return "chat", {"channel": channel, "text": text[:500]}


def _spec_fields(args: dict[str, Any]) -> dict[str, Any]:
    out: dict[str, Any] = {}
    try:
        if "shape" in args:
            out["shape"] = protocol.shape_enum(args["shape"])
        if "scale" in args:
            out["scale"] = protocol.scale_vec(args["scale"])
        if "color" in args:
            out["color"] = protocol.parse_color(args["color"])
        if "material" in args:
            out["material"] = protocol.material_enum(args["material"])
        if "yaw_deg" in args:
            out["rotation"] = protocol.yaw_quat(float(args["yaw_deg"]))
    except ValueError as e:
        raise ToolError(str(e)) from e
    if "name" in args:
        out["name"] = str(args["name"])[:64]
    return out


def build_create_object(args: dict[str, Any]) -> tuple[str, dict[str, Any]]:
    for key in ("shape", "x", "y", "z"):
        if key not in args:
            raise ToolError(f"{key} is required")
    spec = {
        "name": "",
        "position": protocol.vec3(args["x"], args["y"], args["z"]),
        "rotation": protocol.yaw_quat(0.0),
        "scale": protocol.vec3(1, 1, 1),
        "color": {"r": 0.8, "g": 0.8, "b": 0.8, "a": 1.0},
        "material": "MATERIAL_MATTE",
        # The protocol default is false; things people build are usually solid.
        "solid": True,
        "locked": False,
    }
    spec.update(_spec_fields(args))
    return "objectCreate", {"spec": spec}


def build_edit_object(args: dict[str, Any], state: WorldState) -> tuple[str, dict[str, Any]]:
    oid = _own_object_id(args, state)
    patch: dict[str, Any] = {"id": oid}
    patch.update(_spec_fields(args))
    if any(k in args for k in ("x", "y", "z")):
        old = state.objects[oid]["spec"]["position"]
        patch["position"] = protocol.vec3(args.get("x", old["x"]), args.get("y", old["y"]), args.get("z", old["z"]))
    if len(patch) == 1:
        raise ToolError("nothing to change")
    return "objectUpdate", patch


def build_delete_object(args: dict[str, Any], state: WorldState) -> tuple[str, dict[str, Any]]:
    return "objectDelete", {"id": _own_object_id(args, state)}


def _own_object_id(args: dict[str, Any], state: WorldState) -> str:
    try:
        oid = protocol.short_id(args.get("id"))
    except (TypeError, ValueError):
        raise ToolError("id must be an object id such as \"12\"") from None
    obj = state.objects.get(oid)
    if obj is None:
        raise ToolError(f"there is no object {oid}")
    if state.me is None or obj.get("ownerId") != state.me.user_id:
        # The server would allow editing unlocked objects of others; a guest
        # leaves other people's things alone.
        raise ToolError(f"object {oid} belongs to someone else; I only change my own objects")
    return oid


def describe_object(o: dict[str, Any]) -> str:
    s = o.get("spec", {})
    p = s.get("position", {})
    sc = s.get("scale", {})
    name = f' "{s["name"]}"' if s.get("name") else ""
    return (f'{s.get("shape", "").removeprefix("SHAPE_").lower()}{name} id {o.get("id")} at '
            f'({p.get("x", 0):.1f}, {p.get("y", 0):.1f}, {p.get("z", 0):.1f}), '
            f'size {sc.get("x", 0):g}x{sc.get("y", 0):g}x{sc.get("z", 0):g} m')


# --------------------------------------------------------------- execution

class ToolBox:
    """Runs tools against a live connection."""

    def __init__(self, conn: Connection, state: WorldState,
                 speak: Callable[[str, str], Awaitable[bool]]) -> None:
        self.conn = conn
        self.state = state
        self.speak = speak
        self.created_this_request = 0

    async def run(self, name: str, args: dict[str, Any]) -> str:
        """Runs one tool; returns its text result. Raises ToolError."""
        try:
            if name == "look":
                return await self.look(args)
            if name == "move_to":
                return await self.move_to(args)
            if name == "say":
                body, payload = build_say(args)
                ok = await self.speak(payload["text"], payload["channel"])
                return "said" if ok else "not said: you are talking too much; wait a little"
            if name == "create_object":
                if self.created_this_request >= MAX_OBJECTS_PER_REQUEST:
                    raise ToolError(f"at most {MAX_OBJECTS_PER_REQUEST} objects per request")
                _, body = await self.conn.request(*build_create_object(args))
                self.created_this_request += 1
                return "created " + describe_object(body["object"])
            if name == "edit_object":
                _, body = await self.conn.request(*build_edit_object(args, self.state))
                return "now " + describe_object(body["object"])
            if name == "delete_object":
                _, body = await self.conn.request(*build_delete_object(args, self.state))
                return f"deleted object {body['id']}"
        except ServerError as e:
            raise ToolError(f"the world refused: {e}") from e
        except (ConnectionError, asyncio.TimeoutError) as e:
            raise ToolError(f"no answer from the world: {e}") from e
        raise ToolError(f"unknown tool {name}")

    async def look(self, args: dict[str, Any]) -> str:
        _, obs = await self.conn.request(*build_look(args))
        mine = [o["object"]["id"] for o in obs.get("objects", [])
                if self.state.me and o["object"].get("ownerId") == self.state.me.user_id]
        extra = f"\nObjects you created here: {', '.join(mine)}." if mine else ""
        return obs.get("summary", "") + extra

    async def move_to(self, args: dict[str, Any]) -> str:
        body, payload = build_move_to(args, self.state)
        target = protocol.read_vec3(payload["target"])
        me = self.state.me
        assert me is not None
        if horizontal_distance(me.position, target) < 0.3:
            return "already there"
        await self.conn.request(body, payload)
        arrived = await wait_for_arrival(self.state, target, run=bool(args.get("run")))
        p = self.state.me.position if self.state.me else target
        where = f"({p[0]:.1f}, {p[2]:.1f})"
        return f"arrived at {where}" if arrived else f"stopped at {where} before arriving"


async def wait_for_arrival(state: WorldState, target: tuple[float, float, float],
                           run: bool = False, tolerance: float = 0.05) -> bool:
    """Waits until our avatar stands at `target` (horizontally). The server
    sends our own position in AvatarStates while it moves us; on arrival the
    last entry has the target position, zero velocity and animation "idle"."""
    if state.me is None:
        return False
    speed = float(state.limits.get("runSpeed" if run else "walkSpeed", 4.0) or 4.0)
    deadline = asyncio.get_running_loop().time() + horizontal_distance(state.me.position, target) / speed + 3.0
    while asyncio.get_running_loop().time() < deadline:
        me = state.me
        if me is not None and horizontal_distance(me.position, target) <= tolerance:
            return True
        await asyncio.sleep(0.1)
    return False
