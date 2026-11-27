# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## State of the repo

`docs/SPEC.md` is the design and `docs/TODO.md` is the task plan; read both before starting any work.

- `scripts/check.sh` runs Rust fmt, clippy and tests, and checks the generated C# is current. Run it before merging anything.
- `scripts/gen-proto.sh` regenerates the C# protocol types after a change in `proto/`. The Rust types regenerate on build.
- `scripts/unity.sh` drives the Unity editor in batch mode.
- The server binary is `server/target/debug/tinyverse-server`; `tv-cli smoke` is the end-to-end check against a running server.

Linux is the only target for now.

Use the repo's `tmp/` directory (git-ignored) for temporary files, screenshots, downloads and logs, not the system `/tmp`, which is small. The scripts set `TMPDIR` to it; when running other commands, pass `TMPDIR="$PWD/tmp"` or write there directly. Delete what you put there when you are done.

## Architecture in one paragraph

A Rust server (`server/`, Cargo workspace) owns one world and persists it to SQLite. A Unity client (`client/`) and AI agents all speak the same protobuf protocol over one WebSocket: binary frames for Unity, proto3 JSON text frames for agents. The schema in `proto/tinyverse/v1/` is the single source of truth; Rust and C# types are generated from it. Unity's Netcode for GameObjects is not used.

## Rules for parallel work

- Three contracts are frozen after M0: the proto schema, the public types of `tv-world` (`Command`, `Event`, `World::apply`, `World::tick`), and the Unity assembly layout. A task that needs one changed stops and reports back; it does not edit the contract itself.
- Each task in `docs/TODO.md` lists the paths it owns. Edit nothing outside them.
- Tick the task's checkbox in `docs/TODO.md` when its done-when check passes.

## Unity

- Only one Unity editor can open a project at a time. A task that needs the editor or Unity tests uses its own git worktree.
- A parallel Unity task edits only its own module folder, and commits the `.meta` files Unity generates there. The scene, `ProjectSettings/`, `Packages/`, `Assets/Settings/` and `Bootstrap/` belong to the integration task; if the editor rewrites them (it often touches `UniversalRenderPipelineGlobalSettings.asset`), check them back out.
- Runtime assets are loaded from a `Resources/Tinyverse/<Module>/` folder inside the module. A shader or material that no asset references is stripped from the player, so materials must be real `.mat` assets.
- Drive the editor with `scripts/unity.sh compile | test-edit | test-play | build`.
- There is one scene with one bootstrap object. Create everything else from code, not by editing the scene.
- Assemblies: `Net` → `Proto`; `Build` → `World`; `World`, `Avatar`, `UI`, `Host` reference none of the others; `Bootstrap` references all and holds the glue. Reference assemblies by name, never by GUID.
- Only `Net` and `Bootstrap` use `Tinyverse.Proto` types. Its `Color`, `Vec3`, `Quat`, `Material` and `Shape` clash with Unity's.

## Protocol conventions

Metres and seconds; left-handed, Y up (Unity's convention, so the client converts nothing); quaternions as `x y z w`. Protobuf fields are only added, never renumbered.

## Git

- Work lands directly on `master`. Parallel tasks run in worktrees and are merged locally. No pull requests.
- Do not add a `Co-Authored-By` trailer to commit messages; a global commit-msg hook rejects it.
- Leave `.serena/` untracked.
