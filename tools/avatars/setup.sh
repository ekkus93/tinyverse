#!/usr/bin/env bash
# Installs what generate.sh needs, without root and outside the repository:
#   MPFB 2.0.18 (MakeHuman for Blender) as a Blender extension, the CC0 MakeHuman asset packs presets.json uses
#   (system assets, bodyparts05 for the beard), and numpy
#   for Blenders built against the system Python (Debian's blender package has none).
# Everything goes to $TINYVERSE_TOOLS (default ~/.local/share/tinyverse-tools). Blender itself must be on PATH
# (or set BLENDER); MPFB needs Blender 4.2 or newer.
#
# Usage: tools/avatars/setup.sh
set -euo pipefail

tools="${TINYVERSE_TOOLS:-$HOME/.local/share/tinyverse-tools}"
blender="${BLENDER:-blender}"
user="$tools/blender-user"
mkdir -p "$tools/dl" "$tools/packs-zip" "$user"
export BLENDER_USER_RESOURCES="$user"

mpfb_zip="$tools/dl/mpfb-2.0.18.zip"
mpfb_sha="c075b3e9b94b8e5556a5f03f4653602d02ea306de08126e8f55ed6dee28a2dad"
if [ ! -f "$mpfb_zip" ]; then
  curl -fL -o "$mpfb_zip" "https://extensions.blender.org/download/sha256:$mpfb_sha/add-on-mpfb-v2.0.18.zip"
fi
echo "$mpfb_sha  $mpfb_zip" | sha256sum -c -

if ! "$blender" -b --python-expr "import numpy" >/dev/null 2>&1; then
  python3 -m pip install --quiet --target "$user/scripts/modules" numpy
fi
"$blender" -b --command extension install-file -r user_default -e "$mpfb_zip"

# MPFB's user data folder (bpy.utils.extension_path_user for the extension).
data="$user/extensions/.user/user_default/mpfb/data"
mkdir -p "$data"
for pack in makehuman_system_assets bodyparts05; do
  zip="$tools/packs-zip/${pack}_cc0.zip"
  [ -f "$zip" ] || curl -fL -o "$zip" "https://files.makehumancommunity.org/asset_packs/$pack/${pack}_cc0.zip"
  unzip -oq "$zip" -d "$data"
done
echo "installed into $tools"
