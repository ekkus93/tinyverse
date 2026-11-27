using System;
using Tinyverse.Building;
using Tinyverse.World;
using UnityEngine;
using UIBuildTool = Tinyverse.UI.BuildTool;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// <c>--demo-build</c>: after joining, enters build mode, shows the placement preview, places one prim of
    /// each shape in a row in front of the avatar through the real BuildController → network path, selects
    /// one and steps through the move, rotate and scale gizmos, so screenshots show every part of build mode.
    /// </summary>
    public sealed class DemoBuild
    {
        public readonly ScriptedBuildInput Input = new ScriptedBuildInput();

        static readonly (PrimShape shape, string hex, PrimMaterial material)[] Row =
        {
            (PrimShape.Box, "#ff8000", PrimMaterial.Matte),
            (PrimShape.Sphere, "#2e86de", PrimMaterial.Glossy),
            (PrimShape.Cylinder, "#c8c8c8", PrimMaterial.Metal),
            (PrimShape.Cone, "#66ccff", PrimMaterial.Glass),
            (PrimShape.Wedge, "#ff3366", PrimMaterial.Emissive),
            (PrimShape.Plane, "#44bb44", PrimMaterial.Matte),
        };

        readonly WorldSession session;
        float t = -1f;
        int step;
        ulong firstId;

        public DemoBuild(WorldSession session)
        {
            this.session = session;
            session.Ready += () => { if (t < 0f) t = 0f; };
        }

        /// <summary>What the demo is showing now, for logs and screenshot names.</summary>
        public string Phase { get; private set; } = "waiting";

        public void Tick(float dt)
        {
            if (t < 0f) return;
            t += dt;
            var build = session.Build;
            if (build == null) return;
            var panel = session.UI?.Build;

            // Point at the ground a little ahead of the avatar, below the screen centre.
            Input.Pointer = new Vector2(Screen.width * 0.5f, Screen.height * 0.3f);

            if (Due(1f))
            {
                Phase = "preview";
                session.SetBuildMode(true);
                if (panel != null) panel.ChooseTool(UIBuildTool.Place); else build.Tool = BuildTool.Place;
                build.CurrentShape = PrimShape.Box;
                build.CurrentColor = Hex("#ff8000");
            }
            else if (Due(5f))
            {
                Phase = "placing";
                var avatar = session.LocalAvatar;
                var basePos = avatar.transform.position;
                var right = avatar.transform.right;
                var originalPos = build.AvatarPosition;
                for (int i = 0; i < Row.Length; i++)
                {
                    var (shape, hex, material) = Row[i];
                    build.CurrentShape = shape;
                    build.CurrentColor = Hex(hex);
                    build.CurrentMaterial = material;
                    var at = basePos + right * ((i - (Row.Length - 1) * 0.5f) * 1.8f);
                    build.AvatarPosition = () => at;
                    build.PlaceInFrontOfAvatar();
                }
                build.AvatarPosition = originalPos;
                if (panel != null) panel.ChooseTool(UIBuildTool.Select); else build.Tool = BuildTool.Select;
                Input.Pointer = new Vector2(10f, 10f);
            }
            else if (Due(7f))
            {
                Phase = "move";
                firstId = FindBox();
                build.Select(firstId);
                if (panel != null) panel.ChooseTool(UIBuildTool.Move); else build.Tool = BuildTool.Move;
            }
            else if (Due(11f))
            {
                Phase = "rotate";
                if (panel != null) panel.ChooseTool(UIBuildTool.Rotate); else build.Tool = BuildTool.Rotate;
            }
            else if (Due(15f))
            {
                Phase = "scale";
                if (panel != null) panel.ChooseTool(UIBuildTool.Scale); else build.Tool = BuildTool.Scale;
            }
            else if (Due(19f))
            {
                Phase = "done";
            }
            if (step >= 1 && step < 2) Input.Pointer = new Vector2(Screen.width * 0.5f, Screen.height * 0.3f);
            else Input.Pointer = new Vector2(10f, 10f);
        }

        bool Due(float at)
        {
            int index = (int)Math.Round(at) switch { 1 => 0, 5 => 1, 7 => 2, 11 => 3, 15 => 4, _ => 5 };
            if (step != index || t < at) return false;
            step++;
            return true;
        }

        ulong FindBox()
        {
            var target = Hex("#ff8000");
            foreach (var d in session.Objects.All)
                if (d.Shape == PrimShape.Box && Approximately(d.Color, target) && d.OwnerId == session.UserId) return d.Id;
            foreach (var d in session.Objects.All) return d.Id;
            return 0;
        }

        static bool Approximately(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

        static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.white;
    }
}
