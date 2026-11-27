"""A local copy of the parts of the world the greeter cares about, kept up to
date from server events, and the proximity logic that decides when to greet."""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Any

from .protocol import read_vec3

Vec = tuple[float, float, float]


@dataclass
class Avatar:
    id: str
    user_id: str
    name: str
    kind: str  # "CLIENT_KIND_HUMAN" or "CLIENT_KIND_AGENT"
    role: str
    position: Vec = (0.0, 0.0, 0.0)
    yaw: float = 0.0
    velocity: Vec = (0.0, 0.0, 0.0)
    animation: str = ""

    @property
    def is_agent(self) -> bool:
        return self.kind == "CLIENT_KIND_AGENT"


@dataclass
class WorldState:
    """Everything learned from Welcome, Snapshot and the event stream."""

    me: Avatar | None = None
    world_name: str = ""
    spawn: Vec = (0.0, 0.0, 0.0)
    bounds_min: Vec = (-1e9, -1e9, -1e9)
    bounds_max: Vec = (1e9, 1e9, 1e9)
    limits: dict[str, Any] = field(default_factory=dict)
    avatars: dict[str, Avatar] = field(default_factory=dict)
    objects: dict[str, dict[str, Any]] = field(default_factory=dict)

    # ---------------------------------------------------------------- events

    def apply(self, kind: str, body: dict[str, Any]) -> None:
        """Updates the copy from one server message body."""
        if kind == "welcome":
            self.me = avatar_from(body.get("avatar", {}))
            world = body.get("world", {})
            self.world_name = world.get("name", "")
            self.spawn = read_vec3(world.get("spawnPoint"))
            self.bounds_min = read_vec3(world.get("boundsMin"))
            self.bounds_max = read_vec3(world.get("boundsMax"))
            self.limits = body.get("limits", {})
            self.avatars.clear()
            self.objects.clear()
        elif kind == "snapshot":
            for a in body.get("avatars", []):
                av = avatar_from(a)
                self.avatars[av.id] = av
            for o in body.get("objects", []):
                self.objects[str(o["id"])] = o
        elif kind == "avatarJoined":
            av = avatar_from(body.get("avatar", {}))
            self.avatars[av.id] = av
        elif kind == "avatarLeft":
            self.avatars.pop(str(body.get("avatarId")), None)
        elif kind == "avatarUpdated":
            av = avatar_from(body.get("avatar", {}))
            if self.me is not None and av.id == self.me.id:
                self.me = av
            else:
                self.avatars[av.id] = av
        elif kind == "avatarStates":
            for entry in body.get("states", []):
                self._move(str(entry.get("avatarId")), entry.get("transform", {}))
        elif kind == "avatarCorrection":
            if self.me is not None:
                self._move(self.me.id, body.get("transform", {}))
        elif kind in ("objectCreated", "objectUpdated"):
            o = body.get("object", {})
            self.objects[str(o.get("id"))] = o
        elif kind == "objectDeleted":
            self.objects.pop(str(body.get("id")), None)

    def _move(self, avatar_id: str, t: dict[str, Any]) -> None:
        target = self.me if self.me is not None and self.me.id == avatar_id else self.avatars.get(avatar_id)
        if target is None:
            return
        target.position = read_vec3(t.get("position"))
        target.yaw = float(t.get("yaw", 0.0))
        target.velocity = read_vec3(t.get("velocity"))
        target.animation = t.get("animation", "")

    # --------------------------------------------------------------- queries

    def distance_to(self, avatar: Avatar) -> float:
        if self.me is None:
            return math.inf
        return distance(self.me.position, avatar.position)

    def find_by_name(self, name: str) -> Avatar | None:
        n = name.strip().lower()
        for a in self.avatars.values():
            if a.name.lower() == n:
                return a
        for a in self.avatars.values():
            if a.name.lower().startswith(n):
                return a
        return None

    def own_objects(self) -> list[dict[str, Any]]:
        if self.me is None:
            return []
        return [o for o in self.objects.values() if o.get("ownerId") == self.me.user_id]

    def ground_y(self) -> float:
        """Where walking ends: the plane y = 0, clamped into the bounds."""
        return min(self.bounds_max[1], max(self.bounds_min[1], 0.0))

    def clamp_to_bounds(self, p: Vec) -> Vec:
        return tuple(min(hi, max(lo, v)) for v, lo, hi in zip(p, self.bounds_min, self.bounds_max))  # type: ignore[return-value]


def avatar_from(a: dict[str, Any]) -> Avatar:
    t = a.get("transform", {})
    return Avatar(
        id=str(a.get("id", "")),
        user_id=a.get("userId", ""),
        name=a.get("name", ""),
        kind=a.get("kind", "CLIENT_KIND_HUMAN"),
        role=a.get("role", ""),
        position=read_vec3(t.get("position")),
        yaw=float(t.get("yaw", 0.0)),
        velocity=read_vec3(t.get("velocity")),
        animation=t.get("animation", ""),
    )


def distance(a: Vec, b: Vec) -> float:
    return math.dist(a, b)


def horizontal_distance(a: Vec, b: Vec) -> float:
    return math.hypot(a[0] - b[0], a[2] - b[2])


def point_near(target: Vec, origin: Vec, gap: float) -> Vec:
    """A ground point `gap` metres short of `target` on the line from
    `origin`, so that walking there stops beside someone, not inside them."""
    dx, dz = target[0] - origin[0], target[2] - origin[2]
    d = math.hypot(dx, dz)
    if d <= gap:
        return (origin[0], origin[1], origin[2])
    k = (d - gap) / d
    return (origin[0] + dx * k, origin[1], origin[2] + dz * k)


def heading_from(origin: Vec, target: Vec) -> float:
    """Yaw in degrees (clockwise from +Z seen from above) pointing from
    `origin` to `target`."""
    return math.degrees(math.atan2(target[0] - origin[0], target[2] - origin[2])) % 360.0


class Proximity:
    """Decides whom to greet.

    A *visit* is one avatar's stay in the world: from its arrival (Snapshot
    or AvatarJoined) until AvatarLeft. Someone is greeted the first time
    during a visit that they are within `greet_radius` metres, and not
    again until they leave the world and come back (which gives them a new
    avatar id). Walking away and back, or the greeter walking over to them,
    does not count as a new visit.
    """

    def __init__(self, greet_radius: float = 4.0) -> None:
        self.greet_radius = greet_radius
        self._greeted: set[str] = set()

    def update(self, me: Vec, others: dict[str, Vec]) -> list[str]:
        """Feeds the current positions of everyone present (avatar id ->
        position); returns the ids to greet now, in id order."""
        # Forget avatars that have left, so the set does not grow forever.
        self._greeted &= set(others)
        arrived = [i for i, pos in others.items()
                   if i not in self._greeted and distance(me, pos) <= self.greet_radius]
        self._greeted.update(arrived)
        return sorted(arrived, key=lambda i: (len(i), i))

    def greeted(self, avatar_id: str) -> bool:
        return avatar_id in self._greeted

    def reset(self) -> None:
        self._greeted.clear()
