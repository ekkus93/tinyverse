---
name: run-wave
description: Run one wave or serial task group from docs/TODO.md with parallel subagents in git worktrees, then merge the results into master and tick the boxes.
disable-model-invocation: true
---

Run the wave or milestone named in `$ARGUMENTS` (for example `wave 1`, `M0`, `M2.I`). If it is empty, pick the first wave in `docs/TODO.md` that has unticked tasks and say which one you picked.

## 1. Prepare

1. Read `docs/TODO.md`, `docs/SPEC.md` and `CLAUDE.md`.
2. List the unticked tasks in the wave. For each, note its id, owned paths, dependencies and done-when check.
3. Stop and report if any dependency is unticked, if `git status` is not clean, or if two tasks in the wave own overlapping paths.
4. If the wave is marked **serial** (M0 and every `.I` integration task), do the work yourself in the main checkout, one task at a time, and skip to step 4 of the next section's checks. Do not spawn subagents for serial work.

## 2. Launch

Launch one subagent per task, all in a single message so they run at once, each with `isolation: "worktree"`. Each brief must contain:

- the task id and its full text from `docs/TODO.md`;
- the paths it owns, and the instruction to edit nothing else;
- the relevant section numbers of `docs/SPEC.md`, and to read `CLAUDE.md`;
- the done-when check, and that it must run the check and report the real output;
- to commit its work on its worktree branch with no `Co-Authored-By` trailer;
- to stop and report, without editing, if it needs a frozen contract changed (proto schema, `tv-world` public types, Unity assembly layout);
- for Unity tasks: write C#, UXML and USS only; no scenes, prefabs, `.meta` files, `ProjectSettings/` or `Packages/`.

## 3. Merge

When every subagent has reported:

1. For each task, check that its branch touches only the paths it owns (`git diff --stat master...<branch>`). Reject a branch that strays and say why.
2. Merge the accepted branches into `master` one at a time. Resolve nothing by guessing: a conflict means two tasks overlapped, so stop and report it.
3. Run `scripts/check.sh` if it exists, otherwise `cargo test --workspace` in `server/` if that exists.
4. Tick the checkbox of each task whose done-when check passed, and commit `docs/TODO.md`.
5. Do not push. Do not start the next wave.

## 4. Report

For each task: done, failed, or blocked, with the check output for failures and any contract change it asked for. Then name the next wave.
