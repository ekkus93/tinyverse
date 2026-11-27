# Tinyverse — Specification

Status: draft v0.1 (2026-11-26)

## 1. What it is

Tinyverse is a tiny, self-hosted virtual world. One person runs a server that
holds one world (a room, a house, a town, an island). Friends and AI agents
connect to it, each with an avatar. They walk or fly around, talk, and build
things together. There is no goal.

### Goals

- One host, one world, a handful of visitors (target 16 concurrent, hard cap 64).
- Humans join through a Unity desktop client. AI agents join through the same
  network API, with no Unity involved.
- Everything built in the world persists on the host's disk.
- The server is a single small Rust binary with no external services.
- Linux first. Mac, Windows and mobile clients come later.

### Non-goals for v1

- A shared grid, world-to-world travel, or central accounts.
- An economy, marketplace or asset store.
- User scripting and games (planned after v1, see section 12).
- Voice chat (planned after v1).
- Joining over the internet without the host opening a port. v1 covers the
  local network and direct connections; the approach for internet play is
  still to be designed (section 13).

## 2. Architecture

```
 Unity client (human) ─┐
 Unity client (human) ─┼── WebSocket ──► tinyverse-server (Rust) ──► world.db (SQLite)
 AI agent (JSON) ──────┤                  authoritative world state
 MCP bridge ── agent ──┘
```

- The server is authoritative for world state: who is present, what objects
  exist, who may change them.
- Every participant speaks one protocol over one WebSocket. The Unity client has
  no privileged path, so anything a human can do an agent can do.
- The protocol is defined once, in protobuf, under `proto/`. Rust and C# code is
  generated from it. That schema is the contract between all workstreams.
- Hosting from the desktop app means the Unity client launches the bundled
  server binary as a child process and connects to it on localhost. The server
  also runs standalone and headless.

## 3. Repository layout

```
docs/                 SPEC.md, TODO.md, later PROTOCOL.md and AGENT_API.md
proto/tinyverse/v1/   *.proto — the protocol contract
server/               Cargo workspace
  crates/tv-proto/    generated protobuf types + JSON mapping
  crates/tv-world/    pure world state and rules (no IO, no async)
  crates/tv-store/    SQLite persistence
  crates/tv-tls/      certificate fingerprints and pinning, shared by server and clients
  crates/tv-server/   binary: networking, sessions, tick loop, CLI
  crates/tv-cli/      binary: test client and scriptable bot
  crates/tv-mcp/      binary: MCP bridge for AI agents (milestone 3)
client/               Unity project
  Assets/Tinyverse/
    Proto/            generated C# protobuf types (Proto/Generated, do not edit)
    Net/              connection, message pump, typed events
    World/            ground, sky, object rendering and registry
    Avatar/           locomotion, camera, remote interpolation
    Build/            in-world building tools
    UI/               UI Toolkit screens and HUD
    Host/             launching the bundled server (milestone 4)
    Bootstrap/        the single scene, and the session glue that wires the rest together
    Editor/           build script
agents/               example agents
scripts/              codegen, checks, packaging
```

## 4. Protocol

### 4.1 Transport

- WebSocket, default port 7878, path `/ws`.
- Binary frames carry protobuf-encoded messages. Text frames carry the same
  messages in proto3 JSON. The server replies in whichever encoding the client
  used for `Hello`. Unity uses binary; agents normally use JSON.
- `GET /info` returns world name, description, player count and protocol version
  as JSON without authentication. `GET /health` returns 200.
- TLS: the server serves `wss://` (and `https://`) on the same port when given
  a certificate (`--tls-cert`/`--tls-key`, or `cert.pem`/`key.pem` in the
  world directory), or plain `ws://` otherwise. `run --self-signed` and
  `tls self-signed` make a self-signed certificate valid for `localhost`,
  `127.0.0.1`, `::1` and every current network address (`--host` adds more).
  With TLS, `run` prints `tls certificate sha256=<hex>` before its ready line
  and `join with wss://<address>:<port>/ws#sha256=<hex>` lines after it;
  `tls fingerprint <dir>` prints the same. `GET /info` does not carry it.
- Trust on first use by pin: a world address may end in `#sha256=<hex>`, the
  SHA-256 of the server's leaf certificate. With a pin a client accepts exactly
  that certificate (host name and chain unchecked, handshake signature still
  checked) and sends nothing before checking; without one, normal WebPKI
  verification applies (plus `--ca-file` in `tv-cli` and `tv-mcp`). The
  fragment never leaves the client. No client has a switch that turns
  verification off. Shared Rust code is in `tv-tls`.
- The Unity client remembers the pin per `host:port` once the player accepts
  a certificate (shown by fingerprint) or joins by a pinned address. A
  different certificate later is refused, with the choice to forget the old
  pin. Hosting from the client uses TLS with a self-signed certificate by
  default; the Host screen shows the pinned LAN address, and the host joins its
  own world with the pin.
- Limits: 64 KB per message, per-connection rate limits per message type,
  30 s idle timeout with ping/pong.

### 4.2 Conventions

- Units are metres and seconds. Coordinates are left-handed, Y up (Unity's
  convention), so the client needs no conversion. Rotations are quaternions
  `x y z w`. Avatars carry yaw only, in degrees, clockwise seen from above,
  0 facing +Z. Colours are sRGB 0..1 floats, as in a hex code. Timestamps are milliseconds since the
  Unix epoch.
- Entity ids are `uint64`, assigned by the server, never reused. User ids are
  UUID strings.
- Every client message carries a `seq`. Replies and errors carry `reply_to`.
  An event a client caused itself (for example `ObjectCreated`) carries
  `reply_to` on that client's copy and 0 on everyone else's.
- In JSON, field names are camelCase (snake_case is also accepted on input),
  enums are their full names such as `MOVE_MODE_WALK`, 64-bit integers are
  written as strings, and every field is written even when zero.
- `protocol_version` is an integer checked in `Hello`. Fields are only added,
  never renumbered.

### 4.3 Messages

Client to server:

| Message | Purpose |
|---|---|
| `Hello` | protocol version, client kind (`human`/`agent`), display name, credential (password or token), appearance |
| `AvatarState` | own position, yaw, velocity, move mode (`walk`/`run`/`fly`), animation hint |
| `MoveTo` / `Stop` | ask the server to move the avatar to a point (for agents) |
| `Chat` | text to `local` (within 20 m) or `world` |
| `ChatHistoryRequest` | ask for retained `world` chat, optionally since a timestamp |
| `ObjectCreate` | new primitive with full properties |
| `ObjectUpdate` | changed fields of one object |
| `ObjectDelete` | remove one object |
| `Observe` | request a description of everything within a radius |
| `SetAppearance` | change own avatar appearance |
| `Admin` | kick, ban, unban, set role (owner only; milestone 4) |
| `Ping` | keepalive and latency |

Server to client:

| Message | Purpose |
|---|---|
| `Welcome` | own user id, avatar id, role, session token, world info, tick rate, limits |
| `Snapshot` | chunk of existing avatars and objects, last chunk flagged |
| `AvatarJoined` / `AvatarLeft` | presence |
| `AvatarStates` | batch of changed avatar transforms, sent each tick |
| `AvatarCorrection` | server overrides the client's own position |
| `AvatarUpdated` | an avatar's name, role or appearance changed |
| `ChatMessage` | sender id, name, channel, text, timestamp |
| `ChatHistory` | retained `world` messages, oldest first; sent after the snapshot on join and in reply to `ChatHistoryRequest` |
| `ObjectCreated` / `ObjectUpdated` / `ObjectDeleted` | world edits, including the sender's own |
| `Observation` | reply to `Observe` |
| `Error` | code, human-readable message, `reply_to` |
| `Kicked` | reason, then the server closes the socket |
| `Pong` | keepalive |
| `Ack` | a request succeeded and has no more specific reply |

### 4.4 Session flow

1. Client connects and sends `Hello`.
2. Server checks version, credential, ban list and capacity, then sends
   `Welcome` or `Error` followed by close.
3. Server sends `Snapshot` chunks, then starts streaming live events. Events that
   happen during the snapshot are queued and delivered after it.
4. On disconnect the server broadcasts `AvatarLeft`.

## 5. World model

### 5.1 World

A world is a directory:

```
myworld/
  world.toml    name, description, password hash, max users, bounds, spawn point, environment
  world.db      SQLite: users, objects, bans, metadata
```

The environment in v1 is a flat ground plane of configurable size, a sky preset
and a time of day. Terrain and world templates (room, island) are later work.
World bounds are an axis-aligned box. Nothing may be placed or moved outside it.

### 5.2 Avatars

- One avatar per connection. State: position, yaw, velocity, move mode,
  animation hint, appearance, display name, kind (human or agent).
- Movement is client-driven. The client simulates its own avatar and reports
  `AvatarState` at up to 15 Hz while moving and 1 Hz when idle. The server
  checks speed against the move mode (walk 4 m/s, run 8 m/s, fly 16 m/s, with
  tolerance) and world bounds. On a violation it sends `AvatarCorrection`.
- The server broadcasts changed avatar states at 20 Hz. Clients render other
  avatars about 100 ms in the past and interpolate.
- Agents may use `MoveTo` instead. The server then moves the avatar in a
  straight line at walk or fly speed. In v1 this ignores obstacles.
- Avatars are ordinary, realistically proportioned people, not cartoon
  characters. Until milestone 4 a coloured capsule with a name tag stands in.
  Milestone 4 adds rigged human models with idle, walk, run and fly animation
  and an appearance editor (body preset, skin tone, hair, clothing colours).
  The models must be licensed for redistribution with the client; MakeHuman
  exports (CC0) are the first candidate.

### 5.3 Objects

Everything built in the world is a primitive object ("prim"):

| Field | Notes |
|---|---|
| `id` | server-assigned |
| `owner` | user id of the creator |
| `name` | optional, up to 64 characters |
| `shape` | box, sphere, cylinder, cone, wedge, plane |
| `position`, `rotation`, `scale` | scale per axis, 0.01 m to 64 m |
| `color` | RGBA |
| `material` | preset: matte, glossy, metal, glass, emissive |
| `flags` | solid (avatars collide), locked (owner-only edits) |

- Edits are last-write-wins. While dragging, a client sends `ObjectUpdate` at
  up to 10 Hz and the server relays it to everyone.
- Limits: 10,000 objects per world by default, per-user creation rate limit.
- Collision between avatars and solid objects is resolved in the Unity client.
  The server does not simulate physics in v1.

### 5.4 Chat

Plain text, 500 characters maximum. `local` reaches avatars within 20 m,
`world` reaches everyone.

The host keeps recent `world` chat for a short time so that latecomers and
agents can read back. It is held in memory only: messages older than the
retention period (default 3 hours, set in `world.toml`) or beyond the newest
1,000 are dropped, and a server restart clears it. Nothing is written to disk.
`local` chat is never retained.

## 6. Identity and permissions

- There are no accounts outside the world. A world may have a join password.
- On first join the server creates a user record and returns a random session
  token. The client stores it per world and presents it next time, so the same
  person keeps the same user id, role and objects.
- Agents use tokens the host creates ahead of time:
  `tinyverse-server token create <world-dir> --name scout --role builder`.
- Roles:

| Role | Can do |
|---|---|
| `visitor` | move, chat, observe |
| `builder` | also create objects, edit and delete own objects and unlocked objects of others |
| `owner` | everything, plus edit any object, kick, ban, change roles |

- The default role for new users is set in `world.toml` (default `builder`).
- Only token hashes are stored. The password is stored as an Argon2 hash.

## 7. Server design

- Crates: `tokio`, `axum` (WebSocket and HTTP), `prost` with a proto3 JSON
  mapping, `rusqlite` (bundled SQLite), `glam`, `clap`, `tracing`.
- `tv-world` is pure and synchronous:
  `World::join(request, now_ms)`, `World::leave(avatar, now_ms)`,
  `World::apply(actor, Command, now_ms) -> Result<Vec<Event>, WorldError>` and
  `World::tick(dt, now_ms) -> Vec<Event>`. It holds all rules: validation,
  permissions, limits. It has no knowledge of sockets, protobuf or the clock
  (time is passed in), so it is fully unit-tested. Each `Event` names its
  audience: everyone, everyone but one avatar, one avatar, a set, or the host
  process itself (to persist a ban or a role change).
- `tv-server` runs one world task that owns the `World`. Each connection is a
  task that decodes messages into commands, sends them over a channel, and
  receives events to encode. There are no locks around world state.
- The world task ticks at 20 Hz: drain commands, advance server-driven avatars,
  batch avatar states, fan events out to sessions. A slow client's queue is
  bounded. When it overflows the client is dropped.
- `tv-store` loads the world at start and writes behind: changed objects are
  flushed every 5 s and on shutdown, on a blocking thread. SIGINT and SIGTERM
  trigger a clean flush.
- CLI: `tinyverse-server init <dir>`, `run <dir> [--port] [--bind]
  [--self-signed | --tls-cert --tls-key | --no-tls]`,
  `tls self-signed|fingerprint`, `token create|list|revoke`,
  `user list|set-role|ban`, `password`.
- Budget: 16 users and 10,000 objects should sit under 50 MB of memory and a few
  percent of one core.

## 8. Unity client design

- Unity 6000.6 (the version installed on the dev machine), URP, Input System,
  UI Toolkit. Linux standalone build first.
- Minimum graphics: an integrated GPU with hardware acceleration. A dedicated
  GPU is not required, so visitors can join from ordinary laptops. Software
  rendering (llvmpipe) is used only for automated screenshots and is not a
  supported way to play. Content is budgeted for this: an avatar is at most
  about 15,000 triangles, 4 materials and 1024-pixel textures, with plain URP
  Lit materials.
- Networking uses `System.Net.WebSockets.ClientWebSocket` for `ws://` and
  `Google.Protobuf`. No Netcode for GameObjects: the Rust server replaces it.
  Receive runs off the main thread and hands decoded messages to a main-thread
  queue. For `wss://` the client opens TCP and `SslStream` itself (with its own
  certificate check: the pin, or the system's verdict), does the HTTP upgrade
  by hand and frames with `WebSocket.CreateFromStream`: Unity's Mono
  `ClientWebSocket` ignores both `ClientWebSocketOptions.RemoteCertificateValidationCallback`
  and `ServicePointManager.ServerCertificateValidationCallback` (checked in
  6000.6), so it cannot accept a self-signed certificate by fingerprint.
- One assembly definition per folder under `Assets/Tinyverse/`. `Net`
  references `Proto`; `Build` references `World`; `World`, `Avatar`, `UI` and
  `Host` reference none of the others and expose plain C# types, events and
  methods. `Bootstrap` references all of them and holds the session glue that
  translates between protocol messages and the modules. Assemblies are
  referenced by name, not GUID, so code can be added without the editor.
- There is a single scene. It contains one bootstrap object. Everything else is
  created from code or from prefabs loaded by the bootstrap, which keeps scene
  files out of merge conflicts.
- Screens: connect (address, name, password, recent worlds), in-world HUD
  (chat, people list, build toolbar), settings.
- Controls: WASD and mouse-look in third person, shift to run, `F` toggles fly,
  space and ctrl go up and down while flying, `Enter` focuses chat, `B` toggles
  build mode.
- Build mode: pick a shape and place it in front of the avatar or on a surface;
  select; move, rotate and scale gizmos with grid snapping; colour and material
  pickers; duplicate; delete; undo for own actions.

## 9. Agent API

- Agents connect to the same WebSocket and send JSON text frames.
- An agent cannot stream 15 Hz movement, so the API has three conveniences:
  `MoveTo`, `Observe`, and the normal event stream for chat and world changes.
- `Observation` lists nearby avatars and objects with id, name, kind, position,
  distance and bearing relative to the agent, plus a short generated text
  summary suitable for putting straight into a model's context.
- `tv-mcp` is a small bridge that exposes the world as MCP tools (`look`,
  `move_to`, `say`, `create_object`, `edit_object`, `delete_object`,
  `recent_chat`), so an MCP-capable assistant can join with only a token.
- Agents are marked as such in `AvatarJoined`, and the client shows a badge on
  their name tag. Agents get the same roles and limits as people.
- `docs/AGENT_API.md` documents the JSON form of every message with examples.

## 10. Milestones

| # | Name | Result |
|---|---|---|
| M0 | Foundations | repo skeleton, protocol schema, generated code on both sides, empty server and client that build |
| M1 | Walk and talk | two Unity clients and a bot see each other move and chat in a flat world |
| M2 | Build and persist | objects can be created and edited by anyone present and survive a server restart |
| M3 | Agents | JSON API documented, `Observe` and `MoveTo`, MCP bridge, an example AI visitor |
| M4 | Host and polish | "Host a world" inside the client, roles and moderation, TLS, better avatars, Linux package |

## 11. Working in parallel

The plan in `TODO.md` assumes several agents working at once. The rules that
make this safe:

- M0 is done first, before any parallel work. It freezes the three contracts: the proto
  schema, the `tv-world` command and event types, and the Unity assembly layout.
- After M0 each task owns a set of directories and edits nothing outside them.
  A change to a contract goes through the coordinator, not a track.
- Rust tracks each own one crate. They can build and test independently.
- Unity tracks write C#, UXML and USS only, inside their own folder. One Unity
  integrator owns the scene, prefabs, `ProjectSettings/`, `Packages/` and
  `.meta` files, and is the only one that opens the editor on the main checkout.
  The protocol types `Color`, `Vec3`, `Quat`, `Material` and `Shape` share
  names with Unity types, so only `Net` and `Bootstrap` touch `Tinyverse.Proto`
  and the other modules stay on Unity types.
  Only one editor can open a project at a time, so a Unity track that needs to
  run tests uses its own git worktree.
- Each milestone ends with one serial integration task that wires the tracks
  together and runs the end-to-end check.
- `tv-cli` exists early so server work can be tested without Unity, and Unity
  work can be tested against a server with a bot walking around.

## 12. After v1

- Scripting and games: scripts attached to objects, run on the server in a
  sandbox (Lua or WebAssembly), reacting to touch, chat and timers.
- Voice chat with distance attenuation.
- Sitting, gestures, and importing custom avatar models.
- Imported meshes (glTF) and textures, with an asset store on the host.
- Terrain and world templates.
- Joining over the internet without port forwarding (approach undecided).
- Mac and Windows clients, then mobile.
- Server-side collision so agents and `MoveTo` respect walls.

## 13. Open questions

1. Internet play: how do visitors reach a host behind a home router? Tailscale
   is ruled out for now. Candidates are a relay service, hole punching, or
   automatic port mapping (UPnP). Until this is settled, v1 works on a local
   network or with a manually forwarded port.
2. Scripting language for games: Lua or WebAssembly? Only affects work after v1.

Decided on 2026-11-26:

- Avatars are realistic people, not stylised (section 5.2).
- The host keeps `world` chat for a few hours, in memory only (section 5.4).
