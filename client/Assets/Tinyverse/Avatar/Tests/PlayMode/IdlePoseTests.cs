using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Avatars.Tests
{
    /// <summary>The standing idle on every body of both styles: grounded, still feet, a calm, natural stance.</summary>
    public class IdlePoseTests
    {
        private AvatarTestScene scene;
        private AvatarStyle styleBefore;

        [SetUp]
        public void SetUp()
        {
            scene = new AvatarTestScene();
            styleBefore = HumanCatalog.Style;
        }

        [TearDown]
        public void TearDown()
        {
            scene.Dispose();
            HumanCatalog.Style = styleBefore;
        }

        private static IEnumerable<BodyPreset> AllPresets() => HumanCatalog.ModernPresets.Concat(HumanCatalog.ClassicPresets);

        private static readonly AvatarPose Standing = new AvatarPose(Vector3.zero, 0f, Vector3.zero, MoveKind.Walk, "idle");

        /// <summary>A figure of <paramref name="preset"/> at the origin facing +Z (its left is -X), settled into the idle.</summary>
        private (HumanAvatarVisual visual, HumanFigure figure) Stand(BodyPreset preset)
        {
            HumanCatalog.Style = preset.Style;
            var remote = scene.Remote(20, "Idle");
            var look = AvatarLook.Default;
            look.BodyPreset = preset.Id;
            remote.SetLook(look);
            remote.SnapTo(Standing);
            var visual = (HumanAvatarVisual)remote.Visual;
            Assert.That(visual.Figure, Is.Not.Null, preset.Id);
            Assert.That(visual.Figure.Preset, Is.SameAs(preset));
            for (int i = 0; i < 30; i++) visual.UpdatePose(Standing, AvatarTestScene.Dt);
            return (visual, visual.Figure);
        }

        private static Transform Bone(HumanFigure f, HumanBodyBones b) => f.Animator.GetBoneTransform(b);

        /// <summary>Calls <paramref name="check"/> every <paramref name="step"/> seconds over one idle loop and a bit.</summary>
        private static void OverTheLoop(HumanAvatarVisual visual, float step, System.Action<float> check)
        {
            int frames = Mathf.RoundToInt(8.5f / AvatarTestScene.Dt);
            int every = Mathf.Max(1, Mathf.RoundToInt(step / AvatarTestScene.Dt));
            for (int i = 0; i <= frames; i++)
            {
                visual.UpdatePose(Standing, AvatarTestScene.Dt);
                if (i % every == 0) check(i * AvatarTestScene.Dt);
            }
        }

        [UnityTest]
        public IEnumerator FeetStayFlatOnTheGroundWithoutSliding()
        {
            foreach (var preset in AllPresets())
            {
                var (visual, f) = Stand(preset);
                string what = $"{preset.Style} {preset.Id}";
                var feet = new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
                var start = feet.Select(b => Bone(f, b).position).ToArray();
                float sole = f.Animator.leftFeetBottomHeight;
                OverTheLoop(visual, 1f / 15f, t =>
                {
                    for (int k = 0; k < 2; k++)
                    {
                        var p = Bone(f, feet[k]).position;
                        Assert.That(p.y - sole, Is.InRange(-0.02f, 0.02f), $"{what} {feet[k]} at {t:0.00} s: on the ground");
                        var drift = new Vector2(p.x - start[k].x, p.z - start[k].z).magnitude;
                        Assert.That(drift, Is.LessThan(0.01f), $"{what} {feet[k]} at {t:0.00} s: does not slide");
                    }
                    float toes = Mathf.Min(Bone(f, HumanBodyBones.LeftToes).position.y, Bone(f, HumanBodyBones.RightToes).position.y);
                    Assert.That(toes, Is.InRange(-0.02f, 0.06f), $"{what} at {t:0.00} s: the toes are down too (flat feet)");
                });
                Object.Destroy(visual.transform.root.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator StanceIsAboutHipWidth()
        {
            foreach (var preset in AllPresets())
            {
                HumanCatalog.Style = preset.Style;
                var rest = HumanFigure.Create(scene.Track(new GameObject("Rest").transform), preset);
                float KneeBend(HumanFigure fig, HumanBodyBones hip, HumanBodyBones knee, HumanBodyBones ankle) =>
                    Vector3.Angle(Bone(fig, knee).position - Bone(fig, hip).position, Bone(fig, ankle).position - Bone(fig, knee).position);
                float restBend = KneeBend(rest, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
                Object.Destroy(rest.transform.parent.gameObject);

                var (visual, f) = Stand(preset);
                string what = $"{preset.Style} {preset.Id}";
                float feet = Vector3.Distance(Bone(f, HumanBodyBones.LeftFoot).position, Bone(f, HumanBodyBones.RightFoot).position);
                float shoulders = Vector3.Distance(Bone(f, HumanBodyBones.LeftUpperArm).position, Bone(f, HumanBodyBones.RightUpperArm).position);
                float hips = Vector3.Distance(Bone(f, HumanBodyBones.LeftUpperLeg).position, Bone(f, HumanBodyBones.RightUpperLeg).position);
                Assert.That(feet, Is.LessThanOrEqualTo(shoulders), $"{what}: feet {feet:0.000} m apart, no wider than the shoulders {shoulders:0.000} m");
                Assert.That(feet, Is.InRange(hips * 0.8f, hips * 1.5f), $"{what}: feet {feet:0.000} m apart, about hip width {hips:0.000} m");
                // Knees nearly straight: the hip, knee and ankle about as much in line as in the rest pose.
                foreach (var (hip, knee, ankle) in new[] { (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot), (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot) })
                {
                    float bend = KneeBend(f, hip, knee, ankle);
                    Assert.That(bend - restBend, Is.LessThan(12f), $"{what} {knee}: bent {bend:0.0} degrees ({restBend:0.0} at rest)");
                }
                // Upright: the head above the hips.
                var lean = Bone(f, HumanBodyBones.Head).position - Bone(f, HumanBodyBones.Hips).position;
                Assert.That(Vector3.Angle(lean, Vector3.up), Is.LessThan(8f), $"{what}: upright");
                Object.Destroy(visual.transform.root.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator ArmsHangRelaxedAtTheSides()
        {
            foreach (var preset in AllPresets())
            {
                HumanCatalog.Style = preset.Style;
                // The rest (T-pose) height of the shoulder joints below the neck, before the figure is animated.
                var rest = HumanFigure.Create(scene.Track(new GameObject("Rest").transform), preset);
                float restDrop = Bone(rest, HumanBodyBones.Neck).position.y - Bone(rest, HumanBodyBones.LeftUpperArm).position.y;
                Object.Destroy(rest.transform.parent.gameObject);

                var (visual, f) = Stand(preset);
                string what = $"{preset.Style} {preset.Id}";
                OverTheLoop(visual, 0.5f, t =>
                {
                    // Hip height: the hip joints (the classic rigs put their Hips bone at the feet).
                    float hipHeight = Mathf.Min(Bone(f, HumanBodyBones.LeftUpperLeg).position.y, Bone(f, HumanBodyBones.RightUpperLeg).position.y);
                    foreach (var (hand, knuckle, upper, side) in new[] { (HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftUpperArm, -1f), (HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightUpperArm, 1f) })
                    {
                        var h = (Bone(f, hand).position + Bone(f, knuckle).position) * 0.5f; // the palm
                        Assert.That(h.y, Is.LessThan(hipHeight), $"{what} {hand} at {t:0.00} s: the palm below the hip joints");
                        Assert.That(h.x * side, Is.GreaterThan(0.1f), $"{what} {hand} at {t:0.00} s: at the side, not in front of the body");
                        Assert.That(Mathf.Abs(h.z), Is.LessThan(0.15f), $"{what} {hand} at {t:0.00} s: hanging, not reaching");
                        // Not shrugged: the shoulder joint stays about as far below the neck as at rest.
                        float drop = Bone(f, HumanBodyBones.Neck).position.y - Bone(f, upper).position.y;
                        Assert.That(drop - restDrop, Is.InRange(-0.025f, 0.04f), $"{what} {upper} at {t:0.00} s: shoulders not raised");
                    }
                });
                Object.Destroy(visual.transform.root.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator HandsDoNotSinkIntoHipsOrThighs()
        {
            var mesh = new Mesh();
            var problems = new List<string>();
            try
            {
                foreach (var preset in AllPresets())
                {
                    var (visual, f) = Stand(preset);
                    int most = 0;
                    float deepest = 0f;
                    OverTheLoop(visual, 1f, t =>
                    {
                        var (inside, worst) = HandIntrusion(f, mesh);
                        most = Mathf.Max(most, inside);
                        deepest = Mathf.Max(deepest, worst);
                    });
                    Debug.Log($"[IdlePoseTests] {preset.Style} {preset.Id}: at most {most} hand vertices inside, deepest {deepest * 1000f:0} mm");
                    if (most > 2) problems.Add($"{preset.Style} {preset.Id}: up to {most} hand vertices inside the body (deepest {deepest * 1000f:0} mm)");
                    Object.Destroy(visual.transform.root.gameObject);
                    yield return null;
                }
            }
            finally
            {
                Object.Destroy(mesh);
            }
            Assert.That(problems, Is.Empty);
        }

        /// <summary>
        /// Counts the hand vertices that lie inside the body: for a hand vertex at (x, y, z) in the figure's space,
        /// a vertex of the torso or legs at about the same height and depth but farther from the middle on that side
        /// means the hand is under the surface there. Returns the count and the deepest such vertex's depth.
        /// </summary>
        private static (int, float) HandIntrusion(HumanFigure f, Mesh baked)
        {
            var root = f.transform;
            var hands = new HashSet<Transform>(Bone(f, HumanBodyBones.LeftHand).GetComponentsInChildren<Transform>()
                .Concat(Bone(f, HumanBodyBones.RightHand).GetComponentsInChildren<Transform>()));
            var excluded = new HashSet<Transform>(new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.Neck }
                .Select(b => Bone(f, b)).Where(b => b != null).SelectMany(b => b.GetComponentsInChildren<Transform>()));
            var handVerts = new List<Vector3>();
            var bodyVerts = new List<Vector3>();
            foreach (var smr in f.Renderers.OfType<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh == null) continue;
                smr.BakeMesh(baked, true);
                var verts = baked.vertices;
                var weights = smr.sharedMesh.boneWeights;
                var bones = smr.bones;
                var toRoot = root.worldToLocalMatrix * Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                for (int i = 0; i < verts.Length && i < weights.Length; i++)
                {
                    var w = weights[i];
                    int best = w.boneIndex0;
                    float bw = w.weight0;
                    if (w.weight1 > bw) { best = w.boneIndex1; bw = w.weight1; }
                    if (w.weight2 > bw) { best = w.boneIndex2; bw = w.weight2; }
                    if (w.weight3 > bw) best = w.boneIndex3;
                    var bone = best >= 0 && best < bones.Length ? bones[best] : null;
                    if (bone == null) continue;
                    var p = toRoot.MultiplyPoint3x4(verts[i]);
                    if (hands.Contains(bone)) handVerts.Add(p);
                    else if (!excluded.Contains(bone)) bodyVerts.Add(p);
                }
            }
            Assert.That(handVerts.Count, Is.GreaterThan(50), "hand vertices found");
            Assert.That(bodyVerts.Count, Is.GreaterThan(500), "body vertices found");
            const float band = 0.015f;
            int inside = 0;
            float worst = 0f;
            foreach (var v in handVerts)
            {
                float depth = 0f;
                foreach (var c in bodyVerts)
                {
                    if (Mathf.Abs(c.y - v.y) > band || Mathf.Abs(c.z - v.z) > band || c.x * v.x <= 0f) continue;
                    depth = Mathf.Max(depth, Mathf.Abs(c.x) - Mathf.Abs(v.x));
                }
                if (depth > 0.004f) { inside++; worst = Mathf.Max(worst, depth); }
            }
            return (inside, worst);
        }

        [UnityTest]
        public IEnumerator StoppingAndStartingBlendWithoutAPop()
        {
            foreach (var preset in new[] { HumanCatalog.ModernPresets[0], HumanCatalog.ClassicPresets[1] })
            {
                var (visual, f) = Stand(preset);
                var walking = new AvatarPose(Vector3.zero, 0f, new Vector3(0f, 0f, 1.2f), MoveKind.Walk, "walk");
                var bones = new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.Head, HumanBodyBones.Hips };
                float Step(AvatarPose pose)
                {
                    var before = bones.Select(b => Bone(f, b).position).ToArray();
                    visual.UpdatePose(pose, AvatarTestScene.Dt);
                    return bones.Select((b, i) => Vector3.Distance(before[i], Bone(f, b).position)).Max();
                }
                // Largest per-frame movement of any end bone while walking steadily: the yardstick.
                for (int i = 0; i < 60; i++) Step(walking);
                float walkMax = 0f;
                for (int i = 0; i < 90; i++) walkMax = Mathf.Max(walkMax, Step(walking));
                float change = 0f;
                for (int i = 0; i < 60; i++) change = Mathf.Max(change, Step(Standing));
                for (int i = 0; i < 60; i++) change = Mathf.Max(change, Step(walking));
                Assert.That(change, Is.LessThanOrEqualTo(walkMax * 1.1f), $"{preset.Style} {preset.Id}: stopping and starting move no bone faster than walking does ({change:0.000} vs {walkMax:0.000} m per frame)");
                float still = 0f;
                for (int i = 0; i < 120; i++) Step(Standing);
                for (int i = 0; i < 60; i++) still = Mathf.Max(still, Step(Standing));
                Assert.That(still, Is.LessThan(0.002f), $"{preset.Style} {preset.Id}: standing, the body barely moves ({still * 1000f:0.0} mm per frame)");
                Object.Destroy(visual.transform.root.gameObject);
                yield return null;
            }
        }
    }
}
