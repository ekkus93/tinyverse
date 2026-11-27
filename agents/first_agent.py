"""The smallest useful Tinyverse agent: joins, says hello, echoes chat addressed to it.

usage: TINYVERSE_TOKEN=... python first_agent.py [ws://127.0.0.1:7878/ws]

A wss:// URL may end in #sha256=<hex>, the fingerprint of a world's self-signed
certificate (the host's Host screen and `tinyverse-server run` show such
addresses): then exactly that certificate is accepted, checked before the token
is sent.
"""
import asyncio, hashlib, json, os, ssl, sys, time
import websockets

URL = sys.argv[1] if len(sys.argv) > 1 else "ws://127.0.0.1:7878/ws"
URL, _, FRAGMENT = URL.partition("#")
PIN = FRAGMENT[len("sha256="):].replace(":", "").lower() if FRAGMENT.lower().startswith("sha256=") else None
if FRAGMENT and (PIN is None or len(PIN) != 64):
    sys.exit("the only URL fragment understood is #sha256=<64 hex digits>")

def tls():
    if not URL.startswith("wss://"):
        return None
    ctx = ssl.create_default_context()
    if PIN:  # trust by fingerprint instead of by authority; checked after the handshake
        ctx.check_hostname, ctx.verify_mode = False, ssl.CERT_NONE
    return ctx

async def main():
    async with websockets.connect(URL, ssl=tls()) as ws:
        if PIN:
            der = ws.transport.get_extra_info("ssl_object").getpeercert(binary_form=True)
            if hashlib.sha256(der).hexdigest() != PIN:
                sys.exit("the server's certificate does not match the #sha256= pin; not sending the token")
        seq = 1
        async def send(body, payload):
            nonlocal seq
            seq += 1
            await ws.send(json.dumps({"seq": seq, body: payload}))
        await ws.send(json.dumps({"seq": 1, "hello": {
            "protocolVersion": 1, "kind": "CLIENT_KIND_AGENT",
            "token": os.environ["TINYVERSE_TOKEN"], "clientName": "first-agent/0.1"}}))
        welcome = json.loads(await ws.recv())
        if "error" in welcome:
            sys.exit(f"refused: {welcome['error']}")
        me = welcome["welcome"]["avatar"]
        print(f"joined as {me['name']} (avatar {me['id']})", flush=True)
        await send("chat", {"channel": "CHAT_CHANNEL_LOCAL", "text": "Hello! I'm a small demo agent."})
        last_ping = time.monotonic()
        while True:
            try:
                msg = json.loads(await asyncio.wait_for(ws.recv(), timeout=10))
            except asyncio.TimeoutError:
                msg = {}
            if time.monotonic() - last_ping > 10:  # stay well inside the 30 s idle timeout
                await send("ping", {"clientTimeMs": str(int(time.time() * 1000))})
                last_ping = time.monotonic()
            entry = msg.get("chatMessage", {}).get("entry")
            if (entry and entry["senderUserId"] != me["userId"]
                    and entry["senderKind"] == "CLIENT_KIND_HUMAN"  # never answer agents: no loops
                    and me["name"].lower() in entry["text"].lower()):
                await send("chat", {"channel": entry["channel"], "text": f"{entry['senderName']}, you said: {entry['text']}"[:500]})

try:
    asyncio.run(main())
except KeyboardInterrupt:
    pass
