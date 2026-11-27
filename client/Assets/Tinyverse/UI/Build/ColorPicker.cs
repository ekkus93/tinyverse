using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>
    /// Saturation/value square, hue strip, opacity slider, hex field and recent colours.
    /// <see cref="ColorChanged"/> fires continuously while dragging; <see cref="ColorCommitted"/>
    /// fires once when the user lets go, submits the hex field or picks a swatch.
    /// </summary>
    public sealed class ColorPicker
    {
        public const int MaxRecent = 10;
        const int TexSize = 64;

        static Texture2D s_hue, s_white, s_black;

        readonly VisualElement _root;
        readonly VisualElement _sv;
        readonly VisualElement _svCursor;
        readonly VisualElement _hue;
        readonly VisualElement _hueCursor;
        readonly Slider _alpha;
        readonly TextField _hex;
        readonly VisualElement _preview;
        readonly VisualElement _recentRow;
        readonly List<Color> _recent = new List<Color>();
        Hsv _hsv = new Hsv(0.58f, 0.5f, 0.85f);
        float _a = 1f;
        bool _alphaDragging;

        public event Action<Color> ColorChanged;
        public event Action<Color> ColorCommitted;

        public Color Color => ColorMath.HsvToRgb(_hsv, _a);
        public Hsv Hsv => _hsv;
        public string HexText => _hex.value;
        public IReadOnlyList<Color> RecentColors => _recent;

        internal ColorPicker(VisualElement root)
        {
            _root = root;
            _sv = UIUtil.Require<VisualElement>(root, "colorSv");
            _svCursor = UIUtil.Require<VisualElement>(root, "colorSvCursor");
            _hue = UIUtil.Require<VisualElement>(root, "colorHue");
            _hueCursor = UIUtil.Require<VisualElement>(root, "colorHueCursor");
            _alpha = UIUtil.Require<Slider>(root, "colorAlpha");
            _hex = UIUtil.Require<TextField>(root, "colorHex");
            _preview = UIUtil.Require<VisualElement>(root, "colorPreview");
            _recentRow = UIUtil.Require<VisualElement>(root, "colorRecent");

            EnsureTextures();
            UIUtil.Require<VisualElement>(root, "colorSvWhite").style.backgroundImage = new StyleBackground(s_white);
            UIUtil.Require<VisualElement>(root, "colorSvBlack").style.backgroundImage = new StyleBackground(s_black);
            _hue.style.backgroundImage = new StyleBackground(s_hue);
            _svCursor.pickingMode = PickingMode.Ignore;
            _hueCursor.pickingMode = PickingMode.Ignore;

            RegisterDrag(_sv, p => SetSv(p.x, 1f - p.y));
            RegisterDrag(_hue, p => SetHue(p.x));

            _alpha.lowValue = 0f;
            _alpha.highValue = 1f;
            _alpha.RegisterValueChangedCallback(e =>
            {
                _a = e.newValue;
                Refresh();
                ColorChanged?.Invoke(Color);
                if (!_alphaDragging) Commit();
            });
            _alpha.RegisterCallback<PointerDownEvent>(_ => _alphaDragging = true, TrickleDown.TrickleDown);
            _alpha.RegisterCallback<PointerCaptureOutEvent>(_ => EndAlphaDrag(), TrickleDown.TrickleDown);
            _alpha.RegisterCallback<PointerUpEvent>(_ => EndAlphaDrag(), TrickleDown.TrickleDown);

            _hex.maxLength = 9;
            _hex.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.StopPropagation();
                    SubmitHex(_hex.value);
                }
            }, TrickleDown.TrickleDown);
            _hex.RegisterCallback<FocusOutEvent>(_ => SubmitHex(_hex.value));

            Refresh();
            RebuildRecent();
        }

        /// <summary>Shows a colour without raising events. A grey keeps the current hue.</summary>
        public void SetColor(Color c)
        {
            var hsv = ColorMath.RgbToHsv(c);
            if (hsv.S <= 1e-4f || hsv.V <= 1e-4f) hsv.H = _hsv.H;
            if (hsv.V <= 1e-4f) hsv.S = _hsv.S;
            _hsv = hsv;
            _a = Mathf.Clamp01(c.a);
            Refresh();
        }

        /// <summary>Sets HSV as the user would; raises <see cref="ColorChanged"/>.</summary>
        public void SetHsv(Hsv hsv)
        {
            _hsv = new Hsv(hsv.H - Mathf.Floor(hsv.H), Mathf.Clamp01(hsv.S), Mathf.Clamp01(hsv.V));
            Refresh();
            ColorChanged?.Invoke(Color);
        }

        /// <summary>Picks a point in the SV square (0..1 each, value 1 at the top); raises <see cref="ColorChanged"/>.</summary>
        public void SetSv(float saturation, float value) => SetHsv(new Hsv(_hsv.H, saturation, value));

        /// <summary>Picks a hue (0..1); raises <see cref="ColorChanged"/>.</summary>
        public void SetHue(float hue) => SetHsv(new Hsv(Mathf.Clamp(hue, 0f, 0.9999f), _hsv.S, _hsv.V));

        /// <summary>Parses and applies a hex colour as if typed and submitted. Returns false (and reverts) if invalid.</summary>
        public bool SubmitHex(string text)
        {
            if (!ColorMath.TryParseHex(text, out var c))
            {
                _hex.AddToClassList("tv-field--invalid");
                _hex.SetValueWithoutNotify(ColorMath.ToHex(Color));
                return false;
            }
            _hex.RemoveFromClassList("tv-field--invalid");
            // Typing only RGB keeps the current opacity.
            if (text.Trim().TrimStart('#').Length <= 6) c.a = _a;
            bool changed = !ColorMath.Approximately(c, Color);
            SetColor(c);
            if (changed)
            {
                ColorChanged?.Invoke(Color);
                Commit();
            }
            return true;
        }

        /// <summary>Picks a recent colour, as clicking its swatch does.</summary>
        public void PickRecent(int index)
        {
            if (index < 0 || index >= _recent.Count) return;
            SetColor(_recent[index]);
            ColorChanged?.Invoke(Color);
            Commit();
        }

        /// <summary>Ends an edit: remembers the colour among the recent ones and raises <see cref="ColorCommitted"/>.</summary>
        public void Commit()
        {
            var c = Color;
            AddRecent(c);
            ColorCommitted?.Invoke(c);
        }

        public void SetRecentColors(IEnumerable<Color> colors)
        {
            _recent.Clear();
            if (colors != null)
                foreach (var c in colors)
                {
                    if (_recent.Count >= MaxRecent) break;
                    _recent.Add(c);
                }
            RebuildRecent();
        }

        public void SetEnabled(bool enabled) => _root.SetEnabled(enabled);

        void AddRecent(Color c)
        {
            _recent.RemoveAll(x => ColorMath.Approximately(x, c));
            _recent.Insert(0, c);
            if (_recent.Count > MaxRecent) _recent.RemoveAt(_recent.Count - 1);
            RebuildRecent();
        }

        void EndAlphaDrag()
        {
            if (!_alphaDragging) return;
            _alphaDragging = false;
            Commit();
        }

        void RegisterDrag(VisualElement area, Action<Vector2> pick)
        {
            area.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                area.CapturePointer(e.pointerId);
                pick(Normalized(area, e.localPosition));
                e.StopPropagation();
            });
            area.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (area.HasPointerCapture(e.pointerId)) pick(Normalized(area, e.localPosition));
            });
            area.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!area.HasPointerCapture(e.pointerId)) return;
                area.ReleasePointer(e.pointerId);
                Commit();
            });
        }

        static Vector2 Normalized(VisualElement area, Vector3 local)
        {
            float w = Mathf.Max(1f, area.layout.width), h = Mathf.Max(1f, area.layout.height);
            return new Vector2(Mathf.Clamp01(local.x / w), Mathf.Clamp01(local.y / h));
        }

        void Refresh()
        {
            var c = Color;
            _sv.style.backgroundColor = ColorMath.HsvToRgb(new Hsv(_hsv.H, 1f, 1f));
            _svCursor.style.left = Length.Percent(_hsv.S * 100f);
            _svCursor.style.top = Length.Percent((1f - _hsv.V) * 100f);
            _hueCursor.style.left = Length.Percent(_hsv.H * 100f);
            _preview.style.backgroundColor = c;
            _alpha.SetValueWithoutNotify(_a);
            if (_hex.focusController?.focusedElement != _hex) _hex.SetValueWithoutNotify(ColorMath.ToHex(c));
            _hex.RemoveFromClassList("tv-field--invalid");
        }

        void RebuildRecent()
        {
            _recentRow.Clear();
            for (int i = 0; i < MaxRecent; i++)
            {
                var swatch = new Button { focusable = i < _recent.Count };
                swatch.AddToClassList("tv-swatch");
                if (i < _recent.Count)
                {
                    int index = i;
                    var c = _recent[i];
                    var fill = new VisualElement { pickingMode = PickingMode.Ignore };
                    fill.AddToClassList("tv-swatch__fill");
                    fill.style.backgroundColor = c;
                    swatch.Add(fill);
                    swatch.tooltip = ColorMath.ToHex(c, c.a < 1f);
                    swatch.clicked += () => PickRecent(index);
                }
                else
                {
                    swatch.AddToClassList("tv-swatch--empty");
                    swatch.SetEnabled(false);
                }
                _recentRow.Add(swatch);
            }
        }

        internal TextField HexField => _hex;
        internal Slider AlphaSlider => _alpha;

        static void EnsureTextures()
        {
            if (s_hue != null) return;
            s_hue = NewTexture(TexSize, 1);
            s_white = NewTexture(TexSize, 1);
            s_black = NewTexture(1, TexSize);
            for (int i = 0; i < TexSize; i++)
            {
                float t = i / (TexSize - 1f);
                s_hue.SetPixel(i, 0, ColorMath.HsvToRgb(new Hsv(Mathf.Min(t, 0.9999f), 1f, 1f)));
                s_white.SetPixel(i, 0, new Color(1f, 1f, 1f, 1f - t));
                // Texture rows go bottom-up: black at the bottom, clear at the top.
                s_black.SetPixel(0, i, new Color(0f, 0f, 0f, 1f - t));
            }
            s_hue.Apply(false, true);
            s_white.Apply(false, true);
            s_black.Apply(false, true);
        }

        static Texture2D NewTexture(int w, int h) => new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
            name = "TinyverseColorPicker",
        };
    }
}
