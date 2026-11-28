using System.Collections;
using NUnit.Framework;
using Tinyverse.World;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Bootstrap.Tests
{
    /// <summary>The open-water test wired into <see cref="Tinyverse.Avatars.AvatarWater"/> for a "water" ground.</summary>
    public sealed class OpenWaterTests
    {
        GameObject root;
        ObjectRegistry objects;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("OpenWaterTests");
            objects = new ObjectRegistry(root.transform);
        }

        [TearDown]
        public void TearDown()
        {
            objects.Clear();
            Object.Destroy(root);
        }

        static PrimData Disc(ulong id, Vector3 position, Vector3 scale, bool solid) => new PrimData
        {
            Id = id,
            Name = solid ? "beach" : "shallows",
            Shape = PrimShape.Cylinder,
            Position = position,
            Rotation = Quaternion.identity,
            Scale = scale,
            Color = Color.white,
            Solid = solid,
        };

        [UnityTest]
        public IEnumerator SolidObjectsAreLandAndPaintedWaterIsWater()
        {
            // A solid sand disc around x = 10 and a non-solid disc of shallows around x = -10, a few centimetres thick.
            objects.Upsert(Disc(1, new Vector3(10f, 0.017f, 0f), new Vector3(8f, 0.034f, 8f), true));
            objects.Upsert(Disc(2, new Vector3(-10f, 0.005f, 0f), new Vector3(8f, 0.01f, 8f), false));
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();

            Assert.That(OpenWater.At(new Vector3(0f, 0f, 0f), objects), Is.True, "the open sea");
            Assert.That(OpenWater.At(new Vector3(-10f, 0f, 0f), objects), Is.True, "painted shallows are still water");
            Assert.That(OpenWater.At(new Vector3(10f, 0.034f, 0f), objects), Is.False, "standing on the sand");
            Assert.That(OpenWater.At(new Vector3(0f, 2f, 0f), objects), Is.False, "in the air over the sea");
            Assert.That(OpenWater.At(new Vector3(10f, 2f, 0f), objects), Is.False, "in the air over the sand");

            Assert.That(OpenWater.For("water", objects), Is.Not.Null);
            Assert.That(OpenWater.For("grass", objects), Is.Null, "no sea, nobody swims");
            Assert.That(OpenWater.For("", objects), Is.Null);
        }
    }
}
