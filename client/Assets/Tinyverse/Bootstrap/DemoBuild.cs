using System.Collections.Generic;
using System.Linq;
using Tinyverse.Building;
using Tinyverse.World;
using UnityEngine;
using UIBuildTool = Tinyverse.UI.BuildTool;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// <c>--demo-build</c>: after joining, enters build mode, shows the placement preview, places a row of prims
    /// (every shape and material, plus white and black) in front of the avatar through the real
    /// BuildController → network path, steps through the move, rotate and scale gizmos on one of them, then
    /// selects each prim in turn so screenshots show the outline on every colour and material.
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
            (PrimShape.Box, "#ffffff", PrimMaterial.Matte),
            (PrimShape.Box, "#000000", PrimMaterial.Glossy),
        };

        const float PreviewAt = 1f, PlaceAt = 5f, MoveAt = 7f, RotateAt = 11f, ScaleAt = 15f, CycleAt = 19f, CycleEvery = 2f;

        readonly WorldSession session;
        float t = -1f;
        int step;
        List<ulong> placed;
        int cycled = -1;

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

            // During the preview the pointer rests on the ground ahead of the avatar; otherwise in a corner.
            bool previewing = step == 1;
            Input.Pointer = previewing ? new Vector2(Screen.width * 0.5f, Screen.height * 0.3f) : new Vector2(10f, 10f);

            if (Due(0, PreviewAt))
            {
                Phase = "preview";
                session.SetBuildMode(true);
                Tool(UIBuildTool.Place);
                build.CurrentShape = PrimShape.Box;
                build.CurrentColor = Hex("#ff8000");
                panel?.SetShape(Tinyverse.UI.ShapeKind.Box);
                panel?.SetColor(Hex("#ff8000"));
            }
            else if (Due(1, PlaceAt))
            {
                Phase = "placing";
                PlaceRow(build);
                Tool(UIBuildTool.Select);
            }
            else if (Due(2, MoveAt))
            {
                Phase = "move";
                placed = Own().ToList();
                build.Select(placed.FirstOrDefault());
                Tool(UIBuildTool.Move);
            }
            else if (Due(3, RotateAt))
            {
                Phase = "rotate";
                Tool(UIBuildTool.Rotate);
            }
            else if (Due(4, ScaleAt))
            {
                Phase = "scale";
                Tool(UIBuildTool.Scale);
            }
            else if (step >= 5 && t >= CycleAt)
            {
                // Select each placed prim in turn with the Select tool: the outline on every colour and material.
                int i = (int)((t - CycleAt) / CycleEvery);
                if (i != cycled)
                {
                    cycled = i;
                    if (i == 0) Tool(UIBuildTool.Select);
                    placed = Own().ToList();
                    if (i < placed.Count)
                    {
                        build.Select(placed[i]);
                        Phase = "select-" + i;
                    }
                    else Phase = "done";
                }
            }
        }

        void PlaceRow(BuildController build)
        {
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
                // Left of centre, so the row stays clear of the build panel on the right of the screen.
                var at = basePos + right * ((i - (Row.Length - 1) * 0.5f) * 1.45f - 1.2f);
                build.AvatarPosition = () => at;
                build.PlaceInFrontOfAvatar();
            }
            build.AvatarPosition = originalPos;
        }

        void Tool(UIBuildTool tool)
        {
            var panel = session.UI?.Build;
            if (panel != null) panel.ChooseTool(tool);
            else session.Build.Tool = (BuildTool)(int)tool;
        }

        bool Due(int index, float at)
        {
            if (step != index || t < at) return false;
            step++;
            return true;
        }

        /// <summary>This user's prims, left to right as seen from the avatar.</summary>
        IEnumerable<ulong> Own()
        {
            var a = session.LocalAvatar.transform;
            return session.Objects.All.Where(d => d.OwnerId == session.UserId)
                .OrderBy(d => Vector3.Dot(d.Position - a.position, a.right)).Select(d => d.Id);
        }

        static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.white;
    }
}
