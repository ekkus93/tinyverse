using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Tinyverse.Avatars.Tests
{
    /// <summary>The generated (modern) avatar assets keep to the budgets for integrated graphics.</summary>
    public class ModernAvatarAssetTests
    {
        private const string Root = "Assets/Tinyverse/Avatar/Resources/Tinyverse/Avatar/Modern";

        private static int Triangles(Mesh m)
        {
            long n = 0;
            for (int s = 0; s < m.subMeshCount; s++) n += m.GetIndexCount(s);
            return (int)(n / 3);
        }

        private static GameObject Model(string name)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>($"{Root}/Models/{name}.fbx");
            Assert.That(go, Is.Not.Null, name);
            return go;
        }

        [Test]
        public void EveryPresetHasItsModelsAndMaterials()
        {
            foreach (var p in HumanCatalog.ModernPresets)
            {
                Assert.That(Resources.Load<GameObject>(HumanCatalog.ModernModelsFolder + p.SkeletonModel), Is.Not.Null, p.Id);
                foreach (var mat in new[] { p.SkinMaterial, p.TopMaterial, p.BottomMaterial })
                    Assert.That(Resources.Load<Material>(HumanCatalog.ModernMaterialsFolder + mat), Is.Not.Null, mat);
                foreach (var h in HumanCatalog.HairStyles)
                {
                    string model = HumanCatalog.ModernHairModel(p, h);
                    Assert.That(Resources.Load<GameObject>(HumanCatalog.ModernModelsFolder + model), Is.Not.Null, model);
                    Assert.That(Resources.Load<Material>(HumanCatalog.ModernMaterialsFolder + model), Is.Not.Null, model + " material");
                }
            }
        }

        [Test]
        public void DressedAvatarsKeepToTheTriangleBudget()
        {
            foreach (var p in HumanCatalog.ModernPresets)
            {
                var body = Model(p.SkeletonModel);
                var meshes = body.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Assert.That(meshes.Select(m => m.name).OrderBy(n => n), Is.EqualTo(new[] { "Bottom", "Body", "Top" }.OrderBy(n => n)), p.Id);
                Assert.That(meshes.All(m => m.sharedMesh.subMeshCount == 1), Is.True, "one material per mesh");
                int bodyTris = meshes.Sum(m => Triangles(m.sharedMesh));
                foreach (var h in HumanCatalog.HairStyles)
                {
                    var hair = Model(HumanCatalog.ModernHairModel(p, h)).GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    Assert.That(hair.Length, Is.EqualTo(1), "hair, eyebrows and eyelashes are one mesh");
                    int total = bodyTris + Triangles(hair[0].sharedMesh);
                    Assert.That(total, Is.LessThanOrEqualTo(15000), $"{p.Id} with {h.Id}: {total} triangles");
                }
            }
        }

        [Test]
        public void ModelsImportAsHumanoidsWithFourBoneWeights()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { Root + "/Models" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                bool body = Path.GetFileName(path).StartsWith("Body_");
                Assert.That(importer.skinWeights, Is.EqualTo(ModelImporterSkinWeights.Standard), path);
                Assert.That(importer.materialImportMode, Is.EqualTo(ModelImporterMaterialImportMode.None), path);
                Assert.That(importer.animationType, Is.EqualTo(body ? ModelImporterAnimationType.Human : ModelImporterAnimationType.Generic), path);
                if (body)
                {
                    var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<UnityEngine.Avatar>().Single();
                    Assert.That(avatar.isHuman && avatar.isValid, Is.True, path);
                }
            }
        }

        [Test]
        public void TexturesAndMaterialsKeepToTheBudget()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Root + "/Textures" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                int limit = Path.GetFileName(path).StartsWith("Skin_") ? 2048 : 1024;
                Assert.That(Mathf.Max(tex.width, tex.height), Is.LessThanOrEqualTo(limit), path);
            }
            var mats = AssetDatabase.FindAssets("t:Material", new[] { Root + "/Materials" })
                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g))).ToList();
            Assert.That(mats.Count, Is.GreaterThanOrEqualTo(HumanCatalog.ModernPresets.Count * (3 + HumanCatalog.HairStyles.Count)));
            foreach (var m in mats)
            {
                Assert.That(m.shader.name, Is.EqualTo("Universal Render Pipeline/Lit"), m.name);
                Assert.That(m.GetTexture("_BaseMap"), Is.Not.Null, m.name);
            }
        }
    }
}
