using System;
using System.Globalization;
using UnityEngine;

namespace Tinyverse.UI
{
    /// <summary>Hue, saturation and value, each 0 to 1.</summary>
    public struct Hsv
    {
        public float H;
        public float S;
        public float V;

        public Hsv(float h, float s, float v)
        {
            H = h;
            S = s;
            V = v;
        }

        public override string ToString() => $"Hsv({H:0.###}, {S:0.###}, {V:0.###})";
    }

    /// <summary>Pure colour conversions used by the colour picker.</summary>
    public static class ColorMath
    {
        /// <summary>Converts RGB (alpha ignored) to HSV. Grey gives hue 0 and saturation 0.</summary>
        public static Hsv RgbToHsv(Color c)
        {
            float r = Mathf.Clamp01(c.r), g = Mathf.Clamp01(c.g), b = Mathf.Clamp01(c.b);
            float max = Mathf.Max(r, Mathf.Max(g, b));
            float min = Mathf.Min(r, Mathf.Min(g, b));
            float d = max - min;
            float h = 0f;
            if (d > 1e-6f)
            {
                if (max == r) h = ((g - b) / d) % 6f;
                else if (max == g) h = (b - r) / d + 2f;
                else h = (r - g) / d + 4f;
                h /= 6f;
                if (h < 0f) h += 1f;
            }
            float s = max <= 1e-6f ? 0f : d / max;
            return new Hsv(h, s, max);
        }

        /// <summary>Converts HSV to an opaque RGB colour. Hue wraps; S and V are clamped.</summary>
        public static Color HsvToRgb(Hsv hsv, float alpha = 1f)
        {
            float h = hsv.H - Mathf.Floor(hsv.H);
            float s = Mathf.Clamp01(hsv.S), v = Mathf.Clamp01(hsv.V);
            float c = v * s;
            float hp = h * 6f;
            float x = c * (1f - Mathf.Abs(hp % 2f - 1f));
            float r, g, b;
            switch ((int)hp)
            {
                case 0: r = c; g = x; b = 0; break;
                case 1: r = x; g = c; b = 0; break;
                case 2: r = 0; g = c; b = x; break;
                case 3: r = 0; g = x; b = c; break;
                case 4: r = x; g = 0; b = c; break;
                default: r = c; g = 0; b = x; break;
            }
            float m = v - c;
            return new Color(r + m, g + m, b + m, alpha);
        }

        /// <summary>Formats as <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when <paramref name="withAlpha"/>.</summary>
        public static string ToHex(Color c, bool withAlpha = false)
        {
            int r = To255(c.r), g = To255(c.g), b = To255(c.b), a = To255(c.a);
            return withAlpha ? $"#{r:X2}{g:X2}{b:X2}{a:X2}" : $"#{r:X2}{g:X2}{b:X2}";
        }

        /// <summary>
        /// Parses <c>RGB</c>, <c>RRGGBB</c> or <c>RRGGBBAA</c>, with or without a leading <c>#</c>,
        /// ignoring surrounding whitespace and case. Alpha is 1 when not given.
        /// </summary>
        public static bool TryParseHex(string text, out Color color)
        {
            color = Color.black;
            if (text == null) return false;
            string s = text.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            if (s.Length != 6 && s.Length != 8) return false;
            if (!uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v)) return false;
            if (s.Length == 6) v = (v << 8) | 0xFF;
            color = new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
            return true;
        }

        /// <summary>True when the colours match to 8-bit precision per channel.</summary>
        public static bool Approximately(Color a, Color b) =>
            To255(a.r) == To255(b.r) && To255(a.g) == To255(b.g) && To255(a.b) == To255(b.b) && To255(a.a) == To255(b.a);

        static int To255(float f) => Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);
    }
}
