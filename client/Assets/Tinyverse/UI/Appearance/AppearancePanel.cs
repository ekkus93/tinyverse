using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>One choice in a list of body presets or hair styles: the id sent over the wire and a label.</summary>
    public readonly struct AppearanceOption
    {
        public readonly string Id;
        public readonly string Label;

        public AppearanceOption(string id, string label)
        {
            Id = id ?? "";
            Label = string.IsNullOrEmpty(label) ? Id : label;
        }
    }

    /// <summary>What the appearance editor edits. Colours are sRGB, opaque.</summary>
    public struct AppearanceChoice
    {
        public string BodyPreset;
        public string HairStyle;
        public Color Skin;
        public Color Hair;
        public Color Top;
        public Color Bottom;

        public override string ToString() =>
            $"{BodyPreset} {HairStyle} skin {ColorMath.ToHex(Skin)} hair {ColorMath.ToHex(Hair)} top {ColorMath.ToHex(Top)} bottom {ColorMath.ToHex(Bottom)}";
    }

    /// <summary>
    /// The appearance editor: body preset, hair style, skin tone, hair colour, top and trouser colours. It sits on
    /// the right so the own avatar stays visible; every change raises <see cref="Changed"/> for a live preview,
    /// Done raises <see cref="Saved"/>, Cancel (or Escape, which opens the menu) raises <see cref="Cancelled"/>
    /// with the choice the panel was opened with. Storing and sending is the glue's job.
    /// </summary>
    public sealed class AppearancePanel : UIPanel
    {
        public const string AssetPath = UIResources.Folder + "Appearance";

        public static readonly IReadOnlyList<Color> SkinTones = new[]
        {
            Hex("#f6dccb"), Hex("#efc8ae"), Hex("#e3b190"), Hex("#d69f7e"), Hex("#c68a62"),
            Hex("#ad7650"), Hex("#8d5a3a"), Hex("#70452b"), Hex("#56331f"), Hex("#3e2416"),
        };

        public static readonly IReadOnlyList<Color> HairColors = new[]
        {
            Hex("#151210"), Hex("#2c1f17"), Hex("#4a3122"), Hex("#6e4a2e"), Hex("#8f3c1e"),
            Hex("#b8642e"), Hex("#c9a36a"), Hex("#e6d3a3"), Hex("#9c9c9c"), Hex("#e8e6e1"),
        };

        public static readonly IReadOnlyList<Color> ClothColors = new[]
        {
            Hex("#f2f2ee"), Hex("#b9bcc2"), Hex("#5a5e66"), Hex("#25272d"), Hex("#4d8bd9"),
            Hex("#2a4a7f"), Hex("#3e9b6a"), Hex("#4f5d37"), Hex("#d8b04a"), Hex("#d96a2b"),
            Hex("#c63d46"), Hex("#7a2f3b"), Hex("#8a5ac0"), Hex("#e48fb0"), Hex("#8a6a4a"),
            Hex("#c9b28d"),
        };

        readonly VisualElement _body, _hairStyle, _skin, _hairColor, _top, _bottom;
        readonly TextField _topHex, _bottomHex;
        readonly List<(Button button, string id)> _bodyButtons = new List<(Button, string)>();
        readonly List<(Button button, string id)> _hairButtons = new List<(Button, string)>();
        AppearanceChoice _choice;
        AppearanceChoice _original;

        /// <summary>Any change by the user, for a live preview.</summary>
        public event Action<AppearanceChoice> Changed;
        /// <summary>Done: keep the choice.</summary>
        public event Action<AppearanceChoice> Saved;
        /// <summary>Cancel or Escape: the choice the panel was opened with, to restore.</summary>
        public event Action<AppearanceChoice> Cancelled;

        public AppearanceChoice Choice => _choice;

        public AppearancePanel(VisualElement root) : base(root)
        {
            _body = Q<VisualElement>("appearanceBody");
            _hairStyle = Q<VisualElement>("appearanceHairStyle");
            _skin = Q<VisualElement>("appearanceSkin");
            _hairColor = Q<VisualElement>("appearanceHairColor");
            _top = Q<VisualElement>("appearanceTop");
            _bottom = Q<VisualElement>("appearanceBottom");
            _topHex = Q<TextField>("appearanceTopHex");
            _bottomHex = Q<TextField>("appearanceBottomHex");
            Q<Button>("appearanceDone").clicked += Done;
            Q<Button>("appearanceCancel").clicked += Cancel;

            Swatches(_skin, SkinTones, c => Change(x => { x.Skin = c; return x; }));
            Swatches(_hairColor, HairColors, c => Change(x => { x.Hair = c; return x; }));
            Swatches(_top, ClothColors, c => Change(x => { x.Top = c; return x; }));
            Swatches(_bottom, ClothColors, c => Change(x => { x.Bottom = c; return x; }));
            HexField(_topHex, () => _choice.Top, c => Change(x => { x.Top = c; return x; }));
            HexField(_bottomHex, () => _choice.Bottom, c => Change(x => { x.Bottom = c; return x; }));
            Hide();
        }

        /// <summary>
        /// Creates the panel inside <paramref name="ui"/> (under the overlays, so the menu covers it) and adds an
        /// "Appearance" button to the in-world menu that opens it. Escape and leaving the world cancel it.
        /// </summary>
        public static AppearancePanel Attach(UIRoot ui)
        {
            var panel = new AppearancePanel(UIResources.Clone(AssetPath));
            var root = ui.RootElement;
            var overlay = root.Q<VisualElement>("overlay");
            int index = overlay != null ? root.IndexOf(overlay) : -1;
            if (index >= 0) root.Insert(index, panel.Root);
            else root.Add(panel.Root);

            var settingsButton = ui.Menu.Root.Q<Button>("menuSettings");
            var open = new Button(() =>
            {
                ui.CloseMenu();
                panel.OpenRequested?.Invoke();
            }) { name = "menuAppearance", text = "Appearance" };
            foreach (var cls in settingsButton?.GetClasses() ?? new string[0]) open.AddToClassList(cls);
            if (settingsButton != null) settingsButton.parent.Insert(settingsButton.parent.IndexOf(settingsButton), open);
            panel.MenuButton = open;

            ui.Menu.VisibilityChanged += shown => { if (shown && panel.IsVisible) panel.Cancel(); };
            ui.ScreenChanged += screen => { if (screen != UIScreen.Hud && panel.IsVisible) panel.Cancel(); };
            return panel;
        }

        /// <summary>The menu's Appearance button was clicked (the menu has closed); the glue calls <see cref="Open"/>.</summary>
        public event Action OpenRequested;

        /// <summary>The button added to the in-world menu by <see cref="Attach"/>.</summary>
        public Button MenuButton { get; private set; }

        /// <summary>Lists the body presets and hair styles to offer.</summary>
        public void SetOptions(IReadOnlyList<AppearanceOption> bodyPresets, IReadOnlyList<AppearanceOption> hairStyles)
        {
            _body.Clear();
            _bodyButtons.Clear();
            foreach (var o in bodyPresets ?? new AppearanceOption[0])
            {
                string id = o.Id;
                var b = UIUtil.Button(o.Label, "tv-segment", () => Change(x => { x.BodyPreset = id; return x; }));
                b.name = "appearanceBody-" + id;
                _body.Add(b);
                _bodyButtons.Add((b, id));
            }
            _hairStyle.Clear();
            _hairButtons.Clear();
            foreach (var o in hairStyles ?? new AppearanceOption[0])
            {
                string id = o.Id;
                var b = UIUtil.Button(o.Label, "tv-appearance__chip", () => Change(x => { x.HairStyle = id; return x; }));
                b.name = "appearanceHair-" + id;
                _hairStyle.Add(b);
                _hairButtons.Add((b, id));
            }
            Refresh();
        }

        /// <summary>Shows <paramref name="current"/> and remembers it for Cancel.</summary>
        public void Open(AppearanceChoice current)
        {
            _original = current;
            SetChoice(current);
            Show();
        }

        /// <summary>Shows a choice without raising <see cref="Changed"/>.</summary>
        public void SetChoice(AppearanceChoice c)
        {
            _choice = c;
            Refresh();
        }

        public void Done()
        {
            if (!IsVisible) return;
            Hide();
            Saved?.Invoke(_choice);
        }

        public void Cancel()
        {
            if (!IsVisible) return;
            _choice = _original;
            Hide();
            Cancelled?.Invoke(_original);
        }

        void Change(Func<AppearanceChoice, AppearanceChoice> apply)
        {
            _choice = apply(_choice);
            Refresh();
            Changed?.Invoke(_choice);
        }

        void Swatches(VisualElement row, IReadOnlyList<Color> colors, Action<Color> pick)
        {
            for (int i = 0; i < colors.Count; i++)
            {
                var c = colors[i];
                var b = new Button(() => pick(c)) { tooltip = ColorMath.ToHex(c) };
                b.AddToClassList("tv-appearance__swatch");
                b.userData = c;
                var fill = new VisualElement { pickingMode = PickingMode.Ignore };
                fill.AddToClassList("tv-appearance__swatch-fill");
                fill.style.backgroundColor = c;
                b.Add(fill);
                row.Add(b);
            }
        }

        void HexField(TextField field, Func<Color> current, Action<Color> apply)
        {
            void Submit()
            {
                if (ColorMath.TryParseHex(field.value, out var c))
                {
                    c.a = 1f;
                    if (ColorMath.ToHex(c) != ColorMath.ToHex(current())) apply(c);
                }
                else Refresh();
            }
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                Submit();
                field.Blur();
            }, TrickleDown.TrickleDown);
            field.RegisterCallback<FocusOutEvent>(_ => Submit());
        }

        void Refresh()
        {
            foreach (var (b, id) in _bodyButtons) UIUtil.SetClass(b, "tv-segment--active", Same(id, _choice.BodyPreset));
            foreach (var (b, id) in _hairButtons) UIUtil.SetClass(b, "tv-toggle-button--on", Same(id, _choice.HairStyle));
            Mark(_skin, _choice.Skin);
            Mark(_hairColor, _choice.Hair);
            Mark(_top, _choice.Top);
            Mark(_bottom, _choice.Bottom);
            _topHex.SetValueWithoutNotify(ColorMath.ToHex(_choice.Top));
            _bottomHex.SetValueWithoutNotify(ColorMath.ToHex(_choice.Bottom));
        }

        static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        static void Mark(VisualElement row, Color selected)
        {
            foreach (var child in row.Children())
                UIUtil.SetClass(child, "tv-appearance__swatch--selected",
                    child.userData is Color c && ColorMath.ToHex(c) == ColorMath.ToHex(selected));
        }

        static Color Hex(string s) => ColorMath.TryParseHex(s, out var c) ? c : Color.magenta;

        // Test hooks: act as a user would.
        internal void PickBody(string id) => Click(_body, "appearanceBody-" + id);
        internal void PickHairStyle(string id) => Click(_hairStyle, "appearanceHair-" + id);
        internal void PickSkin(int index) => ClickAt(_skin, index);
        internal void PickHairColor(int index) => ClickAt(_hairColor, index);
        internal void PickTop(int index) => ClickAt(_top, index);
        internal void PickBottom(int index) => ClickAt(_bottom, index);
        internal TextField TopHexField => _topHex;
        internal int BodyOptionCount => _bodyButtons.Count;
        internal int HairOptionCount => _hairButtons.Count;

        static void Click(VisualElement parent, string name)
        {
            var b = UIUtil.Require<Button>(parent, name);
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
        }

        static void ClickAt(VisualElement row, int index)
        {
            var b = (Button)row[index];
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
        }
    }
}
