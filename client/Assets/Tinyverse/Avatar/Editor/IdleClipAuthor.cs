using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Tinyverse.Avatars.Editor
{
    /// <summary>
    /// Writes <c>Animations/Idle.anim</c>, our own standing idle (no third-party source): an upright, relaxed pose,
    /// feet about hip-width apart, knees nearly straight, arms hanging at the sides, with slow breathing and a slight
    /// shift of weight from foot to foot. It is a humanoid muscle clip, so it retargets onto every body like the
    /// library clips; the feet are pinned by IK goals that stay put while the body moves, so they do not slide.
    /// Every curve is a sum of sines whose periods divide <see cref="Length"/>, so the loop has no seam.
    /// <code>Unity -batchmode -nographics -quit -projectPath client -executeMethod Tinyverse.Avatars.Editor.IdleClipAuthor.Author</code>
    /// </summary>
    public static class IdleClipAuthor
    {
        /// <summary>Loop length, seconds: one weight shift (left and back) and two breaths.</summary>
        public const float Length = 8f;

        private const int SampleRate = 30;
        private const string Target = AvatarAssetTool.AnimationsFolder + "/Idle.anim";

        /// <summary>The body the pose is measured on (feet height, stance width, IK goal frames).</summary>
        private const string ReferenceModel = AvatarAssetTool.ModernModels + "/Body_masculine.fbx";

        /// <summary>A clip with foot IK goals, for learning the reference body's goal frame.</summary>
        private const string CalibrationClip = AvatarAssetTool.AnimationsFolder + "/Walk.anim";

        /// <summary>
        /// The resting pose, in Unity muscle units (see <see cref="HumanTrait.MuscleName"/>). "Both" entries apply to
        /// the left and right muscle of that name. Leg spread and foot pitch are solved for, see <see cref="Author"/>.
        /// </summary>
        private static readonly (string muscle, float value)[] RestPose =
        {
            ("Spine Front-Back", 0.08f), ("Chest Front-Back", 0.02f), ("UpperChest Front-Back", 0.0f),
            ("Neck Nod Down-Up", -0.1f), ("Head Nod Down-Up", -0.25f),
            ("Both Upper Leg Front-Back", 0.6f), ("Both Upper Leg Twist In-Out", 0.12f),
            ("Both Lower Leg Stretch", 1f), ("Both Lower Leg Twist In-Out", 0f), ("Both Foot Twist In-Out", 0f),
            ("Both Toes Up-Down", 0f),
            ("Both Shoulder Down-Up", -0.12f), ("Both Shoulder Front-Back", 0.06f),
            ("Both Arm Down-Up", -0.57f), ("Both Arm Front-Back", 0.24f), ("Both Arm Twist In-Out", -0.1f),
            ("Both Forearm Stretch", 0.95f), ("Both Forearm Twist In-Out", -0.25f),
            ("Both Hand Down-Up", 0.0f), ("Both Hand In-Out", 0.05f),
            ("Both Thumb 1 Stretched", 0.1f), ("Both Thumb Spread", -0.3f), ("Both Thumb 2 Stretched", 0.3f), ("Both Thumb 3 Stretched", 0.3f),
            ("Both Index 1 Stretched", 0.25f), ("Both Index Spread", -0.15f), ("Both Index 2 Stretched", 0.25f), ("Both Index 3 Stretched", 0.45f),
            ("Both Middle 1 Stretched", 0.2f), ("Both Middle Spread", -0.15f), ("Both Middle 2 Stretched", 0.2f), ("Both Middle 3 Stretched", 0.4f),
            ("Both Ring 1 Stretched", 0.15f), ("Both Ring Spread", -0.15f), ("Both Ring 2 Stretched", 0.15f), ("Both Ring 3 Stretched", 0.35f),
            ("Both Little 1 Stretched", 0.1f), ("Both Little Spread", -0.2f), ("Both Little 2 Stretched", 0.1f), ("Both Little 3 Stretched", 0.3f),
        };

        /// <summary>
        /// Slow motion on top of the pose: (muscle, amplitude, cycles per loop, phase in cycles). Breathing is two
        /// cycles per loop (4 s); the weight shift one (8 s). "Left"/"Right" entries with opposite signs make the
        /// sway lean the body toward the loaded leg.
        /// </summary>
        private static readonly (string muscle, float amplitude, int cycles, float phase)[] Motion =
        {
            // Breathing: the chest rises and opens, the shoulders follow a little.
            ("Chest Front-Back", -0.025f, 2, 0f), ("UpperChest Front-Back", -0.03f, 2, 0f),
            ("Both Shoulder Down-Up", 0.015f, 2, 0f), ("Neck Nod Down-Up", 0.015f, 2, 0f),
            ("Both Arm Front-Back", 0.01f, 2, 0.1f),
            // Weight shift: the spine and head counter the hip's lean, the arms swing a touch.
            ("Spine Left-Right", 0.03f, 1, 0f), ("Chest Left-Right", 0.02f, 1, 0f), ("Head Tilt Left-Right", -0.025f, 1, 0.05f),
            ("Left Arm Down-Up", 0.01f, 1, 0f), ("Right Arm Down-Up", -0.01f, 1, 0f),
            ("Head Turn Left-Right", 0.02f, 1, 0.3f),
        };

        /// <summary>Weight shift: sideways body travel (normalised units, about 1.6 cm) and roll (degrees).</summary>
        private const float SwayX = 0.015f, SwayRoll = 1.2f, SwayDip = 0.002f;

        /// <summary>How far the body is raised above the pose whose feet define the goals, metres (straighter knees).</summary>
        private const float BodyLift = 0.008f;

        /// <summary>Feet apart, centre to centre, as a multiple of the distance between the hip joints.</summary>
        private const float StanceWidth = 1.12f;

        /// <summary>Height of the toe joint above the sole, metres on the reference body (a flat foot).</summary>
        private const float ToeAboveSole = 0.009f;

        public static void Author()
        {
            var report = new StringBuilder();
            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ReferenceModel);
            var go = Object.Instantiate(modelAsset);
            try
            {
                var animator = go.GetComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                float s = animator.humanScale;
                var (goalRot, goalOffset) = Calibrate(animator);
                report.AppendLine($"reference {ReferenceModel}: humanScale {s:0.0000}, goal rotation {goalRot.eulerAngles:F1}, offset {goalOffset:F4}");

                using var handler = new HumanPoseHandler(animator.avatar, go.transform);
                var muscles = RestMuscles();
                var pose = new HumanPose { bodyPosition = new Vector3(0f, 1f, 0f), bodyRotation = Quaternion.identity, muscles = muscles };
                var lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
                var lToes = animator.GetBoneTransform(HumanBodyBones.LeftToes);
                var lHip = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                var rHip = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);

                void Apply() { handler.SetHumanPose(ref pose); }

                // Leg spread: feet apart by StanceWidth times the hip joints' distance.
                int inOutL = Muscle("Left Upper Leg In-Out"), inOutR = Muscle("Right Upper Leg In-Out");
                Apply();
                float hipWidth = Vector3.Distance(lHip.position, rHip.position);
                float spread = Solve(-0.6f, 0.6f, v =>
                {
                    pose.muscles[inOutL] = pose.muscles[inOutR] = v;
                    Apply();
                    return (rFoot.position.x - lFoot.position.x) - hipWidth * StanceWidth; // left is -x
                });
                // Foot pitch: flat soles (the toe joint just above the sole).
                int upDownL = Muscle("Left Foot Up-Down"), upDownR = Muscle("Right Foot Up-Down");
                float soleDrop = animator.leftFeetBottomHeight; // foot joint above the sole
                float pitch = Solve(-0.8f, 0.8f, v =>
                {
                    pose.muscles[upDownL] = pose.muscles[upDownR] = v;
                    Apply();
                    return (lFoot.position.y - lToes.position.y) - (soleDrop - ToeAboveSole);
                });
                // Height: soles on the ground.
                Apply();
                float sole = Mathf.Min(lFoot.position.y, rFoot.position.y) - soleDrop;
                pose.bodyPosition.y -= sole / s;
                Apply();
                // The goals are taken here; lifting the body a little afterwards lets the foot IK straighten the
                // knees fully (Unity's solver otherwise keeps a slight bend for a leg at full length).
                var footGoals = new[] { GoalOf(lFoot, goalRot, goalOffset), GoalOf(rFoot, goalRot, goalOffset) };
                pose.bodyPosition.y += BodyLift / s;
                var restBody = pose.bodyPosition;
                muscles = (float[])pose.muscles.Clone();
                report.AppendLine($"hip width {hipWidth:0.000} m, leg in-out {spread:0.000}, foot up-down {pitch:0.000}, body height {restBody.y:0.0000}");
                report.AppendLine($"feet at {lFoot.position:F3} {rFoot.position:F3}, toes {lToes.position:F3}");
                var knee = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg).position;
                report.AppendLine($"left hip {lHip.position:F3} knee {knee:F3} ankle {lFoot.position:F3}: bend {Vector3.Angle(knee - lHip.position, lFoot.position - knee):0.0} degrees");


                var clip = new AnimationClip { name = "Idle", frameRate = SampleRate };
                var curves = new Dictionary<string, List<Keyframe>>();
                void Key(string attr, float t, float v, float slope)
                {
                    if (!curves.TryGetValue(attr, out var keys)) curves[attr] = keys = new List<Keyframe>();
                    keys.Add(new Keyframe(t, v, slope, slope));
                }
                int frames = Mathf.RoundToInt(Length * SampleRate);
                const float h = 1e-3f;
                for (int f = 0; f <= frames; f++)
                {
                    float t = f * Length / frames;
                    var m = MusclesAt(muscles, t);
                    var mNext = MusclesAt(muscles, t + h);
                    for (int i = 0; i < m.Length; i++) Key(ClipName(HumanTrait.MuscleName[i]), t, m[i], (mNext[i] - m[i]) / h);
                    var (bp, br) = BodyAt(restBody, t);
                    var (bpN, brN) = BodyAt(restBody, t + h);
                    KeyVector("RootT", bp, bpN);
                    KeyQuat("RootQ", br, brN);
                    for (int side = 0; side < 2; side++)
                    {
                        string n = side == 0 ? "LeftFoot" : "RightFoot";
                        var (gp, gq) = StoredGoal(footGoals[side], bp, br, s);
                        var (gpN, gqN) = StoredGoal(footGoals[side], bpN, brN, s);
                        KeyVector(n + "T", gp, gpN);
                        KeyQuat(n + "Q", gq, gqN);
                    }

                    void KeyVector(string name, Vector3 v, Vector3 vN)
                    {
                        Key(name + ".x", t, v.x, (vN.x - v.x) / h);
                        Key(name + ".y", t, v.y, (vN.y - v.y) / h);
                        Key(name + ".z", t, v.z, (vN.z - v.z) / h);
                    }
                    void KeyQuat(string name, Quaternion q, Quaternion qN)
                    {
                        Key(name + ".x", t, q.x, (qN.x - q.x) / h);
                        Key(name + ".y", t, q.y, (qN.y - q.y) / h);
                        Key(name + ".z", t, q.z, (qN.z - q.z) / h);
                        Key(name + ".w", t, q.w, (qN.w - q.w) / h);
                    }
                }
                foreach (var kv in curves)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), kv.Key), new AnimationCurve(kv.Value.ToArray()));

                // The same loop and root settings as the library clips.
                var walk = AssetDatabase.LoadAssetAtPath<AnimationClip>(CalibrationClip);
                var settings = AnimationUtility.GetAnimationClipSettings(walk);
                settings.startTime = 0f;
                settings.stopTime = Length;
                settings.loopTime = true;
                settings.loopBlend = false; // the curves already meet; Unity's loop pose blending would only blur them
                settings.cycleOffset = 0f;
                AnimationUtility.SetAnimationClipSettings(clip, settings);

                AssetDatabase.DeleteAsset(Target);
                AssetDatabase.CreateAsset(clip, Target);
                AssetDatabase.SaveAssets();
                report.AppendLine($"wrote {Target}: {clip.length:0.00} s, human {clip.humanMotion}, {curves.Count} curves, {frames + 1} keys each");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
            Debug.Log("[IdleClipAuthor]\n" + report);
        }

        // ---------------------------------------------------------------- pose

        private static int Muscle(string name)
        {
            int i = Array.IndexOf(HumanTrait.MuscleName, name);
            if (i < 0) throw new ArgumentException("no muscle " + name);
            return i;
        }

        private static IEnumerable<string> Sides(string muscle) =>
            muscle.StartsWith("Both ") ? new[] { "Left " + muscle.Substring(5), "Right " + muscle.Substring(5) } : new[] { muscle };

        private static float[] RestMuscles()
        {
            var m = new float[HumanTrait.MuscleCount];
            foreach (var (muscle, value) in RestPose)
                foreach (var name in Sides(muscle))
                    m[Muscle(name)] = value;
            return m;
        }

        private static float Wave(float t, int cycles, float phase) => Mathf.Sin(2f * Mathf.PI * (cycles * t / Length + phase));

        private static float[] MusclesAt(float[] rest, float t)
        {
            var m = (float[])rest.Clone();
            foreach (var (muscle, amplitude, cycles, phase) in Motion)
                foreach (var name in Sides(muscle))
                    m[Muscle(name)] += amplitude * Wave(t, cycles, phase);
            return m;
        }

        /// <summary>Body (centre of mass) position and rotation, normalised: sways toward one foot and back.</summary>
        private static (Vector3, Quaternion) BodyAt(Vector3 rest, float t)
        {
            float sway = Wave(t, 1, 0f);
            // Lowest when over a foot (|sway| = 1), so a dip of twice the weight-shift frequency.
            var p = rest + new Vector3(SwayX * sway, -SwayDip * sway * sway, 0f);
            return (p, Quaternion.Euler(0f, 0f, -SwayRoll * sway));
        }

        /// <summary>The goal of a foot in its world (reference body) pose: position and rotation.</summary>
        private static (Vector3, Quaternion) GoalOf(Transform foot, Quaternion goalRot, Vector3 goalOffset)
        {
            var q = foot.rotation * goalRot;
            return (foot.position + q * goalOffset, q);
        }

        /// <summary>A goal as stored in a humanoid clip: normalised and relative to the body.</summary>
        private static (Vector3, Quaternion) StoredGoal((Vector3 pos, Quaternion rot) goal, Vector3 body, Quaternion bodyRot, float scale)
        {
            var inv = Quaternion.Inverse(bodyRot);
            return (inv * (goal.pos / scale - body), inv * goal.rot);
        }

        /// <summary>Clip property name of a muscle: finger muscles are written "LeftHand.Index.1 Stretched".</summary>
        private static string ClipName(string muscle)
        {
            var m = Regex.Match(muscle, @"^(Left|Right) (Thumb|Index|Middle|Ring|Little) (.+)$");
            return m.Success ? $"{m.Groups[1].Value}Hand.{m.Groups[2].Value}.{m.Groups[3].Value}" : muscle;
        }

        /// <summary>Bisection for the root of a monotonic function on [lo, hi].</summary>
        private static float Solve(float lo, float hi, Func<float, float> f)
        {
            float flo = f(lo);
            for (int i = 0; i < 40; i++)
            {
                float mid = 0.5f * (lo + hi);
                float fm = f(mid);
                if (Mathf.Sign(fm) == Mathf.Sign(flo)) { lo = mid; flo = fm; }
                else hi = mid;
            }
            float x = 0.5f * (lo + hi);
            f(x);
            return x;
        }

        // ---------------------------------------------------------------- goal frame

        /// <summary>
        /// The reference body's foot goal frame, learnt from a clip whose goals Unity's importer computed: plays it
        /// with foot IK (so the foot sits on its goal) and compares the foot bone with the goal. Returns the goal's
        /// rotation relative to the foot bone and the goal's offset from the bone in the goal's frame (metres).
        /// </summary>
        private static (Quaternion, Vector3) Calibrate(Animator animator)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(CalibrationClip);
            float C(string a, float t) => AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), a)).Evaluate(t);
            var graph = PlayableGraph.Create("Idle calibration");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(graph, "Pose", animator);
                var p = AnimationClipPlayable.Create(graph, clip);
                p.SetApplyFootIK(true);
                output.SetSourcePlayable(p);
                graph.Play();
                float t = 0f; // the left foot is planted at the start of the walk
                p.SetTime(t);
                graph.Evaluate(0f);
                var foot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rootT = new Vector3(C("RootT.x", t), C("RootT.y", t), C("RootT.z", t));
                var rootQ = new Quaternion(C("RootQ.x", t), C("RootQ.y", t), C("RootQ.z", t), C("RootQ.w", t));
                var goalT = new Vector3(C("LeftFootT.x", t), C("LeftFootT.y", t), C("LeftFootT.z", t));
                var goalQ = new Quaternion(C("LeftFootQ.x", t), C("LeftFootQ.y", t), C("LeftFootQ.z", t), C("LeftFootQ.w", t));
                var worldGoal = (rootT + rootQ * goalT) * animator.humanScale;
                var worldRot = rootQ * goalQ;
                var rel = Quaternion.Inverse(foot.rotation) * worldRot;
                var offset = Quaternion.Inverse(worldRot) * (worldGoal - foot.position);
                return (rel, offset);
            }
            finally
            {
                graph.Destroy();
            }
        }
    }
}
