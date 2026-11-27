using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// A name above an avatar's head, drawn with the legacy <see cref="TextMesh"/> and Unity's built-in
    /// LegacyRuntime font, so it needs no imported assets. It faces the camera and is sized in screen pixels:
    /// never taller than <see cref="MaxScreenFraction"/> of the screen when near (so it cannot cover the UI), and
    /// kept at least <see cref="MinScreenFraction"/> when far until it reaches <see cref="MaxWorldScale"/>. It fades
    /// out within about a metre of the camera and far away. A dark outline keeps it readable against sky and
    /// ground. An optional "AI" badge sits above the name. Tags that would overlap on screen are nudged apart by
    /// <see cref="NameTagLayout"/>, the nearest staying in place.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class NameTag : MonoBehaviour
    {
        /// <summary>Camera the tags face. Null means <see cref="Camera.main"/>.</summary>
        public static Camera ViewCamera { get; set; }

        private const int FontSize = 64;
        private const float CharacterSize = 0.035f;

        /// <summary>World height of one line of name text at scale 1, metres (TextMesh: fontSize × characterSize / 10).</summary>
        public const float BaseLineHeight = FontSize * CharacterSize / 10f;

        private static readonly Color NameColor = Color.white;
        private static readonly Color BadgeColor = new Color(0.45f, 0.90f, 1f);
        private static readonly Color OutlineColor = new Color(0.05f, 0.06f, 0.09f, 0.9f);
        private static readonly Vector2[] OutlineOffsets =
        {
            new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(-1f, 1f), new Vector2(1f, 1f),
        };
        private static Font font;

        /// <summary>Largest on-screen height of the name line, as a fraction of the screen height (≈ 1.5 chat lines).</summary>
        public float MaxScreenFraction { get; set; } = 0.028f;

        /// <summary>Smallest on-screen height of the name line while it can still grow, fraction of screen height.</summary>
        public float MinScreenFraction { get; set; } = 0.017f;

        /// <summary>Largest growth factor in world space; past it, far tags get smaller and then fade.</summary>
        public float MaxWorldScale { get; set; } = 4f;

        /// <summary>Closer than this to the camera the tag is invisible, metres.</summary>
        public float NearHide { get; set; } = 1f;

        /// <summary>From this distance on the tag is fully opaque again, metres.</summary>
        public float NearFull { get; set; } = 2f;

        /// <summary>Distance where fading starts, metres.</summary>
        public float FadeStart { get; set; } = 35f;

        /// <summary>Distance where the tag is fully gone, metres.</summary>
        public float FadeEnd { get; set; } = 50f;

        private TextMesh nameText, badgeText;
        private TextMesh[] nameOutline, badgeOutline;
        private float alpha = -1f;
        private bool visible = true;
        private bool suppressed;
        private bool isAgent;
        private float anchorHeight;
        private float layoutPixels, layoutTargetPixels;
        private bool layoutShownLastFrame;

        public string Text => nameText != null ? nameText.text : "";
        public bool ShowsAgentBadge => isAgent;

        /// <summary>The current opacity from distance fading, 0…1.</summary>
        public float Alpha => Mathf.Max(0f, alpha);

        /// <summary>True when the tag is drawn at all (visible, not suppressed, not faded out).</summary>
        public bool IsShown => visible && !suppressed && alpha > 0.001f;

        /// <summary>The text mesh that shows the name.</summary>
        public TextMesh NameText => nameText;

        /// <summary>The text mesh of the agent badge; inactive unless the avatar is an agent.</summary>
        public TextMesh BadgeText => badgeText;

        /// <summary>Height of the tag's anchor above its parent's origin, metres (before any layout nudge).</summary>
        public float AnchorHeight
        {
            get => anchorHeight;
            set
            {
                anchorHeight = value;
                transform.localPosition = new Vector3(0f, value, 0f);
            }
        }

        /// <summary>How far the layout currently lifts the tag to keep it clear of nearer tags, pixels.</summary>
        public float LayoutShiftPixels => layoutPixels;

        /// <summary>The on-screen height of the name line last frame, pixels.</summary>
        public float ScreenHeightPixels { get; private set; }

        /// <summary>
        /// Hides the tag independently of <see cref="SetVisible"/> (which follows first person). Used for the local
        /// player's own tag.
        /// </summary>
        public bool Suppressed
        {
            get => suppressed;
            set
            {
                suppressed = value;
                ApplyAlpha(alpha < 0f ? 1f : alpha, force: true);
            }
        }

        internal static Font BuiltinFont
        {
            get
            {
                if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return font;
            }
        }

        /// <summary>Creates a tag as a child of <paramref name="parent"/> at the given local height.</summary>
        public static NameTag Create(Transform parent, float height)
        {
            var go = new GameObject("NameTag");
            go.transform.SetParent(parent, false);
            var tag = go.AddComponent<NameTag>();
            tag.AnchorHeight = height;
            tag.Build();
            return tag;
        }

        private void Build()
        {
            const float badgeSize = 0.75f;
            var badgeOffset = new Vector3(0f, BaseLineHeight * 0.92f, 0f);
            nameOutline = Outline("Name", Vector3.zero, 1f, 0.012f);
            nameText = MakeText("Name", Vector3.zero, NameColor, 1f, FontStyle.Normal);
            badgeOutline = Outline("AgentBadge", badgeOffset, badgeSize, 0.009f);
            badgeText = MakeText("AgentBadge", badgeOffset, BadgeColor, badgeSize, FontStyle.Bold);
            badgeText.text = "AI";
            foreach (var t in badgeOutline) t.text = "AI";
            SetAgent(false);
        }

        private TextMesh[] Outline(string baseName, Vector3 at, float sizeFactor, float offset)
        {
            var result = new TextMesh[OutlineOffsets.Length];
            for (int i = 0; i < OutlineOffsets.Length; i++)
            {
                var o = OutlineOffsets[i] * offset;
                result[i] = MakeText($"{baseName}Outline{i}", at + new Vector3(o.x, o.y, 0.004f), OutlineColor, sizeFactor,
                    baseName == "Name" ? FontStyle.Normal : FontStyle.Bold);
            }
            return result;
        }

        private TextMesh MakeText(string objectName, Vector3 localPosition, Color color, float sizeFactor, FontStyle style)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPosition;
            var text = go.AddComponent<TextMesh>();
            text.font = BuiltinFont;
            text.fontSize = FontSize;
            text.fontStyle = style;
            text.characterSize = CharacterSize * sizeFactor;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.richText = false;
            text.color = color;
            var renderer = go.GetComponent<MeshRenderer>();
            if (BuiltinFont != null) renderer.sharedMaterial = BuiltinFont.material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return text;
        }

        public void SetText(string displayName)
        {
            var s = displayName ?? "";
            nameText.text = s;
            foreach (var t in nameOutline) t.text = s;
        }

        public void SetAgent(bool agent)
        {
            isAgent = agent;
            badgeText.gameObject.SetActive(agent);
            foreach (var t in badgeOutline) t.gameObject.SetActive(agent);
        }

        public void SetVisible(bool show)
        {
            visible = show;
            ApplyAlpha(alpha < 0f ? 1f : alpha, force: true);
        }

        /// <summary>
        /// World scale that keeps the name line between the screen-height limits for a camera at
        /// <paramref name="depth"/> metres (along its view direction) with the given vertical field of view.
        /// </summary>
        public static float ScaleFor(float depth, float verticalFovDegrees, float minScreenFraction, float maxScreenFraction, float maxWorldScale)
        {
            float viewHeight = 2f * Mathf.Max(0.01f, depth) * Mathf.Tan(Mathf.Clamp(verticalFovDegrees, 1f, 179f) * 0.5f * Mathf.Deg2Rad);
            float fraction = BaseLineHeight / viewHeight; // screen fraction at scale 1
            float scale = 1f;
            if (fraction > maxScreenFraction) scale = maxScreenFraction / fraction; // near: shrink to the cap
            else if (fraction < minScreenFraction) scale = Mathf.Min(maxWorldScale, minScreenFraction / fraction); // far: grow
            return scale;
        }

        /// <summary>Opacity for a tag at <paramref name="distance"/> metres from the camera.</summary>
        public static float AlphaFor(float distance, float nearHide, float nearFull, float fadeStart, float fadeEnd)
        {
            float near = Mathf.InverseLerp(nearHide, Mathf.Max(nearHide + 0.01f, nearFull), distance);
            float far = 1f - Mathf.InverseLerp(fadeStart, Mathf.Max(fadeStart + 0.01f, fadeEnd), distance);
            return Mathf.Clamp01(Mathf.Min(near, far));
        }

        private void OnEnable() => NameTagLayout.Register(this);

        private void OnDisable() => NameTagLayout.Unregister(this);

        private void LateUpdate()
        {
            var cam = ViewCamera != null ? ViewCamera : Camera.main;
            if (cam == null) return;
            transform.localPosition = new Vector3(0f, anchorHeight, 0f);
            var camTransform = cam.transform;
            transform.rotation = camTransform.rotation;
            var toTag = transform.position - camTransform.position;
            float distance = toTag.magnitude;
            float depth = Vector3.Dot(toTag, camTransform.forward);

            float scale;
            if (cam.orthographic)
            {
                scale = 1f;
            }
            else
            {
                scale = ScaleFor(depth, cam.fieldOfView, MinScreenFraction, MaxScreenFraction, MaxWorldScale);
                float viewHeight = 2f * Mathf.Max(0.01f, depth) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                ScreenHeightPixels = BaseLineHeight * scale / viewHeight * cam.pixelHeight;
            }
            var parentScale = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            float ps = Mathf.Max(1e-4f, Mathf.Abs(parentScale.y));
            transform.localScale = Vector3.one * (scale / ps);

            float a = depth <= 0f ? 0f : AlphaFor(distance, NearHide, NearFull, FadeStart, FadeEnd);
            ApplyAlpha(a, force: false);
        }

        /// <summary>
        /// The tag's on-screen rectangle (pixels, y up) at its anchor, and its depth along the view. False when it
        /// is behind the camera.
        /// </summary>
        public bool TryGetScreenRect(Camera cam, out Rect rect, out float depth)
        {
            rect = default;
            depth = Vector3.Dot(transform.position - cam.transform.position, cam.transform.forward);
            if (depth <= 0.01f || nameText == null) return false;
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            bool ok = Accumulate(cam, nameText, ref xMin, ref yMin, ref xMax, ref yMax);
            if (isAgent && badgeText != null) ok &= Accumulate(cam, badgeText, ref xMin, ref yMin, ref xMax, ref yMax);
            if (!ok) return false;
            rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return true;
        }

        private static bool Accumulate(Camera cam, TextMesh text, ref float xMin, ref float yMin, ref float xMax, ref float yMax)
        {
            var r = text.GetComponent<MeshRenderer>();
            var b = r.localBounds;
            if (b.size.x <= 1e-5f)
            {
                // No mesh yet: estimate from the text length (about half an em per character).
                float line = text.fontSize * text.characterSize / 10f;
                b = new Bounds(new Vector3(0f, line * 0.5f, 0f), new Vector3(line * 0.55f * Mathf.Max(1, text.text.Length), line, 0f));
            }
            var m = text.transform.localToWorldMatrix;
            for (int i = 0; i < 4; i++)
            {
                var local = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, b.center.z);
                var sp = cam.WorldToScreenPoint(m.MultiplyPoint3x4(local));
                if (sp.z <= 0f) return false;
                xMin = Mathf.Min(xMin, sp.x); xMax = Mathf.Max(xMax, sp.x);
                yMin = Mathf.Min(yMin, sp.y); yMax = Mathf.Max(yMax, sp.y);
            }
            return true;
        }

        /// <summary>Sets how far (pixels) the layout wants the tag lifted this frame.</summary>
        internal void SetLayoutTarget(float pixels, Camera cam)
        {
            layoutTargetPixels = Mathf.Max(0f, pixels);
            if (!IsShown)
            {
                layoutPixels = 0f;
                layoutShownLastFrame = false;
            }
        }

        /// <summary>Eases toward the layout target and lifts the tag along the camera's up direction.</summary>
        internal void ApplyLayout(Camera cam, float dt)
        {
            // A tag that just appeared takes its place at once; one already shown glides.
            float k = layoutShownLastFrame ? 1f - Mathf.Exp(-12f * Mathf.Max(0f, dt)) : 1f;
            layoutShownLastFrame = true;
            layoutPixels = Mathf.Lerp(layoutPixels, layoutTargetPixels, k);
            if (layoutPixels < 0.01f) return;
            float depth = Mathf.Max(0.01f, Vector3.Dot(transform.position - cam.transform.position, cam.transform.forward));
            float worldPerPixel = cam.orthographic
                ? 2f * cam.orthographicSize / Mathf.Max(1, cam.pixelHeight)
                : 2f * depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, cam.pixelHeight);
            transform.position += cam.transform.up * (layoutPixels * worldPerPixel);
        }

        private void ApplyAlpha(float a, bool force)
        {
            if (!force && Mathf.Abs(a - alpha) < 0.01f && !(a == 0f && alpha != 0f)) return;
            alpha = a;
            bool show = visible && !suppressed && a > 0.001f;
            SetTextAlpha(nameText, NameColor, a, show);
            foreach (var t in nameOutline) SetTextAlpha(t, OutlineColor, a, show);
            SetTextAlpha(badgeText, BadgeColor, a, show);
            foreach (var t in badgeOutline) SetTextAlpha(t, OutlineColor, a, show);
        }

        private static void SetTextAlpha(TextMesh text, Color baseColor, float a, bool show)
        {
            if (text == null) return;
            var c = baseColor;
            c.a *= a;
            text.color = c;
            var r = text.GetComponent<MeshRenderer>();
            if (r != null) r.enabled = show;
        }
    }
}
