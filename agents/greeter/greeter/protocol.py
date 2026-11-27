"""Building and reading Tinyverse protocol messages (proto3 JSON).

Pure functions only: nothing here touches the network. See docs/AGENT_API.md
for the meaning of every message and field.
"""

from __future__ import annotations

import math
from typing import Any

PROTOCOL_VERSION = 1

#: The bodies a ServerMessage can carry. Exactly one is set per message.
SERVER_BODIES = (
    "welcome",
    "snapshot",
    "avatarJoined",
    "avatarLeft",
    "avatarStates",
    "avatarCorrection",
    "avatarUpdated",
    "chatMessage",
    "chatHistory",
    "objectCreated",
    "objectUpdated",
    "objectDeleted",
    "observation",
    "error",
    "kicked",
    "pong",
    "ack",
)

SHAPES = ("box", "sphere", "cylinder", "cone", "wedge", "plane")
MATERIALS = ("matte", "glossy", "metal", "glass", "emissive")

#: A few colour names, as sRGB 0..1 (the protocol's colour space).
COLOR_NAMES: dict[str, tuple[float, float, float]] = {
    "red": (0.86, 0.13, 0.13),
    "orange": (1.0, 0.55, 0.0),
    "yellow": (1.0, 0.85, 0.1),
    "green": (0.15, 0.65, 0.2),
    "blue": (0.15, 0.35, 0.9),
    "purple": (0.55, 0.25, 0.75),
    "pink": (1.0, 0.5, 0.7),
    "white": (1.0, 1.0, 1.0),
    "grey": (0.5, 0.5, 0.5),
    "gray": (0.5, 0.5, 0.5),
    "black": (0.05, 0.05, 0.05),
    "brown": (0.45, 0.28, 0.12),
    "cyan": (0.0, 0.8, 0.85),
}


def message(seq: int, body: str, payload: dict[str, Any]) -> dict[str, Any]:
    """A ClientMessage: `seq` plus exactly one body."""
    return {"seq": seq, body: payload}


def body_of(msg: dict[str, Any]) -> tuple[str, dict[str, Any]]:
    """The name and content of the body a ServerMessage carries."""
    for name in SERVER_BODIES:
        if name in msg:
            return name, msg[name] or {}
    return "", {}


def hello(name: str = "", token: str = "", password: str = "",
          client_name: str = "tinyverse-greeter/0.1",
          body_color: tuple[float, float, float] | None = None) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "protocolVersion": PROTOCOL_VERSION,
        "kind": "CLIENT_KIND_AGENT",
        "name": name,
        "token": token,
        "password": password,
        "clientName": client_name,
    }
    if body_color is not None:
        r, g, b = body_color
        payload["appearance"] = {"bodyColor": {"r": r, "g": g, "b": b, "a": 1.0}}
    return payload


def vec3(x: float, y: float, z: float) -> dict[str, float]:
    return {"x": float(x), "y": float(y), "z": float(z)}


def read_vec3(v: dict[str, Any] | None) -> tuple[float, float, float]:
    v = v or {}
    return (float(v.get("x", 0.0)), float(v.get("y", 0.0)), float(v.get("z", 0.0)))


def yaw_quat(yaw_deg: float) -> dict[str, float]:
    """A rotation of `yaw_deg` degrees about +Y: clockwise seen from above,
    the same as Unity's Quaternion.Euler(0, yaw, 0)."""
    half = math.radians(yaw_deg) / 2.0
    return {"x": 0.0, "y": math.sin(half), "z": 0.0, "w": math.cos(half)}


def parse_color(value: Any) -> dict[str, float]:
    """A colour name, '#rgb', '#rrggbb' or '#rrggbbaa' as a protocol Color
    (sRGB, each channel 0..1, as in a hex code)."""
    if isinstance(value, dict):
        return {k: float(min(1.0, max(0.0, value.get(k, 1.0)))) for k in ("r", "g", "b", "a")}
    text = str(value).strip().lower()
    if text in COLOR_NAMES:
        r, g, b = COLOR_NAMES[text]
        return {"r": r, "g": g, "b": b, "a": 1.0}
    if text.startswith("#"):
        h = text[1:]
        if len(h) == 3:
            h = "".join(c * 2 for c in h)
        if len(h) in (6, 8) and all(c in "0123456789abcdef" for c in h):
            chans = [int(h[i:i + 2], 16) / 255.0 for i in range(0, len(h), 2)]
            if len(chans) == 3:
                chans.append(1.0)
            return dict(zip(("r", "g", "b", "a"), chans))
    raise ValueError(f"{value!r} is not a colour; use a name like 'red' or a hex code like '#ff8800'")


def shape_enum(name: str) -> str:
    n = str(name).strip().lower()
    if n == "cube":
        n = "box"
    if n == "ball":
        n = "sphere"
    if n not in SHAPES:
        raise ValueError(f"unknown shape {name!r}; use one of {', '.join(SHAPES)}")
    return "SHAPE_" + n.upper()


def material_enum(name: str) -> str:
    n = str(name).strip().lower()
    if n not in MATERIALS:
        raise ValueError(f"unknown material {name!r}; use one of {', '.join(MATERIALS)}")
    return "MATERIAL_" + n.upper()


def scale_vec(value: Any) -> dict[str, float]:
    """One number for all three axes, or {x, y, z}."""
    if isinstance(value, (int, float)):
        return vec3(value, value, value)
    if isinstance(value, dict):
        return vec3(value.get("x", 1.0), value.get("y", 1.0), value.get("z", 1.0))
    raise ValueError("scale must be a number or {x, y, z}")


def short_id(value: Any) -> str:
    """64-bit ids travel as strings in JSON."""
    return str(int(value))
