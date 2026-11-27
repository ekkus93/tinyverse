using System;
using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// What an avatar looks like on screen. The avatar root (feet at its origin, facing its +Z) owns movement;
    /// the visual is a child of it and only draws: <see cref="HumanAvatarVisual"/> (a rigged, animated person, or
    /// the <see cref="PlaceholderAvatarVisual"/> figure for an unknown body preset).
    /// </summary>
    public interface IAvatarVisual
    {
        /// <summary>The visual's own transform, a child of the avatar root.</summary>
        Transform Root { get; }

        /// <summary>Height of the top of the head above the feet, metres.</summary>
        float Height { get; }

        /// <summary>The name tag above the head.</summary>
        NameTag NameTag { get; }

        void SetLook(AvatarLook look);

        void SetName(string displayName);

        /// <summary>Shows or hides the AI-agent badge on the name tag.</summary>
        void SetAgent(bool isAgent);

        /// <summary>Hides the body and name tag (first-person view) without destroying anything.</summary>
        void SetVisible(bool visible);

        /// <summary>
        /// Called once per frame with the avatar's current pose. The root already has the position and yaw; the
        /// visual uses velocity and the animation hint to animate.
        /// </summary>
        void UpdatePose(in AvatarPose pose, float deltaTime);

        /// <summary>Destroys the visual's objects.</summary>
        void Destroy();
    }

    /// <summary>Creates the visual for every avatar. Replace <see cref="Factory"/> to swap in real models.</summary>
    public static class AvatarVisuals
    {
        /// <summary>Builds a visual under the given avatar root. Defaults to <see cref="HumanAvatarVisual.Create"/>.</summary>
        public static Func<Transform, IAvatarVisual> Factory { get; set; } = HumanAvatarVisual.Create;

        internal static IAvatarVisual CreateFor(Transform avatarRoot)
        {
            var factory = Factory ?? HumanAvatarVisual.Create;
            return factory(avatarRoot) ?? PlaceholderAvatarVisual.Create(avatarRoot);
        }
    }

    /// <summary>
    /// Maps a collider back to the avatar it belongs to. On the root of every local and remote avatar.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AvatarId : MonoBehaviour
    {
        /// <summary>The avatar's entity id, set by the owner.</summary>
        public ulong Id { get; set; }

        public bool IsLocal { get; internal set; }

        /// <summary>The avatar id for a collider (for example from a raycast hit), if it belongs to an avatar.</summary>
        public static bool TryGet(Collider collider, out ulong id)
        {
            id = 0;
            if (collider == null) return false;
            var tag = collider.GetComponentInParent<AvatarId>();
            if (tag == null) return false;
            id = tag.Id;
            return true;
        }
    }
}
