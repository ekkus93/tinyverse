using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// Stand-in body until real models arrive: a deliberately simple, friendly figure about 1.8 m tall built from
    /// primitives — torso, two legs, two arms with hands, neck and a round head with hair on the top and back and
    /// two eyes on the face, so the facing direction reads from the front (eyes, skin) and from behind (hair).
    /// Torso and arms take <see cref="AvatarLook.BodyColor"/>, legs a darker shade of it. Arms and legs swing, the
    /// body bobs and leans according to the animation hint.
    /// </summary>
    public sealed class PlaceholderAvatarVisual : MonoBehaviour, IAvatarVisual
    {
        public const string BodyMaterialPath = "Tinyverse/Avatar/AvatarBody";
        public const float TotalHeight = 1.8f;

        private const float HipHeight = 0.86f;
        private const float ShoulderHeight = 1.42f;
        private const float HeadCenter = 1.655f;

        private static readonly Color EyeColor = new Color(0.07f, 0.07f, 0.09f);
        private static Mesh capsuleMesh, sphereMesh;
        private static Material baseMaterial;

        private Transform body, head, leftArm, rightArm, leftLeg, rightLeg;
        private Material bodyMaterial, legMaterial, skinMaterial, hairMaterial, eyeMaterial;
        private Renderer[] renderers;
        private NameTag nameTag;
        private float phase, lean, armSwing, legSwing, bob, armSpread, legSpread, legTrail;
        private bool visible = true;

        public Transform Root => transform;
        public float Height => TotalHeight;
        public NameTag NameTag => nameTag;

        /// <summary>Builds the placeholder as a child of the avatar root.</summary>
        public static IAvatarVisual Create(Transform avatarRoot) => Build(avatarRoot, "Visual", withNameTag: true);

        /// <summary>The figure alone, without a name tag (for a visual that owns its tag).</summary>
        internal static PlaceholderAvatarVisual CreateBody(Transform parent) => Build(parent, "Placeholder", withNameTag: false);

        private static PlaceholderAvatarVisual Build(Transform parent, string objectName, bool withNameTag)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(parent, false);
            var visual = go.AddComponent<PlaceholderAvatarVisual>();
            visual.Build(withNameTag);
            return visual;
        }

        private void Build(bool withNameTag)
        {
            var mat = BaseMaterial;
            bodyMaterial = new Material(mat) { name = "AvatarBody (instance)" };
            legMaterial = new Material(mat) { name = "AvatarLegs (instance)" };
            skinMaterial = new Material(mat) { name = "AvatarSkin (instance)" };
            hairMaterial = new Material(mat) { name = "AvatarHair (instance)" };
            eyeMaterial = new Material(mat) { name = "AvatarEyes (instance)" };
            SetColor(eyeMaterial, EyeColor);

            body = new GameObject("Body").transform;
            body.SetParent(transform, false);

            // Torso from just below the hips to the shoulders (0.78 … 1.50 m).
            Part("Torso", Capsule, bodyMaterial, body, new Vector3(0f, 1.14f, 0f), new Vector3(0.40f, 0.36f, 0.25f));

            leftLeg = Limb("LeftLeg", body, new Vector3(-0.10f, HipHeight, 0f), legMaterial, 0.15f, HipHeight * 0.5f, null);
            rightLeg = Limb("RightLeg", body, new Vector3(0.10f, HipHeight, 0f), legMaterial, 0.15f, HipHeight * 0.5f, null);
            leftArm = Limb("LeftArm", body, new Vector3(-0.255f, ShoulderHeight, 0f), bodyMaterial, 0.10f, 0.27f, skinMaterial);
            rightArm = Limb("RightArm", body, new Vector3(0.255f, ShoulderHeight, 0f), bodyMaterial, 0.10f, 0.27f, skinMaterial);

            Part("Neck", Capsule, skinMaterial, body, new Vector3(0f, 1.52f, 0f), new Vector3(0.10f, 0.06f, 0.10f));

            head = new GameObject("Head").transform;
            head.SetParent(body, false);
            head.localPosition = new Vector3(0f, HeadCenter, 0f);
            Part("Face", Sphere, skinMaterial, head, Vector3.zero, new Vector3(0.24f, 0.27f, 0.25f));
            // Hair covers the top and back of the head, leaving the face (+Z) bare.
            Part("Hair", Sphere, hairMaterial, head, new Vector3(0f, 0.045f, -0.03f), new Vector3(0.255f, 0.23f, 0.25f));
            Part("LeftEye", Sphere, eyeMaterial, head, new Vector3(-0.05f, 0.0f, 0.112f), new Vector3(0.045f, 0.055f, 0.03f));
            Part("RightEye", Sphere, eyeMaterial, head, new Vector3(0.05f, 0.0f, 0.112f), new Vector3(0.045f, 0.055f, 0.03f));

            renderers = GetComponentsInChildren<Renderer>(true);
            if (withNameTag) nameTag = NameTag.Create(transform, TotalHeight + 0.2f);
            SetLook(AvatarLook.Default);
            UpdatePose(new AvatarPose(Vector3.zero, 0f), 0f);
        }

        /// <summary>A limb hanging from a pivot: a capsule of the given thickness and half-length, optionally a hand.</summary>
        private static Transform Limb(string objectName, Transform parent, Vector3 pivotPosition, Material material, float thickness, float halfLength, Material handMaterial)
        {
            var pivot = new GameObject(objectName).transform;
            pivot.SetParent(parent, false);
            pivot.localPosition = pivotPosition;
            Part("Segment", Capsule, material, pivot, new Vector3(0f, -halfLength, 0f), new Vector3(thickness, halfLength, thickness));
            if (handMaterial != null)
                Part("Hand", Sphere, handMaterial, pivot, new Vector3(0f, -halfLength * 2f - 0.01f, 0f), Vector3.one * 0.095f);
            return pivot;
        }

        private static void Part(string objectName, Mesh mesh, Material material, Transform parent, Vector3 position, Vector3 scale)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.On;
        }

        public void SetLook(AvatarLook look)
        {
            // An all-zero colour means "not set" (a default struct), not black.
            var defaults = AvatarLook.Default;
            var bodyColor = IsUnset(look.BodyColor) ? defaults.BodyColor : look.BodyColor;
            var skin = IsUnset(look.SkinColor) ? defaults.SkinColor : look.SkinColor;
            var hair = IsUnset(look.HairColor) ? defaults.HairColor : look.HairColor;
            bodyColor.a = skin.a = hair.a = 1f;
            var legs = Color.Lerp(bodyColor, Color.black, 0.35f);
            legs.a = 1f;
            SetColor(bodyMaterial, bodyColor);
            SetColor(legMaterial, legs);
            SetColor(skinMaterial, skin);
            SetColor(hairMaterial, hair);
        }

        private static bool IsUnset(Color c) => c.r == 0f && c.g == 0f && c.b == 0f && c.a == 0f;

        /// <summary>The body colour currently drawn.</summary>
        public Color BodyColor => bodyMaterial != null && bodyMaterial.HasProperty("_BaseColor") ? bodyMaterial.GetColor("_BaseColor") : Color.clear;

        public void SetName(string displayName) => nameTag?.SetText(displayName);

        public void SetAgent(bool isAgent) => nameTag?.SetAgent(isAgent);

        public void SetVisible(bool show)
        {
            visible = show;
            foreach (var r in renderers)
                if (r != null) r.enabled = show;
            nameTag?.SetVisible(show);
        }

        public bool Visible => visible;

        public void UpdatePose(in AvatarPose pose, float deltaTime)
        {
            float dt = Mathf.Max(0f, deltaTime);
            var v = pose.Velocity;
            float speed = new Vector2(v.x, v.z).magnitude;
            string anim = string.IsNullOrEmpty(pose.Animation) ? AvatarAnimations.Idle : pose.Animation;

            // Targets per hint: body lean, arm and leg swing amplitude (degrees), bob (m), arm and leg spread
            // sideways (degrees) and legs trailing behind (degrees, for flight).
            float tLean, tArm, tLeg, tBob, tArmSpread, tLegSpread, tTrail;
            switch (anim)
            {
                case AvatarAnimations.Walk:
                    tLean = 3f; tArm = 20f; tLeg = 24f; tBob = 0.03f; tArmSpread = 5f; tLegSpread = 0f; tTrail = 0f;
                    break;
                case AvatarAnimations.Run:
                    tLean = 8f; tArm = 34f; tLeg = 36f; tBob = 0.05f; tArmSpread = 6f; tLegSpread = 0f; tTrail = 0f;
                    break;
                case AvatarAnimations.Fly:
                    tLean = Mathf.Lerp(0f, 15f, Mathf.Clamp01(speed / 12f)); tArm = 4f; tLeg = 3f; tBob = 0.04f;
                    tArmSpread = 16f; tLegSpread = 3f; tTrail = Mathf.Lerp(4f, 18f, Mathf.Clamp01(speed / 12f));
                    break;
                case AvatarAnimations.Fall:
                    tLean = 0f; tArm = 6f; tLeg = 6f; tBob = 0f; tArmSpread = 40f; tLegSpread = 7f; tTrail = 0f;
                    break;
                default:
                    tLean = 0f; tArm = 1.5f; tLeg = 0f; tBob = 0.006f; tArmSpread = 5f; tLegSpread = 0f; tTrail = 0f;
                    break;
            }

            float k = dt > 0f ? 1f - Mathf.Exp(-8f * dt) : 1f;
            lean = Mathf.Lerp(lean, tLean, k);
            armSwing = Mathf.Lerp(armSwing, tArm, k);
            legSwing = Mathf.Lerp(legSwing, tLeg, k);
            bob = Mathf.Lerp(bob, tBob, k);
            armSpread = Mathf.Lerp(armSpread, tArmSpread, k);
            legSpread = Mathf.Lerp(legSpread, tLegSpread, k);
            legTrail = Mathf.Lerp(legTrail, tTrail, k);

            // Stride frequency follows ground speed; idle, fly and fall breathe slowly.
            bool striding = anim == AvatarAnimations.Walk || anim == AvatarAnimations.Run;
            float frequency = striding ? Mathf.Clamp(speed * 0.45f, 1.0f, 3.0f) : 0.3f;
            phase = Mathf.Repeat(phase + dt * frequency * Mathf.PI, 2f * Mathf.PI);

            // One stride cycle = phase 0…2π; s > 0 puts the left leg forward and the right arm forward.
            float s = Mathf.Sin(phase);
            float lift = striding ? Mathf.Abs(Mathf.Cos(phase)) * bob : (Mathf.Sin(phase * 2f) * 0.5f + 0.5f) * bob;
            body.localPosition = new Vector3(0f, lift, 0f);
            body.localRotation = Quaternion.Euler(lean, 0f, 0f);
            // About X, a negative angle swings a hanging limb forward.
            leftLeg.localRotation = Quaternion.Euler(-s * legSwing + legTrail, 0f, -legSpread);
            rightLeg.localRotation = Quaternion.Euler(s * legSwing + legTrail, 0f, legSpread);
            leftArm.localRotation = Quaternion.Euler(s * armSwing, 0f, -armSpread);
            rightArm.localRotation = Quaternion.Euler(-s * armSwing, 0f, armSpread);
            head.localRotation = Quaternion.Euler(-lean * 0.6f, 0f, 0f);
        }

        public void Destroy()
        {
            if (this != null && gameObject != null) Object.Destroy(gameObject);
        }

        private void OnDestroy()
        {
            DestroyMaterial(bodyMaterial);
            DestroyMaterial(legMaterial);
            DestroyMaterial(skinMaterial);
            DestroyMaterial(hairMaterial);
            DestroyMaterial(eyeMaterial);
        }

        private static void DestroyMaterial(Material m)
        {
            if (m == null) return;
            if (Application.isPlaying) Object.Destroy(m);
            else Object.DestroyImmediate(m);
        }

        private static void SetColor(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        private static Material BaseMaterial
        {
            get
            {
                if (baseMaterial != null) return baseMaterial;
                baseMaterial = Resources.Load<Material>(BodyMaterialPath);
                if (baseMaterial == null)
                {
                    Debug.LogWarning($"[Avatar] Resources/{BodyMaterialPath} missing; falling back to a runtime URP Lit material.");
                    var shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null) shader = Shader.Find("Standard");
                    baseMaterial = new Material(shader) { name = "AvatarBody (fallback)" };
                }
                return baseMaterial;
            }
        }

        private static Mesh Capsule => capsuleMesh != null ? capsuleMesh : capsuleMesh = PrimitiveMesh(PrimitiveType.Capsule);
        private static Mesh Sphere => sphereMesh != null ? sphereMesh : sphereMesh = PrimitiveMesh(PrimitiveType.Sphere);

        private static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            go.SetActive(false); // its collider must not exist even for the rest of this frame
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
            return mesh;
        }
    }
}
