# Third-party assets in the Avatar module

All avatar models, textures and animations are by Quaternius and dedicated to the public domain under
**CC0 1.0 Universal** (https://creativecommons.org/publicdomain/zero/1.0/). CC0 allows copying, modifying and
redistributing them, including in a public repository and in a shipped game, with no attribution required (we
credit the author anyway). The licence was checked on each pack's web page and in the `License*.txt` file inside
each downloaded archive on 2026-11-27. Only the free "Standard" version of each pack was used.

| Pack | Source | Licence | Retrieved |
|---|---|---|---|
| Universal Base Characters (Standard) | https://quaternius.com/packs/universalbasecharacters.html, download https://quaternius.itch.io/universal-base-characters | CC0 1.0 (page and `License_Standard.txt`) | 2026-11-27 |
| Modular Character Outfits - Fantasy (Standard) | https://quaternius.com/packs/modularcharacteroutfitsfantasy.html, download https://quaternius.itch.io/modular-character-outfits-fantasy | CC0 1.0 (page and `License_Standard.txt`) | 2026-11-27 |
| Universal Animation Library (Standard) | https://quaternius.com/packs/universalanimationlibrary.html, download https://quaternius.itch.io/universal-animation-library | CC0 1.0 (page, `License.txt` and `README.txt`) | 2026-11-27 |

## What is used, and how it was changed

Everything lives under `Resources/Tinyverse/Avatar/`. `Editor/AvatarAssetTool.cs` made the derived files from the
unzipped packs (`-executeMethod Tinyverse.Avatars.Editor.AvatarAssetTool.Prepare -avatarSource DIR`).

- `Models/Male_Peasant.fbx`, `Models/Female_Peasant.fbx` — Outfits pack, `Exports/FBX (Unity)/Outfits/`. Unchanged
  files; they carry the humanoid skeletons and the clothes (shirt, sleeves and hands, trousers, boots).
- `Models/Superhero_Male_FullBody.fbx`, `Models/Superhero_Female_FullBody.fbx` — Base Characters pack,
  `Base Characters/Unity/`. Unchanged files; at runtime only the head and neck, eyes and eyebrows are used.
- `Models/Hair_SimpleParted.fbx`, `Hair_Buzzed.fbx`, `Hair_BuzzedFemale.fbx`, `Hair_Long.fbx`, `Hair_Buns.fbx`,
  `Hair_Beard.fbx` — Base Characters pack, `Hairstyles/Rigged to Head Bone/FBX (Unity)/`. Unchanged files.
- `Animations/Idle.anim`, `Walk.anim`, `Jog.anim`, `Sprint.anim`, `Hover.anim`, `FlyForward.anim`, `Fall.anim` —
  the clips `Idle_Loop`, `Walk_Loop`, `Jog_Fwd_Loop`, `Sprint_Loop`, `Swim_Idle_Loop`, `Swim_Fwd_Loop` and
  `Jump_Loop` from the Animation Library's `Unity/UAL1_Standard.fbx`, imported as humanoid clips with the root
  baked into the pose and saved on their own. The 23 MB library file itself is not in the repository.
- `Textures/Skin_Masculine.png`, `Skin_Feminine.png` — `T_Superhero_Male_Ligh.png` and
  `T_Superhero_Female_Light_BaseColor.png` from the Base Characters pack, reduced to 1024 px, turned to greyscale
  and brightened so that the material colour sets the skin tone.
- `Textures/Hair_1.png`, `Hair_2.png` — `T_Hair_1_BaseColor.png` and `T_Hair_2_BaseColor.png`, reduced to 512 px,
  greyscale, brightened the same way.
- `Textures/Cloth_Normal.png` — `Textures/Peasant/T_Peasant_Normal.png` from the Outfits pack, reduced to 1024 px.
- `Textures/Eye.png` — `T_Eye_Brown.png` from the Base Characters pack, unchanged.

The `.mat` files beside them are ours (plain URP Lit).
