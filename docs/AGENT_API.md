# Tinyverse Agent API

Reference for writing an AI agent that joins a Tinyverse world. Protocol
version 1, server 0.1.0. The schema in `proto/tinyverse/v1/*.proto` is the
source of truth; this document describes the JSON form of it and what the
server actually does. Every example below was captured from a running
`tinyverse-server`; ids, user ids and timestamps are from those runs.

Contents:

1. [Agents in Tinyverse](#1-agents-in-tinyverse)
2. [Getting a token](#2-getting-a-token)
3. [Connecting](#3-connecting)
4. [The session](#4-the-session)
5. [Client messages](#5-client-messages)
6. [Server messages](#6-server-messages)
7. [Shared types](#7-shared-types)
8. [Limits and rate limits](#8-limits-and-rate-limits)
9. [Error codes](#9-error-codes)
10. [Staying connected and being a good guest](#10-staying-connected-and-being-a-good-guest)
11. [Your first agent in 30 lines](#11-your-first-agent-in-30-lines)
12. [Things that may surprise you](#12-things-that-may-surprise-you)

## 1. Agents in Tinyverse

An agent is a program that is present in a world as an avatar, like a person
using the Unity client. It joins through the same WebSocket and the same
protocol, with the same roles and limits; there is no separate agent API, only
three conveniences meant for programs: `MoveTo` (the server walks the avatar),
`Observe` (a description of what is nearby, including a plain-text summary for
a language model) and the ordinary event stream. Agents are marked as such
(`CLIENT_KIND_AGENT`) to everyone, and the client shows them with a badge.

There are two ways to join:

| Way | What you write | Pick it when |
|---|---|---|
| Raw WebSocket JSON (this document) | A program that sends and receives JSON text frames | You are writing your own agent loop in any language, want every event as it happens, or need full control (timing, movement, reacting to people). The example in `agents/greeter/` works this way. |
| MCP bridge `tv-mcp` | Nothing: an MCP-capable assistant (Claude Code, Claude Desktop, ...) gets tools such as `look`, `move_to`, `say`, `create_object` | You want an existing assistant to visit a world in its own turns. See [`server/crates/tv-mcp/README.md`](../server/crates/tv-mcp/README.md). |

## 2. Getting a token

Agents normally log in with a token that the host creates ahead of time on the
machine that holds the world directory:

```text
$ tinyverse-server token create ./myworld --name Scout --role builder
created agent user "Scout" with role builder
user id: 4769c8b8-dde4-418a-999f-40c84900e880
token:   9c9990f89e5ad3869d0a56d44baae82b3517adc85af7d9873f351578ecfe6cc6
This token is shown only once. Put it in Hello.token to log in.
```

`token create <DIR> --name NAME [--role visitor|builder|owner] [--kind agent|human]`.
The role defaults to `builder`, the kind to `agent`. The command may be run
while the server is running. `token list <DIR>` shows users with tokens and
`token revoke <DIR> <USER_ID>` disables one (the user and their objects stay).

The token identifies one user: a stable user id, a display name, a kind and a
role. Logging in with it makes you that user every time.

| Role | Can do |
|---|---|
| `ROLE_VISITOR` | Move, chat, observe, change own appearance. |
| `ROLE_BUILDER` | Also create objects; edit and delete own objects and *unlocked* objects of others; lock and unlock own objects. |
| `ROLE_OWNER` | Everything, plus edit and delete any object, lock or unlock any object, and the `Admin` messages (kick, ban, unban, set role). |

An agent can also join without a token, like a person: it sends a name (and the
world's password, if it has one) and becomes a new user with the world's
default role (`builder` unless `world.toml` says otherwise). The `Welcome` then
carries a `sessionToken`; send it as `Hello.token` on later connections to come
back as the same user.

The host sets the join password with `tinyverse-server init <DIR>
--password-stdin` (or `TINYVERSE_PASSWORD`), and changes it later with
`tinyverse-server password <DIR> --set-stdin`, `--set-from-env` or `--clear`;
a change takes effect at the next start. A token login never needs the
password.

## 3. Connecting

### Endpoint

- `ws://HOST:PORT/ws`, default port 7878, or `wss://HOST:PORT/ws` when the
  host serves TLS (see [Connecting over wss://](#connecting-over-wss)). A
  server serves one or the other on its port, never both; `run` prints which,
  e.g. `listening on wss://127.0.0.1:53377/ws`. Plain `ws://` sends tokens and
  passwords in clear text, so use it only on a network you trust.
- `GET /info` returns, without authentication (over `https://` on a TLS server):
  `{"name":"Doc World","description":"for docs","users":0,"max_users":16,"protocol_version":1,"server_version":"0.1.0","password_required":false}`
  (note: this HTTP endpoint uses snake_case keys).
- `GET /health` returns `200 ok`.

### Connecting over wss://

The server serves `wss://` (and `https://` for `/info` and `/health`) when
it is started with `--tls-cert FILE --tls-key FILE`, or when the world
directory holds `cert.pem` and `key.pem` (`--no-tls` turns that off). A host
without a certificate from a public authority creates a self-signed one,
either with `run --self-signed` (makes one on the first start, then reuses
it) or ahead of time:

```text
$ tinyverse-server tls self-signed ./myworld
wrote ./myworld/cert.pem (certificate, public)
wrote ./myworld/key.pem (private key, mode 0600)
valid for: localhost, 127.0.0.1, ::1, 192.168.1.20
tls certificate sha256=041f8e50d2f5d096c380cf7a24ad5d45ccdccd600136163cf76519837cba0db7
SHA-256 fingerprint: 04:1F:8E:50:D2:F5:D0:96:C3:80:CF:7A:24:AD:5D:45:CC:DC:CD:60:01:36:16:3C:F7:65:19:83:7C:BA:0D:B7
```

The certificate is valid for `localhost`, `127.0.0.1`, `::1` and every
network address the machine has at that moment; `--host NAME_OR_IP`
(repeatable) adds more. `tinyverse-server tls fingerprint ./myworld` prints
the fingerprint again. The protocol over `wss://` is exactly the same.

#### Pinned addresses (trust on first use)

A self-signed certificate is not trusted by any authority, so clients trust
it by its SHA-256 fingerprint instead. A world address may carry it as its
fragment:

```text
wss://192.168.1.20:7878/ws#sha256=041f8e50d2f5d096c380cf7a24ad5d45ccdccd600136163cf76519837cba0db7
```

`run` prints such addresses after its ready line, one per network address:

```text
tls certificate sha256=041f8e50…0db7
listening on wss://0.0.0.0:7878/ws
join with wss://192.168.1.20:7878/ws#sha256=041f8e50…0db7
join with wss://127.0.0.1:7878/ws#sha256=041f8e50…0db7
```

and the desktop client's Host screen shows one under "Friends join with".
The rules, the same in every Tinyverse client (Unity, `tv-cli`, `tv-mcp`, the
Python examples):

- The pin is the SHA-256 of the server's leaf certificate (its DER bytes): 64
  hex digits, either case, colons allowed. Only `#sha256=` is accepted after
  `#`, and only on `wss://`.
- With a pin, the connection succeeds only if the server presents exactly that
  certificate. The host name and the chain are then not checked (the
  handshake signature still is, so the server must hold the certificate's
  key). Nothing is sent, not even the Hello, before the check.
- Without a pin, normal verification applies: a public authority (or the
  `--ca-file` of `tv-cli` and `tv-mcp`) must vouch for the certificate and its
  name must match. There is no switch that turns verification off.
- The fragment never leaves the client: the request line is `GET /ws`.
- Get the pinned address from the host over a channel you trust (in person,
  a chat you trust). Whoever gives you the address decides which server you
  trust.

In Python with `websockets`, trusting by pin (the token goes only after the
check):

```python
import hashlib, ssl, websockets

URL, PIN = "wss://192.168.1.20:7878/ws", "041f8e50…0db7"   # from the #sha256= address
ctx = ssl.create_default_context()
ctx.check_hostname, ctx.verify_mode = False, ssl.CERT_NONE  # the pin replaces both
async with websockets.connect(URL, ssl=ctx) as ws:
    der = ws.transport.get_extra_info("ssl_object").getpeercert(binary_form=True)
    if hashlib.sha256(der).hexdigest() != PIN:
        raise SystemExit("not the pinned certificate")
    ...  # now send the Hello
```

Or trust exactly the world's `cert.pem` (public; `key.pem` is secret and
stays with the host) as the only authority, with hostname checks on:
`ssl.create_default_context(cafile="cert.pem")`, connecting with a name or
address the certificate lists. `ssl.create_default_context()` with no
arguments uses the system's trusted authorities, which is right for a
certificate from a public authority. What happens otherwise:

| Mistake | Result (Python `websockets`) |
|---|---|
| `wss://` to a self-signed server without `cafile` or a pin | `ssl.SSLCertVerificationError`: "certificate verify failed: self-signed certificate" |
| `ws://` to a TLS server | `websockets.InvalidMessage`: "did not receive a valid HTTP response" |
| `wss://` to a plain server | `ssl.SSLError`: "wrong version number" |

`tv-cli --url` and `tv-mcp --url` take pinned addresses as they are, and
`--ca-file cert.pem` as the alternative; refused certificates are reported
with their fingerprint and the pinned address to use once the host confirms
it. With curl: `curl --cacert cert.pem https://127.0.0.1:7878/info`, or
`curl -k https://…/info` only to read the public `/info`.

### Frames

- One message per WebSocket frame. Agents send **text** frames holding one
  `ClientMessage` in proto3 JSON. The server answers in the encoding of your
  `Hello`: text frames with one `ServerMessage` each. (Unity uses binary
  protobuf frames on the same endpoint.)
- A frame may be at most 65,536 bytes. A larger frame is answered with
  `ERROR_CODE_BAD_REQUEST` and the connection is closed (close code 1009).
- A `ClientMessage` is `{"seq": N, "<body>": {...}}` with exactly one body.
  A `ServerMessage` is `{"replyTo": N, "<body>": {...}}` with exactly one body.

### `seq` and `replyTo`

- `seq` is a `uint32` you choose, normally counting up from 1 (the `Hello`).
  `0` means "no correlation wanted".
- Everything the server sends in direct response to a message carries that
  message's `seq` as `replyTo`: the reply itself (`Ack`, `Observation`,
  `Pong`, `ChatHistory`, `Error`, ...) and **your copy** of any broadcast
  event your message caused (`ChatMessage`, `ObjectCreated`, `ObjectUpdated`,
  `ObjectDeleted`, `AvatarUpdated`, and the `AvatarLeft` of someone you
  kicked). Everyone else's copy of the same event has `replyTo: 0`.
- Events nobody asked for (`AvatarJoined`, other people's chat, every
  `AvatarStates`, `Snapshot`, `ChatHistory` at join, `Kicked`) have
  `replyTo: 0`.
- So: to wait for the result of a request, wait for the first message whose
  `replyTo` equals its `seq`. A message that has no effect and no reply (an
  accepted `AvatarState`) produces nothing.

### JSON conventions

| Rule | Detail |
|---|---|
| Field names | camelCase (`protocolVersion`, `chatHistoryRequest`). The server also accepts snake_case (`protocol_version`, `chat_history_request`) on input; it always writes camelCase. |
| Enums | Written as their full names: `"CLIENT_KIND_AGENT"`, `"MOVE_MODE_WALK"`, `"CHAT_CHANNEL_LOCAL"`. On input the number is accepted too (`"channel": 2` is world chat). An unknown name or number is `ERROR_CODE_BAD_REQUEST`. |
| 64-bit integers | `uint64` and `int64` (entity ids, timestamps) are written as **strings**: `"avatarId": "45"`, `"timestampMs": "1795764386168"`. On input both strings and numbers are accepted: `{"id": "3"}` and `{"id": 3}` are the same. 32-bit integers (`seq`, `replyTo`, `limit`) are plain numbers. |
| Defaults | The server writes every field of every message, including empty strings, `false`, zeros and empty lists. On input you may leave out any field; it takes its zero value (empty string, 0, `false`, the `_UNSPECIFIED` enum, or "absent" for a sub-message) and the per-message rules below say what that means. |
| Unknown fields | Refused: `{"seq":14,"chatt":{}}` is `ERROR_CODE_BAD_REQUEST` naming the field and listing the valid ones. |
| Floats | Plain JSON numbers. Positions, scales and colours are 32-bit floats, so `0.1` may come back as `0.1` or `0.099999994`. |

### Coordinates

Metres and seconds. The coordinate system is Unity's: **left-handed, Y up**.
The ground is the plane `y = 0`. Seen from above, with +Z pointing up the page,
+X points to the right:

```text
                    +Z   yaw 0     ("north")
                     ^
                     |
     yaw 270  -X ----+----> +X     yaw 90 ("east")
     ("west")        |
                     |
                    -Z   yaw 180   ("south")

         seen from above; +Y comes out of the page towards you
```

- **Positions**: an avatar's `position` is its feet. An object's `position` is
  its centre, so a 1 m box resting on the ground has `y = 0.5`.
- **Yaw** (avatars only): degrees, clockwise seen from above, 0 = facing +Z,
  90 = facing +X. The server keeps it in 0..360 when it moves you.
- **Rotation** (objects): a quaternion `{x, y, z, w}`, the same as Unity's.
  A turn of θ degrees about +Y (clockwise from above, like yaw) is
  `{x: 0, y: sin(θ/2), z: 0, w: cos(θ/2)}`; 90° is
  `{"x": 0, "y": 0.7071068, "z": 0, "w": 0.7071068}`. An all-zero or missing
  quaternion means identity. The server normalises what you send.
- **Bearing** (in `Observation`): the horizontal direction to something,
  relative to *your own* yaw, in degrees: 0 = straight ahead, positive = to
  your right, negative = to your left, range -180..180. Something straight
  above or below you has bearing 0. Example: facing +Z at the origin, a box at
  (2, 0.5, 3) has bearing 33.69 (ahead and to the right).
- **Distance** (in `Observation`): straight-line 3D distance in metres from your
  feet to the other avatar's feet or to the object's centre.
- **World bounds**: an axis-aligned box (`Welcome.world.boundsMin/boundsMax`,
  default -128..128 in x and z, -16..256 in y). Nothing may be placed or moved
  outside it.

### Colours and time

- **Colours** are `{r, g, b, a}`, each 0..1, in **sRGB**, exactly as in a hex
  code: `#ff8000` is `{"r": 1.0, "g": 0.502, "b": 0.0, "a": 1.0}`. `a` is
  opacity (1 = opaque). Values outside 0..1 are clamped; a non-finite value is
  `ERROR_CODE_INVALID_ARGUMENT`. `tv-cli --color '#808080'` sends 0.5019608
  per channel, the same as an agent following this rule.
- **Timestamps** are milliseconds since the Unix epoch (UTC), as strings in
  JSON (`"serverTimeMs": "1795765495153"`).
- **Ids**: avatars and objects share one id space of `uint64` entity ids,
  assigned by the server and never reused, even across restarts. Every
  connection gets a new avatar id. User ids are UUID strings and are stable
  across connections.

## 4. The session

1. Open the WebSocket and send `Hello` within 10 seconds (otherwise:
   `ERROR_CODE_BAD_REQUEST "no Hello within the time limit"` and close).
2. The server checks the protocol version, then the token (or, for a new user,
   the password, the name and the capacity). On refusal it sends one `Error`
   with `replyTo` = your `seq` and closes the socket with code 1008 and the
   error code name as the reason.
3. On success it sends, in this order and before anything else:
   - `Welcome` (`replyTo` = the Hello's `seq`): who you are and the world's
     description and limits;
   - one or more `Snapshot` chunks: every other avatar present (all in the
     first chunk) and every object (at most 100 per chunk); the last chunk has
     `"last": true`. There is always at least one chunk;
   - `ChatHistory`: the retained world chat, oldest first.
4. Then live events. Anything that happened while the join sequence was being
   built is delivered after it, never before.
5. When you disconnect, everyone else receives `AvatarLeft`.

A real join, using the token from section 2 (Ada, a person, and two objects
are already in the world; this is the full output):

```json
>> {"seq": 1, "hello": {"protocolVersion": 1, "kind": "CLIENT_KIND_AGENT", "clientName": "doc-capture/0.1", "token": "9c9990f89e5ad3869d0a56d44baae82b3517adc85af7d9873f351578ecfe6cc6"}}
```

```json
{
  "replyTo": 1,
  "welcome": {
    "protocolVersion": 1,
    "userId": "4769c8b8-dde4-418a-999f-40c84900e880",
    "avatarId": "45",
    "role": "ROLE_BUILDER",
    "sessionToken": "",
    "world": {
      "name": "Doc World",
      "description": "for docs",
      "boundsMin": {"x": -128.0, "y": -16.0, "z": -128.0},
      "boundsMax": {"x": 128.0, "y": 256.0, "z": 128.0},
      "spawnPoint": {"x": 0.0, "y": 0.0, "z": 0.0},
      "groundSize": 256.0,
      "sky": "day",
      "timeOfDay": 12.0
    },
    "limits": {
      "maxMessageBytes": 65536,
      "maxChatChars": 500,
      "maxNameChars": 64,
      "maxObjects": 10000,
      "localChatRadius": 20.0,
      "minScale": 0.01,
      "maxScale": 64.0,
      "walkSpeed": 4.0,
      "runSpeed": 8.0,
      "flySpeed": 16.0,
      "maxObserveRadius": 100.0
    },
    "tickRateHz": 20,
    "serverTimeMs": "1795765495153",
    "serverVersion": "0.1.0",
    "avatar": {
      "id": "45",
      "userId": "4769c8b8-dde4-418a-999f-40c84900e880",
      "name": "Scout",
      "kind": "CLIENT_KIND_AGENT",
      "role": "ROLE_BUILDER",
      "appearance": {
        "bodyColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0},
        "bodyPreset": "",
        "skinColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0},
        "hairColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0},
        "hairStyle": "",
        "topColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0},
        "bottomColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0}
      },
      "transform": {
        "position": {"x": 0.0, "y": 0.0, "z": 0.0},
        "yaw": 0.0,
        "velocity": {"x": 0.0, "y": 0.0, "z": 0.0},
        "moveMode": "MOVE_MODE_WALK",
        "animation": "idle"
      }
    }
  }
}
{
  "replyTo": 0,
  "snapshot": {
    "avatars": [
      {
        "id": "33",
        "userId": "bc2b3230-be36-47c8-b52c-1f27ff366e6b",
        "name": "Ada",
        "kind": "CLIENT_KIND_HUMAN",
        "role": "ROLE_BUILDER",
        "appearance": {
          "bodyColor": {"r": 0.19755876, "g": 1.0, "b": 0.0, "a": 1.0},
          "bodyPreset": "",
          "skinColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0},
          "hairColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0},
          "hairStyle": "",
          "topColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0},
          "bottomColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0}
        },
        "transform": {
          "position": {"x": 0.0, "y": 0.0, "z": 0.0},
          "yaw": 0.0,
          "velocity": {"x": 0.0, "y": 0.0, "z": 0.0},
          "moveMode": "MOVE_MODE_WALK",
          "animation": "idle"
        }
      }
    ],
    "objects": [
      {
        "id": "8",
        "ownerId": "4769c8b8-dde4-418a-999f-40c84900e880",
        "spec": {
          "name": "blue ball",
          "shape": "SHAPE_SPHERE",
          "position": {"x": -3.0, "y": 1.0, "z": 4.0},
          "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0},
          "scale": {"x": 2.0, "y": 2.0, "z": 2.0},
          "color": {"r": 0.1, "g": 0.3, "b": 0.9, "a": 1.0},
          "material": "MATERIAL_GLOSSY",
          "solid": true,
          "locked": true
        }
      },
      {
        "id": "9",
        "ownerId": "4769c8b8-dde4-418a-999f-40c84900e880",
        "spec": {
          "name": "",
          "shape": "SHAPE_BOX",
          "position": {"x": 1.0, "y": 0.5, "z": -2.0},
          "rotation": {"x": 0.0, "y": 0.7071068, "z": 0.0, "w": 0.7071068},
          "scale": {"x": 2.0, "y": 0.2, "z": 2.0},
          "color": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0},
          "material": "MATERIAL_MATTE",
          "solid": false,
          "locked": false
        }
      }
    ],
    "last": true
  }
}
{
  "replyTo": 0,
  "chatHistory": {
    "entries": [
      {
        "senderAvatarId": "40",
        "senderUserId": "56e8c039-ad7f-4791-95b8-1c3819d36572",
        "senderName": "Dee",
        "senderKind": "CLIENT_KIND_HUMAN",
        "channel": "CHAT_CHANNEL_WORLD",
        "text": "hello Scout, what can you do?",
        "timestampMs": "1795765371790"
      },
      {
        "senderAvatarId": "37",
        "senderUserId": "4769c8b8-dde4-418a-999f-40c84900e880",
        "senderName": "Scout",
        "senderKind": "CLIENT_KIND_AGENT",
        "channel": "CHAT_CHANNEL_WORLD",
        "text": "Hello Dee!",
        "timestampMs": "1795765371792"
      }
    ]
  }
}
```

Everyone already present receives `AvatarJoined` for the newcomer at the same
time. From here on the agent sends requests and reads events, for example:

```json
>> {"seq": 2, "ping": {"clientTimeMs": "1764230000000"}}
<< {"replyTo": 2, "pong": {"clientTimeMs": "1764230000000", "serverTimeMs": "1795765495653"}}
```

In the examples below, `>>` is a frame the agent sent and `<<` one it
received; long `AvatarInfo` blocks are shortened with a `// ...` comment where
they repeat the shape shown above.

## 5. Client messages

Every client message is `{"seq": N, "<body>": {...}}`. The table lists the
bodies; each has its own section.

| Body | Purpose | Reply |
|---|---|---|
| `hello` | Join. First message only. | `welcome` + snapshot + chat history, or `error` and close |
| `ping` | Keepalive, latency | `pong` |
| `observe` | What is nearby | `observation` |
| `moveTo` | Server walks or flies you to a point | `ack`, then your position in `avatarStates` |
| `stop` | Cancel a `moveTo` | `ack` |
| `avatarState` | Report your own simulated position (as Unity does) | nothing, or `avatarCorrection` |
| `chat` | Say something, local or world | your copy of `chatMessage` |
| `chatHistoryRequest` | Read retained world chat | `chatHistory` |
| `objectCreate` | Build a primitive | `objectCreated` |
| `objectUpdate` | Change an object | `objectUpdated` |
| `objectDelete` | Remove an object | `objectDeleted` |
| `setAppearance` | Change your avatar's look | `avatarUpdated` |
| `admin` | Kick, ban, unban, set role (owners) | `ack` |

Any message may also be answered with `error` (section 9). Every message type
except `hello` is rate limited per connection (section 8).

### hello

Must be the first message, and only the first.

| Field | Type | Meaning |
|---|---|---|
| `protocolVersion` | uint32 | Must be `1`, else `ERROR_CODE_UNSUPPORTED_VERSION`. |
| `kind` | ClientKind | `CLIENT_KIND_AGENT` for agents. Unspecified means human. Ignored with a token: the kind stored with the token's user is used. |
| `name` | string | Display name, 1..64 characters after trimming. Required without a token. With a token: empty keeps the stored name; non-empty renames the user (and is remembered). |
| `password` | string | The world's join password, if it has one. Not needed with a token. |
| `token` | string | An agent token from the host, or the `sessionToken` of an earlier `Welcome`. Empty for a first visit. |
| `appearance` | Appearance | Optional starting look. Missing colours are white. |
| `clientName` | string | Name and version of your program, for the host's log. |

```json
>> {"seq": 1, "hello": {"protocolVersion": 1, "kind": "CLIENT_KIND_AGENT", "name": "Newcomer", "clientName": "doc/0.1"}}
<< {"replyTo": 1, "welcome": {"protocolVersion": 1, "userId": "f5724c0e-057e-4ac9-9d95-a0c8ed871189", "avatarId": "15", "role": "ROLE_BUILDER", "sessionToken": "a2d72a35cd6b44a20bbfe654aacf22c14829ec8548a9b0ae31ab5e32745e93c0", ...}}
```

Refusals (each followed by close code 1008), checked in this order:

| Situation | Error |
|---|---|
| First message is not `hello` | `ERROR_CODE_BAD_REQUEST` "the first message must be Hello" |
| Frame is not valid JSON for a `ClientMessage` | `ERROR_CODE_BAD_REQUEST` |
| `protocolVersion` is not 1 | `ERROR_CODE_UNSUPPORTED_VERSION` "this server speaks protocol version 1, the client sent 2" |
| `token` unknown or revoked | `ERROR_CODE_INVALID_TOKEN` "the token is unknown or has been revoked; leave `token` empty to join as a new user" |
| The token's user is banned | `ERROR_CODE_BANNED` "you are banned from this world: doc test" |
| No token, world has a password, none given | `ERROR_CODE_AUTH_FAILED` "this world needs a password; set `password` in Hello" |
| No token, wrong password | `ERROR_CODE_AUTH_FAILED` "wrong password" |
| No token and no name | `ERROR_CODE_INVALID_ARGUMENT` "`hello.name` is required when joining without a token" |
| Name longer than 64 characters | `ERROR_CODE_INVALID_ARGUMENT` |
| The world already holds `max_users` avatars (default 16) | `ERROR_CODE_WORLD_FULL` "the world is full (16 users)" for a new user, "the world is full" with a token (checked for token logins too) |
| `hello` sent again after joining | `ERROR_CODE_BAD_REQUEST` "already joined; Hello may only be sent once" (connection stays open) |

If the same user is already connected, the older connection is removed: it
receives `kicked` with reason "signed in again from another connection" and is
closed, and the new one joins normally.

### ping

| Field | Type | Meaning |
|---|---|---|
| `clientTimeMs` | int64 | Any value; echoed back. |

```json
>> {"seq": 2, "ping": {"clientTimeMs": "1764230000000"}}
<< {"replyTo": 2, "pong": {"clientTimeMs": "1764230000000", "serverTimeMs": "1795765495653"}}
```

Answered by the connection itself, without touching the world. See section 10
for the idle timeout.

### observe

| Field | Type | Meaning |
|---|---|---|
| `radius` | float | Metres. `0` (or absent) means the default, 30 m. Values above `limits.maxObserveRadius` (100) are capped. Negative is `ERROR_CODE_INVALID_ARGUMENT`. |

Reply: `observation` (section 6), only to you.

How the server builds it:

- **Who and what**: every other avatar whose feet, and every object whose
  centre, is within `radius` (3D distance) of your feet. You are not in the
  list; you are in `self`.
- **Order**: nearest first; equal distances by ascending id.
- **Cap**: at most 50 avatars and at most 50 objects. If either list was cut,
  `truncated` is `true` and the summary ends with "More is nearby than
  listed." (The cap is not in `Limits`.)
- **`radius`** in the reply is the radius actually used.
- **`summary`**: three or four lines of plain English, ready to put into a
  model's context:

  ```text
  You are Near at (0.0, 0.0, 0.0), facing north.
  People within 50 m: Ada (human), 0.0 m here; Far (agent), 30.0 m right.
  Objects within 50 m: white box 2x0.2x2 m, 2.3 m behind-right, id 9; blue sphere "blue ball" 2x2x2 m, 5.1 m ahead-left, id 8.
  ```

  - Line 1: your name, position (one decimal) and facing as one of eight
    compass points (north = +Z, east = +X).
  - Line 2: `People within R m:` then `Name (human|agent), D m DIRECTION`
    separated by `;`, or `No one else within R m.`
  - Line 3: `Objects within R m:` then `COLOUR SHAPE ["name"] XxYxZ m, D m
    DIRECTION, id N` separated by `;`, or `No objects within R m.` The size is
    the object's scale.
  - DIRECTION is one of `ahead`, `ahead-right`, `right`, `behind-right`,
    `behind`, `behind-left`, `left`, `ahead-left` (the bearing rounded to 45°),
    or `above`, `below` or `here` for things within 0.5 m horizontally.
  - COLOUR is one or two words from: black, white, grey, red, orange, brown,
    yellow, green, cyan, blue, purple, pink, with "translucent" in front when
    alpha is below 0.5.

```json
>> {"seq": 2, "observe": {"radius": 0}}
```

```jsonc
{
  "replyTo": 2,
  "observation": {
    "self": {
      "id": "6",
      "userId": "4769c8b8-dde4-418a-999f-40c84900e880",
      "name": "Scout",
      "kind": "CLIENT_KIND_AGENT",
      "role": "ROLE_BUILDER",
      "appearance": { /* as in Welcome */ },
      "transform": {
        "position": {"x": 0.0, "y": 0.0, "z": 0.0},
        "yaw": 0.0,
        "velocity": {"x": 0.0, "y": 0.0, "z": 0.0},
        "moveMode": "MOVE_MODE_WALK",
        "animation": "idle"
      }
    },
    "avatars": [
      {
        "avatar": {
          "id": "4",
          "userId": "c6188cf7-aca9-4d56-8f84-1a8823c3e7a5",
          "name": "Ada",
          "kind": "CLIENT_KIND_HUMAN",
          "role": "ROLE_BUILDER",
          "appearance": { /* ... */ },
          "transform": {
            "position": {"x": 0.0, "y": 0.0, "z": 0.0},
            "yaw": 0.0,
            "velocity": {"x": 0.0, "y": 0.0, "z": 0.0},
            "moveMode": "MOVE_MODE_WALK",
            "animation": "idle"
          }
        },
        "distance": 0.0,
        "bearing": 0.0
      }
    ],
    "objects": [
      {
        "object": {
          "id": "3",
          "ownerId": "4769c8b8-dde4-418a-999f-40c84900e880",
          "spec": {
            "name": "crate",
            "shape": "SHAPE_BOX",
            "position": {"x": 2.0, "y": 0.5, "z": 3.0},
            "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0},
            "scale": {"x": 1.0, "y": 1.0, "z": 1.0},
            "color": {"r": 1.0, "g": 0.0, "b": 0.0, "a": 1.0},
            "material": "MATERIAL_MATTE",
            "solid": true,
            "locked": false
          }
        },
        "distance": 3.640055,
        "bearing": 33.690063
      }
    ],
    "summary": "You are Scout at (0.0, 0.0, 0.0), facing north.\nPeople within 30 m: Ada (human), 0.0 m here.\nObjects within 30 m: red box \"crate\" 1x1x1 m, 3.6 m ahead-right, id 3.",
    "truncated": false,
    "radius": 30.0
  }
}
```

```json
>> {"seq": 3, "observe": {"radius": 1000}}
<< {"replyTo": 3, "observation": {..., "summary": "You are Scout at (0.0, 0.0, 0.0), facing north.\nPeople within 100 m: ...", "truncated": false, "radius": 100.0}}
>> {"seq": 4, "observe": {"radius": -1}}
<< {"replyTo": 4, "error": {"code": "ERROR_CODE_INVALID_ARGUMENT", "message": "invalid argument: the radius must not be negative"}}
```

### moveTo

Ask the server to move your avatar in a straight line. This is how an agent
walks: it does not need to stream positions.

| Field | Type | Meaning |
|---|---|---|
| `target` | Vec3 | Required (`ERROR_CODE_INVALID_ARGUMENT` "`move_to.target` is required"). |
| `mode` | MoveMode | `MOVE_MODE_WALK` (4 m/s), `MOVE_MODE_RUN` (8 m/s) or `MOVE_MODE_FLY` (16 m/s). Unspecified means walk. |

Behaviour:

- **Fly** moves in a straight 3D line to `target` at 16 m/s.
- **Walk and run** ignore `target.y`. They go to *ground level*: the plane
  y = 0, clamped into the world bounds (the server knows no other floor; it
  has no collision). Each tick they move horizontally towards the target at
  the mode's speed and, at the same time, bring y towards ground level at up
  to 10 m/s, up or down. So an avatar that walks off while flying comes down
  as it goes.
- The target, with ground level as its y for walk and run, must lie inside
  the world bounds, else `ERROR_CODE_OUT_OF_BOUNDS` and nothing moves. A
  non-finite coordinate is `ERROR_CODE_INVALID_ARGUMENT`.
- On success the reply is `ack`. Each tick (20 per second) the server moves
  you, turns your yaw to face the horizontal direction of travel (unchanged
  when only y is changing), sets `velocity`, sets `moveMode` to the mode and
  sets `animation` to `walk`, `run` or `fly`, or to `fall` while a walk or run
  is still more than 0.5 m above ground level.
- Obstacles are ignored: you walk through objects and people.
- A new `moveTo` replaces the current one. `stop` cancels it. An `avatarState`
  from you also cancels it.
- While the server moves you, your own avatar **is** included in the
  `avatarStates` you receive (normally it is not).

**How to tell you have arrived.** There is no arrival message. Watch the
`avatarStates` entries with your own `avatarId`. You have arrived on the tick
whose entry has `position` equal to the target (for walk and run: the
horizontal target *and* y at ground level), `velocity` zero and `animation`
`"idle"`; after that no more entries for you arrive. Checking only x and z is
not enough: a walk that starts in the air can reach the horizontal target
while still coming down. Expect arrival after the longer of horizontal
distance ÷ speed and height above ground ÷ 10 m/s.

A walk on the ground from (0, 0, 0) to (6, 5, 8): 10 m at 4 m/s, so 2.5 s and
51 ticks; `y: 5` is ignored:

```json
>> {"seq": 2, "moveTo": {"target": {"x": 6, "y": 5, "z": 8}, "mode": "MOVE_MODE_WALK"}}
<< {"replyTo": 2, "ack": {}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766389579", "states": [{"avatarId": "55", "transform": {"position": {"x": 0.12010057, "y": 0.0, "z": 0.16013409}, "yaw": 36.869896, "velocity": {"x": 2.4, "y": 0.0, "z": 3.2}, "moveMode": "MOVE_MODE_WALK", "animation": "walk"}}]}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766389630", "states": [{"avatarId": "55", "transform": {"position": {"x": 0.24327236, "y": 0.0, "z": 0.3243631}, "yaw": 36.869896, "velocity": {"x": 2.4, "y": 0.0, "z": 3.1999998}, "moveMode": "MOVE_MODE_WALK", "animation": "walk"}}]}}
   ... 47 more ticks ...
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766392028", "states": [{"avatarId": "55", "transform": {"position": {"x": 5.9989343, "y": 0.0, "z": 7.998579}, "yaw": 36.869896, "velocity": {"x": 2.4, "y": 0.0, "z": 3.2}, "moveMode": "MOVE_MODE_WALK", "animation": "walk"}}]}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766392079", "states": [{"avatarId": "55", "transform": {"position": {"x": 6.0, "y": 0.0, "z": 8.0}, "yaw": 36.869896, "velocity": {"x": 0.0, "y": 0.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "idle"}}]}}
```

A walk that starts in the air: after flying to (0, 6, 0), walk to (1, 0, 0).
The horizontal metre takes 0.25 s, the 6 m descent 0.6 s; the avatar reaches
x = 1 on the fifth tick while still 3.5 m up, and arrives only on the
thirteenth (some ticks left out):

```json
>> {"seq": 3, "moveTo": {"target": {"x": 1, "y": 0, "z": 0}, "mode": "MOVE_MODE_WALK"}}
<< {"replyTo": 3, "ack": {}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766092876", "states": [{"avatarId": "48", "transform": {"position": {"x": 0.19941728, "y": 5.5014567, "z": 0.0}, "yaw": 90.0, "velocity": {"x": 4.0, "y": -10.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "fall"}}]}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766093077", "states": [{"avatarId": "48", "transform": {"position": {"x": 1.0, "y": 3.4917722, "z": 0.0}, "yaw": 90.0, "velocity": {"x": 0.0, "y": -10.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "fall"}}]}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766093326", "states": [{"avatarId": "48", "transform": {"position": {"x": 1.0, "y": 0.9983174, "z": 0.0}, "yaw": 90.0, "velocity": {"x": 0.0, "y": -10.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "fall"}}]}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766093376", "states": [{"avatarId": "48", "transform": {"position": {"x": 1.0, "y": 0.4949047, "z": 0.0}, "yaw": 90.0, "velocity": {"x": 0.0, "y": -10.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "walk"}}]}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766093426", "states": [{"avatarId": "48", "transform": {"position": {"x": 1.0, "y": 0.000238657, "z": 0.0}, "yaw": 90.0, "velocity": {"x": 0.0, "y": -10.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "walk"}}]}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795766093476", "states": [{"avatarId": "48", "transform": {"position": {"x": 1.0, "y": 0.0, "z": 0.0}, "yaw": 90.0, "velocity": {"x": 0.0, "y": 0.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "idle"}}]}}
```

Errors:

```json
>> {"seq": 20, "moveTo": {}}
<< {"replyTo": 20, "error": {"code": "ERROR_CODE_INVALID_ARGUMENT", "message": "`move_to.target` is required"}}
>> {"seq": 21, "moveTo": {"target": {"x": 1000, "y": 0, "z": 0}}}
<< {"replyTo": 21, "error": {"code": "ERROR_CODE_OUT_OF_BOUNDS", "message": "out of bounds: the target (1000, 0, 0) is outside the world"}}
```

### stop

No fields: `{"seq": 5, "stop": {}}`. Cancels a `moveTo` in progress: you stay
where you are, `velocity` becomes zero and `animation` `"idle"` (sent to
everyone, you included, in the next `avatarStates`). Always answered with
`ack`, also when you were not moving.

```json
>> {"seq": 4, "moveTo": {"target": {"x": 6, "y": 3, "z": 0}, "mode": "MOVE_MODE_FLY"}}
<< {"replyTo": 4, "ack": {}}
   ... avatarStates while flying ...
>> {"seq": 5, "stop": {}}
<< {"replyTo": 5, "ack": {}}
<< {"replyTo": 0, "avatarStates": {"serverTimeMs": "1795764453834", "states": [{"avatarId": "10", "transform": {"position": {"x": 6.0, "y": 1.7600993, "z": 3.306402}, "yaw": 180.0, "velocity": {"x": 0.0, "y": 0.0, "z": 0.0}, "moveMode": "MOVE_MODE_FLY", "animation": "idle"}}]}}
```

A flying avatar that stops stays in the air.

### avatarState

The client's own simulated state; this is how the Unity client moves. Agents
rarely need it (use `moveTo`), but may use it to turn on the spot or to step
precisely.

| Field | Type | Meaning |
|---|---|---|
| `transform` | AvatarTransform | Required, with `position` required. `velocity` defaults to zero, `moveMode` to walk. `animation` is cut to 32 characters. |

The server compares the new position with your last accepted one:

- **Speed rule.** With `elapsed` = time since the last accepted state, clamped
  to 0.05..2.0 s, the move is allowed if its length is at most
  `speed × 1.5 × elapsed + 0.001 m`, where `speed` is 4, 8 or 16 m/s for the
  `moveMode` in the message and 1.5 is the tolerance. For fly the 3D distance
  counts. For walk and run the horizontal distance counts, and vertically you
  may rise at most 10 m/s and fall at most 60 m/s (no tolerance).
- **Bounds.** The position must be inside the world bounds.
- Accepted: no reply. The new state is broadcast to the others in the next
  `avatarStates` (not to you). A `moveTo` in progress is cancelled.
- Rejected: you receive `avatarCorrection` with your last accepted transform
  and the reason `"too fast"` or `"out of bounds"`; snap to it. This is not an
  `error`.
- A non-finite position, yaw or velocity is `ERROR_CODE_INVALID_ARGUMENT`.

```json
>> {"seq": 22, "avatarState": {"transform": {"position": {"x": 50, "y": 0, "z": 0}, "yaw": 90, "moveMode": "MOVE_MODE_WALK"}}}
<< {"replyTo": 22, "avatarCorrection": {"transform": {"position": {"x": 0.0, "y": 0.0, "z": 0.0}, "yaw": 0.0, "velocity": {"x": 0.0, "y": 0.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "idle"}, "reason": "too fast"}}
>> {"seq": 23, "avatarState": {"transform": {"position": {"x": 0.5, "y": 0, "z": 0}, "yaw": 90, "moveMode": "MOVE_MODE_WALK", "animation": "walk"}}}
   (accepted: no reply)
```

Send at most 15 per second while moving and about one per second when idle.

### chat

| Field | Type | Meaning |
|---|---|---|
| `channel` | ChatChannel | `CHAT_CHANNEL_LOCAL` (unspecified means local) or `CHAT_CHANNEL_WORLD`. |
| `text` | string | Trimmed by the server; must then be 1..500 characters (Unicode characters, not bytes). |

- **Local** chat reaches every avatar within 20 m (`limits.localChatRadius`,
  3D distance from the speaker's feet), the speaker included. It is never
  stored.
- **World** chat reaches everyone and is retained in the server's memory for
  late-comers: messages older than 3 hours (`chat_retention_minutes` in
  `world.toml`) or beyond the newest 1,000 (`chat_history_max`) are dropped,
  and a restart clears it. Nothing is written to disk.
- You receive your own line back as a `chatMessage` with `replyTo` set; that
  is the confirmation.

```json
>> {"seq": 5, "chat": {"channel": "CHAT_CHANNEL_LOCAL", "text": "  hi Ada  "}}
<< {"replyTo": 5, "chatMessage": {"entry": {"senderAvatarId": "6", "senderUserId": "4769c8b8-dde4-418a-999f-40c84900e880", "senderName": "Scout", "senderKind": "CLIENT_KIND_AGENT", "channel": "CHAT_CHANNEL_LOCAL", "text": "hi Ada", "timestampMs": "1795764386168"}}}
>> {"seq": 6, "chat": {"channel": "CHAT_CHANNEL_WORLD", "text": "hello everyone"}}
<< {"replyTo": 6, "chatMessage": {"entry": {"senderAvatarId": "6", ..., "channel": "CHAT_CHANNEL_WORLD", "text": "hello everyone", "timestampMs": "1795764386470"}}}
>> {"seq": 7, "chat": {"text": "   "}}
<< {"replyTo": 7, "error": {"code": "ERROR_CODE_INVALID_ARGUMENT", "message": "invalid argument: the message is empty"}}
>> {"seq": 2, "chat": {"text": "yyyy…(501 characters)"}}
<< {"replyTo": 2, "error": {"code": "ERROR_CODE_INVALID_ARGUMENT", "message": "invalid argument: the message is longer than 500 characters"}}
```

With an agent 30 m away, a local line from a speaker at the origin did not
reach it; the world line did.

### chatHistoryRequest

| Field | Type | Meaning |
|---|---|---|
| `sinceMs` | int64 | Only messages with a timestamp strictly greater than this. `0` means all retained. |
| `limit` | uint32 | At most this many, keeping the newest. `0` means no limit. |

Reply: `chatHistory`, oldest first, world chat only.

```json
>> {"seq": 9, "chatHistoryRequest": {"sinceMs": "0", "limit": 1}}
<< {"replyTo": 9, "chatHistory": {"entries": [{"senderAvatarId": "6", "senderUserId": "4769c8b8-dde4-418a-999f-40c84900e880", "senderName": "Scout", "senderKind": "CLIENT_KIND_AGENT", "channel": "CHAT_CHANNEL_WORLD", "text": "hello everyone", "timestampMs": "1795764386470"}]}}
>> {"seq": 10, "chat_history_request": {"since_ms": 1795764330583}}
<< {"replyTo": 10, "chatHistory": {"entries": [{... "text": "hello everyone", "timestampMs": "1795764386470"}]}}
```

(The second request uses snake_case names and a numeric `since_ms`; both are
accepted. The line with timestamp 1795764330583 itself is not included.)

### objectCreate

Requires role builder or owner.

| Field | Type | Meaning |
|---|---|---|
| `spec` | ObjectSpec | Required. See section 7 for every field. |

Rules, applied to the spec:

| Field | Absent means | Checked |
|---|---|---|
| `shape` | refused: `ERROR_CODE_INVALID_ARGUMENT` "`object_create.spec.shape` is required (SHAPE_BOX, ...)" | must be a known shape |
| `position` | refused: "`object_create.spec.position` is required" | inside the world bounds, else `ERROR_CODE_OUT_OF_BOUNDS` |
| `rotation` | identity | normalised; all zeros is identity |
| `scale` | 1 × 1 × 1 | each axis 0.01..64 m, else `ERROR_CODE_INVALID_ARGUMENT`; for `SHAPE_PLANE` the y scale is clamped into range instead |
| `color` | opaque white | clamped to 0..1 |
| `material` | `MATERIAL_MATTE` | must be known |
| `solid` | `false` (avatars walk through it) | |
| `locked` | `false` | |
| `name` | empty | trimmed; at most 64 characters |

The world holds at most 10,000 objects (`limits.maxObjects`); beyond that,
`ERROR_CODE_LIMIT_EXCEEDED`. On success everyone receives `objectCreated`
(yours with `replyTo`); the object belongs to your user.

```json
>> {"seq": 9, "objectCreate": {"spec": {"name": "blue ball", "shape": "SHAPE_SPHERE", "position": {"x": -3, "y": 1, "z": 4}, "scale": {"x": 2, "y": 2, "z": 2}, "color": {"r": 0.1, "g": 0.3, "b": 0.9, "a": 1}, "material": "MATERIAL_GLOSSY", "solid": true, "locked": true}}}
```

```json
{
  "replyTo": 9,
  "objectCreated": {
    "object": {
      "id": "8",
      "ownerId": "4769c8b8-dde4-418a-999f-40c84900e880",
      "spec": {
        "name": "blue ball",
        "shape": "SHAPE_SPHERE",
        "position": {"x": -3.0, "y": 1.0, "z": 4.0},
        "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0},
        "scale": {"x": 2.0, "y": 2.0, "z": 2.0},
        "color": {"r": 0.1, "g": 0.3, "b": 0.9, "a": 1.0},
        "material": "MATERIAL_GLOSSY",
        "solid": true,
        "locked": true
      }
    },
    "byAvatarId": "7"
  }
}
```

The smallest valid create and its defaults:

```json
>> {"seq": 14, "objectCreate": {"spec": {"shape": "SHAPE_BOX", "position": {"x": 1, "y": 0.5, "z": -2}}}}
<< {"replyTo": 14, "objectCreated": {"object": {"id": "9", "ownerId": "4769c8b8-dde4-418a-999f-40c84900e880", "spec": {"name": "", "shape": "SHAPE_BOX", "position": {"x": 1.0, "y": 0.5, "z": -2.0}, "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}, "scale": {"x": 1.0, "y": 1.0, "z": 1.0}, "color": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0}, "material": "MATERIAL_MATTE", "solid": false, "locked": false}}, "byAvatarId": "7"}}
```

Errors:

```json
>> {"seq": 10, "objectCreate": {"spec": {"position": {"x": 0, "y": 0.5, "z": 0}}}}
<< {"replyTo": 10, "error": {"code": "ERROR_CODE_INVALID_ARGUMENT", "message": "`object_create.spec.shape` is required (SHAPE_BOX, SHAPE_SPHERE, SHAPE_CYLINDER, SHAPE_CONE, SHAPE_WEDGE or SHAPE_PLANE)"}}
>> {"seq": 11, "objectCreate": {"spec": {"shape": "SHAPE_BOX", "position": {"x": 0, "y": 0.5, "z": 0}, "scale": {"x": 100, "y": 1, "z": 1}}}}
<< {"replyTo": 11, "error": {"code": "ERROR_CODE_INVALID_ARGUMENT", "message": "invalid argument: scale x = 100 is outside 0.01..64 m"}}
>> {"seq": 12, "objectCreate": {"spec": {"shape": "SHAPE_BOX", "position": {"x": 500, "y": 0.5, "z": 0}}}}
<< {"replyTo": 12, "error": {"code": "ERROR_CODE_OUT_OF_BOUNDS", "message": "out of bounds: the position (500, 0.5, 0) is outside the world"}}
>> {"seq": 13, "objectCreate": {"spec": {"shape": "SHAPE_BOX"}}}
<< {"replyTo": 13, "error": {"code": "ERROR_CODE_INVALID_ARGUMENT", "message": "`object_create.spec.position` is required"}}
```

As a visitor:

```json
>> {"seq": 2, "objectCreate": {"spec": {"shape": "SHAPE_BOX", "position": {"x": 0, "y": 0.5, "z": 2}}}}
<< {"replyTo": 2, "error": {"code": "ERROR_CODE_PERMISSION_DENIED", "message": "permission denied: visitors cannot build"}}
```

### objectUpdate

Change some fields of an object. Fields you leave out stay as they are.

| Field | Type | Meaning |
|---|---|---|
| `id` | uint64 | The object. Unknown: `ERROR_CODE_NOT_FOUND` "not found: object 999". |
| `name` | string, optional | New label; `""` removes it. |
| `shape` | Shape, optional | `SHAPE_UNSPECIFIED` is refused. |
| `position` | Vec3 | New centre. |
| `rotation` | Quat | New rotation (all zeros = identity). |
| `scale` | Vec3 | New size. |
| `color` | Color | New colour. |
| `material` | Material, optional | |
| `solid` | bool, optional | |
| `locked` | bool, optional | |

Who may: visitors never ("permission denied: visitors cannot change objects");
builders may change their own objects and unlocked objects of others ("the
object is locked by its owner" otherwise); owners any object. Changing
`locked` (to a different value) is allowed only for the object's creator and
world owners ("only the object's owner or a world owner may lock or unlock
it"). The merged result is checked like a create (scale range, bounds, name
length). On success everyone receives `objectUpdated` with the **full** new
object. An update that names only `id` is accepted and broadcast unchanged.

```json
>> {"seq": 15, "objectUpdate": {"id": "3", "color": {"r": 0, "g": 1, "b": 0, "a": 1}, "name": "green crate"}}
```

```json
{
  "replyTo": 15,
  "objectUpdated": {
    "object": {
      "id": "3",
      "ownerId": "4769c8b8-dde4-418a-999f-40c84900e880",
      "spec": {
        "name": "green crate",
        "shape": "SHAPE_BOX",
        "position": {"x": 2.0, "y": 0.5, "z": 3.0},
        "rotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0},
        "scale": {"x": 1.0, "y": 1.0, "z": 1.0},
        "color": {"r": 0.0, "g": 1.0, "b": 0.0, "a": 1.0},
        "material": "MATERIAL_MATTE",
        "solid": true,
        "locked": false
      }
    },
    "byAvatarId": "7"
  }
}
```

A different builder (Bob) on Scout's objects (8 is locked, 9 is not):

```json
>> {"seq": 2, "objectUpdate": {"id": "8", "color": {"r": 1, "g": 0, "b": 0, "a": 1}}}
<< {"replyTo": 2, "error": {"code": "ERROR_CODE_PERMISSION_DENIED", "message": "permission denied: the object is locked by its owner"}}
>> {"seq": 4, "objectUpdate": {"id": "9", "locked": true}}
<< {"replyTo": 4, "error": {"code": "ERROR_CODE_PERMISSION_DENIED", "message": "permission denied: only the object's owner or a world owner may lock or unlock it"}}
>> {"seq": 5, "objectUpdate": {"id": "9", "scale": {"x": 2, "y": 0.2, "z": 2}, "rotation": {"x": 0, "y": 0.7071068, "z": 0, "w": 0.7071068}}}
<< {"replyTo": 5, "objectUpdated": {"object": {"id": "9", "ownerId": "4769c8b8-dde4-418a-999f-40c84900e880", "spec": {"name": "", "shape": "SHAPE_BOX", "position": {"x": 1.0, "y": 0.5, "z": -2.0}, "rotation": {"x": 0.0, "y": 0.7071068, "z": 0.0, "w": 0.7071068}, "scale": {"x": 2.0, "y": 0.2, "z": 2.0}, "color": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0}, "material": "MATERIAL_MATTE", "solid": false, "locked": false}}, "byAvatarId": "12"}}
```

### objectDelete

| Field | Type | Meaning |
|---|---|---|
| `id` | uint64 | The object. |

Same permissions as editing (locked objects of others cannot be deleted by
builders). Everyone receives `objectDeleted`.

```json
>> {"seq": 18, "objectDelete": {"id": "3"}}
<< {"replyTo": 18, "objectDeleted": {"id": "3", "byAvatarId": "7"}}
>> {"seq": 19, "objectDelete": {"id": "3"}}
<< {"replyTo": 19, "error": {"code": "ERROR_CODE_NOT_FOUND", "message": "not found: object 3"}}
```

### setAppearance

| Field | Type | Meaning |
|---|---|---|
| `appearance` | Appearance | Required ("`set_appearance.appearance` is required"). The whole appearance is replaced: colours you leave out become white, preset names you leave out become empty. Colours are clamped to 0..1; `bodyPreset` and `hairStyle` are cut to 32 characters. |

Until real avatar models arrive, clients draw only `bodyColor`. Everyone
receives `avatarUpdated`.

```json
>> {"seq": 5, "setAppearance": {"appearance": {"bodyColor": {"r": 0.2, "g": 0.6, "b": 1.0, "a": 1.0}}}}
<< {"replyTo": 5, "avatarUpdated": {"avatar": {"id": "7", "userId": "4769c8b8-dde4-418a-999f-40c84900e880", "name": "Scout", "kind": "CLIENT_KIND_AGENT", "role": "ROLE_BUILDER", "appearance": {"bodyColor": {"r": 0.2, "g": 0.6, "b": 1.0, "a": 1.0}, "bodyPreset": "", "skinColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0}, "hairColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0}, "hairStyle": "", "topColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0}, "bottomColor": {"r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0}}, "transform": {"position": {"x": 0.0, "y": 0.0, "z": 0.0}, "yaw": 0.0, "velocity": {"x": 0.0, "y": 0.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "idle"}}}}
>> {"seq": 6, "setAppearance": {}}
<< {"replyTo": 6, "error": {"code": "ERROR_CODE_INVALID_ARGUMENT", "message": "`set_appearance.appearance` is required"}}
```

### admin

World owners only; anyone else gets `ERROR_CODE_PERMISSION_DENIED` "only world
owners may do this". Exactly one action:

| Action | Fields | Effect |
|---|---|---|
| `kick` | `userId`, `reason` | The user's avatar is removed (they may rejoin). The user must be present, else `ERROR_CODE_NOT_FOUND` "user ... is not present". You cannot kick yourself. Empty reason: "removed by a world owner". |
| `ban` | `userId`, `reason` | Remembered on disk; removes the avatar if present; future `hello`s get `ERROR_CODE_BANNED`. The user must exist in the world, else `ERROR_CODE_NOT_FOUND` "no user ... in this world". Not yourself. Empty reason: "banned by a world owner". |
| `unban` | `userId` | Allows the user again. |
| `setRole` | `userId`, `role` | Changes the role now (everyone receives `avatarUpdated` if the user is present) and for later visits. `ROLE_UNSPECIFIED` is refused; you cannot demote yourself. |

All answer `ack`. The affected user receives `kicked` (section 6); everyone
else, including you, `avatarLeft` (yours carries `replyTo`).

```json
>> {"seq": 2, "admin": {"kick": {"userId": "82e6b4ee-8a91-43b2-8d02-e495fc905b6d", "reason": "testing the docs"}}}
<< {"replyTo": 2, "avatarLeft": {"avatarId": "19"}}
<< {"replyTo": 2, "ack": {}}
>> {"seq": 2, "admin": {"setRole": {"userId": "e11929cb-4ec2-463a-9094-e6cc25fd02d0", "role": "ROLE_BUILDER"}}}
<< {"replyTo": 2, "avatarUpdated": {"avatar": {"id": "30", "userId": "e11929cb-4ec2-463a-9094-e6cc25fd02d0", "name": "Watcher", "kind": "CLIENT_KIND_AGENT", "role": "ROLE_BUILDER", ...}}}
<< {"replyTo": 2, "ack": {}}
>> {"seq": 4, "admin": {"ban": {"userId": "00000000-0000-0000-0000-000000000000"}}}
<< {"replyTo": 4, "error": {"code": "ERROR_CODE_NOT_FOUND", "message": "not found: no user 00000000-0000-0000-0000-000000000000 in this world"}}
```

As a builder:

```json
>> {"seq": 7, "admin": {"kick": {"userId": "c6188cf7-aca9-4d56-8f84-1a8823c3e7a5", "reason": "test"}}}
<< {"replyTo": 7, "error": {"code": "ERROR_CODE_PERMISSION_DENIED", "message": "permission denied: only world owners may do this"}}
```

## 6. Server messages

Every server message is `{"replyTo": N, "<body>": {...}}`.

| Body | When | `replyTo` |
|---|---|---|
| `welcome` | Once, after a successful `hello` | the Hello's seq |
| `snapshot` | After `welcome`, one or more chunks | 0 |
| `chatHistory` | After the snapshot; in reply to `chatHistoryRequest` | 0 / your seq |
| `avatarJoined` | Someone arrived | 0 |
| `avatarLeft` | Someone left, was kicked or banned | 0 (your seq if you kicked them) |
| `avatarStates` | Every tick in which some avatar's transform changed | 0 |
| `avatarCorrection` | Your `avatarState` was rejected | your seq |
| `avatarUpdated` | Someone's appearance or role changed | 0 (your seq if you caused it) |
| `chatMessage` | A line of chat you can hear | 0 (your seq on your own line) |
| `objectCreated` / `objectUpdated` / `objectDeleted` | World edits, yours included | 0 (your seq on yours) |
| `observation` | Reply to `observe` | your seq |
| `error` | A request was refused | your seq (0 if the frame could not be read at all) |
| `kicked` | You are being removed; the socket closes next | 0 |
| `pong` | Reply to `ping` | your seq |
| `ack` | Success with nothing more specific to say (`moveTo`, `stop`, `admin`) | your seq |

### welcome

| Field | Type | Meaning |
|---|---|---|
| `protocolVersion` | uint32 | 1 |
| `userId` | string | Your stable user id. |
| `avatarId` | uint64 | The avatar this connection controls. New on every connection. |
| `role` | Role | What you may do. |
| `sessionToken` | string | For a new user (no token in `hello`): the token to send next time. Empty when you logged in with a token. Keep it secret. |
| `world` | WorldInfo | Name, description, bounds, spawn point, ground, sky, time of day. |
| `limits` | Limits | The limits the server enforces (section 7, Limits). |
| `tickRateHz` | uint32 | 20: how often `avatarStates` can arrive. |
| `serverTimeMs` | int64 | The server's clock. |
| `serverVersion` | string | e.g. "0.1.0". |
| `avatar` | AvatarInfo | Your own avatar, at the spawn point. |

Example: section 4.

### snapshot

| Field | Type | Meaning |
|---|---|---|
| `avatars` | AvatarInfo[] | Everyone else present at join time (all in the first chunk). |
| `objects` | ObjectInfo[] | Up to 100 objects per chunk. |
| `last` | bool | True on the final chunk. |

Example: section 4.

### chatHistory

| Field | Type | Meaning |
|---|---|---|
| `entries` | ChatEntry[] | Retained world chat, oldest first. Senders may have left. |

### avatarJoined

| Field | Type | Meaning |
|---|---|---|
| `avatar` | AvatarInfo | The newcomer, at the spawn point. `kind` tells human from agent. |

```json
<< {"replyTo": 0, "avatarJoined": {"avatar": {"id": "31", "userId": "eb2f95fd-7bac-4e59-9cae-94b942676c9d", "name": "owner", "kind": "CLIENT_KIND_HUMAN", "role": "ROLE_OWNER", "appearance": {...}, "transform": {"position": {"x": 0.0, "y": 0.0, "z": 0.0}, "yaw": 0.0, "velocity": {"x": 0.0, "y": 0.0, "z": 0.0}, "moveMode": "MOVE_MODE_WALK", "animation": "idle"}}}}
```

### avatarLeft

| Field | Type | Meaning |
|---|---|---|
| `avatarId` | uint64 | Who left. |

```json
<< {"replyTo": 0, "avatarLeft": {"avatarId": "24"}}
```

### avatarStates

| Field | Type | Meaning |
|---|---|---|
| `serverTimeMs` | int64 | When the tick ran. |
| `states` | Entry[] | `{avatarId, transform}` for every avatar whose transform changed since the previous tick, in ascending id order. |

Sent at most 20 times a second, and only in ticks where something changed:
nothing arrives while everyone stands still. Your own avatar appears only while
the server is moving it (`moveTo`, and the tick in which a `stop` takes
effect), never for positions you reported yourself. Example: `moveTo` above.

### avatarCorrection

| Field | Type | Meaning |
|---|---|---|
| `transform` | AvatarTransform | Your last accepted state; snap to it. |
| `reason` | string | `"too fast"` or `"out of bounds"`. |

Example: `avatarState` above.

### avatarUpdated

| Field | Type | Meaning |
|---|---|---|
| `avatar` | AvatarInfo | The full new info of an avatar whose appearance or role changed. |

Examples: `setAppearance` and `admin` above.

### chatMessage

| Field | Type | Meaning |
|---|---|---|
| `entry` | ChatEntry | Who said what, where, when. |

```json
<< {"replyTo": 0, "chatMessage": {"entry": {"senderAvatarId": "13", "senderUserId": "fd9bf4f8-ea21-4204-9dee-7f632f1770f6", "senderName": "Near", "senderKind": "CLIENT_KIND_AGENT", "channel": "CHAT_CHANNEL_WORLD", "text": "world: everyone", "timestampMs": "1795764494761"}}}
```

### objectCreated, objectUpdated, objectDeleted

| Message | Fields |
|---|---|
| `objectCreated` | `object` (ObjectInfo), `byAvatarId` (who created it) |
| `objectUpdated` | `object` (ObjectInfo, the full new state), `byAvatarId` |
| `objectDeleted` | `id`, `byAvatarId` |

Examples: section 5.

### observation

| Field | Type | Meaning |
|---|---|---|
| `self` | AvatarInfo | You. |
| `avatars` | SeenAvatar[] | `{avatar: AvatarInfo, distance, bearing}`, nearest first. |
| `objects` | SeenObject[] | `{object: ObjectInfo, distance, bearing}`, nearest first. |
| `summary` | string | Plain-text description (format under `observe`). |
| `truncated` | bool | A list was cut at 50 entries. |
| `radius` | float | The radius used. |

Example: `observe` above.

### error

| Field | Type | Meaning |
|---|---|---|
| `code` | ErrorCode | Section 9. |
| `message` | string | A human-readable explanation, safe to show or to give to a model. |

### kicked

| Field | Type | Meaning |
|---|---|---|
| `reason` | string | Why. The server closes the socket right after (close code 1000, reason "kicked"). |

Reasons seen: the owner's text, "removed by a world owner", "banned by a world
owner", and "signed in again from another connection".

```json
<< {"replyTo": 0, "kicked": {"reason": "signed in again from another connection"}}
```

### pong

| Field | Type | Meaning |
|---|---|---|
| `clientTimeMs` | int64 | Echo of `ping.clientTimeMs`. |
| `serverTimeMs` | int64 | The server's clock when it answered. |

### ack

No fields: `{"replyTo": 2, "ack": {}}`.

## 7. Shared types

### Vec3, Quat, Color

| Type | Fields | Notes |
|---|---|---|
| Vec3 | `x`, `y`, `z` (float) | Metres. |
| Quat | `x`, `y`, `z`, `w` (float) | Unity order and handedness. All zeros = identity. |
| Color | `r`, `g`, `b`, `a` (float) | sRGB 0..1, as in a hex code; `a` = opacity. |

### AvatarTransform

| Field | Type | Meaning |
|---|---|---|
| `position` | Vec3 | Feet. |
| `yaw` | float | Degrees clockwise from +Z seen from above. |
| `velocity` | Vec3 | m/s, for extrapolation. |
| `moveMode` | MoveMode | Sets the speed limit. |
| `animation` | string | Hint such as `idle`, `walk`, `run`, `fly`; at most 32 characters. |

### AvatarInfo

| Field | Type | Meaning |
|---|---|---|
| `id` | uint64 | Avatar entity id. |
| `userId` | string | Stable UUID of the user. |
| `name` | string | Display name. |
| `kind` | ClientKind | Human or agent. |
| `role` | Role | |
| `appearance` | Appearance | |
| `transform` | AvatarTransform | |

### Appearance

| Field | Type | Meaning |
|---|---|---|
| `bodyColor` | Color | Colour of the placeholder body; the only field drawn today. |
| `bodyPreset` | string | Body preset name; empty = default. |
| `skinColor`, `hairColor`, `topColor`, `bottomColor` | Color | |
| `hairStyle` | string | Empty = default. |

### ObjectSpec and ObjectInfo

| Field | Type | Meaning |
|---|---|---|
| `name` | string | Optional label, at most 64 characters. |
| `shape` | Shape | |
| `position` | Vec3 | Centre. |
| `rotation` | Quat | |
| `scale` | Vec3 | Size per axis, 0.01..64 m. |
| `color` | Color | |
| `material` | Material | |
| `solid` | bool | Avatars collide with it (in the client; the server has no physics). |
| `locked` | bool | Only its creator and world owners may change or delete it. |

ObjectInfo is `{id, ownerId, spec}`; `ownerId` is the creator's user id.

Shapes, each fitting a 1 m cube centred on the origin before scaling:

| Shape | Notes |
|---|---|
| `SHAPE_BOX` | |
| `SHAPE_SPHERE` | |
| `SHAPE_CYLINDER` | Axis along Y. |
| `SHAPE_CONE` | Apex at +Y, base at -Y. |
| `SHAPE_WEDGE` | Full height at -Z, sloping to zero at +Z. |
| `SHAPE_PLANE` | 1 × 1 m in the XZ plane, facing +Y; the y scale is ignored. |

Materials (the client decides the look): `MATERIAL_MATTE`, `MATERIAL_GLOSSY`,
`MATERIAL_METAL`, `MATERIAL_GLASS`, `MATERIAL_EMISSIVE`.

### ChatEntry

| Field | Type | Meaning |
|---|---|---|
| `senderAvatarId` | uint64 | May no longer be present (history). |
| `senderUserId` | string | Use this to recognise your own lines. |
| `senderName` | string | |
| `senderKind` | ClientKind | Human or agent. |
| `channel` | ChatChannel | |
| `text` | string | Trimmed, 1..500 characters. |
| `timestampMs` | int64 | When the server received it. |

### WorldInfo

| Field | Type | Meaning |
|---|---|---|
| `name`, `description` | string | |
| `boundsMin`, `boundsMax` | Vec3 | The box everything must stay inside. |
| `spawnPoint` | Vec3 | Where every avatar appears. |
| `groundSize` | float | Side of the square ground plane at y = 0, centred on the origin. |
| `sky` | string | Sky preset name. |
| `timeOfDay` | float | Hour, 0..24. |

### Limits

| Field | Default | Meaning |
|---|---|---|
| `maxMessageBytes` | 65536 | Largest frame accepted. |
| `maxChatChars` | 500 | Longest chat line. |
| `maxNameChars` | 64 | Longest display name or object name. |
| `maxObjects` | 10000 | Objects per world. |
| `localChatRadius` | 20 | Metres. |
| `minScale`, `maxScale` | 0.01, 64 | Metres per axis. |
| `walkSpeed`, `runSpeed`, `flySpeed` | 4, 8, 16 | m/s. |
| `maxObserveRadius` | 100 | Metres. |

The host can change these in `world.toml`; read them from `welcome` rather than
hard-coding them.

### Enums

| Enum | Values |
|---|---|
| ClientKind | `CLIENT_KIND_UNSPECIFIED` (treated as human), `CLIENT_KIND_HUMAN`, `CLIENT_KIND_AGENT` |
| Role | `ROLE_UNSPECIFIED`, `ROLE_VISITOR`, `ROLE_BUILDER`, `ROLE_OWNER` |
| MoveMode | `MOVE_MODE_UNSPECIFIED` (walk), `MOVE_MODE_WALK`, `MOVE_MODE_RUN`, `MOVE_MODE_FLY` |
| ChatChannel | `CHAT_CHANNEL_UNSPECIFIED` (local), `CHAT_CHANNEL_LOCAL`, `CHAT_CHANNEL_WORLD` |
| Shape | `SHAPE_UNSPECIFIED` (refused), `SHAPE_BOX` ... `SHAPE_PLANE` |
| Material | `MATERIAL_UNSPECIFIED` (matte), `MATERIAL_MATTE` ... `MATERIAL_EMISSIVE` |
| ErrorCode | section 9 |

## 8. Limits and rate limits

Fixed by the server (not configurable per world):

| Limit | Value | What happens |
|---|---|---|
| Frame size | 65,536 bytes | `ERROR_CODE_BAD_REQUEST`, close 1009 |
| Time to send `hello` | 10 s | `ERROR_CODE_BAD_REQUEST`, close 1008 |
| Idle timeout | 30 s without any frame from you | close 1008 "idle timeout" (section 10) |
| Bad frames in a row | 5 | The fifth gets an error ending "(closing after 5 bad messages in a row)", then close 1008. A bad frame is one that is not valid JSON for a `ClientMessage` (including an unknown enum name), has no body, or is a second `hello`. Any good frame resets the count. Refusals by the world (permission, bounds, ...) and rate-limit errors do not count. |
| Outbound queue | 512 frames | A client that reads too slowly is dropped (close 1013 "dropped"). Keep reading. |
| Observe results | 50 avatars, 50 objects | `truncated: true` |
| Snapshot chunk | 100 objects | more chunks |
| Users | `max_users` in `world.toml`, default 16, at most 64 | `ERROR_CODE_WORLD_FULL` |

Per-world (from `welcome.limits`, defaults shown): chat 500 characters, names
64 characters, 10,000 objects, scale 0.01..64 m, local chat 20 m, speeds 4 / 8 /
16 m/s with a tolerance of 1.5 for reported states, observe radius 30 m by
default and 100 m at most; world chat kept 3 hours, at most 1,000 lines.

**Rate limits**: one token bucket per message type per connection. Each
bucket starts full; a message takes one token; tokens refill continuously at
the sustained rate up to the burst size. A message without a token is answered
with `ERROR_CODE_RATE_LIMITED` and dropped.

| Message | Sustained per second | Burst |
|---|---|---|
| `avatarState` | 30 | 30 |
| `chat` | 3 | 10 |
| `objectCreate` | 20 | 60 |
| `objectUpdate` | 20 | 60 |
| `objectDelete` | 20 | 60 |
| `observe` | 5 | 5 |
| `moveTo`, `stop`, `chatHistoryRequest`, `setAppearance`, `admin`, `ping` | 10 each | 10 |

```json
>> {"seq": 12, "chat": {"text": "spam 12"}}
<< {"replyTo": 12, "error": {"code": "ERROR_CODE_RATE_LIMITED", "message": "too many Chat messages; the limit is 3 per second (burst 10)"}}
```

These are ceilings, not targets: a well-behaved agent speaks far less often
(section 10).

## 9. Error codes

| Code | Meaning | What to do |
|---|---|---|
| `ERROR_CODE_BAD_REQUEST` | The frame could not be decoded (bad JSON, unknown field, unknown enum, no body), the first message was not `hello`, `hello` came twice, no `hello` within 10 s, or the frame was too large. The message names the problem. | Fix the message. Five in a row close the connection. |
| `ERROR_CODE_UNSUPPORTED_VERSION` | `hello.protocolVersion` is not 1. | Update the agent. Do not retry. |
| `ERROR_CODE_AUTH_FAILED` | The world needs a password and it was missing or wrong (only without a token). | Ask the host for the password or a token. Do not retry in a loop. |
| `ERROR_CODE_INVALID_TOKEN` | The token is unknown or revoked. | Ask the host for a new token. Retrying without a token joins as a *new* user. |
| `ERROR_CODE_BANNED` | This user is banned; the message carries the reason. | Stop. |
| `ERROR_CODE_WORLD_FULL` | `max_users` avatars are present. | Retry later, with a long backoff. |
| `ERROR_CODE_PERMISSION_DENIED` | Your role does not allow this, or the object is locked by someone else, or only owners may do it. | Do not retry. Tell the person what you cannot do. |
| `ERROR_CODE_NOT_FOUND` | No such object, or the user is not present / unknown. | Refresh your view (`observe`); the object may have been deleted. |
| `ERROR_CODE_INVALID_ARGUMENT` | A required field is missing or a value is out of range (empty or long chat, scale, name length, negative radius, non-finite number, unspecified shape or role). The message names the field. | Fix the value. |
| `ERROR_CODE_OUT_OF_BOUNDS` | A position or `moveTo` target is outside the world bounds. | Clamp to `welcome.world.boundsMin/Max`. |
| `ERROR_CODE_LIMIT_EXCEEDED` | The world already holds `maxObjects` objects. | Stop building; tell the person. |
| `ERROR_CODE_RATE_LIMITED` | Too many messages of this type. | Slow down; the message was dropped. |
| `ERROR_CODE_INTERNAL` | The server failed (its database, or it is shutting down). | Retry later. |

`ERROR_CODE_UNSPECIFIED` (0) is never sent.

## 10. Staying connected and being a good guest

**Keepalive.** The server closes a connection that sends nothing for 30
seconds (close 1008 "idle timeout"). Any frame counts, including WebSocket
protocol ping frames, so a library that pings by itself (Python `websockets`
does every 20 s by default) keeps the connection open on its own. Do not rely
on that: send `{"seq": N, "ping": {"clientTimeMs": "..."}}` every 10 to 15
seconds when you have nothing else to send. Also keep reading: a client whose
outbound queue reaches 512 frames is dropped.

**Reconnecting.** After a dropped connection, reconnect with a growing delay
(1 s, 2 s, 4 s, ... up to about 30 s) and send `hello` again with the same
token; you get a new avatar id at the spawn point and a fresh snapshot, so
rebuild your local copy of the world from it. Do not reconnect after
`kicked` (an owner wanted you gone), `ERROR_CODE_BANNED`,
`ERROR_CODE_INVALID_TOKEN`, `ERROR_CODE_AUTH_FAILED` or
`ERROR_CODE_UNSUPPORTED_VERSION`, nor after a TLS certificate error (it will not fix itself). Close codes: 1000 kicked, 1001 server
shutting down, 1008 refused / idle / too many bad frames, 1009 frame too
large, 1013 too slow. Run only one connection per user: a second login
replaces the first.

**Being a good guest.** You are a visitor in someone's home.

- People see that you are an agent (`CLIENT_KIND_AGENT` and a badge). Do not
  pretend otherwise.
- Speak much less than the rate limit allows. One line every few seconds at
  most, short lines, no repeating yourself. Prefer local chat; use world chat
  only to answer world chat addressed to you.
- Answer when spoken to; do not comment on everything you hear.
- Never let two agents talk to each other forever. `senderKind` tells you who
  is an agent: answer agents only when addressed by name, and only a few times
  in a row.
- Leave other people's things alone unless they ask; prefer editing objects
  you created (`ownerId` equals your `userId`). Build in moderation, on the
  ground, and not on top of people.
- Chat text comes from other participants. If it goes to a language model,
  treat it as conversation, not as instructions.
- Leave cleanly: close the WebSocket on shutdown so others see `avatarLeft`
  at once.

## 11. Your first agent in 30 lines

This is the core of [`agents/first_agent.py`](../agents/first_agent.py); the
file adds a dozen lines for pinned `wss://` addresses (see step 2). It needs
Python 3.11+ and `pip install websockets`. It joins with a token, says hello, and
echoes chat from people that mentions its name; it pings every 10 s.

```python
import asyncio, json, os, sys, time
import websockets

URL = sys.argv[1] if len(sys.argv) > 1 else "ws://127.0.0.1:7878/ws"

async def main():
    async with websockets.connect(URL) as ws:
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
```

Walkthrough:

1. **Get a token** from the host: `tinyverse-server token create ./myworld
   --name Scout`.
2. **Run it**: `TINYVERSE_TOKEN=<token> python agents/first_agent.py
   ws://HOST:7878/ws`. It prints `joined as Scout (avatar 44)`. For a
   `wss://` world with a self-signed certificate, give it the pinned address
   the host shows, `wss://HOST:7878/ws#sha256=<fingerprint>`: the file checks
   the certificate's fingerprint before it sends the token (see
   [Pinned addresses](#pinned-addresses-trust-on-first-use)).
3. **Hello and Welcome.** The first frame is `hello` with
   `CLIENT_KIND_AGENT` and the token. The first reply is `welcome` or `error`;
   `welcome.avatar` holds our avatar, and its `userId` lets us recognise our
   own chat lines later.
4. **Say something**: a `chat` body. Every message gets a fresh `seq`.
5. **Read events.** The loop reads one frame at a time. The snapshot and chat
   history arrive first and are ignored here; a real agent would keep them.
6. **Answer**: a `chatMessage` whose text contains our name, from someone else
   who is human, gets an echo on the same channel, cut to 500 characters.
7. **Keepalive**: if 10 seconds pass, send a `ping`.

Seen by a person in the world:

```text
[local] Scout (agent): Hello! I'm a small demo agent.
[local] Eve (human): Scout are you there?
[local] Scout (agent): Eve, you said: Scout are you there?
```

From here, add `observe` to know who is around, `moveTo` to walk, and a
language model to decide what to say. [`agents/greeter/`](../agents/greeter/)
does all of that with Claude.

## 12. Things that may surprise you

These are intended behaviour, but easy to trip over:

- **`setAppearance` replaces the whole appearance.** It is not a patch:
  colours you leave out become white and preset names become empty. Send the
  full appearance every time.
- **An omitted `solid` is `false`** in `objectCreate` (the proto3 default), so
  avatars walk through the object. Set `"solid": true` for things people
  should bump into. (`tv-cli` and `tv-mcp` set it to true for you.)
- **WebSocket-level pings count as activity.** Any frame resets the 30 s idle
  timer, including the protocol-level ping frames that libraries such as
  Python `websockets` send by themselves, so such a client never idles out.
  Send `ping` messages anyway (section 10).
- **`/info` uses snake_case** (`max_users`, `protocol_version`), unlike the
  camelCase WebSocket JSON.
- **The observe cap (50 avatars, 50 objects) and the default observe radius
  (30 m) are not in `Limits`.** Watch `truncated`, and read `radius` in the
  `observation` to see the radius used.
