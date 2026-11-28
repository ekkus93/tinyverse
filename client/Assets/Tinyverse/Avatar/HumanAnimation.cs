using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// The clips a human figure plays, in mixer order. <see cref="SwimIdle"/> and <see cref="SwimForward"/> play
    /// the same clip assets as <see cref="Hover"/> and <see cref="FlyForward"/> (the animation library's swimming
    /// clips), drawn at the depth they were authored at instead of lifted clear of the avatar's position.
    /// </summary>
    public enum HumanClip
    {
        Idle,
        Walk,
        Jog,
        Sprint,
        Hover,
        FlyForward,
        Fall,
        SwimIdle,
        SwimForward,
    }

    /// <summary>What a human figure should be doing: a weight per clip (summing to 1) and the playback rates.</summary>
    public struct HumanAnimationTargets
    {
        public const int ClipCount = 9;

        public float Idle, Walk, Jog, Sprint, Hover, FlyForward, Fall, SwimIdle, SwimForward;

        /// <summary>Playback rate of each stride clip (1 = as authored), so feet roughly match the ground speed.</summary>
        public float WalkRate, JogRate, SprintRate, FlyRate, SwimRate;

        public float this[HumanClip clip]
        {
            get
            {
                switch (clip)
                {
                    case HumanClip.Idle: return Idle;
                    case HumanClip.Walk: return Walk;
                    case HumanClip.Jog: return Jog;
                    case HumanClip.Sprint: return Sprint;
                    case HumanClip.Hover: return Hover;
                    case HumanClip.FlyForward: return FlyForward;
                    case HumanClip.SwimIdle: return SwimIdle;
                    case HumanClip.SwimForward: return SwimForward;
                    default: return Fall;
                }
            }
        }

        /// <summary>The weight of the two swimming clips together.</summary>
        public float Swim => SwimIdle + SwimForward;

        /// <summary>Targets that play <paramref name="clip"/> alone, at its authored rate.</summary>
        public static HumanAnimationTargets Only(HumanClip clip)
        {
            var t = new HumanAnimationTargets { WalkRate = 1f, JogRate = 1f, SprintRate = 1f, FlyRate = 1f, SwimRate = 1f };
            switch (clip)
            {
                case HumanClip.Idle: t.Idle = 1f; break;
                case HumanClip.Walk: t.Walk = 1f; break;
                case HumanClip.Jog: t.Jog = 1f; break;
                case HumanClip.Sprint: t.Sprint = 1f; break;
                case HumanClip.Hover: t.Hover = 1f; break;
                case HumanClip.FlyForward: t.FlyForward = 1f; break;
                case HumanClip.SwimIdle: t.SwimIdle = 1f; break;
                case HumanClip.SwimForward: t.SwimForward = 1f; break;
                default: t.Fall = 1f; break;
            }
            return t;
        }

        /// <summary>The clip with the largest weight.</summary>
        public HumanClip Dominant
        {
            get
            {
                var best = HumanClip.Idle;
                float w = -1f;
                for (int i = 0; i < ClipCount; i++)
                {
                    float v = this[(HumanClip)i];
                    if (v > w) { w = v; best = (HumanClip)i; }
                }
                return best;
            }
        }

        public override string ToString() =>
            $"idle {Idle:0.00} walk {Walk:0.00}@{WalkRate:0.00} jog {Jog:0.00}@{JogRate:0.00} sprint {Sprint:0.00}@{SprintRate:0.00} " +
            $"hover {Hover:0.00} fly {FlyForward:0.00} fall {Fall:0.00} swim {SwimIdle:0.00}/{SwimForward:0.00}@{SwimRate:0.00}";
    }

    /// <summary>
    /// Chooses the animation from an avatar's state. Ground movement blends idle → walk → jog → sprint by
    /// horizontal speed, each clip played at a rate that keeps the feet near the ground speed. With the server's
    /// limits, walking (4 m/s) is mostly a light jog and running (8 m/s) a sprint; slower movement walks. Flying
    /// blends hovering and flying forward by speed; falling has its own clip; in open water (see
    /// <see cref="AvatarWater"/>) an avatar that is not flying treads water or swims forward by speed. Works the
    /// same for the local avatar and for remote ones (from their replicated velocity and hint).
    /// </summary>
    public static class HumanAnimationLogic
    {
        /// <summary>Ground speeds (m/s) the clips were authored at, measured from the library's root motion.</summary>
        public const float WalkClipSpeed = 0.95f, JogClipSpeed = 4.83f, SprintClipSpeed = 7.73f;

        /// <summary>Ground speeds (m/s) at which each clip is fully weighted.</summary>
        public const float WalkPoint = 1.3f, JogPoint = 4.0f, SprintPoint = 7.6f;

        /// <summary>Below this horizontal speed (m/s) an avatar stands.</summary>
        public const float StandSpeed = 0.15f;

        /// <summary>
        /// The animation for <paramref name="hint"/> and <paramref name="velocity"/>. <paramref name="inWater"/>: the
        /// avatar is in open water, so unless it flies it swims (the hint "swim" means the same).
        /// </summary>
        public static HumanAnimationTargets Choose(string hint, Vector3 velocity, bool inWater = false)
        {
            var t = new HumanAnimationTargets { WalkRate = 1f, JogRate = 1f, SprintRate = 1f, FlyRate = 1f, SwimRate = 1f };
            float speed = new Vector2(velocity.x, velocity.z).magnitude;
            if (float.IsNaN(speed) || float.IsInfinity(speed)) speed = 0f;
            hint = string.IsNullOrEmpty(hint) ? AvatarAnimations.Idle : hint;
            if (hint == AvatarAnimations.Fly)
            {
                float forward = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.8f, 4f, speed));
                t.FlyForward = forward;
                t.Hover = 1f - forward;
                t.FlyRate = Mathf.Clamp(0.7f + speed / 16f, 0.7f, 1.6f);
                return t;
            }
            if (inWater || hint == AvatarAnimations.Swim)
            {
                float forward = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 1.2f, speed));
                t.SwimForward = forward;
                t.SwimIdle = 1f - forward;
                t.SwimRate = Mathf.Clamp(0.6f + speed / 5f, 0.6f, 1.3f);
                return t;
            }
            if (hint == AvatarAnimations.Fall)
            {
                t.Fall = 1f;
                return t;
            }

            // Idle, walk, run and anything unknown: by ground speed.
            t.WalkRate = Mathf.Clamp(Mathf.Max(speed, WalkPoint * 0.6f) / WalkClipSpeed, 0.8f, 1.7f);
            t.JogRate = Mathf.Clamp(speed / JogClipSpeed, 0.65f, 1.3f);
            t.SprintRate = Mathf.Clamp(speed / SprintClipSpeed, 0.75f, 1.35f);
            if (speed < StandSpeed)
            {
                t.Idle = 1f;
            }
            else if (speed < WalkPoint)
            {
                float k = Mathf.InverseLerp(StandSpeed, WalkPoint, speed);
                t.Idle = 1f - k;
                t.Walk = k;
            }
            else if (speed < JogPoint)
            {
                float k = Mathf.InverseLerp(WalkPoint, JogPoint, speed);
                t.Walk = 1f - k;
                t.Jog = k;
            }
            else if (speed < SprintPoint)
            {
                float k = Mathf.InverseLerp(JogPoint, SprintPoint, speed);
                t.Jog = 1f - k;
                t.Sprint = k;
            }
            else
            {
                t.Sprint = 1f;
            }
            return t;
        }
    }

    /// <summary>
    /// Plays the human clips on a humanoid <see cref="Animator"/> through a manually evaluated playable graph:
    /// weights ease toward the targets, the stride clips share one phase so blended feet stay in step. Going into
    /// and out of the water is blended more slowly and evenly (<see cref="SwimBlendRate"/>), since the swimming
    /// body is drawn much lower (<see cref="SwimDrop"/>).
    /// </summary>
    public sealed class HumanAnimationPlayer : IDisposable
    {
        /// <summary>
        /// The root height offsets of the Hover and FlyForward clip assets (<see cref="AnimationClipSettings"/>
        /// level, written by Editor/FlightClipLevels.cs, checked against the clips by FlightClipTests), in the
        /// humanoid's normalised units: a negative offset raises the body by that many human scales.
        /// </summary>
        public const float HoverClipLevel = -1.4423474f, FlyForwardClipLevel = -1.0301324f;

        /// <summary>How fast the share of the swimming clips changes, per second (a change takes 1 / this).</summary>
        public const float SwimBlendRate = 1.6f;

        private static AnimationClip[] clips;

        private PlayableGraph graph;
        private AnimationMixerPlayable mixer;
        private readonly AnimationClipPlayable[] playables = new AnimationClipPlayable[HumanAnimationTargets.ClipCount];
        private readonly float[] eased = new float[HumanAnimationTargets.ClipCount];
        private readonly float[] weights = new float[HumanAnimationTargets.ClipCount];
        private readonly float[] times = new float[HumanAnimationTargets.ClipCount];
        private float stridePhase;
        private float swimShare;
        private bool first = true;

        /// <summary>The mixer weights applied last frame (summing to 1), by <see cref="HumanClip"/>.</summary>
        public IReadOnlyList<float> Weights => weights;

        /// <summary>The share of the swimming clips last frame, 0..1.</summary>
        public float SwimShare => swimShare;

        /// <summary>
        /// How far the figure must be moved up (negative: down) so the swimming clips are drawn at the depth they
        /// were authored at, in human scales: undoes the lift of the flying clips they share assets with.
        /// </summary>
        public float SwimDrop => weights[(int)HumanClip.SwimIdle] * HoverClipLevel + weights[(int)HumanClip.SwimForward] * FlyForwardClipLevel;

        /// <summary>The targets of the last update.</summary>
        public HumanAnimationTargets Targets { get; private set; }

        public bool IsValid => graph.IsValid();

        /// <summary>Loads the clips once. False if any is missing.</summary>
        public static bool ClipsAvailable => LoadClips() != null;

        /// <summary>The clip asset a mixer input plays.</summary>
        public static string AssetName(HumanClip clip) =>
            clip == HumanClip.SwimIdle ? nameof(HumanClip.Hover) : clip == HumanClip.SwimForward ? nameof(HumanClip.FlyForward) : clip.ToString();

        private static AnimationClip[] LoadClips()
        {
            if (clips != null) return clips;
            var loaded = new AnimationClip[HumanAnimationTargets.ClipCount];
            for (int i = 0; i < loaded.Length; i++)
            {
                loaded[i] = Resources.Load<AnimationClip>(HumanCatalog.AnimationsFolder + AssetName((HumanClip)i));
                if (loaded[i] == null)
                {
                    Debug.LogWarning($"[Avatar] animation clip {AssetName((HumanClip)i)} missing");
                    return null;
                }
            }
            return clips = loaded;
        }

        public HumanAnimationPlayer(Animator animator, string name)
        {
            var c = LoadClips();
            if (animator == null || c == null) return;
            graph = PlayableGraph.Create("Avatar " + name);
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(graph, "Pose", animator);
            mixer = AnimationMixerPlayable.Create(graph, c.Length);
            for (int i = 0; i < c.Length; i++)
            {
                playables[i] = AnimationClipPlayable.Create(graph, c[i]);
                playables[i].SetApplyFootIK(true);
                playables[i].SetSpeed(0); // times are set by hand each frame
                graph.Connect(playables[i], 0, mixer, i);
            }
            output.SetSourcePlayable(mixer);
            graph.Play();
        }

        private static bool IsSwim(int i) => i == (int)HumanClip.SwimIdle || i == (int)HumanClip.SwimForward;

        /// <summary>Eases toward <paramref name="targets"/>, advances the clips by <paramref name="dt"/> and poses the skeleton.</summary>
        public void Update(in HumanAnimationTargets targets, float dt)
        {
            if (!graph.IsValid()) return;
            Targets = targets;
            dt = Mathf.Max(0f, dt);
            float k = first ? 1f : dt > 0f ? 1f - Mathf.Exp(-7f * dt) : 0f;
            // The swimming clips' share moves at an even rate; within each group (on land or in the air, and in
            // the water) the clips ease as usual.
            float swimTarget = Mathf.Clamp01(targets.Swim);
            swimShare = first ? swimTarget : Mathf.MoveTowards(swimShare, swimTarget, SwimBlendRate * dt);
            first = false;
            float land = 0f, water = 0f;
            for (int i = 0; i < eased.Length; i++)
            {
                eased[i] = Mathf.Lerp(eased[i], targets[(HumanClip)i], k);
                if (IsSwim(i)) water += eased[i];
                else land += eased[i];
            }
            for (int i = 0; i < weights.Length; i++)
            {
                if (IsSwim(i))
                    weights[i] = swimShare * (water > 1e-6f ? eased[i] / water : i == (int)HumanClip.SwimIdle ? 1f : 0f);
                else
                    weights[i] = (1f - swimShare) * (land > 1e-6f ? eased[i] / land : i == (int)HumanClip.Idle ? 1f : 0f);
                mixer.SetInputWeight(i, weights[i]);
            }

            // Stride clips share a normalised phase; its rate is the weighted mean of their cycle rates.
            float strideWeight = eased[(int)HumanClip.Walk] + eased[(int)HumanClip.Jog] + eased[(int)HumanClip.Sprint];
            if (strideWeight > 1e-4f)
            {
                float cycles = (eased[(int)HumanClip.Walk] * targets.WalkRate / clips[(int)HumanClip.Walk].length +
                                eased[(int)HumanClip.Jog] * targets.JogRate / clips[(int)HumanClip.Jog].length +
                                eased[(int)HumanClip.Sprint] * targets.SprintRate / clips[(int)HumanClip.Sprint].length) / strideWeight;
                stridePhase = Mathf.Repeat(stridePhase + dt * cycles, 1f);
            }
            Advance(HumanClip.Idle, dt);
            Advance(HumanClip.Hover, dt);
            Advance(HumanClip.FlyForward, dt * targets.FlyRate);
            Advance(HumanClip.Fall, dt);
            Advance(HumanClip.SwimIdle, dt);
            Advance(HumanClip.SwimForward, dt * targets.SwimRate);
            for (var s = HumanClip.Walk; s <= HumanClip.Sprint; s++)
                times[(int)s] = stridePhase * clips[(int)s].length;
            for (int i = 0; i < playables.Length; i++) playables[i].SetTime(times[i]);
            graph.Evaluate(0f);
        }

        private void Advance(HumanClip clip, float dt)
        {
            int i = (int)clip;
            times[i] = Mathf.Repeat(times[i] + dt, clips[i].length);
        }

        public void Dispose()
        {
            if (graph.IsValid()) graph.Destroy();
        }
    }
}
