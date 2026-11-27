using NUnit.Framework;
using UnityEngine;

namespace Tinyverse.Building.Tests
{
    public class GizmoGeometryTests
    {
        static readonly Vector3 center = new Vector3(1, 2, 3);
        static readonly Vector3 cam = new Vector3(-6, 9, -8);
        const float size = 1.5f;

        static Ray At(Vector3 p) => new Ray(cam, (p - cam).normalized);

        [Test]
        public void PicksMoveArrowsAndPlanes()
        {
            for (int i = 0; i < 3; i++)
            {
                var a = BuildMath.Axis(i);
                var h = GizmoGeometry.Pick(BuildTool.Move, center, Quaternion.identity, size, At(center + a * (0.75f * size)));
                Assert.That(h, Is.EqualTo((BuildHandle)((int)BuildHandle.MoveX + i)));
            }
            Assert.That(GizmoGeometry.Pick(BuildTool.Move, center, Quaternion.identity, size, At(center + new Vector3(0.37f, 0.37f, 0) * size)), Is.EqualTo(BuildHandle.MoveXY));
            Assert.That(GizmoGeometry.Pick(BuildTool.Move, center, Quaternion.identity, size, At(center + new Vector3(0.37f, 0, 0.37f) * size)), Is.EqualTo(BuildHandle.MoveXZ));
            Assert.That(GizmoGeometry.Pick(BuildTool.Move, center, Quaternion.identity, size, At(center + new Vector3(0, 0.37f, 0.37f) * size)), Is.EqualTo(BuildHandle.MoveYZ));
            Assert.That(GizmoGeometry.Pick(BuildTool.Move, center, Quaternion.identity, size, At(center + new Vector3(3, -3, 3) * size)), Is.EqualTo(BuildHandle.None));
        }

        [Test]
        public void PicksRings()
        {
            float d = size / Mathf.Sqrt(2f);
            Assert.That(GizmoGeometry.Pick(BuildTool.Rotate, center, Quaternion.identity, size, At(center + new Vector3(d, 0, d))), Is.EqualTo(BuildHandle.RotateY));
            Assert.That(GizmoGeometry.Pick(BuildTool.Rotate, center, Quaternion.identity, size, At(center + new Vector3(0, d, -d))), Is.EqualTo(BuildHandle.RotateX));
            Assert.That(GizmoGeometry.Pick(BuildTool.Rotate, center, Quaternion.identity, size, At(center + new Vector3(-d, -d, 0))), Is.EqualTo(BuildHandle.RotateZ));
            Assert.That(GizmoGeometry.Pick(BuildTool.Rotate, center, Quaternion.identity, size, At(center)), Is.EqualTo(BuildHandle.None));
        }

        [Test]
        public void PicksScaleHandlesAlongTheirRotatedAxes()
        {
            var rot = Quaternion.Euler(0, 45, 0);
            for (int i = 0; i < 3; i++)
            {
                var a = rot * BuildMath.Axis(i);
                var h = GizmoGeometry.Pick(BuildTool.Scale, center, rot, size, At(center + a * (GizmoGeometry.ScaleHandleDistance * size)));
                Assert.That(h, Is.EqualTo((BuildHandle)((int)BuildHandle.ScaleX + i)));
            }
            Assert.That(GizmoGeometry.Pick(BuildTool.Scale, center, rot, size, At(center)), Is.EqualTo(BuildHandle.ScaleUniform));
            Assert.That(GizmoGeometry.Pick(BuildTool.Scale, center, rot, size, At(center + rot * Vector3.up * (0.9f * size)), hideScaleY: true),
                Is.Not.EqualTo(BuildHandle.ScaleY));
        }

        [Test]
        public void RingDistanceFindsNearestPoint()
        {
            var ray = new Ray(new Vector3(2, 10, 0), Vector3.down);
            float d = GizmoGeometry.RingDistance(ray, Vector3.zero, Vector3.up, 2f, out float t, out var p);
            Assert.That(d, Is.LessThan(0.01f));
            Assert.That(t, Is.EqualTo(10f).Within(0.05f));
            Assert.That((p - new Vector3(2, 0, 0)).magnitude, Is.LessThan(0.05f));
        }
    }
}
