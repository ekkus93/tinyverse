using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.Building
{
    /// <summary>
    /// The visible move, rotate and scale gizmos: renderers with the overlay material (ZTest Always), no colliders.
    /// Picking is done by <see cref="GizmoGeometry.Pick"/> against the same layout.
    /// </summary>
    sealed class TransformGizmo
    {
        static readonly Color[] axisColors =
        {
            new Color(0.95f, 0.26f, 0.24f, 0.95f),
            new Color(0.40f, 0.88f, 0.28f, 0.95f),
            new Color(0.28f, 0.52f, 1.00f, 0.95f),
        };
        static readonly Color uniformColor = new Color(0.92f, 0.92f, 0.92f, 0.95f);
        static readonly Color hoverColor = new Color(1f, 0.86f, 0.2f, 1f);

        sealed class Part
        {
            public BuildHandle Handle;
            public GameObject Go;
            public MeshRenderer Renderer;
            public Color Base;
        }

        readonly GameObject root;
        readonly Transform rootTf;
        readonly Dictionary<BuildTool, GameObject> groups = new Dictionary<BuildTool, GameObject>();
        readonly List<Part> parts = new List<Part>();
        readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        BuildHandle shownHover = (BuildHandle)(-1);
        Part scaleY;

        public TransformGizmo(Transform parent)
        {
            root = new GameObject("BuildGizmo");
            rootTf = root.transform;
            rootTf.SetParent(parent, false);
            var mat = BuildAssets.GizmoMaterial;

            var move = Group(BuildTool.Move);
            for (int i = 0; i < 3; i++)
            {
                AddPart(move, (BuildHandle)((int)BuildHandle.MoveX + i), GizmoMeshes.Arrow,
                    Quaternion.FromToRotation(Vector3.up, BuildMath.Axis(i)), axisColors[i], mat);
                var pc = axisColors[i];
                pc.a = 0.45f;
                AddPart(move, (BuildHandle)((int)BuildHandle.MoveYZ + i), GizmoMeshes.Plane(i), Quaternion.identity, pc, mat);
            }

            var rotate = Group(BuildTool.Rotate);
            for (int i = 0; i < 3; i++)
                AddPart(rotate, (BuildHandle)((int)BuildHandle.RotateX + i), GizmoMeshes.Ring,
                    Quaternion.FromToRotation(Vector3.up, BuildMath.Axis(i)), axisColors[i], mat);

            var scale = Group(BuildTool.Scale);
            for (int i = 0; i < 3; i++)
            {
                var p = AddPart(scale, (BuildHandle)((int)BuildHandle.ScaleX + i), GizmoMeshes.ScaleHandle,
                    Quaternion.FromToRotation(Vector3.up, BuildMath.Axis(i)), axisColors[i], mat);
                if (i == 1) scaleY = p;
            }
            AddPart(scale, BuildHandle.ScaleUniform, GizmoMeshes.Cube, Quaternion.identity, uniformColor, mat);

            Hide();
        }

        public bool Visible { get; private set; }
        public BuildTool Mode { get; private set; }
        public GameObject Root => root;

        GameObject Group(BuildTool tool)
        {
            var g = new GameObject(tool + "Gizmo");
            g.transform.SetParent(rootTf, false);
            groups[tool] = g;
            return g;
        }

        Part AddPart(GameObject group, BuildHandle h, Mesh mesh, Quaternion rot, Color color, Material mat)
        {
            var go = new GameObject(h.ToString());
            go.transform.SetParent(group.transform, false);
            go.transform.localRotation = rot;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            var p = new Part { Handle = h, Go = go, Renderer = r, Base = color };
            parts.Add(p);
            return p;
        }

        public void Show(BuildTool mode, Vector3 center, Quaternion axes, float size, bool hideScaleY)
        {
            if (!root.activeSelf) root.SetActive(true);
            foreach (var kv in groups)
                if (kv.Value.activeSelf != (kv.Key == mode)) kv.Value.SetActive(kv.Key == mode);
            if (scaleY.Go.activeSelf == hideScaleY) scaleY.Go.SetActive(!hideScaleY);
            rootTf.SetPositionAndRotation(center, axes);
            rootTf.localScale = new Vector3(size, size, size);
            Visible = true;
            Mode = mode;
        }

        public void Hide()
        {
            if (root.activeSelf) root.SetActive(false);
            Visible = false;
        }

        /// <summary>Highlights the hovered (or dragged) handle.</summary>
        public void SetHover(BuildHandle hover)
        {
            if (hover == shownHover) return;
            shownHover = hover;
            foreach (var p in parts)
            {
                block.Clear();
                block.SetColor(BuildAssets.ColorId, p.Handle == hover ? hoverColor : p.Base);
                p.Renderer.SetPropertyBlock(block);
            }
        }

        public void Destroy()
        {
            if (root == null) return;
            if (Application.isPlaying) Object.Destroy(root);
            else Object.DestroyImmediate(root);
        }
    }
}
