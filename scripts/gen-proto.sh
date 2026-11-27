#!/usr/bin/env bash
# Regenerates the C# protocol types for the Unity client from proto/.
# The Rust types need no script: tv-proto's build.rs generates them on build.
#
# Usage: scripts/gen-proto.sh [output-dir]
#   output-dir defaults to client/Assets/Tinyverse/Proto/Generated
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Temporary files go in the repo's own tmp/ (git-ignored), not the system /tmp.
export TMPDIR="$root/tmp"
mkdir -p "$TMPDIR"
out="${1:-$root/client/Assets/Tinyverse/Proto/Generated}"

command -v protoc >/dev/null || { echo "protoc not found (apt install protobuf-compiler)" >&2; exit 1; }

mkdir -p "$out"
# Remove stale generated sources but keep Unity's .meta files, so file GUIDs survive.
find "$out" -maxdepth 1 -name '*.cs' -delete
protoc --proto_path="$root/proto" --csharp_out="$out" "$root"/proto/tinyverse/v1/*.proto
echo "generated $(find "$out" -maxdepth 1 -name '*.cs' | wc -l) C# files in $out"
