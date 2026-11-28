using Tinyverse.Avatars;
using Tinyverse.World;
using UnityEngine;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// The open-water test for <see cref="AvatarWater"/> in a world whose ground preset is "water": feet at the
    /// level of the ground plane (y = 0) and no solid object under them. Non-solid objects (a painted disc of
    /// shallows lying on the sea) do not count, so their water is still water.
    /// </summary>
    public static class OpenWater
    {
        public const string WaterGround = "water";

        /// <summary>How far above the ground plane the feet may be and still swim, metres.</summary>
        public const float SurfaceBand = 0.15f;

        static readonly RaycastHit[] hits = new RaycastHit[16];

        /// <summary>The test for a world with <paramref name="groundPreset"/>, or null when its ground is not water.</summary>
        public static System.Func<Vector3, bool> For(string groundPreset, ObjectRegistry objects) =>
            groundPreset == WaterGround && objects != null ? p => At(p, objects) : (System.Func<Vector3, bool>)null;

        public static bool At(Vector3 feet, ObjectRegistry objects)
        {
            if (feet.y > SurfaceBand || feet.y < -0.5f) return false;
            // Straight down from just above the feet to below the plane: a solid object there is land.
            const float above = 0.3f;
            int n = Physics.RaycastNonAlloc(feet + Vector3.up * above, Vector3.down, hits, above + SurfaceBand + 0.1f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (objects.TryGetId(hits[i].collider, out _)) return false;
            return true;
        }
    }
}
