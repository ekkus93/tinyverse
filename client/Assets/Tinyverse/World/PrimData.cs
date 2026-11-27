using System;
using UnityEngine;

namespace Tinyverse.World
{
    /// <summary>Primitive shapes. Each fits a 1 m cube centred on its origin before scaling.</summary>
    public enum PrimShape
    {
        Box,
        Sphere,
        /// <summary>Axis along Y.</summary>
        Cylinder,
        /// <summary>Apex at +Y, base at -Y.</summary>
        Cone,
        /// <summary>A box cut along its diagonal: full height at -Z, sloping to zero at +Z.</summary>
        Wedge,
        /// <summary>A 1 x 1 m quad in the XZ plane facing +Y, visible from both sides. Y scale is ignored.</summary>
        Plane,
    }

    /// <summary>Surface presets. Each maps to one shared material asset.</summary>
    public enum PrimMaterial
    {
        Matte,
        Glossy,
        Metal,
        Glass,
        Emissive,
    }

    /// <summary>Plain description of one prim, independent of the protocol types.</summary>
    [Serializable]
    public struct PrimData
    {
        public ulong Id;
        public string OwnerId;
        public string Name;
        public PrimShape Shape;
        public PrimMaterial Material;
        /// <summary>Centre of the object, world space, metres.</summary>
        public Vector3 Position;
        /// <summary>An all-zero quaternion is treated as identity.</summary>
        public Quaternion Rotation;
        /// <summary>Size per axis in metres.</summary>
        public Vector3 Scale;
        /// <summary>
        /// sRGB RGBA, each channel 0..1: the protocol's convention, the same as a hex code or a
        /// Unity Color from a colour picker. Alpha is linear coverage. The registry linearises
        /// RGB for the shader when the project renders in linear space.
        /// </summary>
        public Color Color;
        /// <summary>Avatars collide with it. A non-solid prim still has a trigger collider for picking.</summary>
        public bool Solid;
        public bool Locked;
    }
}
