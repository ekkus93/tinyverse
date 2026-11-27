using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Tinyverse.Net
{
    /// <summary>
    /// Many producers, one consumer. An AvatarState that is still queued is dropped when a newer one
    /// is enqueued, and the newer one goes to the back, so seq stays increasing on the wire.
    /// </summary>
    sealed class SendQueue
    {
        readonly object gate = new object();
        readonly LinkedList<byte[]> items = new LinkedList<byte[]>();
        readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        LinkedListNode<byte[]> queuedAvatarState;
        bool completed;

        public int Count
        {
            get { lock (gate) return items.Count; }
        }

        /// <summary>True after <see cref="Complete"/>; the consumer stops once the queue is drained.</summary>
        public bool IsCompleted
        {
            get { lock (gate) return completed; }
        }

        public bool Enqueue(byte[] frame, bool isAvatarState)
        {
            lock (gate)
            {
                if (completed) return false;
                if (isAvatarState)
                {
                    if (queuedAvatarState != null) items.Remove(queuedAvatarState);
                    queuedAvatarState = items.AddLast(frame);
                }
                else
                {
                    items.AddLast(frame);
                }
            }
            signal.Release();
            return true;
        }

        public bool TryDequeue(out byte[] frame)
        {
            lock (gate)
            {
                var first = items.First;
                if (first == null)
                {
                    frame = null;
                    return false;
                }
                if (first == queuedAvatarState) queuedAvatarState = null;
                items.RemoveFirst();
                frame = first.Value;
                return true;
            }
        }

        public Task WaitAsync(CancellationToken cancellationToken) => signal.WaitAsync(cancellationToken);

        /// <summary>Refuses further items and wakes the consumer so it can drain and stop.</summary>
        public void Complete()
        {
            lock (gate) completed = true;
            signal.Release();
        }
    }
}
