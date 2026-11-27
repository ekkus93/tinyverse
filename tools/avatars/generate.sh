#!/usr/bin/env bash
# Regenerates the modern avatar models and textures into the client's Avatar module.
#
# Usage: tools/avatars/generate.sh [preset...]     (default: every preset in presets.json)
#   BLENDER          the Blender binary (default: blender on PATH; 4.2 or newer)
#   TINYVERSE_TOOLS  where setup.sh installed MPFB and the asset packs (default ~/.local/share/tinyverse-tools)
# Afterwards run the Unity step once so import settings and materials follow:
#   scripts/unity.sh compile, then the editor method Tinyverse.Avatars.Editor.AvatarAssetTool.PrepareModern
#   (see README.md).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
tools="${TINYVERSE_TOOLS:-$HOME/.local/share/tinyverse-tools}"
blender="${BLENDER:-blender}"
export BLENDER_USER_RESOURCES="${BLENDER_USER_RESOURCES:-$tools/blender-user}"
out="$root/client/Assets/Tinyverse/Avatar/Resources/Tinyverse/Avatar/Modern"
manifests="$here/manifests"

[ -d "$BLENDER_USER_RESOURCES/extensions/user_default/mpfb" ] || { echo "MPFB not installed; run tools/avatars/setup.sh" >&2; exit 1; }

if [ $# -gt 0 ]; then
  presets=("$@")
else
  mapfile -t presets < <(python3 -c 'import json,sys; print("\n".join(json.load(open(sys.argv[1]))["presets"]))' "$here/presets.json")
fi

stamp="$(mktemp)"
for p in "${presets[@]}"; do
  log="$(mktemp)"
  if ! "$blender" -b --python-exit-code 1 --python "$here/generate.py" -- \
      --presets "$here/presets.json" --preset "$p" --out "$out" --manifests "$manifests" >"$log" 2>&1; then
    cat "$log" >&2
    rm -f "$log"
    echo "generating $p failed" >&2
    exit 1
  fi
  grep '^\[avatars\]' "$log" | grep -v '  wrote ' || true
  rm -f "$log"
done
# After a full run, models and textures nothing wrote any more are stale (with their .meta files).
if [ $# -eq 0 ]; then
  find "$out/Models" "$out/Textures" -type f ! -name '*.meta' ! -newer "$stamp" | while read -r f; do
    echo "removing stale $f"
    rm -f "$f" "$f.meta"
  done
fi
rm -f "$stamp"
echo "done: $out"
