using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// Third-person camera around a target. Hold the right mouse button and drag to orbit (the cursor is free
    /// otherwise, so UI works); scroll to zoom from first person out to <see cref="MaxDistance"/>. Pulls in when
    /// geometry is between the camera and the target, and follows smoothly.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class OrbitCamera : MonoBehaviour
    {
        /// <summary>What the camera orbits; its position is the feet of an avatar.</summary>
        public Transform Target { get; set; }

        /// <summary>Height of the orbit pivot above the target, metres (about the eyes).</summary>
        public float PivotHeight { get; set; } = 1.6f;

        /// <summary>Heading of the camera, degrees, clockwise seen from above.</summary>
        public float Yaw { get; set; }

        /// <summary>Downward tilt, degrees; positive looks down.</summary>
        public float Pitch { get; set; } = 15f;

        public float MinPitch { get; set; } = -80f;
        public float MaxPitch { get; set; } = 85f;

        /// <summary>Zoom distance the camera aims for, metres; 0 is first person.</summary>
        public float Distance { get; set; } = 5f;

        public float MaxDistance { get; set; } = 15f;

        /// <summary>Zooming in below this snaps to first person.</summary>
        public float FirstPersonSnap { get; set; } = 0.8f;

        /// <summary>Below this camera distance the target's visual is hidden.</summary>
        public float FirstPersonThreshold { get; set; } = 0.45f;

        /// <summary>Degrees per pixel of mouse drag.</summary>
        public float OrbitSensitivity { get; set; } = 0.2f;

        /// <summary>Zoom factor per scroll notch.</summary>
        public float ZoomFactor { get; set; } = 1.2f;

        /// <summary>Time constant of the pivot following the target, seconds.</summary>
        public float FollowSmoothTime { get; set; } = 0.06f;

        /// <summary>Radius of the sphere cast that keeps the camera out of walls.</summary>
        public float CollisionRadius { get; set; } = 0.2f;

        /// <summary>Layers that block the camera. Triggers (remote avatars) never block.</summary>
        public LayerMask CollisionMask { get; set; } = Physics.DefaultRaycastLayers;

        /// <summary>When false, mouse orbit and zoom are ignored.</summary>
        public bool InputEnabled { get; set; } = true;

        /// <summary>When set and true, starting an orbit and zooming are suppressed (the pointer is over UI).</summary>
        public Func<bool> IsPointerOverUI { get; set; }

        public Camera Camera { get; private set; }

        /// <summary>Distance from the pivot after zoom smoothing and collision pull-in.</summary>
        public float CurrentDistance { get; private set; }

        public bool IsFirstPerson { get; private set; }
        public bool IsOrbiting => orbiting;

        /// <summary>Raised when the view enters or leaves first person.</summary>
        public event Action<bool> FirstPersonChanged;

        private InputAction orbitButton, lookDelta, scroll;
        private bool orbiting;
        private bool initialised;
        private Vector3 pivot, pivotVelocity;
        private float zoomDistance;
        private LocalAvatar followed;
        private readonly RaycastHit[] hits = new RaycastHit[16];

        /// <summary>
        /// Adds an orbit camera to <paramref name="camera"/>, or to <see cref="Camera.main"/>, or to a new camera
        /// tagged MainCamera when there is none.
        /// </summary>
        public static OrbitCamera Create(Camera camera = null)
        {
            if (camera == null) camera = Camera.main;
            if (camera == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                camera = go.AddComponent<Camera>();
                camera.nearClipPlane = 0.05f;
                if (FindAnyObjectByType<AudioListener>() == null) go.AddComponent<AudioListener>();
            }
            var orbit = camera.GetComponent<OrbitCamera>();
            if (orbit == null) orbit = camera.gameObject.AddComponent<OrbitCamera>();
            return orbit;
        }

        /// <summary>
        /// Follows the local avatar: orbits it, makes its movement relative to this camera's heading, hides its
        /// visual in first person, and starts behind it.
        /// </summary>
        public void Follow(LocalAvatar avatar)
        {
            if (followed != null && followed.Visual != null) followed.Visual.SetVisible(true);
            followed = avatar;
            if (avatar == null)
            {
                Target = null;
                return;
            }
            Target = avatar.transform;
            avatar.ViewYaw = () => Yaw;
            Yaw = avatar.CurrentPose.Yaw;
            avatar.Visual.SetVisible(!IsFirstPerson);
            SnapToTarget();
        }

        /// <summary>Jumps to the target without smoothing (after a teleport, for example).</summary>
        public void SnapToTarget()
        {
            if (Target == null) return;
            pivot = Target.position + Vector3.up * PivotHeight;
            pivotVelocity = Vector3.zero;
            zoomDistance = Distance;
            initialised = true;
            Apply(snap: true);
        }

        /// <summary>Rotates the view: x adds to yaw, y adds to pitch (degrees).</summary>
        public void Orbit(Vector2 degrees)
        {
            Yaw = AvatarMotion.NormalizeYaw(Yaw + degrees.x);
            Pitch = Mathf.Clamp(Pitch + degrees.y, MinPitch, MaxPitch);
        }

        /// <summary>Zooms by scroll notches: positive zooms in, toward first person.</summary>
        public void Zoom(float notches)
        {
            if (notches == 0f) return;
            float d = Distance;
            if (notches > 0f)
            {
                d /= Mathf.Pow(ZoomFactor, notches);
                if (d < FirstPersonSnap) d = 0f;
            }
            else
            {
                d = d < FirstPersonSnap ? FirstPersonSnap : d * Mathf.Pow(ZoomFactor, -notches);
            }
            Distance = Mathf.Clamp(d, 0f, MaxDistance);
        }

        private void Awake()
        {
            Camera = GetComponent<Camera>();
            orbitButton = new InputAction("Orbit", InputActionType.Button, "<Mouse>/rightButton");
            lookDelta = new InputAction("Look", InputActionType.Value, "<Mouse>/delta", expectedControlType: "Vector2");
            scroll = new InputAction("Zoom", InputActionType.Value, "<Mouse>/scroll", expectedControlType: "Vector2");
            orbitButton.Enable();
            lookDelta.Enable();
            scroll.Enable();
        }

        private void OnDestroy()
        {
            if (orbiting) ReleaseCursor();
            orbitButton?.Dispose();
            lookDelta?.Dispose();
            scroll?.Dispose();
        }

        private void LateUpdate()
        {
            ReadInput();
            if (Target == null) return;
            if (!initialised) SnapToTarget();
            Apply(snap: false);
        }

        private void ReadInput()
        {
            if (!InputEnabled)
            {
                if (orbiting) ReleaseCursor();
                return;
            }
            bool overUI = IsPointerOverUI != null && IsPointerOverUI();
            if (!orbiting && orbitButton.WasPressedThisFrame() && !overUI)
            {
                orbiting = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (orbiting && !orbitButton.IsPressed())
            {
                ReleaseCursor();
            }

            if (orbiting)
            {
                var d = lookDelta.ReadValue<Vector2>();
                Orbit(new Vector2(d.x, -d.y) * OrbitSensitivity);
            }

            if (!overUI)
            {
                float s = scroll.ReadValue<Vector2>().y;
                // Older Input System versions report 120 per notch on some platforms; newer ones about 1.
                if (Mathf.Abs(s) >= 20f) s /= 120f;
                if (Mathf.Abs(s) > 0.01f) Zoom(Mathf.Clamp(s, -3f, 3f));
            }
        }

        private void ReleaseCursor()
        {
            orbiting = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Apply(bool snap)
        {
            float dt = Time.deltaTime;
            var pivotTarget = Target.position + Vector3.up * PivotHeight;
            if (snap || (pivotTarget - pivot).sqrMagnitude > 100f)
            {
                pivot = pivotTarget;
                pivotVelocity = Vector3.zero;
            }
            else if (dt > 0f)
            {
                pivot = Vector3.SmoothDamp(pivot, pivotTarget, ref pivotVelocity, FollowSmoothTime, Mathf.Infinity, dt);
            }

            float zk = snap ? 1f : 1f - Mathf.Exp(-12f * dt);
            zoomDistance = Mathf.Lerp(zoomDistance, Distance, zk);
            if (Mathf.Abs(zoomDistance - Distance) < 0.005f) zoomDistance = Distance;

            var rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            var back = rotation * Vector3.back;
            float allowed = ObstacleDistance(pivot, back, zoomDistance);
            if (allowed < CurrentDistance || snap) CurrentDistance = allowed; // pull in at once
            else CurrentDistance = Mathf.Lerp(CurrentDistance, allowed, 1f - Mathf.Exp(-6f * dt)); // ease back out
            CurrentDistance = Mathf.Min(CurrentDistance, zoomDistance);

            transform.SetPositionAndRotation(pivot + back * CurrentDistance, rotation);

            bool firstPerson = CurrentDistance < FirstPersonThreshold;
            if (firstPerson != IsFirstPerson)
            {
                IsFirstPerson = firstPerson;
                if (followed != null && followed.Visual != null) followed.Visual.SetVisible(!firstPerson);
                FirstPersonChanged?.Invoke(firstPerson);
            }
        }

        private float ObstacleDistance(Vector3 from, Vector3 direction, float distance)
        {
            if (distance <= 0.01f) return distance;
            int n = Physics.SphereCastNonAlloc(from, CollisionRadius, direction, hits, distance, CollisionMask, QueryTriggerInteraction.Ignore);
            float best = distance;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.collider == null) continue;
                if (Target != null && h.collider.transform.IsChildOf(Target)) continue;
                if (h.distance <= 0f && h.point == Vector3.zero) continue; // started inside: ignore
                if (h.distance < best) best = h.distance;
            }
            return Mathf.Max(0f, best);
        }
    }
}
