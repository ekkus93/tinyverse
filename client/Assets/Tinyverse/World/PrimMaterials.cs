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

        /// <summary>
        /// Instanced per-object colour as the shader consumes it: shading-space RGBA (linear in a
        /// linear-space project). Write it with <see cref="ToShaderColor"/> and SetVector.
        /// </summary>
        public static readonly int ColorId = Shader.PropertyToID("_PrimColor");

        /// <summary>
        /// Converts an sRGB colour (as in <see cref="PrimData.Color"/>, a hex code or a colour
        /// picker) to the value written to <see cref="ColorId"/>: linearised when the project
        /// renders in linear colour space, unchanged in gamma space. Alpha is never converted.
        /// </summary>
        public static Vector4 ToShaderColor(Color srgb)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                var l = srgb.linear;
                return new Vector4(l.r, l.g, l.b, srgb.a);
            }
            return new Vector4(srgb.r, srgb.g, srgb.b, srgb.a);
        }

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

        // ------------------------------------------------------------ selection outline

        public const string OutlineShaderName = "Tinyverse/World/PrimOutline";

        /// <summary>Bright outline band colour (sRGB) and the faint body pulse colour.</summary>
        public static readonly Color OutlineColor = new Color(1f, 0.72f, 0.05f, 1f);

        /// <summary>Dark line drawn tight against the silhouette, inside the bright band.</summary>
        public static readonly Color OutlineInnerColor = new Color(0.06f, 0.05f, 0.04f, 1f);

        /// <summary>Strength of the faint emissive pulse on a highlighted prim's body.</summary>
        public const float HighlightFill = 0.03f;

        /// <summary>The three outline passes, in draw order.</summary>
        public enum OutlineLayer { Mask, Outer, Inner }

        static Material[] outline;

        public static string OutlineResourcePath(OutlineLayer layer) => ResourceFolder + "PrimOutline" + layer;

        /// <summary>
        /// The shared materials of the selection outline renderer, in draw order (mask, outer,
        /// inner). Never modify or destroy them.
        /// </summary>
        public static Material[] GetOutlineMaterials()
        {
            if (outline != null && outline[0] != null) return outline;
            var layers = (OutlineLayer[])System.Enum.GetValues(typeof(OutlineLayer));
            outline = new Material[layers.Length];
            foreach (var layer in layers)
            {
                var m = Resources.Load<Material>(OutlineResourcePath(layer));
                if (m == null)
                {
                    Debug.LogError($"[World] material {OutlineResourcePath(layer)} missing from Resources; building one at runtime");
                    var shader = Shader.Find(OutlineShaderName);
                    m = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Unlit")) { name = "PrimOutline" + layer + " (runtime)" };
                    ConfigureOutline(m, layer);
                }
                outline[(int)layer] = m;
            }
            return outline;
        }

        /// <summary>Sets a material up as one layer of the selection outline (asset authoring and fallback).</summary>
        public static void ConfigureOutline(Material m, OutlineLayer layer)
        {
            m.enableInstancing = true;
            m.SetColor("_PulseColor", new Color(1f, 0.95f, 0.6f, 1f));
            m.SetFloat("_WidthPerMetre", 0.006f);
            m.SetFloat("_MinWidth", 0.015f);
            m.SetFloat("_MaxWidth", 0.6f);
            switch (layer)
            {
                case OutlineLayer.Mask:
                    m.SetFloat("_Grow", 0f);
                    m.SetFloat("_ColorMask", 0f);
                    m.SetFloat("_StencilComp", (float)CompareFunction.Always);
                    m.SetFloat("_StencilPass", (float)StencilOp.Replace);
                    m.SetFloat("_OffsetUnits", -1f);
                    m.SetColor("_OutlineColor", Color.black);
                    m.SetFloat("_PulseAmount", 0f);
                    m.renderQueue = (int)RenderQueue.Transparent + 10;
                    break;
                case OutlineLayer.Outer:
                    m.SetFloat("_Grow", 1f);
                    m.SetFloat("_ColorMask", 15f);
                    m.SetFloat("_StencilComp", (float)CompareFunction.NotEqual);
                    m.SetFloat("_StencilPass", (float)StencilOp.Keep);
                    m.SetFloat("_OffsetUnits", 0f);
                    m.SetColor("_OutlineColor", OutlineColor);
                    m.SetFloat("_PulseAmount", 0.35f);
                    m.renderQueue = (int)RenderQueue.Transparent + 11;
                    break;
                case OutlineLayer.Inner:
                    m.SetFloat("_Grow", 0.35f);
                    m.SetFloat("_ColorMask", 15f);
                    m.SetFloat("_StencilComp", (float)CompareFunction.NotEqual);
                    m.SetFloat("_StencilPass", (float)StencilOp.Keep);
                    m.SetFloat("_OffsetUnits", 0f);
                    m.SetColor("_OutlineColor", OutlineInnerColor);
                    m.SetFloat("_PulseAmount", 0f);
                    m.renderQueue = (int)RenderQueue.Transparent + 12;
                    break;
            }
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
            m.SetColor("_HighlightColor", OutlineColor);
            m.SetFloat("_HighlightFill", HighlightFill);
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
