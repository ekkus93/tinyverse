using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// Stand-in body until real models arrive: a capsule-proportioned figure about 1.8 m tall with a skin-coloured
    /// head, a dark visor on the front of the head showing which way it faces, and two arms. It leans, bobs and
    /// swings its arms according to the animation hint. Coloured by <see cref="AvatarLook.BodyColor"/>.
    /// </summary>
    public sealed class PlaceholderAvatarVisual : MonoBehaviour, IAvatarVisual
    {
        public const string BodyMaterialPath = "Tinyverse/Avatar/AvatarBody";
        public const float TotalHeight = 1.8f;

        private static Mesh capsuleMesh, sphereMesh, cubeMesh;
        private static Material baseMaterial;

        private Transform body, leftArm, rightArm, head;
        private Material bodyMaterial, skinMaterial, visorMaterial;
        private Renderer[] renderers;
        private NameTag nameTag;
        private float phase, lean, swing, bob, armSpread;
        private bool visible = true;

        public Transform Root => transform;
        public float Height => TotalHeight;
        public NameTag NameTag => nameTag;

        /// <summary>Builds the placeholder as a child of the avatar root.</summary>
        public static IAvatarVisual Create(Transform avatarRoot)
        {
            var go = new GameObject("Visual");
            go.transform.SetParent(avatarRoot, false);
            var visual = go.AddComponent<PlaceholderAvatarVisual>();
            visual.Build();
            return visual;
        }

        private void Build()
        {
            var mat = BaseMaterial;
            bodyMaterial = new Material(mat) { name = "AvatarBody (instance)" };
            skinMaterial = new Material(mat) { name = "AvatarSkin (instance)" };
            visorMaterial = new Material(mat) { name = "AvatarVisor (instance)" };
            SetColor(visorMaterial, new Color(0.08f, 0.09f, 0.11f));

            body = new GameObject("Body").transform;
            body.SetParent(transform, false);

            // Torso and legs: a capsule from the feet to the shoulders (0 … 1.42 m).
            Part("Torso", Capsule, bodyMaterial, body, new Vector3(0f, 0.71f, 0f), new Vector3(0.44f, 0.71f, 0.30f));

            head = new GameObject("Head").transform;
            head.SetParent(body, false);
            head.localPosition = new Vector3(0f, 1.64f, 0f);
            Part("Skull", Sphere, skinMaterial, head, Vector3.zero, new Vector3(0.25f, 0.30f, 0.27f));
            // Visor across the face: the facing direction, readable from any angle.
            Part("Visor", Cube, visorMaterial, head, new Vector3(0f, 0.02f, 0.11f), new Vector3(0.21f, 0.07f, 0.08f));
            Part("Nose", Cube, skinMaterial, head, new Vector3(0f, -0.04f, 0.14f), new Vector3(0.04f, 0.06f, 0.05f));

            leftArm = Arm("LeftArm", -1f);
            rightArm = Arm("RightArm", 1f);

            renderers = GetComponentsInChildren<Renderer>(true);
            nameTag = NameTag.Create(transform, TotalHeight + 0.22f);
            SetLook(AvatarLook.Default);
        }

        private Transform Arm(string objectName, float side)
        {
            var pivot = new GameObject(objectName).transform;
            pivot.SetParent(body, false);
            pivot.localPosition = new Vector3(0.27f * side, 1.36f, 0f);
            Part("Upper", Capsule, bodyMaterial, pivot, new Vector3(0f, -0.29f, 0f), new Vector3(0.11f, 0.31f, 0.11f));
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
            var bodyColor = IsUnset(look.BodyColor) ? AvatarLook.Default.BodyColor : look.BodyColor;
            var skin = IsUnset(look.SkinColor) ? AvatarLook.Default.SkinColor : look.SkinColor;
            bodyColor.a = 1f;
            skin.a = 1f;
            SetColor(bodyMaterial, bodyColor);
            SetColor(skinMaterial, skin);
        }

        private static bool IsUnset(Color c) => c.r == 0f && c.g == 0f && c.b == 0f && c.a == 0f;

        /// <summary>The body colour currently drawn.</summary>
        public Color BodyColor => bodyMaterial != null && bodyMaterial.HasProperty("_BaseColor") ? bodyMaterial.GetColor("_BaseColor") : Color.clear;

        public void SetName(string displayName) => nameTag.SetText(displayName);

        public void SetAgent(bool isAgent) => nameTag.SetAgent(isAgent);

        public void SetVisible(bool show)
        {
            visible = show;
            foreach (var r in renderers)
                if (r != null) r.enabled = show;
            nameTag.SetVisible(show);
        }

        public bool Visible => visible;

        public void UpdatePose(in AvatarPose pose, float deltaTime)
        {
            float dt = Mathf.Max(0f, deltaTime);
            var v = pose.Velocity;
            float speed = new Vector2(v.x, v.z).magnitude;
            string anim = string.IsNullOrEmpty(pose.Animation) ? AvatarAnimations.Idle : pose.Animation;

            float targetLean, targetSwing, targetBob, targetSpread;
            switch (anim)
            {
                case AvatarAnimations.Walk:
                    targetLean = 4f; targetSwing = 28f; targetBob = 0.035f; targetSpread = 4f;
                    break;
                case AvatarAnimations.Run:
                    targetLean = 11f; targetSwing = 50f; targetBob = 0.06f; targetSpread = 6f;
                    break;
                case AvatarAnimations.Fly:
                    targetLean = Mathf.Lerp(3f, 28f, Mathf.Clamp01(speed / 10f)); targetSwing = 6f; targetBob = 0.04f; targetSpread = 18f;
                    break;
                case AvatarAnimations.Fall:
                    targetLean = -4f; targetSwing = 8f; targetBob = 0f; targetSpread = 55f;
                    break;
                default:
                    targetLean = 0f; targetSwing = 2f; targetBob = 0.008f; targetSpread = 4f;
                    break;
            }

            float k = 1f - Mathf.Exp(-8f * dt);
            lean = Mathf.Lerp(lean, targetLean, k);
            swing = Mathf.Lerp(swing, targetSwing, k);
            bob = Mathf.Lerp(bob, targetBob, k);
            armSpread = Mathf.Lerp(armSpread, targetSpread, k);

            // Stride frequency follows ground speed; idle, fly and fall breathe slowly.
            bool striding = anim == AvatarAnimations.Walk || anim == AvatarAnimations.Run;
            float frequency = striding ? Mathf.Clamp(speed * 0.55f, 1.2f, 3.2f) : 0.35f;
            phase = Mathf.Repeat(phase + dt * frequency * 2f * Mathf.PI, 2f * Mathf.PI);

            float s = Mathf.Sin(phase);
            body.localPosition = new Vector3(0f, striding ? Mathf.Abs(s) * bob : (s * 0.5f + 0.5f) * bob, 0f);
            body.localRotation = Quaternion.Euler(lean, 0f, 0f);
            leftArm.localRotation = Quaternion.Euler(-s * swing, 0f, -armSpread);
            rightArm.localRotation = Quaternion.Euler(s * swing, 0f, armSpread);
            head.localRotation = Quaternion.Euler(-lean * 0.5f, 0f, 0f);
        }

        public void Destroy()
        {
            if (this != null && gameObject != null) Object.Destroy(gameObject);
        }

        private void OnDestroy()
        {
            DestroyMaterial(bodyMaterial);
            DestroyMaterial(skinMaterial);
            DestroyMaterial(visorMaterial);
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
        private static Mesh Cube => cubeMesh != null ? cubeMesh : cubeMesh = PrimitiveMesh(PrimitiveType.Cube);

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
