using System.Collections.Generic;
using UnityEngine;

namespace Tinyverse.Avatars.Tests
{
    /// <summary>Builds and tears down test geometry and avatars; fixes the frame time at 1/60 s.</summary>
    internal sealed class AvatarTestScene
    {
        public const float Dt = 1f / 60f;
        private readonly List<GameObject> objects = new List<GameObject>();

        public AvatarTestScene()
        {
            Time.captureDeltaTime = Dt;
        }

        public GameObject Box(Vector3 center, Vector3 size, string name = "Box")
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = center;
            go.transform.localScale = size;
            objects.Add(go);
            Physics.SyncTransforms();
            return go;
        }

        /// <summary>A 400 m square floor whose top is at y = 0.</summary>
        public GameObject Ground() => Box(new Vector3(0f, -0.5f, 0f), new Vector3(400f, 1f, 400f), "Ground");

        public (LocalAvatar avatar, ScriptedAvatarInput input) Local(Vector3 position, float yaw = 0f, MoveLimits? limits = null)
        {
            var avatar = LocalAvatar.Create("Tester", AvatarLook.WithBody(Color.red), limits ?? MoveLimits.Default,
                new AvatarPose(position, yaw));
            var input = new ScriptedAvatarInput();
            avatar.InputSource = input;
            avatar.ViewYaw = () => 0f;
            objects.Add(avatar.gameObject);
            return (avatar, input);
        }

        public RemoteAvatar Remote(ulong id, string name, bool agent = false)
        {
            var r = RemoteAvatar.Create(id, name, AvatarLook.WithBody(Color.green), agent);
            objects.Add(r.gameObject);
            return r;
        }

        public T Track<T>(T component) where T : Component
        {
            objects.Add(component.gameObject);
            return component;
        }

        public void Dispose()
        {
            foreach (var go in objects)
                if (go != null) Object.Destroy(go);
            objects.Clear();
            NameTag.ViewCamera = null;
            Time.captureDeltaTime = 0f;
        }
    }
}
