using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI.Tests
{
    public class BuildPanelTests : UITestBase
    {
        BuildPanel B => UI.Build;

        [SetUp]
        public void ShowBuild()
        {
            UI.ShowScreen(UIScreen.Hud);
            UI.SetBuildMode(true);
        }

        static SelectedObjectInfo Sample => new SelectedObjectInfo
        {
            Id = 4242, Name = "Door", Shape = ShapeKind.Wedge, Position = new Vector3(1, 2, 3), Rotation = new Vector3(0, 90, 0),
            Scale = new Vector3(1, 2, 0.1f), Color = new Color(0.8f, 0.2f, 0.2f), Material = MaterialKind.Metal,
            Solid = true, Locked = false, OwnerName = "Ada",
        };

        [Test]
        public void ShapePaletteRaisesEveryShape()
        {
            var got = new List<ShapeKind>();
            B.ShapeSelected += got.Add;
            foreach (ShapeKind s in System.Enum.GetValues(typeof(ShapeKind)))
            {
                Click(B.ShapeButton(s));
                Assert.That(B.Shape, Is.EqualTo(s));
                Assert.That(B.ShapeButton(s).ClassListContains("tv-tile--active"), Is.True);
            }
            Assert.That(got, Is.EqualTo(new[] { ShapeKind.Box, ShapeKind.Sphere, ShapeKind.Cylinder, ShapeKind.Cone, ShapeKind.Wedge, ShapeKind.Plane }));
            Assert.That(B.ShapeButton(ShapeKind.Box).ClassListContains("tv-tile--active"), Is.False);
        }

        [Test]
        public void ToolSelectorRaisesEveryTool()
        {
            var got = new List<BuildTool>();
            B.ToolSelected += got.Add;
            foreach (BuildTool t in System.Enum.GetValues(typeof(BuildTool))) Click(B.ToolButton(t));
            Assert.That(got, Is.EqualTo(new[] { BuildTool.Place, BuildTool.Select, BuildTool.Move, BuildTool.Rotate, BuildTool.Scale }));
            Assert.That(B.Tool, Is.EqualTo(BuildTool.Scale));
            B.SetTool(BuildTool.Select);
            Assert.That(got.Count, Is.EqualTo(5), "setters do not raise events");
        }

        [Test]
        public void SnapToggleAndStep()
        {
            var got = new List<(bool, float)>();
            B.SnapChanged += (e, s) => got.Add((e, s));
            B.SetSnap(true, 0.5f);
            Assert.That(got, Is.Empty);
            Change(B.SnapToggle, false);
            Assert.That(got[0], Is.EqualTo((false, 0.5f)));
            Change(B.SnapToggle, true);
            Change(B.SnapStepField, "0,25");
            PressKey(B.SnapStepField, KeyCode.Return);
            Assert.That(got[2], Is.EqualTo((true, 0.25f)));
            Assert.That(B.SnapStep, Is.EqualTo(0.25f));
            Assert.That(B.CommitSnapStep("nope"), Is.False);
            Assert.That(B.SnapStepField.value, Is.EqualTo("0.25"), "reverts");
            Assert.That(B.CommitSnapStep("-1"), Is.False);
            B.CommitSnapStep("100");
            Assert.That(B.SnapStep, Is.EqualTo(BuildPanel.MaxSnapStep), "clamped");
        }

        [Test]
        public void MaterialPickerRaisesEveryMaterial()
        {
            var got = new List<MaterialKind>();
            B.MaterialSelected += got.Add;
            foreach (MaterialKind m in System.Enum.GetValues(typeof(MaterialKind))) Click(B.MaterialButton(m));
            Assert.That(got, Is.EqualTo(new[] { MaterialKind.Matte, MaterialKind.Glossy, MaterialKind.Metal, MaterialKind.Glass, MaterialKind.Emissive }));
            Assert.That(B.MaterialButton(MaterialKind.Emissive).ClassListContains("tv-material--active"), Is.True);
        }

        [Test]
        public void ColourPickerRoundTrip()
        {
            var live = new List<Color>();
            var committed = new List<Color>();
            B.ColorChanged += live.Add;
            B.ColorCommitted += committed.Add;
            var picker = B.ColorPicker;

            picker.SetColor(new Color(1f, 0f, 0f));
            Assert.That(picker.HexText, Is.EqualTo("#FF0000"));
            Assert.That(live, Is.Empty, "SetColor is silent");

            picker.SetHue(1f / 3f);
            Assert.That(ColorMath.Approximately(picker.Color, Color.green), Is.True, picker.Color.ToString());
            Assert.That(picker.HexText, Is.EqualTo("#00FF00"));
            picker.SetSv(0.5f, 0.5f);
            Assert.That(ColorMath.Approximately(picker.Color, new Color(0.25f, 0.5f, 0.25f)), Is.True, picker.Color.ToString());
            Assert.That(live.Count, Is.EqualTo(2));
            Assert.That(committed, Is.Empty, "dragging is not committed");
            picker.Commit();
            Assert.That(committed.Count, Is.EqualTo(1));

            Change(picker.HexField, "#3366cc");
            PressKey(picker.HexField, KeyCode.Return);
            Assert.That(ColorMath.ToHex(B.Color), Is.EqualTo("#3366CC"));
            Assert.That(committed.Count, Is.EqualTo(2));
            var hsv = picker.Hsv;
            Assert.That(ColorMath.Approximately(ColorMath.HsvToRgb(hsv), B.Color), Is.True, "HSV state matches RGB");

            Assert.That(picker.SubmitHex("zzz"), Is.False);
            Assert.That(picker.HexText, Is.EqualTo("#3366CC"), "invalid hex reverts");
            Assert.That(committed.Count, Is.EqualTo(2));
        }

        [Test]
        public void GreyKeepsHue()
        {
            var picker = B.ColorPicker;
            picker.SetHue(0.6f);
            picker.SetColor(Color.gray);
            Assert.That(picker.Hsv.H, Is.EqualTo(0.6f).Within(1e-4f));
        }

        [Test]
        public void OpacityAndRecentColours()
        {
            var picker = B.ColorPicker;
            var committed = new List<Color>();
            B.ColorCommitted += committed.Add;
            picker.SetColor(Color.blue);
            Change(picker.AlphaSlider, 0.5f);
            Assert.That(B.Color.a, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(committed.Count, Is.EqualTo(1), "keyboard/programmatic slider change commits");
            picker.SubmitHex("#FFFF00");
            Assert.That(B.Color.a, Is.EqualTo(0.5f).Within(1e-4f), "RGB hex keeps opacity");
            Assert.That(picker.RecentColors.Count, Is.EqualTo(2));
            Assert.That(ColorMath.ToHex(picker.RecentColors[0]), Is.EqualTo("#FFFF00"), "newest first");

            for (int i = 0; i < ColorPicker.MaxRecent + 3; i++) picker.SubmitHex(ColorMath.ToHex(new Color(i / 20f, 0.3f, 0.3f)));
            Assert.That(picker.RecentColors.Count, Is.EqualTo(ColorPicker.MaxRecent));

            var target = picker.RecentColors[4];
            int before = committed.Count;
            var swatches = B.Root.Q("colorRecent").Query<Button>(className: "tv-swatch").ToList();
            Click(swatches[4]);
            Assert.That(ColorMath.Approximately(B.Color, target), Is.True);
            Assert.That(committed.Count, Is.EqualTo(before + 1));
            Assert.That(ColorMath.Approximately(picker.RecentColors[0], target), Is.True, "picked swatch moves to the front");
        }

        [Test]
        public void NothingSelectedThenSelection()
        {
            Assert.That(B.HasSelection, Is.False);
            Assert.That(B.NothingSelectedShown, Is.True);
            B.SetSelection(Sample);
            Assert.That(B.NothingSelectedShown, Is.False);
            Assert.That(B.NameField.value, Is.EqualTo("Door"));
            Assert.That(B.PositionFields.Field(1).value, Is.EqualTo("2"));
            Assert.That(B.RotationFields.Field(1).value, Is.EqualTo("90"));
            Assert.That(B.ScaleFields.Field(2).value, Is.EqualTo("0.1"));
            Assert.That(B.SolidToggle.value, Is.True);
            Assert.That(B.LockedToggle.value, Is.False);
            Assert.That(B.OwnerText, Is.EqualTo("Ada"));
            Assert.That(B.IdText, Is.EqualTo("#4242"));
            Assert.That(B.Shape, Is.EqualTo(ShapeKind.Wedge));
            Assert.That(B.Material, Is.EqualTo(MaterialKind.Metal));
            Assert.That(ColorMath.ToHex(B.Color), Is.EqualTo(ColorMath.ToHex(Sample.Color)));
            B.ClearSelection();
            Assert.That(B.HasSelection, Is.False);
            Assert.That(B.NothingSelectedShown, Is.True);
        }

        [Test]
        public void InspectorEditsRaiseEvents()
        {
            B.SetSelection(Sample);
            var names = new List<string>();
            var pos = new List<Vector3>();
            var rot = new List<Vector3>();
            var scl = new List<Vector3>();
            var solid = new List<bool>();
            var locked = new List<bool>();
            int dup = 0, del = 0;
            B.NameChanged += names.Add;
            B.PositionChanged += pos.Add;
            B.RotationChanged += rot.Add;
            B.ScaleChanged += scl.Add;
            B.SolidChanged += solid.Add;
            B.LockedChanged += locked.Add;
            B.DuplicateRequested += () => dup++;
            B.DeleteRequested += () => del++;

            Change(B.NameField, "Front door ");
            PressKey(B.NameField, KeyCode.Return);
            Assert.That(names, Is.EqualTo(new[] { "Front door" }));

            Change(B.PositionFields.Field(0), "4,5");
            PressKey(B.PositionFields.Field(0), KeyCode.Return);
            Assert.That(pos, Is.EqualTo(new[] { new Vector3(4.5f, 2, 3) }));

            Change(B.RotationFields.Field(1), "270");
            PressKey(B.RotationFields.Field(1), KeyCode.Return);
            Assert.That(rot[0].y, Is.EqualTo(-90f).Within(1e-3f), "wrapped to -180..180");

            Change(B.ScaleFields.Field(0), "0");
            PressKey(B.ScaleFields.Field(0), KeyCode.Return);
            Assert.That(scl[0].x, Is.EqualTo(BuildPanel.MinScale), "scale clamped to 0.01");
            Assert.That(B.ScaleFields.Field(0).value, Is.EqualTo("0.01"));

            PressKey(B.PositionFields.Field(2), KeyCode.UpArrow);
            Assert.That(pos[1].z, Is.EqualTo(3.1f).Within(1e-4f), "arrow nudges");

            Change(B.SolidToggle, false);
            Change(B.LockedToggle, true);
            Assert.That(solid, Is.EqualTo(new[] { false }));
            Assert.That(locked, Is.EqualTo(new[] { true }));
            Assert.That(B.Selection.Locked, Is.True);

            Click(B.DuplicateButton);
            Click(B.DeleteButton);
            Assert.That(dup, Is.EqualTo(1));
            Assert.That(del, Is.EqualTo(1));
        }

        [Test]
        public void BadNumbersRevertWithoutEvents()
        {
            B.SetSelection(Sample);
            var pos = new List<Vector3>();
            B.PositionChanged += pos.Add;
            Assert.That(B.PositionFields.Commit(0, "abc"), Is.False);
            Assert.That(B.PositionFields.Field(0).value, Is.EqualTo("1"));
            Assert.That(B.PositionFields.Commit(0, "1"), Is.True);
            Assert.That(pos, Is.Empty, "unchanged value raises nothing");
            Assert.That(B.PositionFields.Commit(0, " 1e1 "), Is.True);
            Assert.That(pos, Is.EqualTo(new[] { new Vector3(10, 2, 3) }));
        }

        [Test]
        public void ReadOnlyInspector()
        {
            B.SetSelection(Sample, editable: false);
            Assert.That(B.IsInspectorReadOnly, Is.True);
            Assert.That(B.NameField.isReadOnly, Is.True);
            Assert.That(B.PositionFields.Field(0).isReadOnly, Is.True);
            Assert.That(B.ScaleFields.IsReadOnly, Is.True);
            Assert.That(B.SolidToggle.enabledSelf, Is.False);
            Assert.That(B.DeleteButton.enabledSelf, Is.False);
            Assert.That(Shown(B.Root.Q("inspectorReadOnly")), Is.True);

            var pos = new List<Vector3>();
            var names = new List<string>();
            int del = 0;
            B.PositionChanged += pos.Add;
            B.NameChanged += names.Add;
            B.DeleteRequested += () => del++;
            Assert.That(B.PositionFields.Commit(0, "9"), Is.False);
            B.CommitName("Hacked");
            Click(B.DeleteButton);
            Assert.That(pos, Is.Empty);
            Assert.That(names, Is.Empty);
            Assert.That(del, Is.EqualTo(0));

            B.SetInspectorReadOnly(false);
            Assert.That(B.NameField.isReadOnly, Is.False);
            Assert.That(B.DeleteButton.enabledSelf, Is.True);
            Assert.That(Shown(B.Root.Q("inspectorReadOnly")), Is.False);
        }

        [Test]
        public void LiveTransformUpdatesFields()
        {
            B.SetSelection(Sample);
            B.UpdateSelectionTransform(new Vector3(7, 8, 9), Vector3.zero, Vector3.one);
            Assert.That(B.PositionFields.Field(0).value, Is.EqualTo("7"));
            Assert.That(B.Selection.Position, Is.EqualTo(new Vector3(7, 8, 9)));
        }

        [Test]
        public void UndoRedoEnabledByGlue()
        {
            int undo = 0, redo = 0;
            B.UndoRequested += () => undo++;
            B.RedoRequested += () => redo++;
            Assert.That(B.CanUndo, Is.False);
            Click(B.UndoButton);
            Assert.That(undo, Is.EqualTo(0), "disabled");
            B.SetUndoRedo(true, false);
            Assert.That(B.CanUndo && !B.CanRedo, Is.True);
            Click(B.UndoButton);
            Click(B.RedoButton);
            Assert.That(undo, Is.EqualTo(1));
            Assert.That(redo, Is.EqualTo(0));
            B.SetUndoRedo(true, true);
            Click(B.RedoButton);
            Assert.That(redo, Is.EqualTo(1));
        }

        [Test]
        public void VisitorsSeeDisabledState()
        {
            B.SetBuildAllowed(false, "Ask an owner to make you a builder.");
            Assert.That(B.BuildAllowed, Is.False);
            Assert.That(B.DisabledReason, Is.EqualTo("Ask an owner to make you a builder."));
            Assert.That(Shown(B.Root.Q("buildDisabled")), Is.True);
            Assert.That(Shown(B.Root.Q("buildTools")), Is.False);
            var tools = new List<BuildTool>();
            B.ToolSelected += tools.Add;
            B.ChooseTool(BuildTool.Move);
            Assert.That(tools, Is.Empty);
            B.SetBuildAllowed(true);
            Assert.That(Shown(B.Root.Q("buildTools")), Is.True);
            Assert.That(B.DisabledReason, Is.Null);
        }
    }
}
