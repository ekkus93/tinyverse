"""Builds the Tinyverse "modern" avatars with MPFB (MakeHuman for Blender). Run it through generate.sh.

    blender -b --python generate.py -- --presets tools/avatars/presets.json --preset masculine --out DIR

For one body preset it makes a MakeHuman character from the preset's parameters, dresses it (eyes, eyebrows,
eyelashes, a T-shirt-and-jeans outfit, shoes, every hair style) on MPFB's "game_engine" rig, deletes the skin
hidden under the clothes, decimates to the triangle budgets, merges the parts into one mesh per
material (skin, top, bottom, hair), packs each material's textures into one atlas, and exports:

    DIR/Models/Body_<preset>.fbx            armature + Body (skin and eyes) + Top + Bottom (trousers and shoes)
    DIR/Models/Hair_<preset>_<style>.fbx    armature + Hair (hair, eyebrows and eyelashes) for each style
    DIR/Textures/*.png|jpg                  greyscale atlases; the client tints them with the avatar's colours
    MANIFESTS/manifest_<preset>.json        triangle counts, bone names, textures (read by the Unity editor tool)

Everything is deterministic: the same inputs give byte-identical outputs.
"""

import argparse
import json
import os
import sys
import zlib

import addon_utils
import bpy
import bmesh
import numpy as np
from mathutils import Matrix, Quaternion, Vector

MPFB = "bl_ext.user_default.mpfb"


def log(*a):
    print("[avatars]", *a, flush=True)


# --------------------------------------------------------------------------------------------- setup


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--presets", required=True)
    p.add_argument("--preset", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--manifests", help="folder for manifest_<preset>.json (default: --out)")
    p.add_argument("--blend", help="also save the scene as this .blend (for debugging)")
    return p.parse_args(argv)


def make_fbx_deterministic():
    """The FBX exporter stamps the current time and derives ids from Python's salted str hash; pin both."""
    import datetime
    from io_scene_fbx import export_fbx_bin, fbx_utils

    class FixedDatetime(datetime.datetime):
        @classmethod
        def now(cls, tz=None):
            return cls(2026, 1, 1, 0, 0, 0)

    class FixedModule:
        datetime = FixedDatetime

    export_fbx_bin.datetime = FixedModule
    original = fbx_utils._key_to_uuid

    def stable_key_to_uuid(uuids, key):
        if isinstance(key, int):
            return original(uuids, key)
        return original(uuids, zlib.crc32(repr(key).encode("utf-8")) * 7919 + 1000003)

    fbx_utils._key_to_uuid = stable_key_to_uuid


# --------------------------------------------------------------------------------------------- assets


class Assets:
    """Finds MPFB assets and the textures their .mhmat files name."""

    def __init__(self):
        from bl_ext.user_default.mpfb.services.assetservice import AssetService
        self.service = AssetService

    def path(self, fragment, subdir):
        p = self.service.find_asset_absolute_path(fragment, asset_subdir=subdir)
        if p is None:
            raise FileNotFoundError(f"MPFB asset {subdir}/{fragment} not found (is its asset pack installed?)")
        return p

    @staticmethod
    def mhmat_texture(mhmat, key="diffuseTexture"):
        with open(mhmat, encoding="utf-8", errors="replace") as f:
            for line in f:
                parts = line.split(None, 1)
                if len(parts) == 2 and parts[0] == key:
                    return os.path.join(os.path.dirname(mhmat), parts[1].strip())
        return None

    def mhclo_texture(self, mhclo, key="diffuseTexture"):
        with open(mhclo, encoding="utf-8", errors="replace") as f:
            for line in f:
                parts = line.split(None, 1)
                if len(parts) == 2 and parts[0] == "material":
                    return self.mhmat_texture(os.path.join(os.path.dirname(mhclo), parts[1].strip()), key)
        return None


# --------------------------------------------------------------------------------------------- blender helpers


def select_only(*objs):
    bpy.ops.object.mode_set(mode="OBJECT") if bpy.context.object and bpy.context.object.mode != "OBJECT" else None
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]


def apply_modifier(obj, name):
    select_only(obj)
    with bpy.context.temp_override(object=obj, active_object=obj):
        bpy.ops.object.modifier_apply(modifier=name)


def tri_count(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def bone_name_set(rig):
    return {b.name for b in rig.data.bones}


def remap_uvs(obj, rect):
    """Squeezes the object's UVs (assumed in 0..1) into rect = (u0, v0, u1, v1) of an atlas."""
    u0, v0, u1, v1 = rect
    uv = obj.data.uv_layers.active.data
    co = np.zeros(len(uv) * 2, dtype=np.float32)
    uv.foreach_get("uv", co)
    co = co.reshape(-1, 2)
    co[:, 0] = u0 + np.clip(co[:, 0], 0.0, 1.0) * (u1 - u0)
    co[:, 1] = v0 + np.clip(co[:, 1], 0.0, 1.0) * (v1 - v0)
    uv.foreach_set("uv", co.ravel())


def join(name, objs):
    objs = [o for o in objs if o is not None]
    for o in objs:
        # Single UV layer called UVMap, no materials: Unity gets one submesh per mesh.
        while len(o.data.uv_layers) > 1:
            o.data.uv_layers.remove(o.data.uv_layers[1])
        o.data.uv_layers[0].name = "UVMap"
        o.data.materials.clear()
    select_only(*objs)
    if len(objs) > 1:
        bpy.ops.object.join()
    result = bpy.context.view_layer.objects.active
    result.name = name
    result.data.name = name
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    result.data.materials.clear()
    result.data.materials.append(mat)
    for p in result.data.polygons:
        p.material_index = 0
        p.use_smooth = True
    return result


def decimate(obj, budget):
    tris = tri_count(obj)
    if tris <= budget:
        return
    select_only(obj)
    m = obj.modifiers.new("Decimate", "DECIMATE")
    m.decimate_type = "COLLAPSE"
    m.ratio = budget / tris * 0.98
    m.use_collapse_triangulate = True
    bpy.ops.object.modifier_move_to_index(modifier="Decimate", index=0)
    apply_modifier(obj, "Decimate")
    log(f"  decimated {obj.name}: {tris} -> {tri_count(obj)} triangles")


def clean_weights(obj, rig):
    """At most four bone weights per vertex, normalised; groups that are not bones removed."""
    bones = bone_name_set(rig)
    for g in list(obj.vertex_groups):
        if g.name not in bones:
            obj.vertex_groups.remove(g)
    select_only(obj)
    bpy.ops.object.vertex_group_clean(group_select_mode="ALL", limit=0.01)
    bpy.ops.object.vertex_group_limit_total(group_select_mode="ALL", limit=4)
    bpy.ops.object.vertex_group_normalize_all(group_select_mode="ALL", lock_active=False)
    unweighted = sum(1 for v in obj.data.vertices if len(v.groups) == 0)
    if unweighted:
        log(f"  WARNING {obj.name}: {unweighted} vertices without weights")


# --------------------------------------------------------------------------------------------- images


def load_rgba(path, size=None):
    """Pixels of an image file as float32 (h, w, 4), row 0 at the bottom (the way UVs count)."""
    img = bpy.data.images.load(path, check_existing=False)
    img.colorspace_settings.name = "Non-Color"  # raw file values, no transform
    if size is not None and (img.size[0], img.size[1]) != tuple(size):
        img.scale(size[0], size[1])
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)


def luminance(px):
    return 0.2126 * px[..., 0] + 0.7152 * px[..., 1] + 0.0722 * px[..., 2]


def normalise(lum, mask, target=0.92, percentile=75):
    """Scales so the given percentile of the masked texels lands on target."""
    vals = lum[mask]
    ref = float(np.percentile(vals, percentile)) if vals.size else 1.0
    return np.clip(lum * (target / max(ref, 0.05)), 0.0, 1.0)


def grey(v, alpha=None):
    out = np.empty(v.shape + (4,), dtype=np.float32)
    out[..., 0] = out[..., 1] = out[..., 2] = v
    out[..., 3] = 1.0 if alpha is None else alpha
    return out


def place(atlas, rect, px):
    """Resamples px (h, w, 4) into rect of the atlas (both bottom-up)."""
    H, W = atlas.shape[:2]
    x0, y0 = int(round(rect[0] * W)), int(round(rect[1] * H))
    x1, y1 = int(round(rect[2] * W)), int(round(rect[3] * H))
    h, w = px.shape[:2]
    ys = (np.arange(y1 - y0) + 0.5) * h / (y1 - y0)
    xs = (np.arange(x1 - x0) + 0.5) * w / (x1 - x0)
    atlas[y0:y1, x0:x1] = px[np.clip(ys.astype(int), 0, h - 1)][:, np.clip(xs.astype(int), 0, w - 1)]


def blur(img, radius):
    """Separable box blur of a 2-D array, twice (close to a Gaussian)."""
    out = img.astype(np.float32)
    k = 2 * radius + 1
    for _ in range(2):
        for axis in (0, 1):
            pad = [(0, 0), (0, 0)]
            pad[axis] = (radius, radius)
            p = np.pad(out, pad, mode="edge")
            c = np.cumsum(p, axis=axis, dtype=np.float64)
            c = np.insert(c, 0, 0.0, axis=axis)
            n = out.shape[axis]
            hi = np.take(c, np.arange(k, k + n), axis=axis)
            lo = np.take(c, np.arange(0, n), axis=axis)
            out = ((hi - lo) / k).astype(np.float32)
    return out


def box_down(px, factor):
    if factor <= 1:
        return px
    h, w, c = px.shape
    return px.reshape(h // factor, factor, w // factor, factor, c).mean(axis=(1, 3))


def save_image(px, path, alpha=False, quality=92):
    h, w = px.shape[:2]
    name = os.path.basename(path)
    img = bpy.data.images.new(name, w, h, alpha=alpha)
    img.colorspace_settings.name = "Non-Color"
    data = px.copy()
    if not alpha:
        data[..., 3] = 1.0
    img.pixels.foreach_set(np.clip(data, 0.0, 1.0).ravel())
    img.filepath_raw = path
    jpeg = path.lower().endswith(".jpg")
    img.file_format = "JPEG" if jpeg else "PNG"
    settings = bpy.context.scene.render.image_settings
    settings.file_format = img.file_format
    settings.color_mode = "RGBA" if alpha else "RGB"
    settings.color_depth = "8"
    settings.quality = quality
    settings.compression = 90
    img.save_render(path, scene=bpy.context.scene)
    bpy.data.images.remove(img)
    log(f"  wrote {path} ({w}x{h})")


# --------------------------------------------------------------------------------------------- rig


def pose_t(rig):
    """Poses arms straight out to the sides with the hands level, and the neck upright: the reference pose Unity's
    humanoid retargeting assumes. apply_pose_as_rest then makes it the rest and bind pose of every mesh."""
    select_only(rig)
    bpy.ops.object.mode_set(mode="POSE")
    for pb in rig.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()

    def rotate_about_head(pb, q):
        head = pb.head.copy()
        t = Matrix.Translation(head) @ q.to_matrix().to_4x4() @ Matrix.Translation(-head)
        pb.matrix = t @ pb.matrix
        bpy.context.view_layer.update()

    def align(name, direction):
        pb = rig.pose.bones[name]
        cur = (pb.tail - pb.head).normalized()
        rotate_about_head(pb, cur.rotation_difference(direction))

    align("neck_01", Vector((0.0, 0.0, 1.0)))
    for side, sx in (("l", 1.0), ("r", -1.0)):
        out = Vector((sx, 0.0, 0.0))
        align(f"upperarm_{side}", out)
        align(f"lowerarm_{side}", out)
        align(f"hand_{side}", out)
        # Raising the arms this way leaves the hands about level; roll the arm about its axis to make it exact.
        hand = rig.pose.bones[f"hand_{side}"]
        normal = (rig.pose.bones[f"index_01_{side}"].head - hand.head).cross(rig.pose.bones[f"pinky_01_{side}"].head - hand.head)
        normal = Vector((0.0, normal.y, normal.z)).normalized()
        target = Vector((0.0, 0.0, 1.0 if normal.z >= 0 else -1.0))
        rotate_about_head(rig.pose.bones[f"upperarm_{side}"], normal.rotation_difference(target))
    bpy.ops.object.mode_set(mode="OBJECT")


def apply_pose_as_rest(rig, meshes):
    for m in meshes:
        mod = next((x for x in m.modifiers if x.type == "ARMATURE"), None)
        if mod is None:
            continue
        name = mod.name
        select_only(m)
        bpy.ops.object.modifier_move_to_index(modifier=name, index=0)
        apply_modifier(m, name)
    select_only(rig)
    bpy.ops.object.mode_set(mode="POSE")
    bpy.ops.pose.armature_apply(selected=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    for m in meshes:
        mod = m.modifiers.new("Armature", "ARMATURE")
        mod.object = rig


# --------------------------------------------------------------------------------------------- mesh surgery


def uv_islands(obj):
    """Face index -> island id, faces joined where they share an edge with the same UVs."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    uvl = bm.loops.layers.uv.active
    parent = list(range(len(bm.faces)))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for e in bm.edges:
        lf = e.link_loops
        if len(lf) != 2:
            continue
        a, b = lf
        # loops on the two faces: a goes v1->v2, b goes v2->v1
        a_uv0, a_uv1 = a[uvl].uv, a.link_loop_next[uvl].uv
        b_uv0, b_uv1 = b.link_loop_next[uvl].uv, b[uvl].uv
        if (a_uv0 - b_uv0).length < 1e-4 and (a_uv1 - b_uv1).length < 1e-4:
            fa, fb = find(a.face.index), find(b.face.index)
            if fa != fb:
                parent[fa] = fb
    result = [find(i) for i in range(len(bm.faces))]
    bm.free()
    return result


def split_suit(suit, waist_z):
    """Splits the outfit into the top (shirt) and the bottom (jeans) by UV island height."""
    islands = uv_islands(suit)
    zs = {}
    for p in suit.data.polygons:
        zs.setdefault(islands[p.index], []).append(p.center.z)
    top_islands = {i for i, z in zs.items() if sum(z) / len(z) > waist_z}
    log(f"  suit: {len(zs)} UV islands, {len(top_islands)} on the top")
    top = suit.copy()
    top.data = suit.data.copy()
    bpy.context.collection.objects.link(top)
    top.name = "TopPart"
    bottom = suit
    bottom.name = "BottomPart"
    for obj, keep_top in ((top, True), (bottom, False)):
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        bm.faces.ensure_lookup_table()
        dead = [f for f in bm.faces if (islands[f.index] in top_islands) != keep_top]
        bmesh.ops.delete(bm, geom=dead, context="FACES")
        bm.to_mesh(obj.data)
        bm.free()
    return top, bottom


def delete_loose(obj):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    loose = [v for v in bm.verts if not v.link_faces]
    bmesh.ops.delete(bm, geom=loose, context="VERTS")
    bm.to_mesh(obj.data)
    bm.free()


def hide_skin_under(body, covers, rig, depth=0.07):
    """Deletes body faces lying inside or just under a cover (clothes), so skin cannot poke through when animated.
    A body vertex counts as covered when a ray from slightly inside it along its outward normal hits a cover within
    `depth` metres; a face goes when all its vertices are covered. The head and hands are never touched."""
    from mathutils.bvhtree import BVHTree
    deps = bpy.context.evaluated_depsgraph_get()
    trees = [BVHTree.FromObject(c, deps) for c in covers]
    protect = set()
    for g in body.vertex_groups:
        if g.name in ("head", "neck_01") or g.name.startswith(("hand_", "thumb_", "index_", "middle_", "ring_", "pinky_")):
            protect.add(g.index)
    me = body.data
    covered = []
    for v in me.vertices:
        groups = sorted(v.groups, key=lambda g: -g.weight)
        if groups and groups[0].group in protect:
            covered.append(False)
            continue
        n = v.normal
        start = v.co - n * 0.005
        # Cover just outside (within depth), or skin already through it (cover a little inside the skin).
        hit = any(t.ray_cast(start, n, depth + 0.005)[0] is not None or
                  t.ray_cast(v.co + n * 0.002, -n, 0.025)[0] is not None for t in trees)
        covered.append(hit)
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    dead = [f for f in bm.faces if all(covered[v.index] for v in f.verts)]
    bmesh.ops.delete(bm, geom=dead, context="FACES")
    bm.to_mesh(me)
    bm.free()
    delete_loose(body)
    log(f"  removed {len(dead)} covered body faces")


# --------------------------------------------------------------------------------------------- textures


SKIN_BODY_RECT = (0.0, 0.0, 1.0, 0.875)
SKIN_EYE_RECT = (0.0, 0.875, 0.125, 1.0)
CLOTH_SUIT_RECT = (0.0, 0.0, 1.0, 0.8125)
CLOTH_SHOE_RECT = (0.0, 0.8125, 0.1875, 1.0)
HAIR_MAIN_RECT = (0.0, 0.0, 1.0, 0.875)
HAIR_BROW_RECT = (0.0, 0.875, 0.5, 1.0)
HAIR_LASH_RECT = (0.5, 0.875, 1.0, 1.0)


def skin_texture(diffuse, size):
    """Skin as a near-white map the client tints: luminance normalised, with a little of the original colour
    variation (lips, cheeks) kept relative to the average skin colour."""
    px = box_down(load_rgba(diffuse), 2048 // size if size < 2048 else 1)
    lum = luminance(px)
    mask = lum > 0.15
    mean = px[mask][:, :3].mean(axis=0)
    norm = normalise(lum, mask, target=0.86)
    balanced = px[..., :3] / mean * norm.mean()  # average skin -> neutral
    chroma = balanced / np.maximum(luminance(balanced), 1e-3)[..., None]
    rgb = norm[..., None] * (1.0 + 0.45 * (chroma - 1.0))
    out = np.ones(px.shape, dtype=np.float32)
    out[..., :3] = np.clip(rgb, 0.0, 1.0)
    return out


def build_skin_atlas(a, preset, eyes_tex, out_dir, sizes):
    s = sizes["skin"]
    atlas = np.zeros((s, s, 4), dtype=np.float32)
    atlas[..., 3] = 1.0
    skin_png = a.mhmat_texture(a.path(preset["skin"], "skins"))
    body = skin_texture(skin_png, 2048)
    place(atlas, SKIN_BODY_RECT, body)
    # The eye rect stays black in the base map; the eyes are drawn by the emission map so the skin tint never
    # darkens them.
    e = sizes["emission"]
    emission = np.zeros((e, e, 4), dtype=np.float32)
    emission[..., 3] = 1.0
    place(emission, SKIN_EYE_RECT, load_rgba(eyes_tex))
    return atlas, emission


def build_cloth_atlases(a, preset, size):
    suit = a.path(preset["suit"], "clothes")
    shoes = a.path(preset["shoes"], "clothes")
    ao = load_rgba(a.mhclo_texture(suit, "aomapTexture") or a.mhclo_texture(suit), (2048, 2048))
    diffuse = load_rgba(a.mhclo_texture(suit), (2048, 2048))
    normal_path = a.mhclo_texture(suit, "normalmapTexture")
    shoe_px = load_rgba(a.mhclo_texture(shoes), (1024, 1024))

    # Top: the shirt drawn from the ambient-occlusion map (folds and seams, no printed logo).
    ao_l = luminance(box_down(ao, 2048 // size))
    top = np.zeros((size, size, 4), dtype=np.float32)
    top[...] = grey(np.full((size, size), 0.9, dtype=np.float32))
    # Blurred and flattened: soft folds only, no printed labels, no near-black corners on the sleeves.
    ao_l = blur(ao_l, 6)
    ao_v = normalise(ao_l, ao_l > 0.2, target=1.0, percentile=60)
    place(top, CLOTH_SUIT_RECT, grey(0.93 * (0.78 + 0.22 * ao_v)))

    # Bottom: the denim's own light and dark, and the shoes much darker, so they read as dark shoes in any colour.
    d_l = luminance(box_down(diffuse, 2048 // size))
    bottom = np.zeros((size, size, 4), dtype=np.float32)
    bottom[...] = grey(np.full((size, size), 0.8, dtype=np.float32))
    place(bottom, CLOTH_SUIT_RECT, grey(normalise(d_l, d_l > 0.15, target=0.9, percentile=60)))
    s_l = luminance(shoe_px)
    place(bottom, CLOTH_SHOE_RECT, grey(0.06 + 0.3 * normalise(s_l, shoe_px[..., 3] > 0.5, target=1.0, percentile=60)))

    normal = np.zeros((size, size, 4), dtype=np.float32)
    normal[...] = (0.5, 0.5, 1.0, 1.0)
    if normal_path:
        n = load_rgba(normal_path)
        n = box_down(n, n.shape[0] // size) if n.shape[0] > size else n
        place(normal, CLOTH_SUIT_RECT, n)
    return top, bottom, normal


def hair_texture(path, darken=1.0, alpha_gain=1.0):
    px = load_rgba(path, (1024, 1024))
    lum = luminance(px)
    alpha = px[..., 3]
    v = normalise(lum, alpha > 0.5, target=0.92 * darken, percentile=70)
    return grey(v, np.clip(alpha * alpha_gain, 0.0, 1.0))


def build_hair_atlas(a, parts, brow_tex, lash_tex, size):
    atlas = np.zeros((size, size, 4), dtype=np.float32)
    n = max(1, len(parts))
    for i, tex in enumerate(parts):
        rect = (i / n, HAIR_MAIN_RECT[1], (i + 1) / n, HAIR_MAIN_RECT[3])
        place(atlas, rect, hair_texture(tex))
    # Eyebrows and eyelashes are faint, thin strokes: darker, and opaque enough to survive the alpha cut-off.
    place(atlas, HAIR_BROW_RECT, hair_texture(brow_tex, 0.55, 2.2))
    place(atlas, HAIR_LASH_RECT, hair_texture(lash_tex, 0.25, 1.8))
    # Fully transparent texels take the colour of their surroundings, so mipmaps do not darken the strand edges.
    alpha = atlas[..., 3]
    if (alpha > 0.5).any():
        mean = atlas[alpha > 0.5][:, :3].mean(axis=0)
        atlas[alpha < 0.02, :3] = mean
    return atlas


# --------------------------------------------------------------------------------------------- export


def stem(filename):
    return os.path.splitext(filename)[0]


def export_fbx(path, objs):
    select_only(*objs)
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        use_mesh_modifiers=True,
        mesh_smooth_type="OFF",
        use_tspace=False,
        use_armature_deform_only=True,
        add_leaf_bones=False,
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        armature_nodetype="NULL",
        bake_anim=False,
        path_mode="STRIP",
        embed_textures=False,
        use_custom_props=False,
    )
    log(f"  wrote {path} ({os.path.getsize(path)} bytes)")


# --------------------------------------------------------------------------------------------- main


def main():
    args = parse_args()
    with open(args.presets, encoding="utf-8") as f:
        config = json.load(f)
    preset = config["presets"][args.preset]
    budget = config["budget"]
    sizes = config["atlas"]
    gender = "feminine" if preset["phenotype"]["gender"] < 0.5 else "masculine"

    addon_utils.enable(MPFB, default_set=True)
    make_fbx_deterministic()
    from bl_ext.user_default.mpfb.services.humanservice import HumanService

    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    a = Assets()
    models = os.path.join(args.out, "Models")
    textures = os.path.join(args.out, "Textures")
    os.makedirs(models, exist_ok=True)
    os.makedirs(textures, exist_ok=True)

    log(f"preset {args.preset}")
    info = HumanService._create_default_human_info_dict()
    info["name"] = "Human"
    for k, v in preset["phenotype"].items():
        info["phenotype"][k] = v
    info["phenotype"]["race"] = {"african": 1 / 3, "asian": 1 / 3, "caucasian": 1 / 3}
    info["rig"] = "game_engine"
    info["eyes"] = config["eyes"]["mesh"]
    info["eyebrows"] = config["eyebrows"]
    info["eyelashes"] = config["eyelashes"]
    info["clothes"] = [preset["suit"], preset["shoes"]]
    info["skin_material_type"] = "NONE"
    settings = HumanService.get_default_deserialization_settings()
    settings["subdiv_levels"] = 0
    body = HumanService.deserialize_from_dict(info, settings)
    rig = body.parent
    rig.name = rig.data.name = "Armature"
    by_source = {}
    for o in bpy.data.objects:
        if o.type == "MESH" and o is not body:
            by_source[o.name.split(".", 1)[-1]] = o
    log("  objects:", sorted(by_source))

    def part(fragment):
        stem = os.path.splitext(os.path.basename(fragment))[0]
        for k, o in by_source.items():
            if k == stem or k.startswith(stem):
                return o
        raise KeyError(fragment)

    suit = part(preset["suit"])
    shoes = part(preset["shoes"])
    eyes = part(config["eyes"]["mesh"])
    brows = part(config["eyebrows"])
    lashes = part(config["eyelashes"])

    # Every hair style's assets, fitted now while the body still has all its vertices.
    hair_objs = {}
    for style, by_gender in config["hair"].items():
        objs = []
        for frag in by_gender[gender]:
            subdir = "hair"
            if frag.startswith("clothes:"):
                subdir, frag = "clothes", frag[len("clothes:"):]
            path = a.path(frag, subdir)
            before = set(bpy.data.objects)
            HumanService.add_mhclo_asset(path, body, asset_type=subdir, subdiv_levels=0, material_type="MAKESKIN")
            new = [o for o in bpy.data.objects if o not in before and o.type == "MESH"]
            for o in new:
                o.name = f"{style}:{os.path.basename(frag)}"
                o["texture"] = a.mhclo_texture(path)
            objs.extend(new)
        hair_objs[style] = objs
    # Only the outfit hides skin; hair and beards must not delete the chin or scalp.
    keep_masks = {"Hide helpers"} | {f"Delete.{os.path.splitext(os.path.basename(p))[0]}" for p in (preset["suit"], preset["shoes"])}
    for m in list(body.modifiers):
        if m.type == "MASK" and m.name not in keep_masks:
            body.modifiers.remove(m)

    # The body: targets baked, helpers and covered skin removed.
    select_only(body)
    if body.data.shape_keys:
        bpy.ops.object.shape_key_remove(all=True, apply_mix=True)
    for m in [m.name for m in body.modifiers if m.type == "MASK"]:
        apply_modifier(body, m)
    delete_loose(body)
    log(f"  body after masks: {tri_count(body)} triangles")

    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    for o in meshes:
        for m in list(o.modifiers):
            if m.type not in ("ARMATURE",):
                apply_modifier(o, m.name)
    pose_t(rig)
    apply_pose_as_rest(rig, meshes)

    waist = rig.data.bones["pelvis"].head_local.z + 0.12
    hide_skin_under(body, [suit, shoes], rig)
    top, bottom = split_suit(suit, waist)

    # Textures, and the UVs moved into their atlas rects.
    skin_atlas, emission = build_skin_atlas(a, preset, a.path(config["eyes"]["texture"], "eyes"), textures, sizes)
    top_px, bottom_px, normal_px = build_cloth_atlases(a, preset, sizes["cloth"])
    remap_uvs(body, SKIN_BODY_RECT)
    remap_uvs(eyes, SKIN_EYE_RECT)
    for o in (top, bottom):
        remap_uvs(o, CLOTH_SUIT_RECT)
    remap_uvs(shoes, CLOTH_SHOE_RECT)

    decimate(body, budget["body"] - tri_count(eyes))
    decimate(top, budget["top"])
    tris_shoes = min(tri_count(shoes), budget["bottom"] // 3)
    decimate(shoes, tris_shoes)
    decimate(bottom, budget["bottom"] - tri_count(shoes))

    body_mesh = join("Body", [body, eyes])
    top_mesh = join("Top", [top])
    bottom_mesh = join("Bottom", [bottom, shoes])
    for o in (body_mesh, top_mesh, bottom_mesh):
        clean_weights(o, rig)

    skin_name = f"Skin_{gender}.jpg"
    cloth_key = os.path.splitext(os.path.basename(preset["suit"]))[0]
    names = {
        "skin": skin_name,
        "eyes": "Eyes_Emission.jpg",
        "top": f"Top_{cloth_key}.jpg",
        "bottom": f"Bottom_{cloth_key}.jpg",
        "cloth_normal": f"ClothNormal_{cloth_key}.png",
    }
    save_image(skin_atlas, os.path.join(textures, names["skin"]))
    save_image(emission, os.path.join(textures, names["eyes"]))
    save_image(top_px, os.path.join(textures, names["top"]))
    save_image(bottom_px, os.path.join(textures, names["bottom"]))
    save_image(normal_px, os.path.join(textures, names["cloth_normal"]))

    manifest = {
        "preset": args.preset,
        "label": preset["label"],
        "gender": gender,
        "default_hair": preset["default_hair"],
        "body_model": f"Body_{args.preset}",
        "textures": names,
        "triangles": {"Body": tri_count(body_mesh), "Top": tri_count(top_mesh), "Bottom": tri_count(bottom_mesh)},
        "hair": {},
        "materials": [
            {"name": f"Skin_{args.preset}", "kind": "skin", "baseMap": stem(names["skin"]), "emissionMap": stem(names["eyes"])},
            {"name": f"Top_{args.preset}", "kind": "top", "baseMap": stem(names["top"]), "normalMap": stem(names["cloth_normal"])},
            {"name": f"Bottom_{args.preset}", "kind": "bottom", "baseMap": stem(names["bottom"]), "normalMap": stem(names["cloth_normal"])},
        ],
    }
    export_fbx(os.path.join(models, f"Body_{args.preset}.fbx"), [rig, body_mesh, top_mesh, bottom_mesh])

    # Hair: each style with its own copy of the eyebrows and eyelashes, one mesh and one atlas.
    brow_tex = a.mhclo_texture(a.path(config["eyebrows"], "eyebrows"))
    lash_tex = a.mhclo_texture(a.path(config["eyelashes"], "eyelashes"))
    for o in (brows, lashes):
        o.hide_set(True)
    for style, objs in hair_objs.items():
        parts = []
        for i, o in enumerate(objs):
            n = len(objs)
            remap_uvs(o, (i / n, HAIR_MAIN_RECT[1], (i + 1) / n, HAIR_MAIN_RECT[3]))
            parts.append(o["texture"])
            decimate(o, (budget["hair"] - tri_count(brows) - tri_count(lashes)) // n)
        b2, l2 = brows.copy(), lashes.copy()
        b2.data, l2.data = brows.data.copy(), lashes.data.copy()
        for c in (b2, l2):
            bpy.context.collection.objects.link(c)
            c.hide_set(False)
        remap_uvs(b2, HAIR_BROW_RECT)
        remap_uvs(l2, HAIR_LASH_RECT)
        hair = join("Hair", objs + [b2, l2])
        clean_weights(hair, rig)
        key = "_".join(os.path.splitext(os.path.basename(t))[0] for t in parts) or "none"
        tex_name = f"Hair_{key}.png"
        save_image(build_hair_atlas(a, parts, brow_tex, lash_tex, sizes["hair"]), os.path.join(textures, tex_name), alpha=True)
        model = f"Hair_{args.preset}_{style}"
        export_fbx(os.path.join(models, model + ".fbx"), [rig, hair])
        manifest["hair"][style] = {"model": model, "texture": tex_name, "triangles": tri_count(hair)}
        manifest["materials"].append({"name": model, "kind": "hair", "baseMap": stem(tex_name)})
        hair.name = f"Hair_{style}_done"
        hair.hide_set(True)

    manifest["bones"] = sorted(bone_name_set(rig))
    rest = {b.name: [round(x, 4) for x in b.head_local] for b in rig.data.bones}
    manifest["height"] = round(max(v.co.z for v in body_mesh.data.vertices), 4)
    manifest["head_bone"] = rest["head"]
    os.makedirs(args.manifests or args.out, exist_ok=True)
    with open(os.path.join(args.manifests or args.out, f"manifest_{args.preset}.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, sort_keys=True)
        f.write("\n")
    log("  triangles:", manifest["triangles"], {k: v["triangles"] for k, v in manifest["hair"].items()})
    if args.blend:
        bpy.ops.wm.save_as_mainfile(filepath=args.blend)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
