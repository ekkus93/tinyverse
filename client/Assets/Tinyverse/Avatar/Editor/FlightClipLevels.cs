using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tinyverse.Avatars.Editor
{
    /// <summary>
    /// Raises the two flying clips so the body is drawn above the avatar's position (its feet). They are the
    /// animation library's swimming clips (<c>Swim_Idle_Loop</c>, <c>Swim_Fwd_Loop</c>), authored low in the
    /// water: as extracted, the hovering figure hangs about 1.3 m below the avatar and the flying one lies with its
    /// limbs half a metre under it. Each clip's root height offset (the importer's Root Transform Position (Y)
    /// Offset, <see cref="AnimationClipSettings.level"/>) is set from the clips themselves. Hovering: measured on
    /// every body of both styles over the whole loop, the lowest skinned vertex stays
    /// <see cref="HoverClearance"/> (to 5 mm, for the body that comes closest) above the avatar's position. Flying
    /// forward: the body's centre of mass, on average, at <see cref="FlyCentre"/>, the middle of the avatar's
    /// capsule, so the flying body lies where the avatar collides and starting or stopping lowers or lifts it only
    /// a little; should that bring a limb within <see cref="FlyClearance"/> of the position, the clip is lifted
    /// until none is. The offset is in the humanoid's normalised units, so it scales with each body, and the mixer blends it
    /// with the clip weights, so going from standing to hovering lifts the figure smoothly.
    /// <see cref="AvatarAssetTool.ExtractClips"/> runs this after extracting the clips; by hand:
    /// <code>Unity -batchmode -nographics -quit -projectPath client -executeMethod Tinyverse.Avatars.Editor.FlightClipLevels.Apply</code>
    /// </summary>
    public static class FlightClipLevels
    {
        /// <summary>Least gap under the hovering figure, metres (it reads as floating, not standing).</summary>
        public const float HoverClearance = 0.08f;

        /// <summary>Least gap under the figure flying forward (its hands, knees and toes), metres.</summary>
        public const float FlyClearance = 0.04f;

        /// <summary>How far above its clearance the lowest of all bodies may end up, metres.</summary>
        private const float Tolerance = 0.005f;

        public static IEnumerable<BodyPreset> AllPresets() => HumanCatalog.ModernPresets.Concat(HumanCatalog.ClassicPresets);

        private static AnimationClip Load(HumanClip id)
        {
            var clip = Resources.Load<AnimationClip>(HumanCatalog.AnimationsFolder + id);
            if (clip == null) throw new InvalidOperationException("clip " + id + " missing");
            return clip;
        }

        /// <summary>Sets the root height offset of the flying clips (see the class summary) and saves them.</summary>
        public static void Apply()
        {
            var scratch = new Mesh();
            var log = new StringBuilder("[FlightClipLevels]\n");
            try
            {
                var hover = Load(HumanClip.Hover);
                var fly = Load(HumanClip.FlyForward);
                Fit(hover, HumanClip.Hover, HoverClearance, scratch, log);

                // Flying forward: the body's centre of mass, on average over the loop, at the middle of the
                // avatar's capsule (the drawn root height is (RootT.y − offset) × human scale).
                var settings = AnimationUtility.GetAnimationClipSettings(fly);
                float scale = AllPresets().Average(p => HumanScale(p));
                settings.level = MeanRootHeight(fly) - FlyCentre / scale;
                AnimationUtility.SetAnimationClipSettings(fly, settings);
                EditorUtility.SetDirty(fly);
                var sweeps = Sweeps(HumanClip.FlyForward, scratch, log, settings.level);
                // Should a body reach below its clearance that way, lift the clip until none does.
                if (sweeps.Any(x => x.s.min < FlyClearance)) Fit(fly, HumanClip.FlyForward, FlyClearance, scratch, log);
                AssetDatabase.SaveAssets();
                // Swimming draws the same clips at their authored depth by undoing these offsets at run time.
                float h = AnimationUtility.GetAnimationClipSettings(hover).level, v = AnimationUtility.GetAnimationClipSettings(fly).level;
                if (h != HumanAnimationPlayer.HoverClipLevel || v != HumanAnimationPlayer.FlyForwardClipLevel)
                    Debug.LogWarning($"[FlightClipLevels] update HumanAnimationPlayer: HoverClipLevel = {h:R}f, FlyForwardClipLevel = {v:R}f");
            }
            finally
            {
                Object.DestroyImmediate(scratch);
            }
            Debug.Log(log.ToString());
        }

        /// <summary>Height of the flying body's centre of mass above the avatar's position: the middle of its capsule, metres.</summary>
        public const float FlyCentre = LocalAvatar.ControllerHeight * 0.5f;

        private static float HumanScale(BodyPreset preset)
        {
            var before = HumanCatalog.Style;
            HumanCatalog.Style = preset.Style;
            var root = new GameObject("Measure");
            try
            {
                var f = HumanFigure.Create(root.transform, preset);
                return f.Animator.humanScale;
            }
            finally
            {
                Object.DestroyImmediate(root);
                HumanCatalog.Style = before;
            }
        }

        /// <summary>The mean of a humanoid clip's root (centre of mass) height curve over its loop, normalised units.</summary>
        private static float MeanRootHeight(AnimationClip clip)
        {
            var binding = AnimationUtility.GetCurveBindings(clip).First(b => b.propertyName == "RootT.y");
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            const int n = 400;
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += curve.Evaluate(clip.length * i / n);
            return sum / n;
        }

        private static List<(BodyPreset p, (float min, float max, float scale) s)> Sweeps(HumanClip id, Mesh scratch, StringBuilder log, float level)
        {
            var sweeps = AllPresets().Select(p => (p, s: Sweep(p, id, scratch))).ToList();
            log.AppendLine($"{id} offset {level:0.0000}: " +
                           string.Join(", ", sweeps.Select(x => $"{x.p.Style} {x.p.Id} lowest {x.s.min:0.000}..{x.s.max:0.000}")));
            return sweeps;
        }

        /// <summary>
        /// Sets the offset of <paramref name="clip"/> so that the lowest point of the body that comes closest is
        /// <paramref name="clearance"/> (to within <see cref="Tolerance"/>) above the avatar's position.
        /// </summary>
        private static void Fit(AnimationClip clip, HumanClip id, float clearance, Mesh scratch, StringBuilder log)
        {
            // The figure moves linearly with the offset: down by offset × human scale metres (a positive offset
            // lowers the body). Start from that and refine by the secant method.
            float prevLevel = float.NaN, prevRaise = float.NaN;
            for (int round = 0; round < 6; round++)
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                var sweeps = Sweeps(id, scratch, log, settings.level);
                float worst = sweeps.Min(x => x.s.min - clearance);
                if (worst >= -1e-4f && worst <= Tolerance) return;
                // How far (normalised) the body that is lowest relative to its clearance must rise.
                float raise = sweeps.Max(x => (clearance - x.s.min) / x.s.scale);
                float slope = float.IsNaN(prevLevel) || Mathf.Abs(settings.level - prevLevel) < 1e-6f
                    ? 1f
                    : (raise - prevRaise) / (settings.level - prevLevel);
                prevLevel = settings.level;
                prevRaise = raise;
                // Aim slightly inside the band so rounding does not leave it just below.
                settings.level -= (raise + Tolerance * 0.3f) / slope;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                EditorUtility.SetDirty(clip);
            }
            throw new InvalidOperationException($"{id}: the offset did not converge (see the log)\n{log}");
        }

        /// <summary>Logs the lowest point of every body in every clip.</summary>
        public static void Measure()
        {
            var scratch = new Mesh();
            var sb = new StringBuilder("[FlightClipLevels] lowest point over each loop, metres above the avatar's position\n");
            foreach (var preset in AllPresets())
                foreach (HumanClip clip in Enum.GetValues(typeof(HumanClip)))
                {
                    var (min, max, scale) = Sweep(preset, clip, scratch);
                    sb.AppendLine($"{preset.Style} {preset.Id} {clip}: {min:0.000} .. {max:0.000} (human scale {scale:0.000})");
                }
            Object.DestroyImmediate(scratch);
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// A figure of <paramref name="preset"/> at the origin playing only <paramref name="clip"/>, sampled every
        /// 1/60 s over one loop: the lowest and highest of its lowest points, and the body's human scale.
        /// </summary>
        public static (float min, float max, float scale) Sweep(BodyPreset preset, HumanClip clip, Mesh scratch)
        {
            var before = HumanCatalog.Style;
            HumanCatalog.Style = preset.Style;
            var root = new GameObject("Measure");
            try
            {
                var f = HumanFigure.Create(root.transform, preset);
                if (f == null) throw new InvalidOperationException($"no figure for {preset.Style} {preset.Id}");
                var look = AvatarLook.Default;
                look.BodyPreset = preset.Id;
                f.ApplyLook(look);
                var t = HumanAnimationTargets.Only(clip);
                var c = Resources.Load<AnimationClip>(HumanCatalog.AnimationsFolder + HumanAnimationPlayer.AssetName(clip));
                int frames = Mathf.CeilToInt(c.length * 60f);
                float min = float.MaxValue, max = float.MinValue;
                f.Player.Update(t, 0f);
                for (int i = 0; i <= frames; i++)
                {
                    float low = f.LowestPoint(scratch);
                    min = Mathf.Min(min, low);
                    max = Mathf.Max(max, low);
                    f.Player.Update(t, 1f / 60f);
                }
                return (min, max, f.Animator.humanScale);
            }
            finally
            {
                Object.DestroyImmediate(root);
                HumanCatalog.Style = before;
            }
        }
    }
}
