using System;
using UnityEngine;

namespace Tinyverse.Building
{
    /// <summary>The active build tool. Keys 1 to 5 select them in this order.</summary>
    public enum BuildTool
    {
        Place,
        Select,
        Move,
        Rotate,
        Scale,
    }

    /// <summary>A gizmo part under the pointer or being dragged.</summary>
    public enum BuildHandle
    {
        None,
        MoveX,
        MoveY,
        MoveZ,
        /// <summary>Moves in the plane spanned by Y and Z (normal X).</summary>
        MoveYZ,
        /// <summary>Moves in the plane spanned by X and Z (normal Y).</summary>
        MoveXZ,
        /// <summary>Moves in the plane spanned by X and Y (normal Z).</summary>
        MoveXY,
        RotateX,
        RotateY,
        RotateZ,
        ScaleX,
        ScaleY,
        ScaleZ,
        ScaleUniform,
    }

    /// <summary>Keys the build tools react to, as reported by <see cref="IBuildInput"/>.</summary>
    [Flags]
    public enum BuildKeys
    {
        None = 0,
        Digit1 = 1 << 0,
        Digit2 = 1 << 1,
        Digit3 = 1 << 2,
        Digit4 = 1 << 3,
        Digit5 = 1 << 4,
        G = 1 << 5,
        Delete = 1 << 6,
        Backspace = 1 << 7,
        D = 1 << 8,
        Z = 1 << 9,
        Y = 1 << 10,
        Escape = 1 << 11,
    }

    /// <summary>One frame of input as the build tools see it.</summary>
    public struct BuildInputFrame
    {
        /// <summary>False when there is no pointer or it is captured (e.g. the cursor is locked for mouse-look).</summary>
        public bool HasPointer;

        /// <summary>Pointer position in screen pixels, origin bottom-left (Unity's screen convention).</summary>
        public Vector2 Pointer;

        /// <summary>The primary (left) button is down.</summary>
        public bool PrimaryHeld;

        /// <summary>The primary button went down this frame.</summary>
        public bool PrimaryPressed;

        /// <summary>The primary button went up this frame.</summary>
        public bool PrimaryReleased;

        public bool Ctrl;
        public bool Shift;

        /// <summary>Keys that went down this frame.</summary>
        public BuildKeys KeysPressed;
    }

    /// <summary>The input seam: the controller calls <see cref="Poll"/> once per frame.</summary>
    public interface IBuildInput
    {
        BuildInputFrame Poll();
    }

    /// <summary>World bounds and scale limits the tools stay inside, so they never propose what the server refuses.</summary>
    [Serializable]
    public struct BuildLimits
    {
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public float MinScale;
        public float MaxScale;

        public static BuildLimits Default => new BuildLimits
        {
            BoundsMin = new Vector3(-10000f, -10000f, -10000f),
            BoundsMax = new Vector3(10000f, 10000f, 10000f),
            MinScale = 0.01f,
            MaxScale = 64f,
        };
    }
}
