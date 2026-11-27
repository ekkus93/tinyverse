using System;
using System.Collections;
using System.Collections.Generic;
using Tinyverse.Net;
using UnityEngine;
using P = Tinyverse.Proto;

namespace Tinyverse.Bootstrap.Tests
{
    /// <summary>A second, plain connection (usually the world owner) that tracks the server's objects.</summary>
    sealed class Observer : IDisposable
    {
        public readonly TinyverseConnection Conn = new TinyverseConnection();
        public readonly Dictionary<ulong, P.ObjectInfo> Objects = new Dictionary<ulong, P.ObjectInfo>();
        GameObject go;

        public IEnumerator Join(string url, string name, string token)
        {
            Conn.Snapshot += (s, _) => { foreach (var o in s.Objects) Objects[o.Id] = o; };
            Conn.ObjectCreated += (m, _) => Objects[m.Object.Id] = m.Object;
            Conn.ObjectUpdated += (m, _) => Objects[m.Object.Id] = m.Object;
            Conn.ObjectDeleted += (m, _) => Objects.Remove(m.Id);
            var t = Conn.ConnectAsync(new ConnectOptions { Url = url, DisplayName = name, Token = token ?? "", Kind = P.ClientKind.Agent });
            yield return Wait.Task(t, 15f, "the observer's Welcome");
            go = new GameObject("Observer pump");
            go.AddComponent<Driver>().OnUpdate = _ => Conn.Pump();
        }

        public void Dispose()
        {
            Conn.Dispose();
            if (go != null) UnityEngine.Object.Destroy(go);
        }
    }
}
