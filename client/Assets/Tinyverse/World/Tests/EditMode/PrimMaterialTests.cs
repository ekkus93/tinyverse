using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.World.Tests
{
    public class PrimMaterialTests
    {
        static readonly PrimMaterial[] AllPresets = (PrimMaterial[])System.Enum.GetValues(typeof(PrimMaterial));

        [Test]
        public void PresetAssetLoadsFromResources([ValueSource(nameof(AllPresets))] PrimMaterial preset)
        {
            var m = Resources.Load<Material>(PrimMaterials.ResourcePath(preset));
            Assert.That(m, Is.Not.Null, PrimMaterials.ResourcePath(preset));
            Assert.That(m.shader.name, Is.EqualTo(PrimMaterials.ShaderName));
            Assert.That(m.enableInstancing, Is.True, "per-object colour relies on GPU instancing");
            Assert.That(PrimMaterials.Get(preset), Is.SameAs(m), "Get returns the asset itself, not a copy");

            bool transparent = preset == PrimMaterial.Glass;
            Assert.That(m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), Is.EqualTo(transparent));
            Assert.That(m.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"), Is.EqualTo(transparent));
            Assert.That(m.IsKeywordEnabled("_EMISSION"), Is.EqualTo(preset == PrimMaterial.Emissive));
            if (transparent)
            {
                Assert.That(m.renderQueue, Is.EqualTo((int)RenderQueue.Transparent));
                Assert.That(m.GetFloat("_ZWrite"), Is.EqualTo(0));
                Assert.That(m.GetFloat("_DstBlend"), Is.EqualTo((float)BlendMode.OneMinusSrcAlpha));
                Assert.That(m.GetTag("RenderType", false), Is.EqualTo("Transparent"));
                Assert.That(m.GetFloat("_Opacity"), Is.LessThan(1f));
            }
            else
            {
                Assert.That(m.renderQueue, Is.EqualTo((int)RenderQueue.Geometry));
                Assert.That(m.GetFloat("_ZWrite"), Is.EqualTo(1));
                Assert.That(m.GetFloat("_DstBlend"), Is.EqualTo((float)BlendMode.Zero));
            }
            if (preset == PrimMaterial.Emissive) Assert.That(m.GetFloat("_EmissionStrength"), Is.GreaterThan(0));
            if (preset == PrimMaterial.Metal) Assert.That(m.GetFloat("_Metallic"), Is.EqualTo(1));
        }

        [Test]
        public void PresetsDiffer()
        {
            var smooth = AllPresets.Select(p => PrimMaterials.Get(p).GetFloat("_Smoothness")).ToArray();
            Assert.That(smooth[(int)PrimMaterial.Glossy], Is.GreaterThan(smooth[(int)PrimMaterial.Matte]));
            Assert.That(AllPresets.Select(PrimMaterials.Get).Distinct().Count(), Is.EqualTo(AllPresets.Length));
        }

        [Test]
        public void EnvironmentMaterialsLoadFromResources()
        {
            var ground = Resources.Load<Material>(WorldEnvironment.GroundMaterialPath);
            Assert.That(ground, Is.Not.Null);
            Assert.That(ground.shader.name, Is.EqualTo(WorldEnvironment.GroundShaderName));
            var sky = Resources.Load<Material>(WorldEnvironment.SkyMaterialPath);
            Assert.That(sky, Is.Not.Null);
            Assert.That(sky.shader.name, Is.EqualTo("Skybox/Procedural"));
        }

        [Test]
        public void OutlineMaterialsLoadFromResourcesInDrawOrder()
        {
            var mats = PrimMaterials.GetOutlineMaterials();
            Assert.That(mats.Length, Is.EqualTo(3));
            for (int i = 0; i < mats.Length; i++)
            {
                var layer = (PrimMaterials.OutlineLayer)i;
                Assert.That(mats[i], Is.SameAs(Resources.Load<Material>(PrimMaterials.OutlineResourcePath(layer))), layer.ToString());
                Assert.That(mats[i].shader.name, Is.EqualTo(PrimMaterials.OutlineShaderName));
                Assert.That(mats[i].renderQueue, Is.GreaterThan((int)RenderQueue.Transparent), "after glass");
                if (i > 0) Assert.That(mats[i].renderQueue, Is.GreaterThan(mats[i - 1].renderQueue), "mask, outer, inner");
            }
            var mask = mats[(int)PrimMaterials.OutlineLayer.Mask];
            Assert.That(mask.GetFloat("_ColorMask"), Is.EqualTo(0));
            Assert.That(mask.GetFloat("_Grow"), Is.EqualTo(0));
            Assert.That(mask.GetFloat("_StencilPass"), Is.EqualTo((float)StencilOp.Replace));
            var outer = mats[(int)PrimMaterials.OutlineLayer.Outer];
            var inner = mats[(int)PrimMaterials.OutlineLayer.Inner];
            Assert.That(outer.GetFloat("_StencilComp"), Is.EqualTo((float)CompareFunction.NotEqual));
            Assert.That(inner.GetFloat("_StencilComp"), Is.EqualTo((float)CompareFunction.NotEqual));
            Assert.That(inner.GetFloat("_Grow"), Is.InRange(0.1f, outer.GetFloat("_Grow") * 0.8f));
            Assert.That(outer.GetColor("_OutlineColor").grayscale, Is.GreaterThan(0.5f), "bright band");
            Assert.That(inner.GetColor("_OutlineColor").grayscale, Is.LessThan(0.1f), "dark line");
        }

        [Test]
        public void HighlightBarelyTintsTheBody([ValueSource(nameof(AllPresets))] PrimMaterial preset)
        {
            // The prim's colour must stay readable while selected: the body only gets a faint pulse.
            Assert.That(PrimMaterials.Get(preset).GetFloat("_HighlightFill"), Is.GreaterThan(0f).And.LessThanOrEqualTo(0.05f));
        }

        [Test]
        public void PrimShaderHasNoErrors()
        {
            var shader = Shader.Find(PrimMaterials.ShaderName);
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            Assert.That(shader.isSupported || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null, Is.True);
        }

        // -nographics never compiles shaders for a GPU, so compile the passes explicitly
        // for Vulkan / Linux with the keyword sets the materials and URP use.
        [TestCase(PrimMaterials.ShaderName, "ForwardLit", new string[0])]
        [TestCase(PrimMaterials.ShaderName, "ForwardLit", new[] { "_SURFACE_TYPE_TRANSPARENT", "_ALPHAPREMULTIPLY_ON" })]
        [TestCase(PrimMaterials.ShaderName, "ForwardLit", new[] { "_EMISSION", "INSTANCING_ON" })]
        [TestCase(PrimMaterials.ShaderName, "ForwardLit", new[] { "_MAIN_LIGHT_SHADOWS_CASCADE", "_ADDITIONAL_LIGHTS", "_CLUSTER_LIGHT_LOOP", "_SHADOWS_SOFT", "_SCREEN_SPACE_OCCLUSION", "INSTANCING_ON" })]
        [TestCase(PrimMaterials.ShaderName, "ShadowCaster", new[] { "INSTANCING_ON" })]
        [TestCase(PrimMaterials.ShaderName, "DepthOnly", new[] { "INSTANCING_ON" })]
        [TestCase(PrimMaterials.ShaderName, "DepthNormals", new[] { "INSTANCING_ON" })]
        [TestCase(PrimMaterials.OutlineShaderName, "Outline", new string[0])]
        [TestCase(PrimMaterials.OutlineShaderName, "Outline", new[] { "INSTANCING_ON" })]
        [TestCase(WorldEnvironment.GroundShaderName, "ForwardLit", new string[0])]
        [TestCase(WorldEnvironment.GroundShaderName, "ForwardLit", new[] { "_MAIN_LIGHT_SHADOWS_CASCADE", "_ADDITIONAL_LIGHTS", "_CLUSTER_LIGHT_LOOP", "_SHADOWS_SOFT", "_SCREEN_SPACE_OCCLUSION" })]
        [TestCase(WorldEnvironment.GroundShaderName, "DepthOnly", new string[0])]
        [TestCase(WorldEnvironment.GroundShaderName, "DepthNormals", new string[0])]
        public void ShaderPassCompilesForVulkan(string shaderName, string passName, string[] keywords)
        {
            var shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, shaderName);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False, shaderName);
            var data = ShaderUtil.GetShaderData(shader);
            var sub = data.GetSerializedSubshader(0);
            ShaderData.Pass pass = null;
            for (int i = 0; i < sub.PassCount; i++)
                if (sub.GetPass(i).Name == passName) pass = sub.GetPass(i);
            Assert.That(pass, Is.Not.Null, passName);
            foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
            {
                var result = pass.CompileVariant(stage, keywords, ShaderCompilerPlatform.Vulkan, BuildTarget.StandaloneLinux64);
                var errors = result.Messages.Where(m => m.severity == ShaderCompilerMessageSeverity.Error)
                    .Select(m => $"{m.message} ({m.file}:{m.line})").ToArray();
                Assert.That(result.Success, Is.True, $"{passName} {stage}: {string.Join("; ", errors)}");
            }
        }
    }
}
