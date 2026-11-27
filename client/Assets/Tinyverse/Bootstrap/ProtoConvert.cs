using System;
using Tinyverse.Avatars;
using Tinyverse.World;
using UnityEngine;
using P = Tinyverse.Proto;
using UI = Tinyverse.UI;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// Every conversion between the protocol types and the modules' plain types. The protocol uses
    /// Unity's conventions (metres, left-handed Y up, yaw clockwise from +Z, sRGB colours), so
    /// values pass through unchanged; only nulls, zero quaternions and enum spellings need care.
    /// </summary>
    public static class ProtoConvert
    {
        // ---------------------------------------------------------------- maths

        public static Vector3 ToVector3(P.Vec3 v) => v == null ? Vector3.zero : new Vector3(v.X, v.Y, v.Z);

        public static P.Vec3 ToProto(Vector3 v) => new P.Vec3 { X = v.x, Y = v.y, Z = v.z };

        /// <summary>A missing or all-zero quaternion is identity (proto comment on Quat).</summary>
        public static Quaternion ToQuaternion(P.Quat q)
        {
            if (q == null || (q.X == 0f && q.Y == 0f && q.Z == 0f && q.W == 0f)) return Quaternion.identity;
            return new Quaternion(q.X, q.Y, q.Z, q.W);
        }

        public static P.Quat ToProto(Quaternion q) => new P.Quat { X = q.x, Y = q.y, Z = q.z, W = q.w };

        /// <summary>sRGB in, sRGB out: no colour-space conversion. Null becomes transparent black ("unset").</summary>
        public static Color ToColor(P.Color c) => c == null ? new Color(0, 0, 0, 0) : new Color(c.R, c.G, c.B, c.A);

        public static P.Color ToProto(Color c) => new P.Color { R = c.r, G = c.g, B = c.b, A = c.a };

        /// <summary>True for a null colour or one with every channel zero (an unset proto message).</summary>
        public static bool IsUnset(P.Color c) => c == null || (c.R == 0f && c.G == 0f && c.B == 0f && c.A == 0f);


        // ---------------------------------------------------------------- enums

        public static PrimShape ToPrimShape(P.Shape s)
        {
            switch (s)
            {
                case P.Shape.Sphere: return PrimShape.Sphere;
                case P.Shape.Cylinder: return PrimShape.Cylinder;
                case P.Shape.Cone: return PrimShape.Cone;
                case P.Shape.Wedge: return PrimShape.Wedge;
                case P.Shape.Plane: return PrimShape.Plane;
                default: return PrimShape.Box; // Box and Unspecified
            }
        }

        public static P.Shape ToProto(PrimShape s)
        {
            switch (s)
            {
                case PrimShape.Sphere: return P.Shape.Sphere;
                case PrimShape.Cylinder: return P.Shape.Cylinder;
                case PrimShape.Cone: return P.Shape.Cone;
                case PrimShape.Wedge: return P.Shape.Wedge;
                case PrimShape.Plane: return P.Shape.Plane;
                default: return P.Shape.Box;
            }
        }

        public static PrimMaterial ToPrimMaterial(P.Material m)
        {
            switch (m)
            {
                case P.Material.Glossy: return PrimMaterial.Glossy;
                case P.Material.Metal: return PrimMaterial.Metal;
                case P.Material.Glass: return PrimMaterial.Glass;
                case P.Material.Emissive: return PrimMaterial.Emissive;
                default: return PrimMaterial.Matte; // Matte and Unspecified
            }
        }

        public static P.Material ToProto(PrimMaterial m)
        {
            switch (m)
            {
                case PrimMaterial.Glossy: return P.Material.Glossy;
                case PrimMaterial.Metal: return P.Material.Metal;
                case PrimMaterial.Glass: return P.Material.Glass;
                case PrimMaterial.Emissive: return P.Material.Emissive;
                default: return P.Material.Matte;
            }
        }

        public static UI.ShapeKind ToUI(PrimShape s) => (UI.ShapeKind)(int)s;
        public static PrimShape ToPrimShape(UI.ShapeKind s) => (PrimShape)(int)s;
        public static UI.MaterialKind ToUI(PrimMaterial m) => (UI.MaterialKind)(int)m;
        public static PrimMaterial ToPrimMaterial(UI.MaterialKind m) => (PrimMaterial)(int)m;

        public static MoveKind ToMoveKind(P.MoveMode m)
        {
            switch (m)
            {
                case P.MoveMode.Run: return MoveKind.Run;
                case P.MoveMode.Fly: return MoveKind.Fly;
                default: return MoveKind.Walk;
            }
        }

        public static P.MoveMode ToProto(MoveKind m)
        {
            switch (m)
            {
                case MoveKind.Run: return P.MoveMode.Run;
                case MoveKind.Fly: return P.MoveMode.Fly;
                default: return P.MoveMode.Walk;
            }
        }

        public static UI.MoveMode ToUI(MoveKind m)
        {
            switch (m)
            {
                case MoveKind.Run: return UI.MoveMode.Run;
                case MoveKind.Fly: return UI.MoveMode.Fly;
                default: return UI.MoveMode.Walk;
            }
        }

        public static UI.Role ToUI(P.Role r)
        {
            switch (r)
            {
                case P.Role.Owner: return UI.Role.Owner;
                case P.Role.Builder: return UI.Role.Builder;
                default: return UI.Role.Visitor; // Visitor and Unspecified: the least privilege
            }
        }

        public static P.Role ToProto(UI.Role r)
        {
            switch (r)
            {
                case UI.Role.Owner: return P.Role.Owner;
                case UI.Role.Builder: return P.Role.Builder;
                default: return P.Role.Visitor;
            }
        }

        public static UI.ChatChannel ToUI(P.ChatChannel c) =>
            c == P.ChatChannel.World ? UI.ChatChannel.World : UI.ChatChannel.Local;

        /// <summary>System lines are never sent; they map to local chat if asked.</summary>
        public static P.ChatChannel ToProto(UI.ChatChannel c) =>
            c == UI.ChatChannel.World ? P.ChatChannel.World : P.ChatChannel.Local;

        public static UI.ConnectionState ToUI(Net.ConnectionState s)
        {
            switch (s)
            {
                case Net.ConnectionState.Connected: return UI.ConnectionState.Connected;
                case Net.ConnectionState.Reconnecting: return UI.ConnectionState.Reconnecting;
                case Net.ConnectionState.Connecting:
                case Net.ConnectionState.Joining: return UI.ConnectionState.Connecting;
                default: return UI.ConnectionState.Disconnected;
            }
        }

        public static bool IsAgent(P.ClientKind k) => k == P.ClientKind.Agent;

        // ---------------------------------------------------------------- avatars

        public static AvatarPose ToPose(P.AvatarTransform t)
        {
            if (t == null) return new AvatarPose(Vector3.zero, 0f);
            return new AvatarPose
            {
                Position = ToVector3(t.Position),
                Yaw = t.Yaw,
                Velocity = ToVector3(t.Velocity),
                Mode = ToMoveKind(t.MoveMode),
                Animation = string.IsNullOrEmpty(t.Animation) ? AvatarAnimations.Idle : t.Animation,
            };
        }

        public static P.AvatarTransform ToProto(AvatarPose p) => new P.AvatarTransform
        {
            Position = ToProto(p.Position),
            Yaw = p.Yaw,
            Velocity = ToProto(p.Velocity),
            MoveMode = ToProto(p.Mode),
            Animation = p.Animation ?? "",
        };

        /// <summary>
        /// How an avatar looks: its appearance where set, with the body colour derived from the user
        /// id when the appearance leaves it unset.
        /// </summary>
        public static AvatarLook ToLook(P.Appearance a, string userId)
        {
            var look = AvatarLook.WithBody(BodyColors.ForUser(userId));
            look.BodyPreset = BodyColors.PresetForUser(userId);
            if (a == null) return look;
            if (!IsUnset(a.BodyColor))
            {
                look.BodyColor = Opaque(ToColor(a.BodyColor));
                look.TopColor = look.BodyColor;
            }
            if (!IsUnset(a.SkinColor)) look.SkinColor = Opaque(ToColor(a.SkinColor));
            if (!IsUnset(a.HairColor)) look.HairColor = Opaque(ToColor(a.HairColor));
            if (!IsUnset(a.TopColor)) look.TopColor = Opaque(ToColor(a.TopColor));
            if (!IsUnset(a.BottomColor)) look.BottomColor = Opaque(ToColor(a.BottomColor));
            if (!string.IsNullOrEmpty(a.BodyPreset)) look.BodyPreset = a.BodyPreset;
            look.HairStyle = a.HairStyle ?? "";
            return look;
        }

        static Color Opaque(Color c)
        {
            c.a = 1f;
            return c;
        }

        /// <summary>The appearance sent in Hello: a body colour only when one was chosen.</summary>
        public static P.Appearance ToAppearance(Color? bodyColor)
        {
            var a = new P.Appearance();
            if (bodyColor.HasValue) a.BodyColor = ToProto(Opaque(bodyColor.Value));
            return a;
        }

        /// <summary>
        /// The appearance sent in Hello: what the appearance editor stored, with the older body colour setting as
        /// the top colour when no top was chosen. Unchosen fields stay unset.
        /// </summary>
        public static P.Appearance ToAppearance(StoredAppearance stored, Color? bodyColor)
        {
            var a = ToAppearance(bodyColor);
            if (stored == null) return a;
            a.BodyPreset = stored.bodyPreset ?? "";
            a.HairStyle = stored.hairStyle ?? "";
            SetIfParsed(stored.skin, c => a.SkinColor = c);
            SetIfParsed(stored.hair, c => a.HairColor = c);
            SetIfParsed(stored.top, c => { a.TopColor = c; if (!bodyColor.HasValue) a.BodyColor = c; });
            SetIfParsed(stored.bottom, c => a.BottomColor = c);
            return a;
        }

        static void SetIfParsed(string hex, Action<P.Color> set)
        {
            var c = SettingsStore.ParseBodyColor(hex);
            if (c.HasValue) set(ToProto(Opaque(c.Value)));
        }

        public static UI.PersonInfo ToPerson(P.AvatarInfo a, ulong localAvatarId) =>
            new UI.PersonInfo(a.Id, a.UserId ?? "", a.Name ?? "", IsAgent(a.Kind), ToUI(a.Role), a.Id == localAvatarId);

        /// <summary>Speed limits from Welcome.limits (server defaults where zero) and the world bounds.</summary>
        public static MoveLimits ToMoveLimits(P.Limits limits, P.WorldInfo world)
        {
            var m = MoveLimits.Default;
            if (limits != null)
            {
                if (limits.WalkSpeed > 0) m.WalkSpeed = limits.WalkSpeed;
                if (limits.RunSpeed > 0) m.RunSpeed = limits.RunSpeed;
                if (limits.FlySpeed > 0) m.FlySpeed = limits.FlySpeed;
            }
            if (world != null && world.BoundsMin != null && world.BoundsMax != null)
            {
                var min = ToVector3(world.BoundsMin);
                var max = ToVector3(world.BoundsMax);
                if (max.x > min.x && max.y > min.y && max.z > min.z)
                {
                    m.BoundsMin = min;
                    m.BoundsMax = max;
                }
            }
            return m;
        }

        // ---------------------------------------------------------------- world

        public static EnvironmentSettings ToEnvironment(P.WorldInfo w)
        {
            if (w == null) return WorldEnvironment.Resolve(default);
            return new EnvironmentSettings
            {
                GroundSize = w.GroundSize,
                Sky = w.Sky ?? "",
                TimeOfDay = w.TimeOfDay,
                BoundsMin = ToVector3(w.BoundsMin),
                BoundsMax = ToVector3(w.BoundsMax),
            };
        }

        public static PrimData ToPrim(P.ObjectInfo o)
        {
            var s = o.Spec ?? new P.ObjectSpec();
            return new PrimData
            {
                Id = o.Id,
                OwnerId = o.OwnerId ?? "",
                Name = s.Name ?? "",
                Shape = ToPrimShape(s.Shape),
                Material = ToPrimMaterial(s.Material),
                Position = ToVector3(s.Position),
                Rotation = ToQuaternion(s.Rotation),
                Scale = s.Scale == null ? Vector3.one : ToVector3(s.Scale),
                Color = s.Color == null ? Color.white : ToColor(s.Color),
                Solid = s.Solid,
                Locked = s.Locked,
            };
        }

        public static P.ObjectSpec ToSpec(PrimData d) => new P.ObjectSpec
        {
            Name = d.Name ?? "",
            Shape = ToProto(d.Shape),
            Position = ToProto(d.Position),
            Rotation = ToProto(d.Rotation),
            Scale = ToProto(d.Scale),
            Color = ToProto(d.Color),
            Material = ToProto(d.Material),
            Solid = d.Solid,
            Locked = d.Locked,
        };

        /// <summary>An ObjectUpdate carrying only the fields of <paramref name="next"/> that differ from <paramref name="current"/>.</summary>
        public static P.ObjectUpdate ToUpdate(PrimData current, PrimData next)
        {
            var u = new P.ObjectUpdate { Id = next.Id };
            if ((current.Name ?? "") != (next.Name ?? "")) u.Name = next.Name ?? "";
            if (current.Shape != next.Shape) u.Shape = ToProto(next.Shape);
            if (current.Position != next.Position) u.Position = ToProto(next.Position);
            if (current.Rotation != next.Rotation) u.Rotation = ToProto(next.Rotation);
            if (current.Scale != next.Scale) u.Scale = ToProto(next.Scale);
            if (current.Color != next.Color) u.Color = ToProto(next.Color);
            if (current.Material != next.Material) u.Material = ToProto(next.Material);
            if (current.Solid != next.Solid) u.Solid = next.Solid;
            if (current.Locked != next.Locked) u.Locked = next.Locked;
            return u;
        }

        /// <summary>An ObjectUpdate that sets every field.</summary>
        public static P.ObjectUpdate ToFullUpdate(PrimData d) => new P.ObjectUpdate
        {
            Id = d.Id,
            Name = d.Name ?? "",
            Shape = ToProto(d.Shape),
            Position = ToProto(d.Position),
            Rotation = ToProto(d.Rotation),
            Scale = ToProto(d.Scale),
            Color = ToProto(d.Color),
            Material = ToProto(d.Material),
            Solid = d.Solid,
            Locked = d.Locked,
        };

        // ---------------------------------------------------------------- chat

        public static DateTime ToLocalTime(long unixMs)
        {
            if (unixMs <= 0) return DateTime.Now;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime; }
            catch (ArgumentOutOfRangeException) { return DateTime.Now; }
        }

        public static UI.ChatLine ToChatLine(P.ChatEntry e) => new UI.ChatLine(
            ToLocalTime(e.TimestampMs), e.SenderName ?? "", IsAgent(e.SenderKind), ToUI(e.Channel), e.Text ?? "");
    }

    /// <summary>A stable, pleasant body colour for each user id.</summary>
    public static class BodyColors
    {
        /// <summary>
        /// Hue from an FNV-1a hash of the id; saturation and value kept in a soft, readable range.
        /// The same id gives the same colour on every client.
        /// </summary>
        public static Color ForUser(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return AvatarLook.Default.BodyColor;
            uint h = 2166136261;
            foreach (char c in userId)
            {
                h ^= c;
                h *= 16777619;
            }
            float hue = (h % 360u) / 360f;
            float sat = 0.45f + ((h >> 9) % 20u) / 100f;   // 0.45 .. 0.64
            float val = 0.72f + ((h >> 17) % 16u) / 100f;  // 0.72 .. 0.87
            return Color.HSVToRGB(hue, sat, val);
        }

        /// <summary>The body preset for a user who has not chosen one: stable per user id, half and half.</summary>
        public static string PresetForUser(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return Tinyverse.Avatars.HumanCatalog.DefaultPreset;
            uint h = 2166136261;
            foreach (char c in userId)
            {
                h ^= c;
                h *= 16777619;
            }
            return (h >> 5) % 2 == 0 ? Tinyverse.Avatars.HumanCatalog.Masculine : Tinyverse.Avatars.HumanCatalog.Feminine;
        }
    }
}
