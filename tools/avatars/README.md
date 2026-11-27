# Avatar generator

Builds the default ("modern") people of the Unity client from MakeHuman assets with MPFB, the MakeHuman add-on for
Blender, headless. The output goes to `client/Assets/Tinyverse/Avatar/Resources/Tinyverse/Avatar/Modern/` and is
committed; a normal build needs none of this. Licences: `client/Assets/Tinyverse/Avatar/THIRD_PARTY.md`.

- `presets.json` — the parameters: per body preset the MakeHuman phenotype (gender, age, weight, height ...), the
  skin, the outfit and shoes; the hair assets of each style; the triangle budgets and atlas sizes.
- `generate.py` — the Blender script: one preset per run (see its docstring for the steps).
- `generate.sh` — runs it for every preset (or the ones named) and removes outputs no longer produced.
- `setup.sh` — installs MPFB 2.0.18, numpy (if Blender's Python lacks it) and the CC0 asset packs into
  `~/.local/share/tinyverse-tools/` (about 0.7 GB). No root needed.
- `manifests/manifest_<preset>.json` — what a run made: triangle counts, textures, materials, bones, height.

## Rerunning

```sh
tools/avatars/setup.sh            # once; needs Blender 4.2 or newer on PATH (or BLENDER=/path/to/blender)
tools/avatars/generate.sh         # all presets, about a minute
client: scripts/unity.sh compile, then once
  Unity -batchmode -nographics -quit -projectPath client \
    -executeMethod Tinyverse.Avatars.Editor.AvatarAssetTool.PrepareModern
```

`PrepareModern` sets the import settings (bodies humanoid, hair generic, four bone weights, texture sizes) and
writes the URP Lit materials from the manifests. The same inputs give byte-identical FBX and texture files
(the FBX exporter's time stamp and ids are pinned). Tested with Blender 4.3.2 (Debian package).

`AvatarGallery.Render` (same way, without `-nographics`, with `-galleryOut DIR`) renders review images of every
preset, skin tone, hair style and clip, next to the classic figures.

## What a preset becomes

One `Body_<preset>.fbx` (skeleton in T-pose; meshes `Body` with the eyes, `Top`, `Bottom` with the shoes) and one
`Hair_<preset>_<style>.fbx` per hair style (hair, eyebrows and eyelashes in one mesh, bound to the same skeleton
by bone name). Each mesh has one material: skin, top, trousers, hair: four per dressed avatar. Textures are
greyscale so the client tints them with the look's colours; the eyes are drawn by the skin material's emission map
so the skin tint does not darken them, and the shoes sit in the trousers' atlas with dark texels.

Skin under the clothes is deleted (MakeHuman's delete groups plus a ray test against the clothes), so it cannot
poke through when animated. Each part is decimated to the budget in `presets.json` (a dressed avatar is at most
about 14,300 triangles).
