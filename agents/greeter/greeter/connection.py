"""One WebSocket connection to a Tinyverse world, speaking proto3 JSON."""

from __future__ import annotations

import asyncio
import hashlib
import itertools
import json
import logging
import ssl
import time
from typing import Any, Awaitable, Callable

import websockets

from . import protocol
from .world import WorldState

log = logging.getLogger(__name__)

#: Bodies that answer a request. The first of these carrying our seq as
#: replyTo resolves the request.
REPLY_BODIES = {
    "error", "ack", "pong", "observation", "chatMessage", "chatHistory",
    "objectCreated", "objectUpdated", "objectDeleted", "avatarUpdated",
}

#: Handshake refusals that retrying cannot fix.
FATAL_CODES = {
    "ERROR_CODE_UNSUPPORTED_VERSION", "ERROR_CODE_AUTH_FAILED",
    "ERROR_CODE_INVALID_TOKEN", "ERROR_CODE_BANNED", "ERROR_CODE_INVALID_ARGUMENT",
}

PING_INTERVAL_S = 10.0


PIN_PREFIX = "sha256="


def split_pin(url: str) -> tuple[str, str | None]:
    """Splits `wss://host:port/ws#sha256=<hex>` into the URL to connect to and
    the pin: the SHA-256 of the server's certificate, 64 hex digits in lower
    case (colons and upper case are accepted). Raises ValueError for any other
    fragment, a malformed pin, or a pin on a ws:// URL."""
    base, sep, fragment = url.partition("#")
    if not sep:
        return url, None
    if not fragment.lower().startswith(PIN_PREFIX):
        raise ValueError(f"the only URL fragment understood is #sha256=<certificate fingerprint>, not #{fragment}")
    digits = fragment[len(PIN_PREFIX):].replace(":", "").lower()
    if len(digits) != 64 or any(c not in "0123456789abcdef" for c in digits):
        raise ValueError("a #sha256= certificate pin must be 64 hex digits")
    if not base.startswith("wss://"):
        raise ValueError("a #sha256= certificate pin only applies to wss:// URLs")
    return base, digits


class CertificateMismatch(Exception):
    """The server's certificate is not the one pinned in the URL."""

    def __init__(self, expected: str, presented: str) -> None:
        super().__init__(f"the server's certificate has SHA-256 {presented}, not the pinned {expected}")
        self.expected = expected
        self.presented = presented


def tls_context(url: str, ca_file: str | None = None) -> ssl.SSLContext | None:
    """The TLS settings for `url`: None for ws://; for wss://, the system's
    trusted certificates, or only `ca_file` (a PEM file such as the world's
    self-signed cert.pem) when given; hostnames are then checked. With a pin
    in the URL (`#sha256=<hex>`) no certificate is trusted by the TLS layer:
    the caller checks the pin (see `check_pin`) before sending anything."""
    base, pin = split_pin(url)
    if not base.startswith("wss://"):
        if ca_file:
            raise ValueError("--ca-file needs a wss:// URL")
        return None
    if pin is not None:
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        return ctx
    return ssl.create_default_context(cafile=ca_file)


def check_pin(ws: Any, pin: str | None) -> None:
    """Raises CertificateMismatch unless the certificate the server of the open
    connection `ws` presented has SHA-256 `pin`. Call it before sending the
    Hello: nothing secret has gone to the server until then."""
    if pin is None:
        return
    ssl_object = ws.transport.get_extra_info("ssl_object")
    der = ssl_object.getpeercert(binary_form=True) if ssl_object is not None else None
    presented = hashlib.sha256(der).hexdigest() if der else ""
    if presented != pin:
        raise CertificateMismatch(pin, presented)


class ServerError(Exception):
    """The server answered a request with Error."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message


class Kicked(Exception):
    """The server removed us (owner kick or ban, or a second login)."""


EventHandler = Callable[[str, dict[str, Any], dict[str, Any]], Awaitable[None] | None]


class Connection:
    def __init__(self, url: str, state: WorldState, on_event: EventHandler | None = None,
                 ca_file: str | None = None) -> None:
        self.url = url
        self.ca_file = ca_file
        self.state = state
        self.on_event = on_event
        self._ws: Any = None
        self._seq = itertools.count(2)  # 1 is the Hello
        self._pending: dict[int, asyncio.Future] = {}
        self._tasks: list[asyncio.Task] = []
        self.closed = asyncio.Event()
        self.kicked_reason: str | None = None

    async def join(self, hello: dict[str, Any], timeout: float = 10.0) -> dict[str, Any]:
        """Connects, sends Hello and reads Welcome, the snapshot and the chat
        history. Returns the Welcome body. Raises ServerError on refusal."""
        # The websockets library sends WebSocket ping frames by itself, which
        # also keep the connection from idling out; we send protocol Pings too.
        base, pin = split_pin(self.url)
        self._ws = await websockets.connect(base, open_timeout=timeout, max_size=2**20,
                                            ssl=tls_context(self.url, self.ca_file))
        try:
            check_pin(self._ws, pin)
        except CertificateMismatch:
            await self._ws.close()
            raise
        await self._ws.send(json.dumps(protocol.message(1, "hello", hello)))
        welcome = None
        async with asyncio.timeout(timeout):
            while True:
                msg = json.loads(await self._ws.recv())
                kind, body = protocol.body_of(msg)
                if kind == "error":
                    await self._ws.close()
                    raise ServerError(body.get("code", ""), body.get("message", ""))
                self.state.apply(kind, body)
                if kind == "welcome":
                    welcome = body
                elif kind == "chatHistory":
                    # The last of the join sequence.
                    await self._dispatch(kind, body, msg)
                    break
                else:
                    await self._dispatch(kind, body, msg)
        self._tasks = [asyncio.create_task(self._reader()), asyncio.create_task(self._pinger())]
        assert welcome is not None
        return welcome

    async def close(self) -> None:
        for t in self._tasks:
            t.cancel()
        if self._ws is not None:
            try:
                await self._ws.close(code=1000, reason="bye")
            except Exception:  # noqa: BLE001 - closing a dead socket
                pass
        self._fail_pending(ConnectionError("connection closed"))
        self.closed.set()

    def next_seq(self) -> int:
        return next(self._seq)

    async def send(self, body: str, payload: dict[str, Any], seq: int = 0) -> None:
        await self._ws.send(json.dumps(protocol.message(seq, body, payload)))

    async def request(self, body: str, payload: dict[str, Any], timeout: float = 10.0) -> tuple[str, dict[str, Any]]:
        """Sends a message and waits for the reply that carries its seq.
        Returns (body name, body). Raises ServerError if the reply is Error."""
        if self.closed.is_set():
            raise ConnectionError("not connected")
        seq = self.next_seq()
        fut: asyncio.Future = asyncio.get_running_loop().create_future()
        self._pending[seq] = fut
        try:
            await self.send(body, payload, seq)
            kind, reply = await asyncio.wait_for(fut, timeout)
        finally:
            self._pending.pop(seq, None)
        if kind == "error":
            raise ServerError(reply.get("code", ""), reply.get("message", ""))
        return kind, reply

    async def _reader(self) -> None:
        try:
            async for raw in self._ws:
                msg = json.loads(raw)
                kind, body = protocol.body_of(msg)
                self.state.apply(kind, body)
                reply_to = int(msg.get("replyTo", 0) or 0)
                if reply_to and kind in REPLY_BODIES:
                    fut = self._pending.pop(reply_to, None)
                    if fut is not None and not fut.done():
                        fut.set_result((kind, body))
                if kind == "kicked":
                    self.kicked_reason = body.get("reason", "")
                await self._dispatch(kind, body, msg)
        except websockets.ConnectionClosed as e:
            log.info("connection closed: %s", e)
        except asyncio.CancelledError:
            raise
        except Exception:  # noqa: BLE001 - keep the agent alive, report it
            log.exception("reader failed")
        finally:
            self._fail_pending(ConnectionError("connection closed"))
            self.closed.set()

    async def _pinger(self) -> None:
        while True:
            await asyncio.sleep(PING_INTERVAL_S)
            try:
                await self.send("ping", {"clientTimeMs": str(int(time.time() * 1000))}, self.next_seq())
            except Exception:  # noqa: BLE001
                return

    async def _dispatch(self, kind: str, body: dict[str, Any], msg: dict[str, Any]) -> None:
        if self.on_event is None:
            return
        try:
            result = self.on_event(kind, body, msg)
            if asyncio.iscoroutine(result):
                await result
        except Exception:  # noqa: BLE001
            log.exception("event handler failed on %s", kind)

    def _fail_pending(self, exc: Exception) -> None:
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(exc)
        self._pending.clear()
