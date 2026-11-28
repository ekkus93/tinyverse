using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>How an avatar moves. Sets the speed limit that applies to it.</summary>
    public enum MoveKind
    {
        Walk,
        Run,
        Fly,
    }

    /// <summary>Where an avatar is and how it is moving.</summary>
    public struct AvatarPose
    {
        /// <summary>Position of the feet, world space, metres.</summary>
        public Vector3 Position;

        /// <summary>Heading in degrees, clockwise seen from above, 0 = facing +Z (same as <c>transform.eulerAngles.y</c>).</summary>
        public float Yaw;

        /// <summary>Metres per second, world space.</summary>
        public Vector3 Velocity;

        public MoveKind Mode;

        /// <summary>"idle", "walk", "run", "fly", "fall" or "swim". May be null or empty (treated as idle).</summary>
        public string Animation;

        public AvatarPose(Vector3 position, float yaw, Vector3 velocity = default, MoveKind mode = MoveKind.Walk, string animation = AvatarAnimations.Idle)
        {
            Position = position;
            Yaw = yaw;
            Velocity = velocity;
            Mode = mode;
            Animation = animation;
        }

        public override string ToString() => $"pos {Position} yaw {Yaw:0.0} vel {Velocity} {Mode} {Animation}";
    }

    /// <summary>The animation hints used by this module.</summary>
    public static class AvatarAnimations
    {
        public const string Idle = "idle";
        public const string Walk = "walk";
        public const string Run = "run";
        public const string Fly = "fly";
        public const string Fall = "fall";
        /// <summary>Swimming (or treading water) in open water; see <see cref="AvatarWater"/>.</summary>
        public const string Swim = "swim";
    }

    /// <summary>How an avatar looks. The placeholder visual draws only <see cref="BodyColor"/>.</summary>
    public struct AvatarLook
    {
        public Color BodyColor;
        /// <summary>Name of a body preset; empty means the default.</summary>
        public string BodyPreset;
        public Color SkinColor;
        public Color HairColor;
        /// <summary>Name of a hair style; empty means the default.</summary>
        public string HairStyle;
        public Color TopColor;
        public Color BottomColor;

        public static AvatarLook Default => new AvatarLook
        {
            BodyColor = new Color(0.30f, 0.55f, 0.85f),
            BodyPreset = "",
            SkinColor = new Color(0.87f, 0.70f, 0.58f),
            HairColor = new Color(0.20f, 0.14f, 0.10f),
            HairStyle = "",
            TopColor = new Color(0.30f, 0.55f, 0.85f),
            BottomColor = new Color(0.20f, 0.22f, 0.28f),
        };

        /// <summary>A default look with the given body colour.</summary>
        public static AvatarLook WithBody(Color bodyColor)
        {
            var look = Default;
            look.BodyColor = bodyColor;
            look.TopColor = bodyColor;
            return look;
        }
    }

    /// <summary>Speed limits (metres per second) and world bounds (feet position) the local avatar keeps to.</summary>
    public struct MoveLimits
    {
        public float WalkSpeed, RunSpeed, FlySpeed;
        public Vector3 BoundsMin, BoundsMax;

        /// <summary>The server defaults: walk 4, run 8, fly 16 m/s; bounds ±1000 m horizontally, −100…500 m vertically.</summary>
        public static MoveLimits Default => new MoveLimits
        {
            WalkSpeed = 4f,
            RunSpeed = 8f,
            FlySpeed = 16f,
            BoundsMin = new Vector3(-1000f, -100f, -1000f),
            BoundsMax = new Vector3(1000f, 500f, 1000f),
        };

        /// <summary>The speed limit for a move mode.</summary>
        public float SpeedFor(MoveKind mode)
        {
            switch (mode)
            {
                case MoveKind.Run: return RunSpeed;
                case MoveKind.Fly: return FlySpeed;
                default: return WalkSpeed;
            }
        }
    }
}
