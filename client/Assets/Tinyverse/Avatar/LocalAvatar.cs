using System;
using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// The player's own avatar: a <see cref="CharacterController"/> that walks, runs, jumps and flies, moving
    /// relative to the view heading, keeping to <see cref="MoveLimits"/> and the world bounds. Knows nothing about
    /// networking; the owner samples <see cref="CurrentPose"/> (or listens to <see cref="PoseChanged"/>) and calls
    /// <see cref="Teleport"/> for spawns and server corrections.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public sealed class LocalAvatar : MonoBehaviour
    {
        public const float ControllerHeight = 1.8f;
        public const float ControllerRadius = 0.3f;

        /// <summary>Downward acceleration on the ground and in the air, m/s².</summary>
        public float Gravity { get; set; } = 20f;

        /// <summary>Terminal fall speed, m/s; under the server's 60 m/s vertical limit.</summary>
        public const float MaxFallSpeed = 55f;

        /// <summary>Upward speed of a jump, m/s (about 0.9 m high with the default gravity).</summary>
        public float JumpSpeed { get => jumpSpeed; set => jumpSpeed = Mathf.Clamp(value, 0f, MaxRiseSpeed); }

        /// <summary>Highest upward speed on foot, m/s; under the server's 10 m/s vertical limit.</summary>
        public const float MaxRiseSpeed = 9.5f;

        private float jumpSpeed = 6f;

        public float GroundAcceleration { get; set; } = 28f;
        public float GroundDeceleration { get; set; } = 36f;
        public float AirAcceleration { get; set; } = 8f;
        public float FlyAcceleration { get; set; } = 24f;
        public float FlyDeceleration { get; set; } = 30f;

        /// <summary>Fraction of fly speed used while flying without the run key.</summary>
        public float FlySlowFactor { get; set; } = 0.5f;

        /// <summary>How quickly the avatar turns toward its direction of travel; higher is snappier.</summary>
        public float TurnSharpness { get; set; } = 12f;

        /// <summary>Read each frame. Defaults to <see cref="KeyboardAvatarInput"/>; replace for tests or scripted moves.</summary>
        public ILocalAvatarInput InputSource { get; set; }

        /// <summary>When false (a text field has focus, a menu is open) input is ignored; gravity still applies.</summary>
        public bool InputEnabled { get; set; } = true;

        /// <summary>
        /// Heading the movement keys are relative to, degrees. <see cref="OrbitCamera.Follow"/> sets it to the
        /// camera's yaw. Null means the main camera's heading, or the avatar's own if there is no camera.
        /// </summary>
        public Func<float> ViewYaw { get; set; }

        public AvatarPose CurrentPose { get; private set; }

        public MoveLimits Limits => limits;
        public bool IsFlying => flying;
        public bool IsGrounded => grounded;

        /// <summary>True while moving or in the air; a sender can drop to a low report rate when false.</summary>
        public bool IsMoving => velocity.sqrMagnitude > 0.0025f || (!grounded && !flying);

        /// <summary>The avatar's entity id, set by the owner. Stored on its <see cref="AvatarId"/>.</summary>
        public ulong Id
        {
            get => avatarId.Id;
            set => avatarId.Id = value;
        }

        public string DisplayName { get; private set; } = "";
        public IAvatarVisual Visual { get; private set; }
        public CharacterController Controller => controller;

        /// <summary>
        /// Raised at the end of a frame in which the pose changed (moved more than a millimetre, turned, or changed
        /// mode or animation), and after <see cref="Teleport"/>. Not raised while standing still.
        /// </summary>
        public event Action<AvatarPose> PoseChanged;

        /// <summary>Raised after <see cref="Teleport"/>.</summary>
        public event Action<AvatarPose> Teleported;

        private CharacterController controller;
        private AvatarId avatarId;
        private KeyboardAvatarInput ownedInput;
        private MoveLimits limits = MoveLimits.Default;
        private Vector3 velocity;
        private float yaw;
        private bool flying;
        private bool grounded;
        private bool hasRaised;
        private AvatarPose lastRaised;

        /// <summary>Creates the avatar with keyboard input at <paramref name="spawn"/>.</summary>
        public static LocalAvatar Create(string displayName, AvatarLook look, MoveLimits limits, AvatarPose spawn)
        {
            var go = new GameObject("LocalAvatar");
            var avatar = go.AddComponent<LocalAvatar>();
            avatar.SetLimits(limits);
            avatar.SetLook(look);
            avatar.SetName(displayName);
            avatar.Teleport(spawn);
            return avatar;
        }

        /// <summary>Creates the avatar at the origin with default limits and look.</summary>
        public static LocalAvatar Create(string displayName = "") =>
            Create(displayName, AvatarLook.Default, MoveLimits.Default, new AvatarPose(Vector3.zero, 0f));

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            controller.height = ControllerHeight;
            controller.radius = ControllerRadius;
            controller.center = new Vector3(0f, ControllerHeight * 0.5f, 0f);
            controller.stepOffset = 0.4f;
            controller.slopeLimit = 50f;
            controller.skinWidth = 0.03f;
            controller.minMoveDistance = 0f;

            avatarId = gameObject.AddComponent<AvatarId>();
            avatarId.IsLocal = true;

            if (InputSource == null)
            {
                ownedInput = new KeyboardAvatarInput();
                InputSource = ownedInput;
            }

            Visual = AvatarVisuals.CreateFor(transform);
            yaw = AvatarMotion.NormalizeYaw(transform.eulerAngles.y);
            CurrentPose = MakePose(Vector3.zero, false);
        }

        private void OnDestroy()
        {
            ownedInput?.Dispose();
            ownedInput = null;
        }

        public void SetLimits(MoveLimits newLimits)
        {
            limits = newLimits;
            var p = transform.position;
            var clamped = AvatarMotion.ClampToBounds(p, limits);
            if (clamped != p) PlaceAt(clamped);
        }

        public void SetLook(AvatarLook look) => Visual.SetLook(look);

        public void SetName(string displayName)
        {
            DisplayName = displayName ?? "";
            Visual.SetName(DisplayName);
        }

        /// <summary>Starts or stops flying from code.</summary>
        public void SetFlying(bool fly)
        {
            flying = fly;
            if (fly) velocity.y = Mathf.Max(0f, velocity.y);
        }

        /// <summary>
        /// Puts the avatar exactly at <paramref name="pose"/> (clamped into the bounds), facing its yaw, flying if
        /// its mode is Fly, with zero velocity. Used at spawn and for server corrections.
        /// </summary>
        public void Teleport(AvatarPose pose)
        {
            velocity = Vector3.zero;
            flying = pose.Mode == MoveKind.Fly;
            grounded = false;
            yaw = AvatarMotion.NormalizeYaw(pose.Yaw);
            PlaceAt(AvatarMotion.ClampToBounds(pose.Position, limits));
            var placed = MakePose(Vector3.zero, false);
            placed.Animation = flying ? AvatarAnimations.Fly : AvatarAnimations.Idle;
            CurrentPose = placed;
            Visual.UpdatePose(CurrentPose, 0f);
            RaisePoseChanged(force: true);
            Teleported?.Invoke(CurrentPose);
        }

        private void PlaceAt(Vector3 position)
        {
            // A CharacterController keeps its own idea of where it is; disable it while moving the transform.
            bool wasEnabled = controller.enabled;
            controller.enabled = false;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            controller.enabled = wasEnabled;
            Physics.SyncTransforms();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            Step(dt);
        }

        private void Step(float dt)
        {
            var input = InputSource;
            bool jump = input != null && input.ConsumeJump();
            bool toggleFly = input != null && input.ConsumeToggleFly();
            Vector2 move = Vector2.zero;
            bool run = false, up = false, down = false;
            if (InputEnabled && input != null)
            {
                move = Vector2.ClampMagnitude(input.Move, 1f);
                run = input.Run;
                up = input.Up;
                down = input.Down;
            }
            else
            {
                jump = toggleFly = false;
            }

            if (toggleFly) SetFlying(!flying);

            float viewYaw = CurrentViewYaw();
            Vector3 desiredHorizontal;
            if (flying)
            {
                float vertical = (up ? 1f : 0f) - (down ? 1f : 0f);
                var desired = AvatarMotion.FlyVelocity(move, viewYaw, vertical, run, limits, FlySlowFactor);
                desiredHorizontal = new Vector3(desired.x, 0f, desired.z);
                velocity = AvatarMotion.Accelerate(velocity, desired, FlyAcceleration, FlyDeceleration, dt);
                velocity = Vector3.ClampMagnitude(velocity, limits.FlySpeed);
            }
            else
            {
                desiredHorizontal = AvatarMotion.GroundVelocity(move, viewYaw, run, limits);
                var horizontal = new Vector3(velocity.x, 0f, velocity.z);
                float accel = grounded ? GroundAcceleration : AirAcceleration;
                float decel = grounded ? GroundDeceleration : AirAcceleration;
                horizontal = AvatarMotion.Accelerate(horizontal, desiredHorizontal, accel, decel, dt);
                float vy = velocity.y;
                if (grounded && jump)
                {
                    vy = JumpSpeed;
                    grounded = false;
                }
                else if (grounded)
                {
                    vy = 0f;
                }
                else
                {
                    vy = Mathf.Max(vy - Gravity * dt, -MaxFallSpeed);
                }
                velocity = new Vector3(horizontal.x, vy, horizontal.z);
            }

            // While standing, push down a little so the controller stays on slopes and steps down onto stairs.
            var displacement = velocity * dt;
            if (!flying && grounded) displacement.y -= 2f * dt + 0.02f;

            var start = transform.position;
            var target = AvatarMotion.ClampToBounds(start + displacement, limits);
            var flags = controller.Move(target - start);

            var end = transform.position;
            var clampedEnd = AvatarMotion.ClampToBounds(end, limits);
            if (clampedEnd != end) PlaceAt(end = clampedEnd);

            bool onFloorOfBounds = end.y <= limits.BoundsMin.y + 1e-3f && velocity.y <= 0f;
            bool hitBelow = (flags & CollisionFlags.Below) != 0 || onFloorOfBounds;
            if (flying)
            {
                // Landing while descending ends flight.
                if (hitBelow && down && velocity.y <= 0f)
                {
                    flying = false;
                    velocity.y = 0f;
                }
                grounded = !flying && hitBelow;
            }
            else
            {
                grounded = hitBelow || (controller.isGrounded && velocity.y <= 0f);
                if (grounded && velocity.y < 0f) velocity.y = 0f;
            }
            if ((flags & CollisionFlags.Above) != 0 && velocity.y > 0f) velocity.y = 0f;

            // Turn toward the direction of travel (so moving forward faces away from the camera).
            if (desiredHorizontal.sqrMagnitude > 0.01f)
            {
                float targetYaw = AvatarMotion.YawOf(desiredHorizontal);
                float k = 1f - Mathf.Exp(-TurnSharpness * dt);
                yaw = PoseInterpolation.LerpYaw(yaw, targetYaw, k);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            }

            var actual = (end - start) / dt;
            CurrentPose = MakePose(actual, run);
            Visual.UpdatePose(CurrentPose, dt);
            RaisePoseChanged(force: false);
        }

        private float CurrentViewYaw()
        {
            if (ViewYaw != null) return ViewYaw();
            var cam = Camera.main;
            return cam != null ? cam.transform.eulerAngles.y : yaw;
        }

        private AvatarPose MakePose(Vector3 actualVelocity, bool running)
        {
            // Report the real velocity. Standing on the ground has no vertical speed.
            var v = velocity;
            if (grounded && !flying) v.y = 0f;
            // When blocked (walking into a wall) report the horizontal movement that actually happened.
            var actualHorizontal = new Vector2(actualVelocity.x, actualVelocity.z);
            if (actualHorizontal.sqrMagnitude < new Vector2(v.x, v.z).sqrMagnitude)
                v = new Vector3(actualHorizontal.x, v.y, actualHorizontal.y);
            float horizontalSpeed = new Vector2(v.x, v.z).magnitude;
            return new AvatarPose
            {
                Position = transform.position,
                Yaw = yaw,
                Velocity = v,
                Mode = AvatarMotion.ClassifyMode(horizontalSpeed, flying, running, limits),
                Animation = AvatarMotion.AnimationFor(flying, grounded, horizontalSpeed, limits),
            };
        }

        private void RaisePoseChanged(bool force)
        {
            var p = CurrentPose;
            if (!force && hasRaised &&
                (p.Position - lastRaised.Position).sqrMagnitude < 1e-6f &&
                Mathf.Abs(Mathf.DeltaAngle(p.Yaw, lastRaised.Yaw)) < 0.1f &&
                (p.Velocity - lastRaised.Velocity).sqrMagnitude < 1e-4f &&
                p.Mode == lastRaised.Mode &&
                p.Animation == lastRaised.Animation)
                return;
            hasRaised = true;
            lastRaised = p;
            PoseChanged?.Invoke(p);
        }
    }
}
