"""The greeter's main loop: join, stand at the post, watch who comes near,
listen to chat, and hand triggers to the brain one at a time."""

from __future__ import annotations

import asyncio
import logging
import math
import signal
import time
from dataclasses import dataclass
from typing import Any

from . import protocol
from .brains import ClaudeBrain, RuleBrain, Trigger
from .connection import FATAL_CODES, Connection, ServerError
from .policy import Heard, Memory, ReplyPolicy, SpeechLimiter, fit_chat
from .tools import ToolBox, ToolError
from .world import Proximity, WorldState, horizontal_distance

log = logging.getLogger("greeter")

#: How far from the spawn point the greeter waits, so it does not stand
#: inside people who appear there.
POST_OFFSET = (2.0, 0.0, 2.0)
#: Walk back to the post after this long without anything to do.
RETURN_AFTER_S = 60.0
#: Pending triggers beyond this are dropped (oldest first).
MAX_QUEUE = 4


@dataclass
class Options:
    url: str
    token: str = ""
    name: str = ""
    password: str = ""
    no_llm: bool = False
    model: str | None = None
    greet_radius: float = 4.0
    hear_radius: float = 10.0
    greet_agents: bool = False


class FatalError(Exception):
    """Joining cannot succeed by retrying (bad token, banned, ...)."""


class Greeter:
    def __init__(self, opts: Options) -> None:
        self.opts = opts
        self.state = WorldState()
        self.memory = Memory()
        self.limiter = SpeechLimiter()
        self.proximity = Proximity(opts.greet_radius)
        self.policy = ReplyPolicy(hear_radius=opts.hear_radius)
        self.queue: asyncio.Queue[Trigger] = asyncio.Queue()
        self.stop = asyncio.Event()
        self.conn: Connection | None = None
        self.tools: ToolBox | None = None
        self.brain: Any = None
        self.last_activity = time.monotonic()
        self.busy = False
        self.session_token = ""

    # -------------------------------------------------------------- speaking

    async def speak(self, text: str, channel: str = "CHAT_CHANNEL_LOCAL") -> bool:
        """Says `text` unless the greeter has been talking too much."""
        text = fit_chat(text, int(self.state.limits.get("maxChatChars", 500) or 500))
        if not text or self.conn is None:
            return False
        if not self.limiter.allow():
            log.info("holding back (own rate limit): %s", text)
            return False
        try:
            await self.conn.request("chat", {"channel": channel, "text": text})
        except (ServerError, ConnectionError, asyncio.TimeoutError) as e:
            log.warning("could not say %r: %s", text, e)
            return False
        return True

    # ---------------------------------------------------------------- events

    def on_event(self, kind: str, body: dict[str, Any], msg: dict[str, Any]) -> None:
        """Called by the connection for every server message, after the world
        copy is updated. Must not wait on requests (it runs in the reader)."""
        me = self.state.me
        if kind == "chatMessage":
            e = body.get("entry", {})
            own = me is not None and e.get("senderUserId") == me.user_id
            who = f"{e.get('senderName')} ({'agent' if e.get('senderKind') == 'CLIENT_KIND_AGENT' else 'human'})"
            chan = "world" if e.get("channel") == "CHAT_CHANNEL_WORLD" else "local"
            self.memory.add(f"[{chan}] {'you' if own else who}: {e.get('text', '')}")
            if not own:
                self._consider_chat(e)
        elif kind == "avatarJoined":
            a = body.get("avatar", {})
            self.memory.add(f"{a.get('name')} joined the world.")
            self._check_proximity()
        elif kind == "avatarLeft":
            self.memory.add("Someone left the world.")
        elif kind == "objectCreated" and me is not None and body.get("byAvatarId") != me.id:
            self.memory.add(f"Someone built a {body['object']['spec']['shape'].removeprefix('SHAPE_').lower()} "
                            f"(id {body['object']['id']}).")
        elif kind == "avatarStates":
            self._check_proximity()

    def _consider_chat(self, e: dict[str, Any]) -> None:
        sender = self.state.avatars.get(str(e.get("senderAvatarId")))
        distance = self.state.distance_to(sender) if sender else math.inf
        heard = Heard(
            sender_user_id=e.get("senderUserId", ""),
            sender_name=e.get("senderName", ""),
            sender_is_agent=e.get("senderKind") == "CLIENT_KIND_AGENT",
            channel=e.get("channel", "CHAT_CHANNEL_LOCAL"),
            text=e.get("text", ""),
            distance=distance,
        )
        if self.policy.should_reply(heard):
            self._enqueue(Trigger("chat", heard.sender_name, heard.sender_is_agent, heard.text,
                                  heard.channel, distance))

    def _check_proximity(self) -> None:
        me = self.state.me
        if me is None:
            return
        others = {a.id: a.position for a in self.state.avatars.values()
                  if self.opts.greet_agents or not a.is_agent}
        for avatar_id in self.proximity.update(me.position, others):
            a = self.state.avatars[avatar_id]
            self.memory.add(f"{a.name} came within {self.opts.greet_radius:g} m of you.")
            self._enqueue(Trigger("greet", a.name, a.is_agent, distance=self.state.distance_to(a)))

    def _enqueue(self, t: Trigger) -> None:
        while self.queue.qsize() >= MAX_QUEUE:
            dropped = self.queue.get_nowait()
            log.info("busy; dropping %s from %s", dropped.kind, dropped.name)
        self.queue.put_nowait(t)

    # ------------------------------------------------------------------ main

    async def run(self) -> int:
        loop = asyncio.get_running_loop()
        for sig in (signal.SIGINT, signal.SIGTERM):
            loop.add_signal_handler(sig, self.stop.set)
        backoff = 1.0
        try:
            while not self.stop.is_set():
                try:
                    await self._session()
                    backoff = 1.0
                except FatalError as e:
                    log.error("%s", e)
                    return 2
                except (OSError, ServerError, asyncio.TimeoutError) as e:
                    log.warning("connection failed: %s", e)
                if self.stop.is_set():
                    break
                if self.conn is not None and self.conn.kicked_reason is not None:
                    log.error("removed from the world: %s", self.conn.kicked_reason)
                    return 3
                log.info("reconnecting in %.0f s", backoff)
                try:
                    await asyncio.wait_for(self.stop.wait(), backoff)
                except asyncio.TimeoutError:
                    pass
                backoff = min(30.0, backoff * 2)
        finally:
            for sig in (signal.SIGINT, signal.SIGTERM):
                loop.remove_signal_handler(sig)
        log.info("stopped")
        return 0

    async def _session(self) -> None:
        conn = Connection(self.opts.url, self.state, self.on_event)
        # After a first join without an agent token, reuse the session token
        # so we come back as the same user.
        token = self.opts.token or self.session_token
        hello = protocol.hello(name=self.opts.name, token=token, password=self.opts.password,
                               body_color=(0.95, 0.75, 0.2))
        try:
            welcome = await conn.join(hello)
        except ServerError as e:
            if e.code in FATAL_CODES:
                raise FatalError(f"the world refused to let us in: {e}") from e
            raise
        self.conn = conn
        self.session_token = welcome.get("sessionToken") or self.session_token
        me = self.state.me
        assert me is not None
        log.info("joined %r as %s (avatar %s, role %s)", self.state.world_name, me.name, me.id, welcome.get("role"))
        self.policy.my_user_id, self.policy.my_name = me.user_id, me.name
        self.proximity.reset()
        self.tools = ToolBox(conn, self.state, self.speak)
        if self.brain is None:
            self.brain = RuleBrain(me.name) if self.opts.no_llm else ClaudeBrain(me.name, self.opts.model)
        self.memory.add(f"You joined {self.state.world_name} as {me.name}.")

        worker = asyncio.create_task(self._worker())
        try:
            await self._go_to_post()
            self._check_proximity()
            stop = asyncio.create_task(self.stop.wait())
            closed = asyncio.create_task(conn.closed.wait())
            while not (self.stop.is_set() or conn.closed.is_set()):
                await asyncio.wait({stop, closed}, timeout=1.0, return_when=asyncio.FIRST_COMPLETED)
                self._check_proximity()
                if (not self.busy and self.queue.empty()
                        and time.monotonic() - self.last_activity > RETURN_AFTER_S):
                    self.last_activity = time.monotonic()
                    await self._go_to_post()
            stop.cancel()
            closed.cancel()
        finally:
            worker.cancel()
            await conn.close()

    async def _worker(self) -> None:
        """Handles triggers one at a time, so the greeter never talks over itself."""
        while True:
            t = await self.queue.get()
            self.busy = True
            self.last_activity = time.monotonic()
            try:
                assert self.tools is not None
                await self.brain.handle(t, self.tools, self.speak, self.memory, self.state)
            except asyncio.CancelledError:
                raise
            except Exception:  # noqa: BLE001 - one bad turn must not end the visit
                log.exception("handling %s from %s failed", t.kind, t.name)
            finally:
                self.busy = False
            self.last_activity = time.monotonic()

    def post(self) -> tuple[float, float, float]:
        sx, sy, sz = self.state.spawn
        return self.state.clamp_to_bounds((sx + POST_OFFSET[0], sy, sz + POST_OFFSET[2]))

    async def _go_to_post(self) -> None:
        me = self.state.me
        if me is None or self.tools is None:
            return
        x, _, z = self.post()
        if horizontal_distance(me.position, (x, 0, z)) < 0.5:
            return
        try:
            await self.tools.run("move_to", {"x": x, "z": z})
        except ToolError as e:
            log.warning("cannot walk to my post: %s", e)
