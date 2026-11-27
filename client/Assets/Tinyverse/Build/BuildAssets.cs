using UnityEngine;

namespace Tinyverse.Building
{
    /// <summary>
    /// The build module's material assets, loaded from <c>Resources/Tinyverse/Build/Materials/</c>. They are real
    /// assets (not runtime-created materials) so that they and their shader are included in a player build.
    /// </summary>
    public static class BuildAssets
    {
        public const string ShaderName = "Tinyverse/Build/Overlay";
        public const string GizmoMaterialPath = "Tinyverse/Build/Materials/GizmoOverlay";
        public const string PreviewMaterialPath = "Tinyverse/Build/Materials/PlacementPreview";

        /// <summary>The shader's colour property (set per renderer with a MaterialPropertyBlock).</summary>
        public static readonly int ColorId = Shader.PropertyToID("_Color");

        static Material gizmo, preview;

        /// <summary>Unlit, ZTest Always, overlay queue: draws on top of everything.</summary>
        public static Material GizmoMaterial => gizmo != null ? gizmo : gizmo = Load(GizmoMaterialPath, 8f, 4000);

        /// <summary>Unlit, translucent, depth-tested.</summary>
        public static Material PreviewMaterial => preview != null ? preview : preview = Load(PreviewMaterialPath, 4f, 3000);

        static Material Load(string path, float zTest, int queue)
        {
            var m = Resources.Load<Material>(path);
            if (m != null) return m;
            Debug.LogError($"[Build] material {path} missing from Resources; building one at runtime (may render pink in a player)");
            var shader = Shader.Find(ShaderName);
            m = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Unlit")) { name = path + " (runtime)" };
            m.SetFloat("_ZTest", zTest);
            m.renderQueue = queue;
            return m;
        }
    }
}
