using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Tinyverse.World.Tests
{
    public class ObjectRegistryTests
    {
        GameObject root;
        ObjectRegistry reg;
        readonly List<string> events = new List<string>();

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("RegistryTestRoot");
            reg = new ObjectRegistry(root.transform);
            events.Clear();
            reg.Added += d => events.Add("added " + d.Id);
            reg.Changed += d => events.Add("changed " + d.Id);
            reg.Removed += id => events.Add("removed " + id);
        }

        [TearDown]
        public void TearDown()
        {
            reg.Clear();
            Object.DestroyImmediate(root);
        }

        static PrimData Prim(ulong id, PrimShape shape = PrimShape.Box, bool solid = true) => new PrimData
        {
            Id = id,
            OwnerId = "owner",
            Name = "p" + id,
            Shape = shape,
            Material = PrimMaterial.Matte,
            Position = new Vector3(id, 1, 0),
            Rotation = Quaternion.identity,
            Scale = Vector3.one,
            Color = new Color(0.2f, 0.4f, 0.6f, 1f),
            Solid = solid,
        };

        static Vector4 ColourOf(GameObject go)
        {
            var block = new MaterialPropertyBlock();
            go.GetComponent<MeshRenderer>().GetPropertyBlock(block);
            return block.GetVector(PrimMaterials.ColorId);
        }

        static float HighlightOf(GameObject go)
        {
            var block = new MaterialPropertyBlock();
            go.GetComponent<MeshRenderer>().GetPropertyBlock(block);
            return block.GetFloat(PrimMaterials.HighlightId);
        }

        [Test]
        public void AddCreatesGameObjectUnderParent()
        {
            reg.Upsert(Prim(1));
            Assert.That(reg.Count, Is.EqualTo(1));
            var go = reg.GetGameObject(1);
            Assert.That(go, Is.Not.Null);
            Assert.That(go.transform.parent, Is.SameAs(root.transform));
            Assert.That(go.transform.position, Is.EqualTo(new Vector3(1, 1, 0)));
            Assert.That(go.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(PrimMeshes.Get(PrimShape.Box)));
            Assert.That(go.GetComponent<MeshRenderer>().sharedMaterial, Is.SameAs(PrimMaterials.Get(PrimMaterial.Matte)));
            Assert.That(go.GetComponent<BoxCollider>(), Is.Not.Null);
            Assert.That(reg.TryGet(1, out var d), Is.True);
            Assert.That(d.Name, Is.EqualTo("p1"));
            Assert.That(events, Is.EqualTo(new[] { "added 1" }));
            Assert.That(reg.All.Select(x => x.Id), Is.EquivalentTo(new ulong[] { 1 }));
        }

        [Test]
        public void ColourGoesThroughPropertyBlockNotMaterial()
        {
            reg.Upsert(Prim(1));
            var p2 = Prim(2);
            p2.Color = new Color(1, 0, 0, 1);
            reg.Upsert(p2);
            var a = reg.GetGameObject(1);
            var b = reg.GetGameObject(2);
            Assert.That(a.GetComponent<MeshRenderer>().sharedMaterial, Is.SameAs(b.GetComponent<MeshRenderer>().sharedMaterial));
            Assert.That(a.GetComponent<MeshRenderer>().HasPropertyBlock(), Is.True);
            // sRGB data reaches the shader converted for the project colour space.
            Assert.That(ColourOf(a), Is.EqualTo(PrimMaterials.ToShaderColor(new Color(0.2f, 0.4f, 0.6f, 1f))));
            Assert.That(ColourOf(b), Is.EqualTo(new Vector4(1, 0, 0, 1)));
            Assert.That(HighlightOf(a), Is.EqualTo(0));

            p2.Color = new Color(0, 1, 0, 0.5f);
            reg.Upsert(p2);
            Assert.That(ColourOf(b), Is.EqualTo(new Vector4(0, 1, 0, 0.5f)));
        }

        [Test]
        public void SrgbColourIsLinearisedForTheShader()
        {
            Assert.That(QualitySettings.activeColorSpace, Is.EqualTo(ColorSpace.Linear), "this project renders in linear space");
            var p = Prim(1);
            p.Color = new Color(0.5f, 0.5f, 0.5f, 0.5f); // mid-grey sRGB, half alpha
            reg.Upsert(p);
            var v = ColourOf(reg.GetGameObject(1));
            Assert.That(v.x, Is.EqualTo(0.214f).Within(0.001f));
            Assert.That(v.y, Is.EqualTo(0.214f).Within(0.001f));
            Assert.That(v.z, Is.EqualTo(0.214f).Within(0.001f));
            Assert.That(v.w, Is.EqualTo(0.5f), "alpha is not converted");

            // #ff8000 → R 1, G linear(0.502) ≈ 0.216, B 0.
            p.Color = new Color(1f, 128f / 255f, 0f, 1f);
            reg.Upsert(p);
            v = ColourOf(reg.GetGameObject(1));
            Assert.That(v.x, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(v.y, Is.EqualTo(0.216f).Within(0.001f));
            Assert.That(v.z, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void ChangeKeepsGameObjectAndCollider()
        {
            reg.Upsert(Prim(1));
            var go = reg.GetGameObject(1);
            var col = go.GetComponent<Collider>();
            var p = Prim(1);
            p.Position = new Vector3(5, 6, 7);
            p.Scale = new Vector3(2, 3, 4);
            p.Rotation = Quaternion.Euler(0, 90, 0);
            p.Name = "renamed";
            reg.Upsert(p);
            Assert.That(reg.GetGameObject(1), Is.SameAs(go));
            Assert.That(go.GetComponent<Collider>(), Is.SameAs(col));
            Assert.That(go.GetComponents<Collider>().Length, Is.EqualTo(1));
            Assert.That(go.transform.position, Is.EqualTo(new Vector3(5, 6, 7)));
            Assert.That(go.transform.localScale, Is.EqualTo(new Vector3(2, 3, 4)));
            Assert.That(Quaternion.Angle(go.transform.rotation, Quaternion.Euler(0, 90, 0)), Is.LessThan(0.01f));
            Assert.That(reg.TryGet(1, out var d) && d.Name == "renamed", Is.True);
            Assert.That(events, Is.EqualTo(new[] { "added 1", "changed 1" }));
            Assert.That(reg.SmoothingCount, Is.EqualTo(0), "non-smooth upsert snaps");
        }

        [Test]
        public void RemoveAndClear()
        {
            reg.Upsert(Prim(1));
            reg.Upsert(Prim(2));
            reg.Upsert(Prim(3));
            var go1 = reg.GetGameObject(1);
            Assert.That(reg.Remove(1), Is.True);
            Assert.That(reg.Remove(1), Is.False);
            Assert.That(go1 == null, Is.True, "GameObject destroyed");
            Assert.That(reg.GetGameObject(1), Is.Null);
            Assert.That(reg.TryGet(1, out _), Is.False);
            Assert.That(reg.Count, Is.EqualTo(2));
            reg.Clear();
            Assert.That(reg.Count, Is.EqualTo(0));
            Assert.That(root.transform.childCount, Is.EqualTo(0));
            Assert.That(events.Where(e => e.StartsWith("removed")), Is.EquivalentTo(new[] { "removed 1", "removed 2", "removed 3" }));
        }

        [Test]
        public void TryGetIdRoundTrip()
        {
            foreach (PrimShape s in System.Enum.GetValues(typeof(PrimShape)))
                reg.Upsert(Prim(10 + (ulong)s, s));
            foreach (PrimShape s in System.Enum.GetValues(typeof(PrimShape)))
            {
                ulong id = 10 + (ulong)s;
                var col = reg.GetGameObject(id).GetComponent<Collider>();
                Assert.That(reg.TryGetId(col, out var found), Is.True, s.ToString());
                Assert.That(found, Is.EqualTo(id));
            }
            var stranger = root.AddComponent<BoxCollider>();
            Assert.That(reg.TryGetId(stranger, out _), Is.False);
            Assert.That(reg.TryGetId(null, out _), Is.False);
        }

        [Test]
        public void RaycastPicksTriggerPrims()
        {
            reg.Upsert(Prim(1, PrimShape.Cone, solid: false));
            Physics.SyncTransforms();
            var hit = Physics.Raycast(new Vector3(1, 10, 0), Vector3.down, out var info, 20f, ~0, QueryTriggerInteraction.Collide);
            Assert.That(hit, Is.True);
            Assert.That(reg.TryGetId(info.collider, out var id) && id == 1, Is.True);
            Assert.That(Physics.Raycast(new Vector3(1, 10, 0), Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore), Is.False);
        }

        [Test]
        public void ShapeChangeSwapsMeshAndCollider()
        {
            reg.Upsert(Prim(1, PrimShape.Box));
            var go = reg.GetGameObject(1);
            var oldCol = go.GetComponent<Collider>();

            reg.Upsert(Prim(1, PrimShape.Cone));
            Assert.That(reg.GetGameObject(1), Is.SameAs(go));
            Assert.That(go.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(PrimMeshes.Get(PrimShape.Cone)));
            Assert.That(go.GetComponents<Collider>().Length, Is.EqualTo(1));
            var mc = go.GetComponent<MeshCollider>();
            Assert.That(mc, Is.Not.Null);
            Assert.That(mc.convex, Is.True);
            Assert.That(mc.sharedMesh, Is.SameAs(PrimMeshes.GetCollision(PrimShape.Cone)));
            Assert.That(reg.TryGetId(mc, out var id) && id == 1, Is.True);
            Assert.That(reg.TryGetId(oldCol, out _), Is.False);

            reg.Upsert(Prim(1, PrimShape.Wedge));
            Assert.That(go.GetComponent<MeshCollider>(), Is.SameAs(mc), "same collider type is reused");
            Assert.That(mc.sharedMesh, Is.SameAs(PrimMeshes.GetCollision(PrimShape.Wedge)));

            reg.Upsert(Prim(1, PrimShape.Sphere));
            Assert.That(go.GetComponent<SphereCollider>(), Is.Not.Null);
            Assert.That(go.GetComponents<Collider>().Length, Is.EqualTo(1));

            var stretched = Prim(1, PrimShape.Sphere);
            stretched.Scale = new Vector3(1, 3, 1);
            reg.Upsert(stretched);
            Assert.That(go.GetComponent<MeshCollider>(), Is.Not.Null, "an uneven sphere needs a mesh collider");

            reg.Upsert(Prim(1, PrimShape.Cylinder));
            Assert.That(go.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(PrimMeshes.GetCollision(PrimShape.Cylinder)));

            var plane = Prim(1, PrimShape.Plane);
            plane.Scale = new Vector3(4, 9, 2);
            reg.Upsert(plane);
            var box = go.GetComponent<BoxCollider>();
            Assert.That(box, Is.Not.Null);
            Assert.That(box.size.y, Is.EqualTo(ObjectRegistry.PlaneColliderThickness));
            Assert.That(go.transform.localScale, Is.EqualTo(new Vector3(4, 1, 2)), "plane ignores Y scale");
            Assert.That(reg.TryGet(1, out var stored) && stored.Scale.y == 9, Is.True, "stored data keeps what was sent");
        }

        [Test]
        public void SolidToggleSwitchesTrigger()
        {
            reg.Upsert(Prim(1, PrimShape.Cone, solid: true));
            var go = reg.GetGameObject(1);
            Assert.That(go.GetComponent<Collider>().isTrigger, Is.False);
            reg.Upsert(Prim(1, PrimShape.Cone, solid: false));
            Assert.That(go.GetComponent<Collider>().isTrigger, Is.True);
            // Survives a collider swap.
            reg.Upsert(Prim(1, PrimShape.Box, solid: false));
            Assert.That(go.GetComponent<BoxCollider>().isTrigger, Is.True);
            reg.Upsert(Prim(1, PrimShape.Box, solid: true));
            Assert.That(go.GetComponent<BoxCollider>().isTrigger, Is.False);
        }

        [Test]
        public void MaterialSwapKeepsColour()
        {
            var p = Prim(1);
            reg.Upsert(p);
            var r = reg.GetGameObject(1).GetComponent<MeshRenderer>();
            foreach (PrimMaterial m in System.Enum.GetValues(typeof(PrimMaterial)))
            {
                p.Material = m;
                reg.Upsert(p);
                Assert.That(r.sharedMaterial, Is.SameAs(PrimMaterials.Get(m)));
                Assert.That(ColourOf(r.gameObject), Is.EqualTo(PrimMaterials.ToShaderColor(new Color(0.2f, 0.4f, 0.6f, 1f))));
                Assert.That(r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off, Is.EqualTo(m == PrimMaterial.Glass));
            }
        }

        [Test]
        public void LocallyControlledKeepsVisibleTransform()
        {
            reg.Upsert(Prim(1));
            var tf = reg.GetGameObject(1).transform;
            reg.SetLocallyControlled(1, true);
            Assert.That(reg.IsLocallyControlled(1), Is.True);
            tf.position = new Vector3(9, 9, 9); // the drag tool moves it

            var p = Prim(1);
            p.Position = new Vector3(3, 3, 3);
            p.Color = Color.red;
            reg.Upsert(p, smooth: true);
            Assert.That(tf.position, Is.EqualTo(new Vector3(9, 9, 9)), "transform untouched while dragged");
            Assert.That(reg.SmoothingCount, Is.EqualTo(0));
            Assert.That(reg.TryGet(1, out var d) && d.Position == new Vector3(3, 3, 3), Is.True, "data updated");
            Assert.That(ColourOf(tf.gameObject), Is.EqualTo(new Vector4(1, 0, 0, 1)), "non-transform changes apply");
            Assert.That(events.Last(), Is.EqualTo("changed 1"));

            reg.SetLocallyControlled(1, false);
            Assert.That(reg.IsLocallyControlled(1), Is.False);
            Assert.That(reg.SmoothingCount, Is.EqualTo(1), "eases back to the stored data");
            reg.Tick(1f);
            Assert.That(tf.position, Is.EqualTo(new Vector3(3, 3, 3)));
            Assert.That(reg.SmoothingCount, Is.EqualTo(0));
        }

        [Test]
        public void SmoothUpsertEasesOverTicks()
        {
            reg.Upsert(Prim(1));
            var tf = reg.GetGameObject(1).transform;
            var p = Prim(1);
            p.Position = new Vector3(11, 1, 0);
            reg.Upsert(p, smooth: true);
            Assert.That(tf.position, Is.EqualTo(new Vector3(1, 1, 0)));
            reg.Tick(ObjectRegistry.SmoothDuration * 0.5f);
            Assert.That(tf.position.x, Is.InRange(2f, 10f));
            reg.Tick(ObjectRegistry.SmoothDuration);
            Assert.That(tf.position, Is.EqualTo(new Vector3(11, 1, 0)));
            Assert.That(reg.SmoothingCount, Is.EqualTo(0));

            // A snap during smoothing cancels it.
            p.Position = new Vector3(0, 0, 0);
            reg.Upsert(p, smooth: true);
            p.Position = new Vector3(5, 0, 0);
            reg.Upsert(p);
            Assert.That(reg.SmoothingCount, Is.EqualTo(0));
            Assert.That(tf.position, Is.EqualTo(new Vector3(5, 0, 0)));

            // Removing a smoothing prim leaves the list consistent.
            reg.Upsert(Prim(2));
            p.Position = new Vector3(1, 2, 3);
            reg.Upsert(p, smooth: true);
            var q = Prim(2);
            q.Position = new Vector3(7, 7, 7);
            reg.Upsert(q, smooth: true);
            Assert.That(reg.SmoothingCount, Is.EqualTo(2));
            reg.Remove(1);
            Assert.That(reg.SmoothingCount, Is.EqualTo(1));
            reg.Tick(1f);
            Assert.That(reg.GetGameObject(2).transform.position, Is.EqualTo(new Vector3(7, 7, 7)));
        }

        [Test]
        public void HighlightSetsInstancedProperty()
        {
            reg.Upsert(Prim(1));
            var go = reg.GetGameObject(1);
            reg.SetHighlighted(1, true);
            Assert.That(reg.IsHighlighted(1), Is.True);
            Assert.That(HighlightOf(go), Is.EqualTo(1));
            Assert.That(go.GetComponent<MeshRenderer>().sharedMaterial, Is.SameAs(PrimMaterials.Get(PrimMaterial.Matte)));
            var p = Prim(1);
            p.Color = Color.blue;
            reg.Upsert(p);
            Assert.That(HighlightOf(go), Is.EqualTo(1), "colour change keeps the highlight");
            reg.SetHighlighted(1, false);
            Assert.That(HighlightOf(go), Is.EqualTo(0));
            Assert.That(ColourOf(go), Is.EqualTo(new Vector4(0, 0, 1, 1)));
            reg.SetHighlighted(999, true); // unknown ids are ignored
        }

        [Test]
        public void InputIsNormalised()
        {
            var p = Prim(1);
            p.Rotation = new Quaternion(0, 0, 0, 0);
            p.Scale = Vector3.zero;
            reg.Upsert(p);
            Assert.That(reg.TryGet(1, out var d), Is.True);
            Assert.That(d.Rotation, Is.EqualTo(Quaternion.identity));
            Assert.That(d.Scale, Is.EqualTo(Vector3.one * ObjectRegistry.MinScale));
            Assert.That(reg.GetGameObject(1).transform.rotation, Is.EqualTo(Quaternion.identity));
        }

        [Test]
        public void TickWithNothingSmoothingIsNoOp()
        {
            for (ulong i = 0; i < 100; i++) reg.Upsert(Prim(i));
            Assert.That(reg.SmoothingCount, Is.EqualTo(0));
            reg.Tick(0.016f);
            Assert.That(reg.GetGameObject(5).transform.position, Is.EqualTo(new Vector3(5, 1, 0)));
        }
    }
}
