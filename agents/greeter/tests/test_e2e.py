"""Runs the greeter in --no-llm mode against a real tinyverse-server.

Skipped unless the server binary has been built (`cargo build` in server/),
or TINYVERSE_SERVER points at one.
"""

import asyncio
import contextlib
import json
import os
import signal
import socket
import ssl
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

import pytest
import websockets

HERE = Path(__file__).resolve().parent
GREETER_DIR = HERE.parent
REPO = GREETER_DIR.parents[1]


def find_server() -> Path | None:
    env = os.environ.get("TINYVERSE_SERVER")
    candidates = [Path(env)] if env else []
    candidates += [REPO / "server/target/debug/tinyverse-server", REPO / "server/target/release/tinyverse-server"]
    return next((c for c in candidates if c.is_file() and os.access(c, os.X_OK)), None)


SERVER = find_server()
pytestmark = pytest.mark.skipif(
    SERVER is None,
    reason="tinyverse-server is not built; run `cargo build` in server/ or set TINYVERSE_SERVER",
)


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@contextlib.contextmanager
def serve_world(tmp_path, tls=False):
    """A fresh world served on a free port; yields (url, greeter token,
    path of the self-signed cert.pem or None)."""
    wdir = tmp_path / "world"
    subprocess.run([SERVER, "init", wdir], check=True, capture_output=True)
    out = subprocess.run([SERVER, "token", "create", wdir, "--name", "Greeter", "--role", "builder"],
                         check=True, capture_output=True, text=True).stdout
    token = next(line.split()[-1] for line in out.splitlines() if line.startswith("token:"))
    cert = None
    if tls:
        subprocess.run([SERVER, "tls", "self-signed", wdir], check=True, capture_output=True)
        cert = wdir / "cert.pem"
    port = free_port()
    log = open(tmp_path / "server.log", "w")
    proc = subprocess.Popen([SERVER, "run", wdir, "--port", str(port), "--bind", "127.0.0.1"],
                            stdout=log, stderr=subprocess.STDOUT)
    scheme = "https" if tls else "http"
    context = ssl.create_default_context(cafile=cert) if tls else None
    deadline = time.time() + 10
    while True:
        try:
            urllib.request.urlopen(f"{scheme}://127.0.0.1:{port}/health", timeout=1, context=context)
            break
        except OSError:
            if time.time() > deadline or proc.poll() is not None:
                proc.kill()
                pytest.fail("the server did not start")
            time.sleep(0.1)
    try:
        yield f"{'wss' if tls else 'ws'}://127.0.0.1:{port}/ws", token, cert
    finally:
        proc.send_signal(signal.SIGTERM)
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
        log.close()


@pytest.fixture
def world(tmp_path):
    with serve_world(tmp_path) as (url, token, _):
        yield url, token


@pytest.fixture
def tls_world(tmp_path):
    with serve_world(tmp_path, tls=True) as served:
        yield served


class Client:
    """A minimal participant: records everything it receives."""

    def __init__(self, ws, welcome):
        self.ws = ws
        self.welcome = welcome
        self.inbox: list[dict] = []
        self.seq = 1
        self.task = asyncio.create_task(self._read())

    @classmethod
    async def join(cls, url, name, kind="CLIENT_KIND_HUMAN", ca_file=None):
        ctx = ssl.create_default_context(cafile=ca_file) if url.startswith("wss://") else None
        ws = await websockets.connect(url, ssl=ctx)
        await ws.send(json.dumps({"seq": 1, "hello": {"protocolVersion": 1, "kind": kind, "name": name}}))
        welcome = json.loads(await ws.recv())["welcome"]
        return cls(ws, welcome)

    async def _read(self):
        try:
            async for raw in self.ws:
                self.inbox.append(json.loads(raw))
        except websockets.ConnectionClosed:
            pass

    async def send(self, body, payload):
        self.seq += 1
        await self.ws.send(json.dumps({"seq": self.seq, body: payload}))

    async def say(self, text, channel="CHAT_CHANNEL_LOCAL"):
        await self.send("chat", {"channel": channel, "text": text})

    def chats_from(self, name):
        return [m["chatMessage"]["entry"]["text"] for m in self.inbox
                if "chatMessage" in m and m["chatMessage"]["entry"]["senderName"] == name]

    async def wait_for(self, pred, timeout=10.0):
        deadline = asyncio.get_running_loop().time() + timeout
        while asyncio.get_running_loop().time() < deadline:
            for m in self.inbox:
                if pred(m):
                    return m
            await asyncio.sleep(0.05)
        raise AssertionError(f"timed out; last messages: {self.inbox[-5:]}")

    async def close(self):
        await self.ws.close()
        self.task.cancel()


def greeter_avatar_id(client):
    for m in client.inbox:
        for a in m.get("snapshot", {}).get("avatars", []) + [m.get("avatarJoined", {}).get("avatar", {})]:
            if a.get("name") == "Greeter":
                return a["id"]
    return None


def position_of(client, avatar_id):
    pos = None
    for m in client.inbox:
        for e in m.get("avatarStates", {}).get("states", []):
            if e["avatarId"] == avatar_id:
                pos = e["transform"]["position"]
    return pos


async def scenario(url, token, tmp_path):
    env = {**os.environ, "PYTHONUNBUFFERED": "1"}
    env.pop("ANTHROPIC_API_KEY", None)
    log = open(tmp_path / "greeter.log", "w")
    greeter = await asyncio.create_subprocess_exec(
        sys.executable, "-m", "greeter", "--url", url, "--token", token, "--no-llm",
        cwd=GREETER_DIR, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        await asyncio.sleep(2.0)  # let it join and walk to its post
        tess = await Client.join(url, "Tess")

        # 1. Greeting: Tess appears at the spawn point, 2.8 m from the post.
        await tess.wait_for(lambda m: any("Hi Tess" in t for t in tess.chats_from("Greeter")))
        gid = greeter_avatar_id(tess)
        assert gid is not None

        # 2. Walk away, then ask the greeter to come over.
        await tess.send("moveTo", {"target": {"x": 12, "y": 0, "z": -8}, "mode": "MOVE_MODE_RUN"})
        await asyncio.sleep(3.0)
        await tess.say("Greeter, come here")
        await tess.wait_for(lambda m: "Here I am." in tess.chats_from("Greeter"), timeout=15)
        p = position_of(tess, gid)
        assert p is not None and ((p["x"] - 12) ** 2 + (p["z"] + 8) ** 2) ** 0.5 < 2.0, p

        # 3. Build on request.
        await tess.say("please build a blue box")
        created = await tess.wait_for(
            lambda m: "objectCreated" in m and m["objectCreated"]["byAvatarId"] == gid)
        spec = created["objectCreated"]["object"]["spec"]
        assert spec["shape"] == "SHAPE_BOX" and spec["color"]["b"] > 0.8

        # 4. Another agent cannot pull it into an endless conversation. Wait
        # first so the greeter's own speech limit (one line per 4 s, burst 3)
        # has refilled after the lines above.
        await asyncio.sleep(10.0)
        robo = await Client.join(url, "Robo", kind="CLIENT_KIND_AGENT")
        await robo.send("moveTo", {"target": {"x": 12, "y": 0, "z": -7}})
        for _ in range(4):
            await robo.say("Greeter, hello again")
            await asyncio.sleep(1.5)
        await asyncio.sleep(1.0)
        to_robo = [t for t in robo.chats_from("Greeter") if "Hello Robo" in t]
        assert len(to_robo) == 2, robo.chats_from("Greeter")
        assert not any("Robo" in t and "welcome" in t for t in robo.chats_from("Greeter"))  # agents not greeted
        await robo.close()

        # 5. SIGINT: it leaves cleanly.
        greeter.send_signal(signal.SIGINT)
        assert await asyncio.wait_for(greeter.wait(), 10) == 0
        await tess.wait_for(lambda m: m.get("avatarLeft", {}).get("avatarId") == gid)
        await tess.close()
    finally:
        if greeter.returncode is None:
            greeter.kill()
            await greeter.wait()
        log.close()


def test_greeter_end_to_end(world, tmp_path):
    url, token = world
    try:
        asyncio.run(scenario(url, token, tmp_path))
    except BaseException:
        print((tmp_path / "greeter.log").read_text())
        raise


async def tls_scenario(url, token, cert, tmp_path):
    env = {**os.environ, "PYTHONUNBUFFERED": "1"}
    env.pop("ANTHROPIC_API_KEY", None)
    base = [sys.executable, "-m", "greeter", "--url", url, "--token", token, "--no-llm"]

    # Without the certificate the greeter refuses the self-signed server and exits 2.
    untrusted = await asyncio.create_subprocess_exec(*base, cwd=GREETER_DIR, env=env,
                                                     stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out, _ = await asyncio.wait_for(untrusted.communicate(), 15)
    assert untrusted.returncode == 2, out
    assert b"--ca-file" in out and b"#sha256=" in out

    # Another certificate's pin: refused before the token is sent, exit 2.
    wrong = await asyncio.create_subprocess_exec(
        sys.executable, "-m", "greeter", "--url", f"{url}#sha256={'0' * 64}", "--token", token, "--no-llm",
        cwd=GREETER_DIR, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out, _ = await asyncio.wait_for(wrong.communicate(), 15)
    assert wrong.returncode == 2, out
    assert b"not the pinned" in out

    # The right pin, no --ca-file: joins and greets.
    fingerprint = subprocess.run([SERVER, "tls", "fingerprint", Path(cert).parent], check=True,
                                 capture_output=True, text=True).stdout
    pin = fingerprint.split()[2].removeprefix("sha256=")
    log = open(tmp_path / "greeter-pinned.log", "w")
    pinned = await asyncio.create_subprocess_exec(
        sys.executable, "-m", "greeter", "--url", f"{url}#sha256={pin}", "--token", token, "--no-llm",
        cwd=GREETER_DIR, env=env, stdout=log, stderr=subprocess.STDOUT)
    try:
        await asyncio.sleep(2.0)
        pia = await Client.join(url, "Pia", ca_file=cert)
        await pia.wait_for(lambda m: any("Hi Pia" in t for t in pia.chats_from("Greeter")))
        pinned.send_signal(signal.SIGINT)
        assert await asyncio.wait_for(pinned.wait(), 10) == 0
        await pia.close()
    finally:
        if pinned.returncode is None:
            pinned.kill()
            await pinned.wait()
        log.close()

    log = open(tmp_path / "greeter-tls.log", "w")
    greeter = await asyncio.create_subprocess_exec(*base, "--ca-file", str(cert), cwd=GREETER_DIR, env=env,
                                                   stdout=log, stderr=subprocess.STDOUT)
    try:
        await asyncio.sleep(2.0)
        tess = await Client.join(url, "Tess", ca_file=cert)
        await tess.wait_for(lambda m: any("Hi Tess" in t for t in tess.chats_from("Greeter")))
        greeter.send_signal(signal.SIGINT)
        assert await asyncio.wait_for(greeter.wait(), 10) == 0
        await tess.close()
    finally:
        if greeter.returncode is None:
            greeter.kill()
            await greeter.wait()
        log.close()


def test_greeter_over_wss(tls_world, tmp_path):
    url, token, cert = tls_world
    assert url.startswith("wss://")
    try:
        asyncio.run(tls_scenario(url, token, cert, tmp_path))
    except BaseException:
        print((tmp_path / "greeter-tls.log").read_text() if (tmp_path / "greeter-tls.log").exists() else "")
        raise
