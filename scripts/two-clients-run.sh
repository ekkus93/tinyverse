#!/usr/bin/env bash
# Runs two built players against one local server at the same time, with a tv-cli bot for company:
# player "Walker" with --demo-walk and player "Mason" with --demo-build, each writing screenshots.
#
# Usage: scripts/two-clients-run.sh <out-dir> [seconds] [width height]
#   Screenshots go to <out-dir>/walker and <out-dir>/mason; the server log to <out-dir>/server.log.
#   Everything it started is stopped on exit.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Temporary files go in the repo's own tmp/ (git-ignored), not the system /tmp.
export TMPDIR="$root/tmp"
mkdir -p "$TMPDIR"
out="$(mkdir -p "${1:?out dir}" && cd "$1" && pwd)"
secs="${2:-60}"
width="${3:-1280}"
height="${4:-720}"
bin="$root/server/target/debug"
player="$root/client/Builds/Linux/tinyverse.x86_64"
[ -x "$bin/tinyverse-server" ] || { echo "build the server first (cargo build --workspace in server/)" >&2; exit 1; }
[ -x "$player" ] || { echo "build the player first (scripts/unity.sh build)" >&2; exit 1; }

work="$(mktemp -d)"
pids=()
cleanup() {
  for p in "${pids[@]}"; do kill "$p" 2>/dev/null; done
  wait 2>/dev/null
  rm -rf "$work"
}
trap cleanup EXIT

"$bin/tinyverse-server" init "$work/world" --name "Two Clients" >/dev/null 2>&1
"$bin/tinyverse-server" run "$work/world" --port 0 --bind 127.0.0.1 >"$work/server.log" 2>&1 &
pids+=($!)
for _ in $(seq 50); do grep -q 'listening on' "$work/server.log" && break; sleep 0.1; done
url="$(grep -o 'ws://[^ ]*' "$work/server.log" | head -1)"
[ -n "$url" ] || { cat "$work/server.log"; exit 1; }
echo "server at $url"
"$bin/tv-cli" --url "$url" --name Circler bot circle --radius 4 >/dev/null 2>&1 & pids+=($!)

run_player() {
  local name="$1"; shift
  mkdir -p "$out/$name"
  "$player" -logFile "$out/$name/player.log" -screen-width "$width" -screen-height "$height" -screen-fullscreen 0 \
    --fresh-profile "$work/profile-$name" --connect "$url" --name "$name" \
    --screenshot-dir "$out/$name" --screenshot-every 4 --quit-after "$secs" "$@" &
  pids+=($!)
}
run_player walker --demo-walk
sleep 2
run_player mason --demo-build
wait "${pids[-1]}" "${pids[-2]}"
echo "players exited; screenshots in $out/walker and $out/mason"
cp "$work/server.log" "$out/server.log"
