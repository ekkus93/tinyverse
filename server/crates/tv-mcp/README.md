# tv-mcp

An [MCP](https://modelcontextprotocol.io) server that lets an MCP-capable
assistant (Claude Code, Claude Desktop, ...) join a Tinyverse world as an agent
avatar. The assistant's host launches `tv-mcp` as a subprocess and talks MCP
over stdin/stdout; `tv-mcp` holds one WebSocket connection to the world and
exposes it as tools: look around, walk, talk, read chat, build.

The assistant works in turns, so `tv-mcp` keeps a local copy of the world
(people present, objects, your own avatar) and a rolling buffer of the last
300 chat lines and 200 notable events (joins, leaves, objects others created,
changed or deleted) for it to read between tool calls.

## Build

From `server/` (needs `protoc` on the `PATH`, like the rest of the workspace):

```sh
cargo build --release -p tv-mcp
# binary: server/target/release/tv-mcp
```

## Get a token

Agents join with a token the host creates ahead of time:

```sh
tinyverse-server token create --name scout --role builder > scout.token
```

Without a token, `tv-mcp` joins as a new user with `--name` (and `--password`
if the world has one), and reuses the session token the server hands back when
it reconnects during the same run.

## Run

```text
tv-mcp [--url ws://host:7878/ws] [--token TOKEN | --token-file FILE] [--name NAME] [--password PW] [-v]
```

| Flag | Meaning |
|---|---|
| `--url` | WebSocket URL of the world. Default `ws://127.0.0.1:7878/ws`. |
| `--token` | Agent token. Also read from `TINYVERSE_TOKEN` when neither flag is given. |
| `--token-file` | File holding the token (whitespace trimmed). An unreadable or empty file is a start-up error. |
| `--name` | Display name. Optional when the token belongs to an existing user. |
| `--password` | The world's join password, if it has one and no token is used. Also `TINYVERSE_PASSWORD`. |
| `-v`, `-vv` | More logging. `RUST_LOG` overrides. |

stdout carries only MCP messages; all logging goes to stderr.

`tv-mcp` connects at start-up. If the world is unreachable or refuses the
login, the MCP server still starts: every tool call returns an error result
that says why, and tries to connect again. After a dropped connection it
reconnects in the background (1 s, 2 s, 4 s ... up to 30 s apart), except
after being kicked, when it waits for the next tool call. It pings the world
every 10 s.

## Add it to an MCP client

### Claude Code

```sh
claude mcp add --transport stdio tinyverse -- \
  /path/to/server/target/release/tv-mcp \
  --url ws://192.168.1.10:7878/ws --token-file /path/to/scout.token --name Scout
```

(`--transport stdio` is the default and may be left out; `-e KEY=value`
before the `--` sets environment variables for the server, e.g.
`-e TINYVERSE_TOKEN=...`.) Add `--scope project` to share it through the
project's `.mcp.json`, or `--scope user` for all your projects. Check it with
`claude mcp get tinyverse`; it should say `✔ Connected`. This was checked with
Claude Code 2.1.332, which speaks the 2026-07-28 revision to `tv-mcp`. The equivalent `.mcp.json` entry:

```json
{
  "mcpServers": {
    "tinyverse": {
      "command": "/path/to/server/target/release/tv-mcp",
      "args": ["--url", "ws://192.168.1.10:7878/ws", "--name", "Scout"],
      "env": { "TINYVERSE_TOKEN": "${TINYVERSE_TOKEN}" }
    }
  }
}
```

### Claude Desktop

Add the server to `claude_desktop_config.json` (Settings → Developer → Edit
Config) and restart the app:

```json
{
  "mcpServers": {
    "tinyverse": {
      "command": "/path/to/server/target/release/tv-mcp",
      "args": [
        "--url", "ws://192.168.1.10:7878/ws",
        "--token-file", "/path/to/scout.token",
        "--name", "Scout"
      ]
    }
  }
}
```

Any other MCP client that launches stdio servers works the same way: run the
binary with the flags above.

## Tools

Coordinates are metres, left-handed and Y up (as in Unity): the ground is at
y = 0, and seen from above +x is to the right of +z. Headings and yaw are
degrees clockwise seen from above, 0 facing +z and 90 facing +x. Object ids
are plain numbers; tools accept them as numbers or strings.

| Tool | Arguments | What it does |
|---|---|---|
| `look` | `radius?` | Asks the server for an `Observation`: its text summary, then nearby avatars and objects, nearest first, with id, position, distance and bearing (0 ahead, positive right). |
| `move_to` | `x`, `z`, `y?`, `mode?` (`walk`/`run`/`fly`), `wait?` (default true), `timeout_s?` | Sends `MoveTo`. The server answers `Ack`; with `wait`, returns when the server's last state puts the avatar at the target, when it stops short, or at the timeout, with the final position. Walking and running ignore `y` and keep the current height (the server does not bring a flying avatar down), so fly to y = 0 before walking. |
| `stop` | — | Sends `Stop`. |
| `say` | `text`, `channel?` (`local`/`world`, default local) | Sends `Chat`. Local chat reaches about 20 m. |
| `recent_chat` | `since_last?` (default true), `limit?` (default 50, max 300), `include_events?` (default true) | Buffered chat and events, oldest first, with UTC times. `since_last` returns only lines not yet returned by this tool or `wait_for_chat`. |
| `wait_for_chat` | `timeout_s?` (default 30, max 120) | Blocks until someone else says something you can hear (returns at once if such lines are unread), then returns the new lines. |
| `who` | — | Everyone present, you first, with name, kind, role, avatar id, position, distance and bearing. |
| `create_object` | `shape`, `position`, `scale?`, `rotation_euler_deg?`, `color?`, `material?`, `name?`, `solid?` (default true), `locked?` (default false) | Sends `ObjectCreate` and returns the new object with its id. |
| `edit_object` | `id` and any of the `create_object` fields | Sends `ObjectUpdate` with only the given fields. An empty `name` removes the label. |
| `delete_object` | `id` | Sends `ObjectDelete`. |
| `list_objects` | `radius?`, `owner?` (`me`/`anyone`), `limit?` (default 50, max 500) | Objects from the local copy, nearest first. |
| `world_info` | — | Name, description, bounds, spawn point, ground, your role and what it allows, server limits. |

Shapes: `box`, `sphere`, `cylinder`, `cone`, `wedge`, `plane`. Materials:
`matte`, `glossy`, `metal`, `glass`, `emissive`. `scale` is metres per axis,
`{x, y, z}` or one number for all three; `position` is the object's centre,
so a 1 m box resting on the ground has y = 0.5.

Colours are `#rgb`, `#rrggbb` or `#rrggbbaa` (sRGB, as in CSS) or a common
name (`red`, `navy`, `skyblue`, ...). The protocol carries the same sRGB
values as 0..1 floats, so `#ff8000` is sent as (1.0, 0.502, 0.0) and shown
back as `#ff8000`.

`rotation_euler_deg` is `{x, y, z}` in degrees, applied as Unity's
`Quaternion.Euler`: z (roll) first, then x (pitch), then y (yaw), about the
world axes, i.e. q = q<sub>y</sub> · q<sub>x</sub> · q<sub>z</sub>.
`{"x": 0, "y": 90, "z": 0}` turns an object clockwise seen from above so its +z
face points to +x. The rotation is absolute.

Results are short plain text. A server `Error` reply becomes a tool error
(`isError: true`) naming the code, e.g.
`The world refused to delete object 7: ERROR_CODE_PERMISSION_DENIED: object is locked`.
Bad arguments are caught before anything is sent, with a message that says
what to fix.

## MCP protocol

`tv-mcp` implements the stdio transport (newline-delimited JSON-RPC 2.0)
itself. It serves MCP revision **2026-07-28**, in which each request carries
its protocol version and client capabilities in `_meta` and
`server/discover` describes the server. It also answers the `initialize`
handshake of the earlier revisions 2025-11-25, 2025-06-18, 2025-03-26 and
2024-11-05, so older clients work too. Supported methods: `initialize`,
`notifications/initialized`, `ping`, `server/discover`, `tools/list`,
`tools/call` and `notifications/cancelled`. It exits when stdin closes.

## Tests

```sh
cargo test -p tv-mcp
```

The tests run the MCP server in-process over in-memory pipes against a
scripted mock world server (a real WebSocket on a random port), and run the
built binary once to check that nothing but JSON-RPC reaches stdout.
`tests/real_server.rs` also starts the real `tinyverse-server` from the
target directory, with a second participant, and runs the tools through the
bridge; it is skipped with a message when that binary has not been built
(`cargo build --workspace`).
