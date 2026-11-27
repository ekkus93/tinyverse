using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>
    /// The build toolbar and inspector, shown in build mode. Setters show state without raising
    /// events; every user change raises an event for the glue.
    /// </summary>
    public sealed class BuildPanel : UIPanel
    {
        public const float MinScale = 0.01f, MaxScale = 64f;
        public const float MinSnapStep = 0.01f, MaxSnapStep = 16f;

        static readonly ShapeKind[] Shapes = (ShapeKind[])Enum.GetValues(typeof(ShapeKind));
        static readonly BuildTool[] Tools = (BuildTool[])Enum.GetValues(typeof(BuildTool));
        static readonly MaterialKind[] Materials = (MaterialKind[])Enum.GetValues(typeof(MaterialKind));

        readonly Dictionary<ShapeKind, Button> _shapeButtons = new Dictionary<ShapeKind, Button>();
        readonly Dictionary<BuildTool, Button> _toolButtons = new Dictionary<BuildTool, Button>();
        readonly Dictionary<MaterialKind, Button> _materialButtons = new Dictionary<MaterialKind, Button>();
        readonly Button _undo, _redo, _close, _duplicate, _delete;
        readonly Toggle _snap;
        readonly TextField _snapStep;
        readonly VisualElement _disabled;
        readonly Label _disabledText;
        readonly VisualElement _tools;
        readonly VisualElement _inspectorBody;
        readonly VisualElement _meta;
        readonly Label _nothing;
        readonly Label _readOnlyNote;
        readonly TextField _name;
        readonly Toggle _solid, _locked;
        readonly Label _owner, _id;

        public ColorPicker ColorPicker { get; }
        public Vector3Fields PositionFields { get; }
        public Vector3Fields RotationFields { get; }
        public Vector3Fields ScaleFields { get; }

        public event Action<ShapeKind> ShapeSelected;
        public event Action<BuildTool> ToolSelected;
        /// <summary>Snap toggled or its step changed: (enabled, step in metres).</summary>
        public event Action<bool, float> SnapChanged;
        /// <summary>Live colour while dragging in the picker.</summary>
        public event Action<Color> ColorChanged;
        /// <summary>Final colour of an edit (release, hex submit, swatch).</summary>
        public event Action<Color> ColorCommitted;
        public event Action<MaterialKind> MaterialSelected;
        public event Action<string> NameChanged;
        public event Action<Vector3> PositionChanged;
        /// <summary>Euler angles in degrees.</summary>
        public event Action<Vector3> RotationChanged;
        public event Action<Vector3> ScaleChanged;
        public event Action<bool> SolidChanged;
        public event Action<bool> LockedChanged;
        public event Action DuplicateRequested;
        public event Action DeleteRequested;
        public event Action UndoRequested;
        public event Action RedoRequested;
        /// <summary>The panel's close button; UIRoot turns it into BuildModeToggled.</summary>
        public event Action CloseRequested;

        public ShapeKind Shape { get; private set; } = ShapeKind.Box;
        public BuildTool Tool { get; private set; } = BuildTool.Place;
        public bool SnapEnabled => _snap.value;
        public float SnapStep { get; private set; } = 0.25f;
        public Color Color => ColorPicker.Color;
        public MaterialKind Material { get; private set; } = MaterialKind.Matte;
        public bool BuildAllowed { get; private set; } = true;
        public bool HasSelection { get; private set; }
        public SelectedObjectInfo Selection { get; private set; }
        public bool IsInspectorReadOnly { get; private set; }
        public bool CanUndo => _undo.enabledSelf;
        public bool CanRedo => _redo.enabledSelf;
        public string DisabledReason => BuildAllowed ? null : _disabledText.text;

        internal BuildPanel(VisualElement root) : base(root)
        {
            _undo = Q<Button>("buildUndo");
            _redo = Q<Button>("buildRedo");
            _close = Q<Button>("buildClose");
            _undo.Add(new Icon(IconKind.Undo));
            _redo.Add(new Icon(IconKind.Redo));
            _close.Add(new Icon(IconKind.Close));
            _undo.clicked += () => UndoRequested?.Invoke();
            _redo.clicked += () => RedoRequested?.Invoke();
            _close.clicked += () => CloseRequested?.Invoke();

            _disabled = Q<VisualElement>("buildDisabled");
            _disabledText = Q<Label>("buildDisabledText");
            _tools = Q<VisualElement>("buildTools");
            // Larger wheel steps so the panel scrolls briskly when it is taller than the window.
            ((ScrollView)_tools).mouseWheelScrollSize = 60f;

            var shapeRow = Q<VisualElement>("buildShapes");
            foreach (var s in Shapes)
            {
                var shape = s;
                var b = IconButton(Icon.For(s), Title(s.ToString()), "tv-tile");
                b.clicked += () => { SetShape(shape); ShapeSelected?.Invoke(shape); };
                _shapeButtons[s] = b;
                shapeRow.Add(b);
            }

            var toolRow = Q<VisualElement>("buildToolRow");
            string[] toolKeys = { "1", "2", "3", "4", "5" };
            for (int i = 0; i < Tools.Length; i++)
            {
                var tool = Tools[i];
                var b = IconButton(Icon.For(tool), Title(tool.ToString()), "tv-segment");
                b.tooltip = Title(tool.ToString()) + " (" + toolKeys[i] + ")";
                b.clicked += () => ChooseTool(tool);
                _toolButtons[tool] = b;
                toolRow.Add(b);
            }

            _snap = Q<Toggle>("buildSnap");
            _snapStep = Q<TextField>("buildSnapStep");
            _snapStep.maxLength = 6;
            _snap.RegisterValueChangedCallback(e => { _snapStep.SetEnabled(e.newValue); SnapChanged?.Invoke(e.newValue, SnapStep); });
            _snapStep.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { e.StopPropagation(); CommitSnapStep(_snapStep.value); }
            }, TrickleDown.TrickleDown);
            _snapStep.RegisterCallback<FocusOutEvent>(_ => CommitSnapStep(_snapStep.value));

            ColorPicker = new ColorPicker(Q<VisualElement>("colorPicker"));
            ColorPicker.ColorChanged += c => ColorChanged?.Invoke(c);
            ColorPicker.ColorCommitted += c => ColorCommitted?.Invoke(c);

            var materialRow = Q<VisualElement>("buildMaterials");
            foreach (var m in Materials)
            {
                var mat = m;
                var b = new Button(() => { SetMaterial(mat); MaterialSelected?.Invoke(mat); }) { tooltip = Title(m.ToString()) };
                b.AddToClassList("tv-material");
                var chip = new VisualElement { pickingMode = PickingMode.Ignore };
                chip.AddToClassList("tv-material__chip");
                chip.AddToClassList("tv-material__chip--" + m.ToString().ToLowerInvariant());
                var shine = new VisualElement { pickingMode = PickingMode.Ignore };
                shine.AddToClassList("tv-material__shine");
                chip.Add(shine);
                b.Add(chip);
                var label = UIUtil.Label(Title(m.ToString()), "tv-material__label");
                label.pickingMode = PickingMode.Ignore;
                b.Add(label);
                _materialButtons[m] = b;
                materialRow.Add(b);
            }

            _inspectorBody = Q<VisualElement>("inspectorBody");
            _meta = Q<VisualElement>("inspectorMeta");
            _nothing = Q<Label>("inspectorNothing");
            _readOnlyNote = Q<Label>("inspectorReadOnly");
            _name = Q<TextField>("inspectorName");
            _name.maxLength = Validation.MaxObjectNameLength;
            _name.textEdition.placeholder = "Unnamed";
            _name.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { e.StopPropagation(); CommitName(_name.value); }
            }, TrickleDown.TrickleDown);
            _name.RegisterCallback<FocusOutEvent>(_ => CommitName(_name.value));

            PositionFields = new Vector3Fields(Q<VisualElement>("inspectorPosition")) { Step = 0.1f };
            RotationFields = new Vector3Fields(Q<VisualElement>("inspectorRotation"), r => Mathf.Repeat(r + 180f, 360f) - 180f) { Step = 15f };
            ScaleFields = new Vector3Fields(Q<VisualElement>("inspectorScale"), s => Mathf.Clamp(s, MinScale, MaxScale)) { Step = 0.1f };
            PositionFields.Changed += v => { var s = Selection; s.Position = v; Selection = s; PositionChanged?.Invoke(v); };
            RotationFields.Changed += v => { var s = Selection; s.Rotation = v; Selection = s; RotationChanged?.Invoke(v); };
            ScaleFields.Changed += v => { var s = Selection; s.Scale = v; Selection = s; ScaleChanged?.Invoke(v); };

            _solid = Q<Toggle>("inspectorSolid");
            _locked = Q<Toggle>("inspectorLocked");
            _solid.RegisterValueChangedCallback(e => { var s = Selection; s.Solid = e.newValue; Selection = s; SolidChanged?.Invoke(e.newValue); });
            _locked.RegisterValueChangedCallback(e => { var s = Selection; s.Locked = e.newValue; Selection = s; LockedChanged?.Invoke(e.newValue); });
            _owner = Q<Label>("inspectorOwner");
            _owner.enableRichText = false;
            _id = Q<Label>("inspectorId");
            _id.selection.isSelectable = true;
            _duplicate = Q<Button>("inspectorDuplicate");
            _delete = Q<Button>("inspectorDelete");
            _duplicate.Insert(0, new Icon(IconKind.Duplicate));
            _delete.Insert(0, new Icon(IconKind.Trash));
            _duplicate.clicked += () => { if (HasSelection) DuplicateRequested?.Invoke(); };
            _delete.clicked += () => { if (HasSelection && !IsInspectorReadOnly) DeleteRequested?.Invoke(); };

            SetShape(ShapeKind.Box);
            SetTool(BuildTool.Place);
            SetSnap(true, SnapStep);
            SetMaterial(MaterialKind.Matte);
            SetUndoRedo(false, false);
            SetBuildAllowed(true);
            ClearSelection();
            Hide();
        }

        public void SetShape(ShapeKind shape)
        {
            Shape = shape;
            foreach (var kv in _shapeButtons) UIUtil.SetClass(kv.Value, "tv-tile--active", kv.Key == shape);
        }

        public void SetTool(BuildTool tool)
        {
            Tool = tool;
            foreach (var kv in _toolButtons) UIUtil.SetClass(kv.Value, "tv-segment--active", kv.Key == tool);
        }

        /// <summary>Selects a tool as the user would (button or number key); raises <see cref="ToolSelected"/>.</summary>
        public void ChooseTool(BuildTool tool)
        {
            if (!BuildAllowed) return;
            SetTool(tool);
            ToolSelected?.Invoke(tool);
        }

        public void SetSnap(bool enabled, float step)
        {
            SnapStep = Mathf.Clamp(step, MinSnapStep, MaxSnapStep);
            _snap.SetValueWithoutNotify(enabled);
            _snapStep.SetValueWithoutNotify(Validation.FormatFloat(SnapStep));
            _snapStep.SetEnabled(enabled);
        }

        /// <summary>Applies a typed snap step as committing the field would. Returns false (and reverts) if invalid.</summary>
        public bool CommitSnapStep(string text)
        {
            if (!Validation.TryParseFloat(text, out float step) || step <= 0f)
            {
                _snapStep.SetValueWithoutNotify(Validation.FormatFloat(SnapStep));
                return false;
            }
            step = Mathf.Clamp(step, MinSnapStep, MaxSnapStep);
            _snapStep.SetValueWithoutNotify(Validation.FormatFloat(step));
            if (Mathf.Approximately(step, SnapStep)) return true;
            SnapStep = step;
            SnapChanged?.Invoke(SnapEnabled, step);
            return true;
        }

        public void SetColor(Color c) => ColorPicker.SetColor(c);

        public void SetMaterial(MaterialKind material)
        {
            Material = material;
            foreach (var kv in _materialButtons) UIUtil.SetClass(kv.Value, "tv-material--active", kv.Key == material);
        }

        public void SetUndoRedo(bool canUndo, bool canRedo)
        {
            _undo.SetEnabled(canUndo);
            _redo.SetEnabled(canRedo);
            _undo.tooltip = "Undo (Ctrl+Z)";
            _redo.tooltip = "Redo (Ctrl+Y)";
        }

        /// <summary>Whether the user may build at all. When not, the tools are replaced by <paramref name="reason"/>.</summary>
        public void SetBuildAllowed(bool allowed, string reason = null)
        {
            BuildAllowed = allowed;
            _disabledText.text = string.IsNullOrEmpty(reason)
                ? "Visitors can look around but not build. Ask an owner of this world to make you a builder."
                : reason;
            UIUtil.SetShown(_disabled, !allowed);
            UIUtil.SetShown(_tools, allowed);
        }

        /// <summary>Shows the selected object. With <paramref name="editable"/> false, the inspector is read-only.</summary>
        public void SetSelection(SelectedObjectInfo info, bool editable = true)
        {
            HasSelection = true;
            Selection = info;
            UIUtil.SetShown(_nothing, false);
            UIUtil.SetShown(_inspectorBody, true);
            UIUtil.SetShown(_meta, true);
            if (_name.focusController?.focusedElement != _name) _name.SetValueWithoutNotify(info.Name ?? "");
            PositionFields.SetValue(info.Position);
            RotationFields.SetValue(info.Rotation);
            ScaleFields.SetValue(info.Scale);
            _solid.SetValueWithoutNotify(info.Solid);
            _locked.SetValueWithoutNotify(info.Locked);
            _owner.text = string.IsNullOrEmpty(info.OwnerName) ? "unknown" : info.OwnerName;
            _id.text = "#" + info.Id.ToString(CultureInfo.InvariantCulture);
            SetShape(info.Shape);
            SetMaterial(info.Material);
            ColorPicker.SetColor(info.Color);
            SetInspectorReadOnly(!editable);
        }

        /// <summary>Updates only the transform, for live changes while a gizmo is dragged. Fields being typed in are left alone.</summary>
        public void UpdateSelectionTransform(Vector3 position, Vector3 rotation, Vector3 scale)
        {
            if (!HasSelection) return;
            var s = Selection;
            s.Position = position;
            s.Rotation = rotation;
            s.Scale = scale;
            Selection = s;
            PositionFields.SetValue(position);
            RotationFields.SetValue(rotation);
            ScaleFields.SetValue(scale);
        }

        public void ClearSelection()
        {
            HasSelection = false;
            Selection = default;
            UIUtil.SetShown(_nothing, true);
            UIUtil.SetShown(_inspectorBody, false);
            UIUtil.SetShown(_meta, false);
            SetInspectorReadOnly(false);
        }

        /// <summary>Marks the inspector fields read-only (for an object the user may not edit).</summary>
        public void SetInspectorReadOnly(bool readOnly)
        {
            IsInspectorReadOnly = readOnly;
            _name.isReadOnly = readOnly;
            _name.focusable = !readOnly;
            UIUtil.SetClass(_name, "tv-field--readonly", readOnly);
            PositionFields.SetReadOnly(readOnly);
            RotationFields.SetReadOnly(readOnly);
            ScaleFields.SetReadOnly(readOnly);
            _solid.SetEnabled(!readOnly);
            _locked.SetEnabled(!readOnly);
            _delete.SetEnabled(!readOnly);
            UIUtil.SetShown(_readOnlyNote, readOnly && HasSelection);
        }

        /// <summary>Applies a typed name as committing the field would.</summary>
        public void CommitName(string text)
        {
            if (!HasSelection || IsInspectorReadOnly) return;
            string n = (text ?? "").Trim();
            if (n.Length > Validation.MaxObjectNameLength) n = n.Substring(0, Validation.MaxObjectNameLength);
            _name.SetValueWithoutNotify(n);
            if (n == (Selection.Name ?? "")) return;
            var s = Selection;
            s.Name = n;
            Selection = s;
            NameChanged?.Invoke(n);
        }

        internal Button ShapeButton(ShapeKind s) => _shapeButtons[s];
        internal Button ToolButton(BuildTool t) => _toolButtons[t];
        internal Button MaterialButton(MaterialKind m) => _materialButtons[m];
        internal Button UndoButton => _undo;
        internal Button RedoButton => _redo;
        internal Button CloseButton => _close;
        internal Button DuplicateButton => _duplicate;
        internal Button DeleteButton => _delete;
        internal Toggle SnapToggle => _snap;
        internal TextField SnapStepField => _snapStep;
        internal TextField NameField => _name;
        internal Toggle SolidToggle => _solid;
        internal Toggle LockedToggle => _locked;
        internal string OwnerText => _owner.text;
        internal string IdText => _id.text;
        internal bool NothingSelectedShown => UIUtil.IsShown(_nothing);

        static Button IconButton(IconKind icon, string label, string cls)
        {
            var b = new Button();
            b.AddToClassList(cls);
            b.Add(new Icon(icon));
            if (label != null)
            {
                var l = UIUtil.Label(label, cls + "__label");
                l.pickingMode = PickingMode.Ignore;
                b.Add(l);
                b.tooltip = label;
            }
            return b;
        }

        static string Title(string enumName) => enumName;
    }
}
