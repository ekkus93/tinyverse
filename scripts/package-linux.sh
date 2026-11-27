#!/usr/bin/env bash
# Builds everything in release mode and packages it as dist/tinyverse-linux-x86_64.tar.gz:
#   tinyverse/                      the Unity player (run ./tinyverse.x86_64)
#     tinyverse_Data/StreamingAssets/server/tinyverse-server   bundled for "Host a world"
#     bin/tinyverse-server, bin/tv-cli, bin/tv-mcp             for headless hosting, tests and agents
#     README.txt
# Usage: scripts/package-linux.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Temporary files go in the repo's own tmp/ (git-ignored), not the system /tmp.
export TMPDIR="$root/tmp"
mkdir -p "$TMPDIR"
dist="$root/dist"
stage="$dist/stage"
name="tinyverse-linux-x86_64"
pkg="$stage/tinyverse"

echo "== building the server (release)"
(cd "$root/server" && cargo build --release --workspace --bins)
for b in tinyverse-server tv-cli tv-mcp; do
  [ -x "$root/server/target/release/$b" ] || { echo "missing server/target/release/$b" >&2; exit 1; }
done

echo "== building the player"
rm -rf "$stage"
mkdir -p "$pkg"
TINYVERSE_BUILD_OUTPUT="$pkg/tinyverse.x86_64" "$root/scripts/unity.sh" build
[ -x "$pkg/tinyverse.x86_64" ] || { echo "the player build failed; see client/Logs/build.log" >&2; exit 1; }
cmp -s "$root/server/target/release/tinyverse-server" "$pkg/tinyverse_Data/StreamingAssets/server/tinyverse-server" \
  || { echo "the bundled server is not the release build" >&2; exit 1; }
rm -rf "$pkg"/*_BackUpThisFolder_ButDontShipItWithYourGame "$pkg"/*_BurstDebugInformation_DoNotShip

mkdir -p "$pkg/bin"
cp "$root/server/target/release/"{tinyverse-server,tv-cli,tv-mcp} "$pkg/bin/"
version="$(sed -n 's/^  bundleVersion: //p' "$root/client/ProjectSettings/ProjectSettings.asset")"
cat >"$pkg/README.txt" <<EOF
Tinyverse ${version} for Linux x86_64
=================================

A small self-hosted virtual world: host one, invite friends and AI agents,
walk, fly, chat and build together.

Play
  ./tinyverse.x86_64
  Join a world by its address (for example 192.168.1.20 or myhost:7878),
  or choose "Host a world" to run your own: the server is bundled and starts
  in the background; friends on your network join with the address shown.

Controls
  WASD move, Shift run, F fly (Space/Ctrl up and down), right-drag to look,
  wheel to zoom, Enter chat, B build mode, P people, Esc menu.

Command line
  --connect <address> [--name <n>] [--password <pw>]   join at start-up
  --host <world-folder> [--port <p>]                    host a world and join it
  --fresh-profile <dir>                                 keep settings and tokens there

Headless hosting and tools (bin/)
  bin/tinyverse-server init <dir> && bin/tinyverse-server run <dir>
  bin/tv-cli --url ws://host:7878/ws connect           a text client and bot
  bin/tv-mcp                                           MCP bridge for AI agents

Worlds you host live in ~/.config/unity3d/Tinyverse/Tinyverse/worlds/
unless you choose another folder.
EOF

echo "== packaging"
mkdir -p "$dist"
tar -C "$stage" -czf "$dist/$name.tar.gz" tinyverse
rm -rf "$stage"
ls -l "$dist/$name.tar.gz"
