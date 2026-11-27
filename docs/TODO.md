# Tinyverse — TODO

See `SPEC.md` for the design. Section numbers below refer to it.

## Status (2026-11-27)

Everything through M3 is implemented and merged, plus most of M4. The work was
done per crate and per client module, not strictly in the waves below; the
boxes record what is finished.

Verified: `scripts/check.sh` (Rust fmt, clippy, all tests including tv-cli's
17-check smoke run and tv-mcp against the real server binary); the Unity
EditMode and PlayMode suites, including Bootstrap's tests against the real
server; the built Linux player on screen through screenshots; the packaged
archive hosting a world with its bundled server.

Not yet verified by a person: real keyboard and mouse input, two Unity
clients in one world, performance on a real GPU, the package on a fresh
machine, and the greeter agent's Claude-backed path (no API key was set).

Open:

- [ ] M4.D realistic human avatars (a capsule placeholder stands in)
- [ ] M4.I on a fresh machine: download, host, and a second machine joins; tag `v0.1.0`
- [ ] Play-test with real input and two Unity clients
- [ ] TLS for LAN guests: the self-signed certificate covers only localhost unless made with `--host <lan-ip>`, and the Unity client has no way to trust it yet

## How to read this

- Each task is sized for one agent and has an id, the paths it owns, what it
  depends on, and a check that says when it is done.
- A task edits only the paths it owns. If it needs a contract changed (proto
  schema, `tv-world` public types, assembly layout), it stops and reports back.
- Tasks in the same wave can run at the same time. Waves run in order.
- Tasks marked **serial** are done by the coordinator or a single agent with
  nothing else running.
- Unity tasks write C#, UXML and USS only. Scenes, prefabs, `.meta` files,
  `ProjectSettings/` and `Packages/` belong to the integration tasks.

## Prerequisites on the dev machine

- [x] Rust toolchain (cargo 1.99)
- [x] Unity 6000.6.11f1 and Unity Hub
- [x] `protoc`

---

## M0 — Foundations (serial, one agent)

Everything else depends on this. Do not parallelise.

- [x] **M0.1 Repo skeleton**
  - Owns: `server/Cargo.toml`, `server/crates/*/` (empty crates), `scripts/`, `.gitignore`, `README.md`
  - Fix `.gitignore`: it is the Unity template with root-anchored patterns (`/Library/`, `/Temp/`, ...), which will not match under `client/`. Re-anchor them to `client/` and add `server/target/`.
  - Done when: `cargo build --workspace` passes with six stub crates, and `git status` is clean after a build.
- [x] **M0.2 Protocol schema**
  - Owns: `proto/tinyverse/v1/`
  - Write every message in SPEC 4.3, including the ones only used in later milestones, so the schema does not churn.
  - Done when: `protoc` accepts the schema and each message has a comment.
- [x] **M0.3 `tv-proto` crate**
  - Owns: `server/crates/tv-proto/`
  - Generated Rust types, proto3 JSON mapping, encode/decode helpers for both frame types.
  - Done when: round-trip tests pass for binary and JSON for every message.
- [x] **M0.4 `tv-world` public types**
  - Owns: `server/crates/tv-world/src/` (types and signatures only)
  - `Command`, `Event`, `WorldError`, `Actor`, `Role`, `World::apply`, `World::tick`, all bodies `todo!()`.
  - Done when: it compiles and `cargo doc` shows the API.
- [x] **M0.5 Unity project**
  - Owns: `client/` (whole project)
  - Create the project (URP, Input System, UI Toolkit), add `Google.Protobuf`, create the folders and assembly definitions from SPEC 8, one scene with an empty bootstrap object.
  - Done when: a Linux build is produced from the command line and opens to an empty sky.
- [x] **M0.6 Codegen and check scripts**
  - Owns: `scripts/gen-proto.sh`, `scripts/check.sh`
  - `gen-proto.sh` writes C# into `client/Assets/Tinyverse/Proto/`. `check.sh` runs fmt, clippy, tests and verifies generated code is current.
  - Done when: `scripts/check.sh` passes on a clean checkout.
- [x] **M0.7 Commit** so worktrees for wave 1 branch from a known point.

---

## M1 — Walk and talk

### Wave 1 (six agents in parallel)

- [x] **M1.A World rules: avatars and chat** — Rust
  - Owns: `server/crates/tv-world/`
  - Depends on: M0.4
  - Join, leave, avatar state with speed and bounds validation, corrections, local and world chat, in-memory chat history with time and count limits, limits (SPEC 5.2, 5.4).
  - Done when: unit tests cover each command, each error, the speed check and history expiry.
- [x] **M1.B Server networking** — Rust
  - Owns: `server/crates/tv-server/`
  - Depends on: M0.3, M0.4 (codes against the `tv-world` signatures while M1.A fills them in)
  - axum WebSocket and `/info`, `/health`; session task; `Hello` handshake with password; world task and 20 Hz tick; snapshot then live events; bounded queues; ping timeout; `init` and `run` commands; `world.toml` loading.
  - Done when: an integration test starts the server in-process, connects two raw WebSocket clients and sees `Welcome`, `Snapshot` and `AvatarJoined`.
- [x] **M1.C Test client and bot** — Rust
  - Owns: `server/crates/tv-cli/`
  - Depends on: M0.3
  - `tv-cli connect` prints events; `say`; `walk-circle` and `wander` bots; `--json` switches to text frames.
  - Done when: it runs against a recorded-message mock in its own tests. Real server check happens in M1.I.
- [x] **M1.D Unity networking** — C#
  - Owns: `client/Assets/Tinyverse/Net/`
  - Depends on: M0.5, M0.6
  - `TinyverseConnection`: connect, `Hello`, background receive, main-thread dispatch, typed C# events per server message, send queue, reconnect with stored token, per-world token storage.
  - Done when: EditMode tests pass against an in-process fake WebSocket server.
- [x] **M1.E Unity avatar and camera** — C#
  - Owns: `client/Assets/Tinyverse/Avatar/`
  - Depends on: M0.5
  - Local controller for walk, run and fly on a `CharacterController`; third-person orbit camera; `RemoteAvatar` component that takes timestamped states and interpolates 100 ms behind; capsule avatar built from code with colour and name tag. No dependency on `Net`: it exposes plain C# inputs and outputs.
  - Done when: PlayMode test moves a local avatar and feeds a remote one a scripted path with no visible snapping.
- [x] **M1.F Unity UI** — C#, UXML, USS
  - Owns: `client/Assets/Tinyverse/UI/`
  - Depends on: M0.5
  - Connect screen (address, name, password, recent worlds, error display), chat panel with input and scrollback that can be pre-filled with the history received on join, people list, connection status. Each screen exposes events and setter methods and knows nothing about `Net`.
  - Done when: each screen can be shown and driven from a test harness script.

### Wave 2 (serial)

- [x] **M1.I Integration**
  - Owns: `client/Assets/Tinyverse/World/`, `client/Assets/Tinyverse/Bootstrap/`, scene, prefabs, `.meta` files
  - Merge wave 1. Write `WorldSession`, which links `Net` events to avatars and UI: spawn and remove remote avatars, send local `AvatarState` at 15 Hz, apply corrections, route chat. Ground plane and sky from `Welcome`.
  - Done when: one server, two Unity clients and one `tv-cli wander` bot all see each other move and chat. Tag `m1`.

---

## M2 — Build and persist

### Wave 3 (five agents in parallel)

- [x] **M2.A World rules: objects and permissions** — Rust
  - Owns: `server/crates/tv-world/`
  - Create, update, delete with validation of shape, scale, bounds, limits; roles and ownership; locked flag (SPEC 5.3, 6).
  - Done when: unit tests cover every permission combination and limit.
- [x] **M2.B Persistence** — Rust
  - Owns: `server/crates/tv-store/`
  - Depends on: M0.4 only, so it may start during wave 1 if an agent is free.
  - SQLite schema with migrations; load world; write-behind of dirty objects; users, token hashes, bans.
  - Done when: tests create, mutate, reopen and compare, including a crash between flushes.
- [x] **M2.C Object rendering** — C#
  - Owns: `client/Assets/Tinyverse/World/Objects/`
  - Mesh for each shape (generate cone and wedge), material presets in URP, `ObjectRegistry` that creates, updates and removes GameObjects from plain data, colliders for solid objects, smooth interpolation of remote edits.
  - Done when: a test script spawns 10,000 objects and holds 60 fps on the dev machine.
- [x] **M2.D Build tools** — C#
  - Owns: `client/Assets/Tinyverse/Build/`
  - Build mode toggle; placement preview on surfaces; selection with outline; move, rotate, scale gizmos with snapping; duplicate; delete; local undo stack. Emits edit intents as plain C# events, takes confirmations as method calls.
  - Done when: a PlayMode harness can place, transform and delete objects with no server.
- [x] **M2.E Build UI** — C#, UXML, USS
  - Owns: `client/Assets/Tinyverse/UI/Build/`
  - Shape palette, colour picker, material picker, object inspector (name, numeric transform, solid, locked), permission-denied toast.
  - Done when: the panel can be driven from the test harness.

### Wave 4 (serial)

- [x] **M2.I Integration**
  - Wire `tv-store` into `tv-server` (load at start, flush timer, clean shutdown on signals). Add object commands to `tv-cli`. Wire `Build` and `ObjectRegistry` through `WorldSession`. Apply roles from `Welcome` in the UI.
  - Done when: two clients build a small house together, the server is restarted, and both see it again on rejoining. Tag `m2`.

---

## M3 — Agents

### Wave 5 (four agents in parallel)

- [x] **M3.A Server-driven movement and observation** — Rust
  - Owns: `server/crates/tv-world/`
  - `MoveTo` and `Stop` advanced in `tick`; `Observe` with distance, bearing and text summary (SPEC 9).
  - Done when: unit tests cover arrival, interruption, bounds and observation contents.
- [x] **M3.B Agent tokens and JSON hardening** — Rust
  - Owns: `server/crates/tv-server/`
  - `token create|list|revoke`; token login; agent kind enforced from the token; clear JSON error messages for malformed input; stricter rate limits for agents.
  - Done when: integration tests log in by token over JSON and exercise each error.
- [x] **M3.C MCP bridge** — Rust
  - Owns: `server/crates/tv-mcp/`
  - stdio MCP server holding one world connection; tools listed in SPEC 9; keeps a rolling buffer of recent chat and events.
  - Done when: an MCP client can list the tools and each tool works against a local server.
- [x] **M3.D Agent documentation and example** — docs and script
  - Owns: `docs/AGENT_API.md`, `agents/`
  - Every message in JSON with an example; a minimal example agent that greets people who come near and answers chat using the Claude API.
  - Done when: someone can follow the document from nothing to a talking agent.

### Wave 6 (serial)

- [x] **M3.I Integration**
  - Agent badge on name tags and in the people list. Run the example agent and an MCP-connected assistant in a world with a human client.
  - Done when: an AI visitor walks to a person, talks, and builds something on request. Tag `m3`.

---

## M4 — Host and polish

### Wave 7 (five agents in parallel)

- [x] **M4.A Moderation** — Rust: `Admin` messages, kick, ban, role changes, `user` CLI. Owns `tv-world`, then `tv-server` after M4.B merges.
- [x] **M4.B TLS** — Rust: `--tls-cert`, `--tls-key`, `wss://`; self-signed helper. Owns `tv-server`.
- [x] **M4.C Host from the client** — C#: bundle the server binary, "Host a world" screen (create or pick a world directory, port, password), start and stop the child process, show its log. Owns `client/Assets/Tinyverse/Host/`.
- [ ] **M4.D Avatars** — C# and assets: realistic human models (SPEC 5.2) with idle, walk, run and fly animation; appearance editor for body preset, skin tone, hair and clothing colours. Confirm the model and animation licences allow redistribution before importing anything. Owns `Avatar/`.
- [x] **M4.E Settings and moderation UI** — C#: graphics, mouse sensitivity, key bindings; people list actions for owners. Owns `UI/Settings/`, `UI/Admin/`.

### Wave 8 (serial)

- [ ] **M4.I Integration and packaging**
  - Merge; `scripts/package-linux.sh` produces one archive with the client and server; first-run flow from download to hosting a world; README with screenshots.
  - Done when: a fresh Linux machine can host a world and a second can join it. Tag `v0.1.0`.

---

## Backlog (after v1, not yet planned)

- [ ] Scripting and mini-games (SPEC 12)
- [ ] Voice chat
- [ ] glTF import and textures
- [ ] Terrain and world templates (room, house, island)
- [ ] Internet play without port forwarding
- [ ] Server-side collision
- [ ] Windows and Mac clients
- [ ] Mobile clients

## Decisions

- [x] Avatar style: realistic people (SPEC 5.2)
- [x] Chat history: `world` chat kept in memory for a few hours (SPEC 5.4)
- [ ] How visitors reach a host over the internet; Tailscale is ruled out (SPEC 13.1)
- [ ] Scripting language (SPEC 13.2) — only blocks the backlog
