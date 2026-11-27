using System.Collections.Generic;
using Tinyverse.Proto;

namespace Tinyverse.Net
{
    /// <summary>
    /// The join-time snapshot assembled from its chunks. Replaced by a new, empty instance on every
    /// Welcome (including after a reconnect). Only touched on the thread that calls Pump.
    /// </summary>
    public sealed class SnapshotState
    {
        readonly List<AvatarInfo> avatars = new List<AvatarInfo>();
        readonly List<ObjectInfo> objects = new List<ObjectInfo>();

        /// <summary>Avatars present at join time, excluding the own one (that is in Welcome).</summary>
        public IReadOnlyList<AvatarInfo> Avatars => avatars;

        public IReadOnlyList<ObjectInfo> Objects => objects;

        /// <summary>True once the chunk flagged <c>last</c> has been pumped.</summary>
        public bool IsComplete { get; private set; }

        public int ChunkCount { get; private set; }

        internal void Add(Snapshot chunk)
        {
            avatars.AddRange(chunk.Avatars);
            objects.AddRange(chunk.Objects);
            ChunkCount++;
            if (chunk.Last) IsComplete = true;
        }
    }
}
