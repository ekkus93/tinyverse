using System;
using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// Where avatars swim. This module cannot see the world (its ground preset and objects), so whoever can sets
    /// <see cref="Test"/>: given an avatar's position (its feet), whether it is in open water — at the level of the
    /// world's base ground, where that ground is water, and not on a solid object. Unset, nobody swims.
    /// An avatar that is not flying swims there: slower (<see cref="SwimSpeed"/>, <see cref="SwimRunSpeed"/>),
    /// without jumping, drawn treading water or swimming, with the body in the water. Every client decides this
    /// for every avatar from the position, so bots and agents that report "walk" are drawn swimming too.
    /// </summary>
    public static class AvatarWater
    {
        /// <summary>Swimming speed, and with the run key held, m/s.</summary>
        public const float SwimSpeed = 2f, SwimRunSpeed = 3f;

        /// <summary>Whether a position is in open water; null when the world has none.</summary>
        public static Func<Vector3, bool> Test { get; set; }

        /// <summary>Whether <paramref name="feet"/> is in open water (false without a <see cref="Test"/>).</summary>
        public static bool IsOpenWater(Vector3 feet)
        {
            var test = Test;
            return test != null && test(feet);
        }
    }
}
