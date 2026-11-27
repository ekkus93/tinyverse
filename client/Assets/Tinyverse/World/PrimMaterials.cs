using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.World
{
    /// <summary>
    /// Shared material for each surface preset. The materials are real assets under
    /// <c>Resources/Tinyverse/World/Materials/</c> so their shader variants survive
    /// build-time stripping. Per-object colour never touches them: it is a GPU-instanced
    /// property set through a MaterialPropertyBlock (see <see cref="ObjectRegistry"/>).
    /// </summary>
    public static class PrimMaterials
    {
        public const string ShaderName = "Tinyverse/World/Prim";
        public const string ResourceFolder = "Tinyverse/World/Materials/";

        /// <summary>Instanced per-object colour (linear RGBA, set with SetVector so no sRGB conversion happens).</summary>
        public static readonly int ColorId = Shader.PropertyToID("_PrimColor");

        /// <summary>Instanced per-object highlight amount, 0 or 1.</summary>
        public static readonly int HighlightId = Shader.PropertyToID("_Highlight");

        static readonly Material[] cache = new Material[5];

        /// <summary>The resource path of a preset's material, relative to a Resources folder.</summary>
        public static string ResourcePath(PrimMaterial material) => ResourceFolder + "Prim" + material;

        /// <summary>The shared material for a preset. Never modify or destroy it.</summary>
        public static Material Get(PrimMaterial material)
        {
            int i = (int)material;
            if (i < 0 || i >= cache.Length) i = 0;
            if (cache[i] != null) return cache[i];

            var loaded = Resources.Load<Material>(ResourcePath((PrimMaterial)i));
            if (loaded == null)
            {
                Debug.LogError($"[World] material {ResourcePath((PrimMaterial)i)} missing from Resources; building one at runtime (may render pink in a player)");
                var shader = Shader.Find(ShaderName);
                loaded = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit"));
                loaded.name = "Prim" + (PrimMaterial)i + " (runtime)";
                Configure(loaded, (PrimMaterial)i);
            }
            cache[i] = loaded;
            return loaded;
        }

        /// <summary>
        /// Sets a material up as the given preset. Used to author the material assets and as a
        /// fallback; at runtime the assets are used as they are.
        /// </summary>
        public static void Configure(Material m, PrimMaterial preset)
        {
            m.enableInstancing = true;
            m.SetColor("_PrimColor", Color.white);
            m.SetFloat("_Highlight", 0);
            m.SetFloat("_Metallic", 0);
            m.SetFloat("_Opacity", 1);
            m.SetFloat("_EmissionStrength", 0);
            m.SetFloat("_Cull", (float)CullMode.Back);
            SetOpaque(m);
            m.DisableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;

            switch (preset)
            {
                case PrimMaterial.Matte:
                    m.SetFloat("_Smoothness", 0.15f);
                    break;
                case PrimMaterial.Glossy:
                    m.SetFloat("_Smoothness", 0.85f);
                    break;
                case PrimMaterial.Metal:
                    m.SetFloat("_Smoothness", 0.75f);
                    m.SetFloat("_Metallic", 1f);
                    break;
                case PrimMaterial.Glass:
                    m.SetFloat("_Smoothness", 0.95f);
                    m.SetFloat("_Opacity", 0.35f);
                    SetTransparent(m);
                    break;
                case PrimMaterial.Emissive:
                    m.SetFloat("_Smoothness", 0.3f);
                    m.SetFloat("_EmissionStrength", 2.5f);
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    break;
            }
        }

        static void SetOpaque(Material m)
        {
            m.SetFloat("_Surface", 0);
            m.SetFloat("_SrcBlend", (float)BlendMode.One);
            m.SetFloat("_DstBlend", (float)BlendMode.Zero);
            m.SetFloat("_ZWrite", 1);
            m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.SetOverrideTag("RenderType", "Opaque");
            m.renderQueue = (int)RenderQueue.Geometry;
            m.SetShaderPassEnabled("DepthOnly", true);
            m.SetShaderPassEnabled("ShadowCaster", true);
        }

        // Premultiplied alpha keeps specular highlights at full strength on see-through glass.
        static void SetTransparent(Material m)
        {
            m.SetFloat("_Surface", 1);
            m.SetFloat("_SrcBlend", (float)BlendMode.One);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetShaderPassEnabled("DepthOnly", false);
            m.SetShaderPassEnabled("ShadowCaster", false);
        }
    }
}
