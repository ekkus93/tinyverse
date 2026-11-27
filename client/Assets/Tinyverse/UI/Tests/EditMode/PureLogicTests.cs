using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI.Tests
{
    public class ColorMathTests
    {
        static readonly Color[] Samples =
        {
            Color.red, Color.green, Color.blue, Color.white, Color.black, Color.gray,
            new Color(1f, 0.5f, 0f), new Color(0.2f, 0.4f, 0.6f), new Color(0.9f, 0.1f, 0.7f), new Color(0.13f, 0.87f, 0.52f),
        };

        [Test]
        public void PrimaryColoursHaveExpectedHsv()
        {
            AssertHsv(ColorMath.RgbToHsv(Color.red), 0f, 1f, 1f);
            AssertHsv(ColorMath.RgbToHsv(Color.green), 1f / 3f, 1f, 1f);
            AssertHsv(ColorMath.RgbToHsv(Color.blue), 2f / 3f, 1f, 1f);
            AssertHsv(ColorMath.RgbToHsv(Color.white), 0f, 0f, 1f);
            AssertHsv(ColorMath.RgbToHsv(Color.black), 0f, 0f, 0f);
            AssertHsv(ColorMath.RgbToHsv(new Color(1f, 0f, 1f)), 5f / 6f, 1f, 1f);
        }

        [Test]
        public void RgbHsvRoundTrip()
        {
            foreach (var c in Samples)
            {
                var back = ColorMath.HsvToRgb(ColorMath.RgbToHsv(c));
                Assert.That(ColorMath.Approximately(back, c), Is.True, $"{c} -> {back}");
            }
        }

        [Test]
        public void HsvToRgbMatchesUnity()
        {
            for (float h = 0f; h < 1f; h += 0.07f)
            for (float s = 0f; s <= 1f; s += 0.25f)
            for (float v = 0f; v <= 1f; v += 0.25f)
            {
                var mine = ColorMath.HsvToRgb(new Hsv(h, s, v));
                var unity = Color.HSVToRGB(h, s, v);
                Assert.That(ColorMath.Approximately(mine, unity), Is.True, $"h={h} s={s} v={v}: {mine} vs {unity}");
            }
        }

        [Test]
        public void HueWrapsAndValuesClamp()
        {
            Assert.That(ColorMath.Approximately(ColorMath.HsvToRgb(new Hsv(1.25f, 1f, 1f)), ColorMath.HsvToRgb(new Hsv(0.25f, 1f, 1f))), Is.True);
            Assert.That(ColorMath.Approximately(ColorMath.HsvToRgb(new Hsv(0f, 2f, 3f)), Color.red), Is.True);
        }

        [Test]
        public void HexRoundTrip()
        {
            foreach (var c in Samples)
            {
                Assert.That(ColorMath.TryParseHex(ColorMath.ToHex(c), out var back), Is.True);
                Assert.That(ColorMath.Approximately(back, c), Is.True);
            }
            Assert.That(ColorMath.ToHex(new Color(1f, 0.5f, 0f)), Is.EqualTo("#FF8000"));
            Assert.That(ColorMath.ToHex(new Color(0f, 0f, 1f, 0.5f), true), Is.EqualTo("#0000FF80"));
        }

        [TestCase("#FF8000", 1f, 128 / 255f, 0f, 1f)]
        [TestCase("ff8000", 1f, 128 / 255f, 0f, 1f)]
        [TestCase("  #f80 ", 1f, 136 / 255f, 0f, 1f)]
        [TestCase("#00000080", 0f, 0f, 0f, 128 / 255f)]
        public void ParsesHexForms(string text, float r, float g, float b, float a)
        {
            Assert.That(ColorMath.TryParseHex(text, out var c), Is.True);
            Assert.That(ColorMath.Approximately(c, new Color(r, g, b, a)), Is.True, c.ToString());
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("#12")]
        [TestCase("#12345")]
        [TestCase("#GGGGGG")]
        [TestCase("#1234567")]
        [TestCase("red")]
        public void RejectsBadHex(string text) => Assert.That(ColorMath.TryParseHex(text, out _), Is.False);

        static void AssertHsv(Hsv hsv, float h, float s, float v)
        {
            Assert.That(hsv.H, Is.EqualTo(h).Within(1e-4f), "hue");
            Assert.That(hsv.S, Is.EqualTo(s).Within(1e-4f), "saturation");
            Assert.That(hsv.V, Is.EqualTo(v).Within(1e-4f), "value");
        }
    }

    public class ValidationTests
    {
        [TestCase("1.5", 1.5f)]
        [TestCase(" -2,25 ", -2.25f)]
        [TestCase("1e2", 100f)]
        [TestCase("0", 0f)]
        [TestCase("+3", 3f)]
        public void ParsesNumbers(string text, float expected)
        {
            Assert.That(Validation.TryParseFloat(text, out float v), Is.True);
            Assert.That(v, Is.EqualTo(expected).Within(1e-5f));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        [TestCase("abc")]
        [TestCase("1.2.3")]
        [TestCase("NaN")]
        [TestCase("Infinity")]
        [TestCase("1e60")]
        public void RejectsBadNumbers(string text) => Assert.That(Validation.TryParseFloat(text, out _), Is.False);

        [Test]
        public void FormatsNumbersInvariantly()
        {
            Assert.That(Validation.FormatFloat(1.5f), Is.EqualTo("1.5"));
            Assert.That(Validation.FormatFloat(2f), Is.EqualTo("2"));
            Assert.That(Validation.FormatFloat(-0.12345f), Is.EqualTo("-0.123"));
        }

        [TestCase("7878", true, 7878)]
        [TestCase(" 1 ", true, 1)]
        [TestCase("65535", true, 65535)]
        [TestCase("0", false, 0)]
        [TestCase("65536", false, 0)]
        [TestCase("-1", false, 0)]
        [TestCase("78a", false, 0)]
        [TestCase("", false, 0)]
        public void ParsesPorts(string text, bool ok, int port)
        {
            Assert.That(Validation.TryParsePort(text, out int p), Is.EqualTo(ok));
            if (ok) Assert.That(p, Is.EqualTo(port));
        }

        [TestCase("localhost")]
        [TestCase("localhost:7878")]
        [TestCase("192.168.1.20:7878")]
        [TestCase("my-pc.local")]
        [TestCase("[::1]:7878")]
        [TestCase("[fe80::1]")]
        [TestCase("ws://example.org:7878/ws")]
        [TestCase("wss://example.org")]
        public void AcceptsAddresses(string a) => Assert.That(Validation.ValidateAddress(a), Is.Null);

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("host name")]
        [TestCase(":7878")]
        [TestCase("host:0")]
        [TestCase("host:99999")]
        [TestCase("host:abc")]
        [TestCase("http://host")]
        [TestCase("::1")]
        [TestCase("[::1")]
        [TestCase("ho$t")]
        public void RejectsAddresses(string a) => Assert.That(Validation.ValidateAddress(a), Is.Not.Null);

        [Test]
        public void ValidatesDisplayNames()
        {
            Assert.That(Validation.ValidateDisplayName("Ada"), Is.Null);
            Assert.That(Validation.ValidateDisplayName("  "), Is.Not.Null);
            Assert.That(Validation.ValidateDisplayName(null), Is.Not.Null);
            Assert.That(Validation.ValidateDisplayName(new string('x', Validation.MaxDisplayNameLength)), Is.Null);
            Assert.That(Validation.ValidateDisplayName(new string('x', Validation.MaxDisplayNameLength + 1)), Is.Not.Null);
        }

        [Test]
        public void ValidatesHostSettings()
        {
            var ok = new HostSettings { WorldName = "Home", WorldFolder = "/tmp/w", Port = 7878, DisplayName = "Ada" };
            Assert.That(Validation.ValidateHost(ok, out var field), Is.Null);
            Assert.That(field, Is.Null);

            var s = ok; s.WorldName = " ";
            Assert.That(Validation.ValidateHost(s, out field), Is.Not.Null); Assert.That(field, Is.EqualTo("worldName"));
            s = ok; s.WorldFolder = "";
            Assert.That(Validation.ValidateHost(s, out field), Is.Not.Null); Assert.That(field, Is.EqualTo("worldFolder"));
            s = ok; s.Port = 0;
            Assert.That(Validation.ValidateHost(s, out field), Is.Not.Null); Assert.That(field, Is.EqualTo("port"));
            s = ok; s.DisplayName = "";
            Assert.That(Validation.ValidateHost(s, out field), Is.Not.Null); Assert.That(field, Is.EqualTo("displayName"));
        }

        [Test]
        public void FormatsRelativeTimes()
        {
            var now = new DateTime(2026, 11, 26, 12, 0, 0);
            Assert.That(Validation.RelativeTime(now.AddSeconds(-5), now), Is.EqualTo("just now"));
            Assert.That(Validation.RelativeTime(now.AddMinutes(-5), now), Is.EqualTo("5 min ago"));
            Assert.That(Validation.RelativeTime(now.AddHours(-3), now), Is.EqualTo("3 h ago"));
            Assert.That(Validation.RelativeTime(now.AddHours(-30), now), Is.EqualTo("yesterday"));
            Assert.That(Validation.RelativeTime(now.AddDays(-4), now), Is.EqualTo("4 days ago"));
            Assert.That(Validation.RelativeTime(new DateTime(2025, 1, 2), now), Is.EqualTo("2 Jan 2025"));
            Assert.That(Validation.RelativeTime(default, now), Is.EqualTo(""));
        }
    }

    public class ScrollbackTrackerTests
    {
        [Test]
        public void SticksToBottomUntilScrolledUp()
        {
            var t = new ScrollbackTracker();
            Assert.That(t.OnLineAdded(), Is.True);
            t.OnScrolled(100f, 100f);
            Assert.That(t.StickToBottom, Is.True);
            Assert.That(t.OnLineAdded(), Is.True);
            Assert.That(t.UnseenCount, Is.EqualTo(0));
        }

        [Test]
        public void CountsUnseenWhileScrolledUpAndResetsAtBottom()
        {
            var t = new ScrollbackTracker();
            t.OnScrolled(20f, 100f);
            Assert.That(t.StickToBottom, Is.False);
            Assert.That(t.OnLineAdded(), Is.False);
            Assert.That(t.OnLineAdded(), Is.False);
            Assert.That(t.UnseenCount, Is.EqualTo(2));
            t.OnScrolled(100f - ScrollbackTracker.BottomTolerance + 1f, 100f);
            Assert.That(t.StickToBottom, Is.True);
            Assert.That(t.UnseenCount, Is.EqualTo(0));
        }

        [Test]
        public void ShortContentAlwaysSticks()
        {
            var t = new ScrollbackTracker();
            t.OnScrolled(0f, 0f);
            Assert.That(t.StickToBottom, Is.True);
        }

        [Test]
        public void JumpToBottomResets()
        {
            var t = new ScrollbackTracker();
            t.OnScrolled(0f, 100f);
            t.OnLineAdded();
            t.OnJumpedToBottom();
            Assert.That(t.StickToBottom, Is.True);
            Assert.That(t.UnseenCount, Is.EqualTo(0));
        }
    }

    public class ScaleTests
    {
        [Test]
        public void ReferenceResolutionIsScaleOne()
        {
            Assert.That(UIRoot.ComputeScale((int)UIRoot.ReferenceResolution.x, (int)UIRoot.ReferenceResolution.y, 1f), Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void ScalesWithWindowFrom720pTo4K()
        {
            float s720 = UIRoot.ComputeScale(1280, 720, 1f);
            float s1080 = UIRoot.ComputeScale(1920, 1080, 1f);
            float s4k = UIRoot.ComputeScale(3840, 2160, 1f);
            Assert.That(s720, Is.LessThan(1f).And.GreaterThan(0.8f), "720p stays legible");
            Assert.That(s4k, Is.EqualTo(s1080 * 2f).Within(1e-3f), "4K doubles 1080p");
            Assert.That(UIRoot.ComputeScale(1920, 1080, 1.25f), Is.EqualTo(s1080 * 1.25f).Within(1e-4f), "user UI scale multiplies");
            Assert.That(UIRoot.ComputeScale(0, 0, 1f), Is.EqualTo(1f));
        }
    }

    public class ResourceTests
    {
        [Test]
        public void EveryUIResourceLoadsWithItsType()
        {
            foreach (var (path, type) in UIResources.All)
                Assert.That(Resources.Load(path, type), Is.Not.Null, $"Resources/{path} as {type.Name}");
        }

        [Test]
        public void ThemeIsAThemeStyleSheet()
        {
            var theme = UIResources.Load<ThemeStyleSheet>(UIResources.Theme);
            Assert.That(theme, Is.InstanceOf<ThemeStyleSheet>());
        }

        [Test]
        public void PanelSettingsAssetUsesOurThemeAndConstantPixelSize()
        {
            var ps = UIResources.Load<PanelSettings>(UIResources.PanelSettingsAsset);
            Assert.That(ps.themeStyleSheet, Is.SameAs(UIResources.Load<ThemeStyleSheet>(UIResources.Theme)));
            Assert.That(ps.scaleMode, Is.EqualTo(PanelScaleMode.ConstantPixelSize), "UIRoot computes the scale itself");
        }

        [Test]
        public void MissingResourceThrowsClearly()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => UIResources.Load<VisualTreeAsset>(UIResources.Folder + "NoSuchThing"));
            Assert.That(ex.Message, Does.Contain("NoSuchThing"));
        }

        [Test]
        public void EveryUxmlClonesToOneRoot()
        {
            foreach (var path in new[] { UIResources.Connect, UIResources.Host, UIResources.Hud, UIResources.Build, UIResources.Overlay })
            {
                var root = UIResources.Clone(path);
                Assert.That(root, Is.Not.Null, path);
                Assert.That(root.styleSheets.count, Is.GreaterThan(0), path + " keeps its style sheet");
            }
        }
    }
}
