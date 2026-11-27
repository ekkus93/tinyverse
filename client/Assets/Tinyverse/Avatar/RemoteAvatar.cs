using System;
using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// Someone else's avatar. The owner pushes timestamped states as they arrive; each frame the avatar is drawn
    /// at <c>ServerTime() − InterpolationDelay</c>, interpolated between the states around that time. It carries a
    /// capsule trigger collider so raycasts can pick it but the local player walks through it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RemoteAvatar : MonoBehaviour
    {
        /// <summary>How far in the past avatars are drawn, seconds.</summary>
        public double InterpolationDelay { get; set; } = 0.1;

        /// <summary>
        /// The current estimate of server time, seconds, on the same clock as the timestamps passed to
        /// <see cref="PushState"/>. When null the avatar estimates it from when samples arrive (see
        /// <see cref="EstimatedServerTime"/>).
        /// </summary>
        public Func<double> ServerTime { get; set; }

        /// <summary>The buffered samples and the interpolation rules (teleport distance, extrapolation cap).</summary>
        public PoseBuffer Buffer { get; } = new PoseBuffer();

        /// <summary>The pose drawn this frame.</summary>
        public AvatarPose CurrentPose { get; private set; }

        public ulong Id
        {
            get => avatarId.Id;
            set => avatarId.Id = value;
        }

        public string DisplayName { get; private set; } = "";
        public bool IsAgent { get; private set; }
        public IAvatarVisual Visual { get; private set; }
        public CapsuleCollider Collider { get; private set; }

        private AvatarId avatarId;
        private bool hasPose;
        private bool hasClockOffset;
        private double clockOffset;

        public static RemoteAvatar Create(ulong id, string displayName, AvatarLook look, bool isAgent = false, Transform parent = null)
        {
            var go = new GameObject($"RemoteAvatar {id}");
            if (parent != null) go.transform.SetParent(parent, false);
            var avatar = go.AddComponent<RemoteAvatar>();
            avatar.Id = id;
            avatar.SetName(displayName);
            avatar.SetLook(look);
            avatar.SetAgent(isAgent);
            return avatar;
        }

        /// <summary>The remote avatar a collider (from a raycast, say) belongs to.</summary>
        public static bool TryGetFromCollider(Collider collider, out RemoteAvatar avatar)
        {
            avatar = collider != null ? collider.GetComponentInParent<RemoteAvatar>() : null;
            return avatar != null;
        }

        private void Awake()
        {
            avatarId = gameObject.AddComponent<AvatarId>();
            Collider = gameObject.AddComponent<CapsuleCollider>();
            Collider.isTrigger = true;
            Collider.direction = 1;
            Collider.height = LocalAvatar.ControllerHeight;
            Collider.radius = LocalAvatar.ControllerRadius;
            Collider.center = new Vector3(0f, LocalAvatar.ControllerHeight * 0.5f, 0f);
            // A moving collider should sit on a kinematic body.
            var body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            Visual = AvatarVisuals.CreateFor(transform);
        }

        public void SetName(string displayName)
        {
            DisplayName = displayName ?? "";
            Visual.SetName(DisplayName);
        }

        public void SetLook(AvatarLook look) => Visual.SetLook(look);

        public void SetAgent(bool isAgent)
        {
            IsAgent = isAgent;
            Visual.SetAgent(isAgent);
        }

        /// <summary>
        /// Adds a state the server stamped with <paramref name="serverTimeSeconds"/>. Out-of-order states are put in
        /// order, duplicates ignored. Returns false if the sample was rejected. The first state is shown at once.
        /// </summary>
        public bool PushState(double serverTimeSeconds, AvatarPose pose)
        {
            UpdateClockEstimate(serverTimeSeconds);
            bool added = Buffer.Add(serverTimeSeconds, pose);
            if (!hasPose)
            {
                hasPose = true;
                Show(pose, 0f);
            }
            return added;
        }

        /// <summary>Clears the buffer and shows <paramref name="pose"/> immediately.</summary>
        public void SnapTo(AvatarPose pose)
        {
            Buffer.Clear();
            hasPose = true;
            Show(pose, 0f);
        }

        /// <summary>The server time used this frame: <see cref="ServerTime"/> if set, else the built-in estimate.</summary>
        public double EstimatedServerTime
        {
            get
            {
                if (ServerTime != null) return ServerTime();
                return hasClockOffset ? Time.unscaledTimeAsDouble + clockOffset : Buffer.NewestTime;
            }
        }

        private void UpdateClockEstimate(double serverTime)
        {
            if (ServerTime != null) return;
            // offset = serverTime - localTime, smoothed; re-anchored after a large jump.
            double measured = serverTime - Time.unscaledTimeAsDouble;
            if (!hasClockOffset || Math.Abs(measured - clockOffset) > 1.0)
            {
                clockOffset = measured;
                hasClockOffset = true;
            }
            else
            {
                clockOffset += (measured - clockOffset) * 0.05;
            }
        }

        private void Update()
        {
            if (Buffer.Count == 0) return;
            double renderTime = EstimatedServerTime - InterpolationDelay;
            if (double.IsNaN(renderTime)) return;
            if (Buffer.TrySample(renderTime, out var pose)) Show(pose, Time.deltaTime);
            Buffer.DiscardBefore(renderTime);
        }

        private void Show(AvatarPose pose, float dt)
        {
            pose.Yaw = AvatarMotion.NormalizeYaw(pose.Yaw);
            CurrentPose = pose;
            transform.SetPositionAndRotation(pose.Position, Quaternion.Euler(0f, pose.Yaw, 0f));
            Visual.UpdatePose(pose, dt);
        }
    }
}
