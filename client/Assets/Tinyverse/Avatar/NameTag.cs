using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// A name above an avatar's head, drawn with the legacy <see cref="TextMesh"/> and Unity's built-in
    /// LegacyRuntime font, so it needs no imported assets. It turns to face the camera, grows with distance so it
    /// stays readable, and fades out far away. An optional "AI" badge sits above the name.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class NameTag : MonoBehaviour
    {
        /// <summary>Camera the tags face. Null means <see cref="Camera.main"/>.</summary>
        public static Camera ViewCamera { get; set; }

        private const int FontSize = 64;
        private const float CharacterSize = 0.035f; // world height of a line is about FontSize * CharacterSize / 10
        private static readonly Color NameColor = Color.white;
        private static readonly Color BadgeColor = new Color(0.35f, 0.85f, 1f);
        private static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.85f);
        private static Font font;

        /// <summary>Up to this distance the tag keeps its base size; beyond it, it grows to keep its size on screen.</summary>
        public float ConstantSizeFrom { get; set; } = 8f;

        /// <summary>Largest growth factor.</summary>
        public float MaxScale { get; set; } = 3.5f;

        /// <summary>Distance where fading starts, metres.</summary>
        public float FadeStart { get; set; } = 35f;

        /// <summary>Distance where the tag is fully gone, metres.</summary>
        public float FadeEnd { get; set; } = 50f;

        private TextMesh nameText, nameShadow, badgeText, badgeShadow;
        private float alpha = -1f;
        private bool visible = true;
        private bool isAgent;

        public string Text => nameText != null ? nameText.text : "";
        public bool ShowsAgentBadge => isAgent;

        /// <summary>The current opacity from distance fading, 0…1.</summary>
        public float Alpha => Mathf.Max(0f, alpha);

        /// <summary>The text mesh that shows the name.</summary>
        public TextMesh NameText => nameText;

        /// <summary>The text mesh of the agent badge; inactive unless the avatar is an agent.</summary>
        public TextMesh BadgeText => badgeText;

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
            go.transform.localPosition = new Vector3(0f, height, 0f);
            var tag = go.AddComponent<NameTag>();
            tag.Build();
            return tag;
        }

        private void Build()
        {
            float line = FontSize * CharacterSize / 10f;
            nameShadow = MakeText("NameShadow", new Vector3(0.012f, -0.012f, 0.01f), ShadowColor);
            nameText = MakeText("Name", Vector3.zero, NameColor);
            badgeShadow = MakeText("BadgeShadow", new Vector3(0.012f, line * 0.8f - 0.012f, 0.01f), ShadowColor, 0.7f);
            badgeText = MakeText("AgentBadge", new Vector3(0f, line * 0.8f, 0f), BadgeColor, 0.7f);
            badgeText.text = badgeShadow.text = "AI agent";
            SetAgent(false);
        }

        private TextMesh MakeText(string objectName, Vector3 localPosition, Color color, float sizeFactor = 1f)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPosition;
            var text = go.AddComponent<TextMesh>();
            text.font = BuiltinFont;
            text.fontSize = FontSize;
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
            nameShadow.text = s;
        }

        public void SetAgent(bool agent)
        {
            isAgent = agent;
            badgeText.gameObject.SetActive(agent);
            badgeShadow.gameObject.SetActive(agent);
        }

        public void SetVisible(bool show)
        {
            visible = show;
            ApplyAlpha(alpha < 0f ? 1f : alpha, force: true);
        }

        private void LateUpdate()
        {
            var cam = ViewCamera != null ? ViewCamera : Camera.main;
            if (cam == null) return;
            var camTransform = cam.transform;
            transform.rotation = camTransform.rotation;
            float distance = Vector3.Distance(camTransform.position, transform.position);
            float scale = Mathf.Clamp(distance / Mathf.Max(0.01f, ConstantSizeFrom), 1f, MaxScale);
            var parentScale = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
            float ps = Mathf.Max(1e-4f, Mathf.Abs(parentScale.y));
            transform.localScale = Vector3.one * (scale / ps);
            float a = 1f - Mathf.InverseLerp(FadeStart, Mathf.Max(FadeStart + 0.01f, FadeEnd), distance);
            ApplyAlpha(a, force: false);
        }

        private void ApplyAlpha(float a, bool force)
        {
            if (!force && Mathf.Abs(a - alpha) < 0.01f) return;
            alpha = a;
            bool show = visible && a > 0.001f;
            SetTextAlpha(nameText, NameColor, a, show);
            SetTextAlpha(nameShadow, ShadowColor, a, show);
            SetTextAlpha(badgeText, BadgeColor, a, show);
            SetTextAlpha(badgeShadow, ShadowColor, a, show);
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
