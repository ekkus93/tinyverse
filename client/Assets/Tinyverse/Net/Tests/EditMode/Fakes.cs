using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using NUnit.Framework;
using Tinyverse.Proto;

namespace Tinyverse.Net.Tests
{
    /// <summary>A clock that only moves when told to.</summary>
    public sealed class ManualClock : IClock
    {
        readonly object gate = new object();
        readonly List<(long due, TaskCompletionSource<bool> tcs)> waiters = new List<(long, TaskCompletionSource<bool>)>();
        long now;

        public long MonotonicMs => Interlocked.Read(ref now);
        public long UnixTimeMs => 1_700_000_000_000 + MonotonicMs;

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long due;
            lock (gate)
            {
                due = now + (long)delay.TotalMilliseconds;
                if (due <= now)
                {
                    tcs.TrySetResult(true);
                    return tcs.Task;
                }
                waiters.Add((due, tcs));
            }
            cancellationToken.Register(() =>
            {
                lock (gate) waiters.RemoveAll(w => w.tcs == tcs);
                tcs.TrySetCanceled();
            });
            return tcs.Task;
        }

        public void Advance(TimeSpan by) => Advance((long)by.TotalMilliseconds);

        public void Advance(long ms)
        {
            List<TaskCompletionSource<bool>> due;
            lock (gate)
            {
                now += ms;
                due = waiters.Where(w => w.due <= now).Select(w => w.tcs).ToList();
                waiters.RemoveAll(w => w.due <= now);
            }
            foreach (var t in due) t.TrySetResult(true);
        }
    }

    /// <summary>An in-process server: creates one <see cref="FakeTransport"/> per connection attempt.</summary>
    public sealed class FakeServer
    {
        readonly object gate = new object();
        readonly List<FakeTransport> connections = new List<FakeTransport>();

        /// <summary>Returns the exception that ConnectAsync of the n-th (0-based) transport throws, or null.</summary>
        public Func<int, Exception> ConnectFailure;

        /// <summary>If set, ConnectAsync waits until cancelled.</summary>
        public bool HangOnConnect;

        /// <summary>Answers Hello. Default: a Welcome with token "token-1".</summary>
        public Action<FakeTransport, ClientMessage> OnHello;

        /// <summary>Answers every other message. Pings are answered first if <see cref="AutoPong"/>.</summary>
        public Action<FakeTransport, ClientMessage> OnMessage;

        public bool AutoPong = true;
        public long ServerTimeOffsetMs = 0;
        public IClock Clock;

        public FakeServer()
        {
            OnHello = (t, m) => t.ServerSends(new ServerMessage { ReplyTo = m.Seq, Welcome = MakeWelcome("token-1") });
        }

        public static Welcome MakeWelcome(string token) => new Welcome
        {
            ProtocolVersion = 1,
            UserId = "user-1",
            AvatarId = 7,
            Role = Role.Builder,
            SessionToken = token,
            TickRateHz = 15,
            ServerTimeMs = 1_700_000_000_000,
            ServerVersion = "fake",
            World = new WorldInfo { Name = "Fake" },
        };

        public IReadOnlyList<FakeTransport> Connections
        {
            get { lock (gate) return connections.ToList(); }
        }

        public FakeTransport Last => Connections.Last();

        public IWebSocketTransport Create()
        {
            lock (gate)
            {
                var t = new FakeTransport(this, connections.Count);
                connections.Add(t);
                return t;
            }
        }

        internal void Handle(FakeTransport t, ClientMessage m)
        {
            if (m.BodyCase == ClientMessage.BodyOneofCase.Hello)
            {
                OnHello?.Invoke(t, m);
                return;
            }
            if (m.BodyCase == ClientMessage.BodyOneofCase.Ping && AutoPong)
            {
                long serverNow = (Clock?.UnixTimeMs ?? 0) + ServerTimeOffsetMs;
                t.ServerSends(new ServerMessage
                {
                    ReplyTo = m.Seq,
                    Pong = new Pong { ClientTimeMs = m.Ping.ClientTimeMs, ServerTimeMs = serverNow },
                });
                return;
            }
            OnMessage?.Invoke(t, m);
        }
    }

    public sealed class FakeTransport : IWebSocketTransport
    {
        readonly FakeServer server;
        readonly ConcurrentQueue<WebSocketFrame> inbound = new ConcurrentQueue<WebSocketFrame>();
        readonly SemaphoreSlim available = new SemaphoreSlim(0);
        readonly object gate = new object();
        readonly List<ClientMessage> sent = new List<ClientMessage>();
        volatile bool aborted;

        public readonly int Index;
        public Uri Uri { get; private set; }
        public bool Disposed { get; private set; }
        public bool Aborted => aborted;
        public bool CloseRequested { get; private set; }

        /// <summary>If set, every SendBinaryAsync waits for this task first.</summary>
        public volatile Task SendGate;

        internal FakeTransport(FakeServer server, int index)
        {
            this.server = server;
            Index = index;
        }

        public IReadOnlyList<ClientMessage> Sent
        {
            get { lock (gate) return sent.ToList(); }
        }

        public IReadOnlyList<ClientMessage> SentExceptPings =>
            Sent.Where(m => m.BodyCase != ClientMessage.BodyOneofCase.Ping).ToList();

        public Hello Hello => Sent.FirstOrDefault(m => m.BodyCase == ClientMessage.BodyOneofCase.Hello)?.Hello;

        public void ServerSends(ServerMessage m)
        {
            inbound.Enqueue(WebSocketFrame.Binary(m.ToByteArray()));
            available.Release();
        }

        public void ServerCloses(string reason = "")
        {
            inbound.Enqueue(WebSocketFrame.Closed(1000, reason));
            available.Release();
        }

        public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            Uri = uri;
            var failure = server.ConnectFailure?.Invoke(Index);
            if (failure != null) throw failure;
            if (server.HangOnConnect) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        int sendCalls;

        /// <summary>Number of SendBinaryAsync calls started, including ones waiting on <see cref="SendGate"/>.</summary>
        public int SendCalls => Volatile.Read(ref sendCalls);

        public async Task SendBinaryAsync(byte[] data, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sendCalls);
            var gateTask = SendGate;
            if (gateTask != null) await gateTask.ConfigureAwait(false);
            if (aborted) throw new InvalidOperationException("aborted");
            var m = ClientMessage.Parser.ParseFrom(data);
            lock (gate) sent.Add(m);
            server.Handle(this, m);
        }

        public async Task<WebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (aborted) throw new InvalidOperationException("aborted");
            inbound.TryDequeue(out var frame);
            return frame;
        }

        public Task CloseAsync(string reason, CancellationToken cancellationToken)
        {
            CloseRequested = true;
            return Task.CompletedTask;
        }

        public void Abort()
        {
            aborted = true;
            available.Release();
        }

        public void Dispose()
        {
            Disposed = true;
            Abort();
        }
    }

    public static class Wait
    {
        /// <summary>Waits in real time for background threads, pumping the connection if given.</summary>
        public static void Until(Func<bool> condition, TinyverseConnection pump = null, int timeoutMs = 5000,
            string what = "condition")
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                pump?.Pump();
                if (condition()) return;
                if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}.");
                Thread.Sleep(2);
            }
        }

        /// <summary>Advances the manual clock in steps until the condition holds.</summary>
        public static void AdvanceUntil(ManualClock clock, Func<bool> condition, TinyverseConnection pump = null,
            long stepMs = 50, int timeoutMs = 10000, string what = "condition")
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                pump?.Pump();
                if (condition()) return;
                if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}.");
                clock.Advance(stepMs);
                Thread.Sleep(1);
            }
        }

        public static T Result<T>(Task<T> task, TinyverseConnection pump = null, int timeoutMs = 5000)
        {
            Until(() => task.IsCompleted, pump, timeoutMs, "task");
            return task.GetAwaiter().GetResult();
        }

        public static Exception Failure(Task task, TinyverseConnection pump = null, int timeoutMs = 5000)
        {
            Until(() => task.IsCompleted, pump, timeoutMs, "task");
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                return e;
            }
            Assert.Fail("Expected the task to fail.");
            return null;
        }
    }
}
