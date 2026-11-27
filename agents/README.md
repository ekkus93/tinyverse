# Example agents

Programs that visit a Tinyverse world through the JSON WebSocket API described
in [`docs/AGENT_API.md`](../docs/AGENT_API.md).

| Path | What it is |
|---|---|
| `first_agent.py` | The smallest useful agent (about 30 lines): joins with a token, says hello, echoes chat that mentions its name. Walked through in section 11 of the API document. |
| `greeter/` | A complete example: an AI visitor that stands near the spawn point, greets people who walk up, answers chat using Claude, walks over when asked and builds simple things on request. |

Both need Python 3.11 or newer, a running world and a token from its host:

```sh
tinyverse-server token create ./myworld --name Greeter --role builder
```

(`--role builder` lets the greeter build; a `visitor` token works for talking
only.)

## first_agent.py

```sh
python3 -m venv .venv && .venv/bin/pip install websockets
TINYVERSE_TOKEN=<token> .venv/bin/python first_agent.py ws://127.0.0.1:7878/ws
```

## greeter

### Install

```sh
cd agents/greeter
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt        # websockets, anthropic
```

### Run

With Claude (reads the API key from `ANTHROPIC_API_KEY`):

```sh
export ANTHROPIC_API_KEY=...
.venv/bin/python -m greeter --url ws://127.0.0.1:7878/ws --token-file greeter.token
```

Without a model, using canned replies and a few rules (no API key needed):

```sh
.venv/bin/python -m greeter --url ws://127.0.0.1:7878/ws --token-file greeter.token --no-llm
```

Over `wss://`, for a world whose host serves TLS. With a self-signed
certificate (`tinyverse-server tls self-signed <DIR>`), get the world's
`cert.pem` from the host and trust it with `--ca-file`; a certificate from a
public authority needs no option:

```sh
.venv/bin/python -m greeter --url wss://192.168.1.10:7878/ws --ca-file cert.pem --token-file greeter.token
```

The certificate must be valid for the address in the URL: by default it
covers `localhost` and `127.0.0.1`, and the host adds others with
`tls self-signed <DIR> --host 192.168.1.10`. If the certificate is not trusted, the greeter says so and exits with status 2
instead of retrying.

Stop it with Ctrl-C (SIGINT) or SIGTERM; it closes the connection cleanly and
exits with status 0.

| Option | Meaning |
|---|---|
| `--url` | WebSocket URL of the world. Default `ws://127.0.0.1:7878/ws`, or `TINYVERSE_URL`. |
| `--ca-file` | PEM certificate(s) to trust for a `wss://` URL, such as the world's self-signed `cert.pem`. Without it the system's trusted authorities are used. Only valid with `wss://`. |
| `--token`, `--token-file` | The agent token (also `TINYVERSE_TOKEN`). Without a token it joins as a new user called `--name`, with `--password` if the world has one, and reuses the session token it is given when it reconnects. |
| `--name` | Display name. Default: the token's name. It is the name people use to address it. |
| `--no-llm` | Rule-based replies; no Claude calls. |
| `--model` | Claude model id. Default `GREETER_MODEL`, else `claude-opus-5-5`. |
| `--greet-radius` | How close someone must come to be greeted, in metres (default 4). |
| `--greet-agents` | Also greet other agents (off by default). |
| `-v` | Debug logging (to stderr). |

Environment: `ANTHROPIC_API_KEY` (required unless `--no-llm`), `GREETER_MODEL`
(model id), `GREETER_EFFORT` (`low`, `medium`, `high`, ...; default `low`, which
keeps replies quick).

### What it does

- **Stands near the spawn point**: it walks to a spot 2 m from the spawn point
  (so it does not stand inside people who appear there) and returns there
  after a minute without anything to do.
- **Greets people** who come within 4 m, once per visit. A visit lasts from
  when someone appears in the world until they leave it; walking away and back
  does not earn a second greeting. Other agents are not greeted.
- **Answers chat** addressed to it by name (any channel) and local chat said
  within 10 m, on the channel it came from.
- **"Come here"**: walks to the person (stopping 1.5 m short) with `MoveTo`
  and waits until the server reports it at the target and on the ground
  (a walking `MoveTo` also brings an airborne avatar down, so the horizontal
  position alone is not arrival).
- **Builds on request** ("build a red box"): with Claude, through tools
  `look`, `move_to`, `say`, `create_object`, `edit_object` and
  `delete_object` in an ordinary tool-use loop; without a model, through a
  rule that understands a colour and a shape ("remove it" deletes the last
  thing it built).

With `--no-llm` it understands: a greeting ("hi"), "come here" / "come over" /
"follow me", "build|make|create a [colour] box|cube|sphere|ball|cylinder|cone|wedge|plane",
and "remove it". Anything else addressed to it gets a short explanation; small
talk nearby that is not addressed to it gets no reply.

### Being a good guest

- It limits its own speech to one line every four seconds on average (bursts
  of three), far below the server's limit. Lines that would exceed it are
  dropped, not queued.
- It answers other agents only when addressed by name, and at most twice per
  agent in five minutes, so two agents cannot keep each other talking.
- It handles one thing at a time and keeps at most four waiting; older ones are
  dropped when busy.
- It keeps a rolling memory of the last 40 lines it heard, said or saw, which
  is what Claude sees as context; nothing is stored on disk.
- It only edits or deletes objects it created, builds at most 25 objects per
  request (10 by instruction to the model), and treats chat text as
  conversation, not instructions.
- It pings every 10 s and reconnects with backoff (1 s up to 30 s) after a
  dropped connection, but not after being kicked or refused.

### Files

| File | Contents |
|---|---|
| `greeter/protocol.py` | Building and reading protocol messages; colours, shapes, rotations. |
| `greeter/connection.py` | The WebSocket connection: join, request/reply by `seq`, pings. |
| `greeter/world.py` | Local copy of the world from events; proximity and geometry. |
| `greeter/policy.py` | Speech limiter, reply policy (loop avoidance), rolling memory. |
| `greeter/tools.py` | The tools: JSON schemas for Claude, the pure tool-to-message mapping, and their execution. |
| `greeter/brains.py` | `RuleBrain` (`--no-llm`) and `ClaudeBrain` (Messages API tool-use loop). |
| `greeter/agent.py` | The main loop: events, greeting, the work queue, reconnecting, signals. |

### Tests

```sh
.venv/bin/pip install -r requirements-dev.txt    # adds pytest
.venv/bin/python -m pytest -q tests/
```

`tests/test_pure.py` covers message building, the world copy, proximity,
loop avoidance, the speech limiter and the tool-to-protocol mapping.
`tests/test_claude_offline.py` runs the Claude tool-use loop through the real
`anthropic` SDK against a mock HTTP transport (no network, no key) and checks
the requests it sends and how it handles tool errors, refusals and API errors.
`tests/test_e2e.py` starts a real `tinyverse-server` on a free port in a
temporary world and runs the greeter in `--no-llm` mode against it, with a
scripted person and a scripted agent: greeting, "come here", building a box,
the agent-loop limit and a clean SIGINT shutdown. A second test serves the
world over `wss://` with a self-signed certificate and checks that the
greeter refuses it without `--ca-file` (exit 2) and joins and greets with it. It looks for the server at
`TINYVERSE_SERVER` or `server/target/{debug,release}/tinyverse-server`, and is
skipped with a message if none is built (`cargo build` in `server/`).
