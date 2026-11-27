#!/usr/bin/env bash
# Runs the built Linux player against a fresh local world with company, and writes screenshots.
#
# Usage: scripts/screenshot-run.sh <out-dir> [seconds] [width height] [extra player args...]
#   Starts tinyverse-server on a free port in a temporary world, two tv-cli bots (wander, circle),
#   creates one object of every shape and material (including a #ff8000 box), says something in
#   chat, then runs client/Builds/Linux/tinyverse.x86_64 with --connect, --demo-walk (DEMO_WALK= to drop it),
#   --screenshot-dir, --screenshot-tour and --quit-after. Everything it started is stopped on exit.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$(mkdir -p "${1:?out dir}" && cd "$1" && pwd)"
secs="${2:-40}"
width="${3:-1280}"
height="${4:-720}"
shift $(( $# < 4 ? $# : 4 ))
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

"$bin/tinyverse-server" init "$work/world" --name "Screenshot World" >/dev/null 2>&1
"$bin/tinyverse-server" run "$work/world" --port 0 --bind 127.0.0.1 >"$work/server.log" 2>&1 &
pids+=($!)
for _ in $(seq 50); do grep -q 'listening on' "$work/server.log" && break; sleep 0.1; done
url="$(grep -o 'ws://[^ ]*' "$work/server.log" | head -1)"
[ -n "$url" ] || { cat "$work/server.log"; exit 1; }
echo "server at $url"

cli() { "$bin/tv-cli" --url "$url" "$@"; }
cli --name Wanderer bot wander >"$work/wander.log" 2>&1 & pids+=($!)
cli --name Circler bot circle --radius 4 >"$work/circle.log" 2>&1 & pids+=($!)

# One of every shape and material, in a row 8 m ahead of the spawn point.
cli --name Builder create --shape box --pos -6,0.5,8 --color '#ff8000' --material matte --object-name "orange box" >/dev/null
cli --name Builder create --shape sphere --pos -3.6,0.5,8 --color '#2e86de' --material glossy >/dev/null
cli --name Builder create --shape cylinder --pos -1.2,0.5,8 --color '#d0d0d0' --material metal >/dev/null
cli --name Builder create --shape cone --pos 1.2,0.5,8 --color '#66ccff' --material glass >/dev/null
cli --name Builder create --shape wedge --pos 3.6,0.5,8 --color '#ff3366' --material emissive >/dev/null
cli --name Builder create --shape plane --pos 6,0.01,8 --scale 2 --color '#44bb44' --material matte >/dev/null
cli --name Builder create --shape box --pos 0,1.5,14 --scale 6,3,0.3 --color '#f5f0e6' --material matte --object-name wall >/dev/null
cli --name Builder say "Hello from tv-cli, the world chat works" >/dev/null

"$player" -logFile "$out/player.log" -screen-width "$width" -screen-height "$height" -screen-fullscreen 0 \
  --fresh-profile "$work/profile" --connect "$url" --name "Camera" ${DEMO_WALK---demo-walk} \
  --screenshot-dir "$out" --screenshot-every 5 --screenshot-tour --quit-after "$secs" "$@"
status=$?
echo "player exited with $status; screenshots in $out"
cp "$work/server.log" "$out/server.log"
exit $status
