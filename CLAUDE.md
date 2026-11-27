# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## State of the repo

There is no code yet. `docs/SPEC.md` is the design and `docs/TODO.md` is the task plan; read both before starting any work. When milestone M0 lands, add the build, test and codegen commands (`scripts/check.sh`, `scripts/gen-proto.sh`) to this file.

Linux is the only target for now.

## Architecture in one paragraph

A Rust server (`server/`, Cargo workspace) owns one world and persists it to SQLite. A Unity client (`client/`) and AI agents all speak the same protobuf protocol over one WebSocket: binary frames for Unity, proto3 JSON text frames for agents. The schema in `proto/tinyverse/v1/` is the single source of truth; Rust and C# types are generated from it. Unity's Netcode for GameObjects is not used.

## Rules for parallel work

- Three contracts are frozen after M0: the proto schema, the public types of `tv-world` (`Command`, `Event`, `World::apply`, `World::tick`), and the Unity assembly layout. A task that needs one changed stops and reports back; it does not edit the contract itself.
- Each task in `docs/TODO.md` lists the paths it owns. Edit nothing outside them.
- Tick the task's checkbox in `docs/TODO.md` when its done-when check passes.

## Unity

- Only one Unity editor can open a project at a time. A task that needs the editor or Unity tests uses its own git worktree.
- Parallel Unity tasks write C#, UXML and USS only. Scenes, prefabs, `.meta` files, `ProjectSettings/` and `Packages/` belong to the integration task of each milestone.
- There is one scene with one bootstrap object. Create everything else from code, not by editing the scene.
- Assemblies: `Net` → `Proto`; `Build` → `World`; `World`, `Avatar`, `UI`, `Host` reference none of the others; `Bootstrap` references all and holds the glue. Reference assemblies by name, never by GUID.
- Only `Net` and `Bootstrap` use `Tinyverse.Proto` types. Its `Color`, `Vec3`, `Quat`, `Material` and `Shape` clash with Unity's.

## Protocol conventions

Metres and seconds; left-handed, Y up (Unity's convention, so the client converts nothing); quaternions as `x y z w`. Protobuf fields are only added, never renumbered.

## Git

- Work lands directly on `master`. Parallel tasks run in worktrees and are merged locally. No pull requests.
- Do not add a `Co-Authored-By` trailer to commit messages; a global commit-msg hook rejects it.
- Leave `.serena/` untracked.
