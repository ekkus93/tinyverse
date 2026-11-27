using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Tinyverse.Proto;

namespace Tinyverse.Net.Tests
{
    [Timeout(30000)]
    public class TinyverseConnectionTests
    {
        FakeServer server;
        ManualClock clock;
        TinyverseConnection conn;
        List<Exception> handlerErrors;

        const string Url = "ws://fake.test:7878/ws";

        TinyverseConnection Make(Action<ConnectionSettings> tweak = null)
        {
            clock = new ManualClock();
            server = new FakeServer { Clock = clock };
            handlerErrors = new List<Exception>();
            var settings = new ConnectionSettings
            {
                TransportFactory = server.Create,
                Clock = clock,
                MaxReconnectAttempts = 0,
                HandlerExceptionLogger = e => handlerErrors.Add(e),
            };
            tweak?.Invoke(settings);
            conn = new TinyverseConnection(settings);
            return conn;
        }

        static ConnectOptions Options(string token = "") => new ConnectOptions
        {
            Url = Url,
            DisplayName = "Alice",
            Password = "secret",
            Token = token,
            Kind = ClientKind.Human,
            Appearance = new Appearance { BodyColor = new Color { R = 1, G = 0.5f, B = 0, A = 1 }, HairStyle = "bob" },
            ClientName = "tests/1.0",
        };

        Welcome Connect(ConnectOptions options = null) => Wait.Result(conn.ConnectAsync(options ?? Options()));

        [TearDown]
        public void TearDown()
        {
            conn?.Dispose();
            if (handlerErrors != null && handlerErrors.Count > 0) Assert.Fail("Handler threw: " + handlerErrors[0]);
        }

        // ---- handshake ---------------------------------------------------------------------

        [Test]
        public void HandshakeSendsHelloAndCompletesWithWelcome()
        {
            Make();
            var w = Connect();

            Assert.That(w.UserId, Is.EqualTo("user-1"));
            Assert.That(conn.Welcome, Is.SameAs(w));
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Connected));

            var t = server.Last;
            Assert.That(t.Uri, Is.EqualTo(new Uri(Url)));
            var first = t.Sent[0];
            Assert.That(first.BodyCase, Is.EqualTo(ClientMessage.BodyOneofCase.Hello));
            Assert.That(first.Seq, Is.Not.EqualTo(0u));
            var hello = first.Hello;
            Assert.That(hello.ProtocolVersion, Is.EqualTo(1u));
            Assert.That(hello.Kind, Is.EqualTo(ClientKind.Human));
            Assert.That(hello.Name, Is.EqualTo("Alice"));
            Assert.That(hello.Password, Is.EqualTo("secret"));
            Assert.That(hello.Token, Is.EqualTo(""));
            Assert.That(hello.ClientName, Is.EqualTo("tests/1.0"));
            Assert.That(hello.Appearance.HairStyle, Is.EqualTo("bob"));
            Assert.That(hello.Appearance.BodyColor.G, Is.EqualTo(0.5f));
        }

        [Test]
        public void StatesAndWelcomedAreRaisedInOrder()
        {
            Make();
            var states = new List<ConnectionState>();
            var welcomed = new List<Welcome>();
            conn.StateChanged += s => states.Add(s);
            conn.Welcomed += w => welcomed.Add(w);
            Connect();
            conn.Pump();
            Assert.That(states, Is.EqualTo(new[] { ConnectionState.Connecting, ConnectionState.Joining, ConnectionState.Connected }));
            Assert.That(welcomed.Count, Is.EqualTo(1));
        }

        [Test]
        public void RefusalSurfacesCodeAndMessage()
        {
            Make();
            server.OnHello = (t, m) =>
            {
                t.ServerSends(new ServerMessage { ReplyTo = m.Seq, Error = new Error { Code = ErrorCode.AuthFailed, Message = "wrong password" } });
                t.ServerCloses();
            };
            var e = Wait.Failure(conn.ConnectAsync(Options()));
            Assert.That(e, Is.TypeOf<TinyverseServerException>());
            var se = (TinyverseServerException)e;
            Assert.That(se.Code, Is.EqualTo(ErrorCode.AuthFailed));
            Assert.That(se.ServerMessage, Is.EqualTo("wrong password"));
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Disconnected));
            Assert.That(server.Last.Disposed, Is.True);
        }

        [Test]
        public void HandshakeTimesOut()
        {
            Make();
            server.OnHello = (t, m) => { }; // never answers
            var task = conn.ConnectAsync(Options());
            Wait.Until(() => server.Connections.Count == 1 && server.Last.Hello != null);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.That(Wait.Failure(task), Is.TypeOf<TimeoutException>());
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Disconnected));
        }

        [Test]
        public void ConnectTimesOut()
        {
            Make();
            server.HangOnConnect = true;
            var task = conn.ConnectAsync(Options());
            Wait.Until(() => server.Connections.Count == 1);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.That(Wait.Failure(task), Is.TypeOf<TimeoutException>());
        }

        [Test]
        public void CloseDuringConnectCancels()
        {
            Make();
            server.HangOnConnect = true;
            bool disconnectedRaised = false;
            conn.Disconnected += (r, k) => disconnectedRaised = true;
            var task = conn.ConnectAsync(Options());
            Wait.Until(() => server.Connections.Count == 1);

            Assert.DoesNotThrow(() => conn.Close());

            Assert.That(Wait.Failure(task), Is.InstanceOf<OperationCanceledException>());
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Disconnected));
            conn.Pump();
            Assert.That(disconnectedRaised, Is.False);
            Wait.Until(() => server.Last.Disposed, what: "transport disposed");
        }

        // ---- dispatch ----------------------------------------------------------------------

        [Test]
        public void NothingIsRaisedBeforePump()
        {
            Make();
            int raised = 0;
            conn.StateChanged += s => raised++;
            conn.Welcomed += w => raised++;
            conn.MessageReceived += m => raised++;
            conn.ChatMessage += (m, r) => raised++;
            Connect();
            server.Last.ServerSends(new ServerMessage { ChatMessage = new ChatMessage { Entry = new ChatEntry { Text = "hi" } } });
            Thread.Sleep(100);
            Assert.That(raised, Is.EqualTo(0));

            int chats = 0;
            conn.ChatMessage += (m, r) => chats++;
            Wait.Until(() => chats == 1, conn);
            Assert.That(raised, Is.GreaterThan(3));
        }

        [Test]
        public void EventsAreRaisedOnThePumpingThread()
        {
            Make();
            int handlerThread = -1;
            conn.ChatMessage += (m, r) => handlerThread = Thread.CurrentThread.ManagedThreadId;
            Connect();
            server.Last.ServerSends(new ServerMessage { ChatMessage = new ChatMessage { Entry = new ChatEntry { Text = "hi" } } });

            int pumpThread = -1;
            var thread = new Thread(() =>
            {
                pumpThread = Thread.CurrentThread.ManagedThreadId;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (handlerThread == -1 && DateTime.UtcNow < deadline)
                {
                    conn.Pump();
                    Thread.Sleep(1);
                }
            });
            thread.Start();
            thread.Join();
            Assert.That(handlerThread, Is.Not.EqualTo(-1));
            Assert.That(handlerThread, Is.EqualTo(pumpThread));
            Assert.That(handlerThread, Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId));
        }

        [Test]
        public void EveryServerBodyReachesItsTypedEvent()
        {
            Make();
            var got = new Dictionary<string, uint>();
            int messageReceived = 0;
            conn.MessageReceived += m => messageReceived++;
            conn.Snapshot += (b, r) => got["Snapshot"] = r;
            conn.AvatarJoined += (b, r) => got["AvatarJoined"] = r;
            conn.AvatarLeft += (b, r) => got["AvatarLeft"] = r;
            conn.AvatarStates += (b, r) => got["AvatarStates"] = r;
            conn.AvatarCorrection += (b, r) => got["AvatarCorrection"] = r;
            conn.AvatarUpdated += (b, r) => got["AvatarUpdated"] = r;
            conn.ChatMessage += (b, r) => got["ChatMessage"] = r;
            conn.ChatHistory += (b, r) => got["ChatHistory"] = r;
            conn.ObjectCreated += (b, r) => got["ObjectCreated"] = r;
            conn.ObjectUpdated += (b, r) => got["ObjectUpdated"] = r;
            conn.ObjectDeleted += (b, r) => got["ObjectDeleted"] = r;
            conn.Observation += (b, r) => got["Observation"] = r;
            conn.Error += (b, r) => got["Error"] = r;
            conn.Kicked += (b, r) => got["Kicked"] = r;
            Connect();
            conn.Pump();
            Wait.Until(() => conn.RoundTripTimeMs >= 0, conn, what: "first pong"); // drain the keepalive ping/pong
            conn.Pump();
            messageReceived = 0;

            var messages = new[]
            {
                new ServerMessage { Snapshot = new Snapshot { Last = true } },
                new ServerMessage { AvatarJoined = new AvatarJoined() },
                new ServerMessage { AvatarLeft = new AvatarLeft { AvatarId = 3 } },
                new ServerMessage { AvatarStates = new AvatarStates() },
                new ServerMessage { AvatarCorrection = new AvatarCorrection() },
                new ServerMessage { AvatarUpdated = new AvatarUpdated() },
                new ServerMessage { ChatMessage = new ChatMessage() },
                new ServerMessage { ChatHistory = new ChatHistory() },
                new ServerMessage { ObjectCreated = new ObjectCreated() },
                new ServerMessage { ObjectUpdated = new ObjectUpdated() },
                new ServerMessage { ObjectDeleted = new ObjectDeleted() },
                new ServerMessage { Observation = new Observation() },
                new ServerMessage { Error = new Error { Code = ErrorCode.NotFound } },
                new ServerMessage { Kicked = new Kicked { Reason = "x" } },
            };
            uint reply = 1000;
            foreach (var m in messages)
            {
                m.ReplyTo = reply++;
                server.Last.ServerSends(m);
            }
            Wait.Until(() => got.Count == messages.Length, conn, what: "all typed events");

            for (int i = 0; i < messages.Length; i++)
            {
                string name = messages[i].BodyCase.ToString();
                Assert.That(got.ContainsKey(name), name);
                Assert.That(got[name], Is.EqualTo(1000u + (uint)i), name);
            }
            Assert.That(messageReceived, Is.EqualTo(messages.Length));
            // The list above covers every body except Welcome (handshake), Pong and Ack (MessageReceived only).
            int bodies = Enum.GetValues(typeof(ServerMessage.BodyOneofCase)).Length - 1;
            Assert.That(messages.Length + 3, Is.EqualTo(bodies));
        }

        [Test]
        public void SnapshotChunksAreAssembledAndCompleteIsRaisedOnce()
        {
            Make();
            server.OnHello = (t, m) =>
            {
                t.ServerSends(new ServerMessage { ReplyTo = m.Seq, Welcome = FakeServer.MakeWelcome("tok") });
                var a = new Snapshot { Last = false };
                a.Avatars.Add(new AvatarInfo { Id = 1, Name = "Bob" });
                a.Objects.Add(new ObjectInfo { Id = 10 });
                var b = new Snapshot { Last = true };
                b.Avatars.Add(new AvatarInfo { Id = 2, Name = "Carol" });
                t.ServerSends(new ServerMessage { Snapshot = a });
                t.ServerSends(new ServerMessage { Snapshot = b });
            };
            int chunks = 0, complete = 0;
            SnapshotState completed = null;
            conn.Snapshot += (s, r) => chunks++;
            conn.SnapshotComplete += s =>
            {
                complete++;
                completed = s;
            };
            Connect();
            Wait.Until(() => complete == 1, conn);
            Assert.That(chunks, Is.EqualTo(2));
            Assert.That(completed, Is.SameAs(conn.SnapshotState));
            Assert.That(completed.IsComplete, Is.True);
            Assert.That(completed.ChunkCount, Is.EqualTo(2));
            Assert.That(completed.Avatars.Select(a => a.Name), Is.EqualTo(new[] { "Bob", "Carol" }));
            Assert.That(completed.Objects.Count, Is.EqualTo(1));
        }

        // ---- sending -----------------------------------------------------------------------

        [Test]
        public void SendAssignsIncreasingSeqAndRepliesCarryIt()
        {
            Make();
            server.OnMessage = (t, m) =>
            {
                if (m.BodyCase == ClientMessage.BodyOneofCase.Chat)
                    t.ServerSends(new ServerMessage { ReplyTo = m.Seq, ChatMessage = new ChatMessage { Entry = new ChatEntry { Text = m.Chat.Text } } });
            };
            var replies = new Dictionary<string, uint>();
            conn.ChatMessage += (c, r) => replies[c.Entry.Text] = r;
            Connect();

            uint a = conn.SendChat(ChatChannel.World, "a");
            uint b = conn.SendChat(ChatChannel.Local, "b");
            Assert.That(a, Is.GreaterThan(0u));
            Assert.That(b, Is.EqualTo(a + 1));

            Wait.Until(() => replies.Count == 2, conn);
            Assert.That(replies["a"], Is.EqualTo(a));
            Assert.That(replies["b"], Is.EqualTo(b));
            var sentChats = server.Last.SentExceptPings.Where(m => m.BodyCase == ClientMessage.BodyOneofCase.Chat).ToList();
            Assert.That(sentChats.Select(m => m.Seq), Is.EqualTo(new[] { a, b }));
            Assert.That(sentChats[1].Chat.Channel, Is.EqualTo(ChatChannel.Local));
        }

        [Test]
        public void ConvenienceSendersProduceTheirBodies()
        {
            Make();
            Connect();
            conn.SendAvatarState(new AvatarTransform { Yaw = 90 });
            conn.SendChat(ChatChannel.World, "hi");
            conn.MoveTo(new Vec3 { X = 1 }, MoveMode.Fly);
            conn.Stop();
            conn.RequestChatHistory(5, 10);
            conn.CreateObject(new ObjectSpec { Shape = Shape.Box });
            conn.UpdateObject(new ObjectUpdate { Id = 4, Name = "n" });
            conn.DeleteObject(4);
            conn.Observe(12);
            conn.SetAppearance(new Appearance { HairStyle = "x" });
            conn.Kick("u1", "r");
            conn.Ban("u2", "r");
            conn.Unban("u3");
            conn.SetRole("u4", Role.Owner);

            Wait.Until(() => server.Last.SentExceptPings.Count == 15);
            var sent = server.Last.SentExceptPings.Skip(1).ToList();
            Assert.That(sent.Select(m => m.BodyCase), Is.EqualTo(new[]
            {
                ClientMessage.BodyOneofCase.AvatarState, ClientMessage.BodyOneofCase.Chat, ClientMessage.BodyOneofCase.MoveTo,
                ClientMessage.BodyOneofCase.Stop, ClientMessage.BodyOneofCase.ChatHistoryRequest, ClientMessage.BodyOneofCase.ObjectCreate,
                ClientMessage.BodyOneofCase.ObjectUpdate, ClientMessage.BodyOneofCase.ObjectDelete, ClientMessage.BodyOneofCase.Observe,
                ClientMessage.BodyOneofCase.SetAppearance, ClientMessage.BodyOneofCase.Admin, ClientMessage.BodyOneofCase.Admin,
                ClientMessage.BodyOneofCase.Admin, ClientMessage.BodyOneofCase.Admin,
            }));
            Assert.That(sent[2].MoveTo.Mode, Is.EqualTo(MoveMode.Fly));
            Assert.That(sent[4].ChatHistoryRequest.Limit, Is.EqualTo(10u));
            Assert.That(sent[10].Admin.Kick.UserId, Is.EqualTo("u1"));
            Assert.That(sent[11].Admin.Ban.UserId, Is.EqualTo("u2"));
            Assert.That(sent[12].Admin.Unban.UserId, Is.EqualTo("u3"));
            Assert.That(sent[13].Admin.SetRole.Role, Is.EqualTo(Role.Owner));
        }

        [Test]
        public void SendWhenNotConnectedReturnsZero()
        {
            Make();
            Assert.That(conn.SendChat(ChatChannel.World, "x"), Is.EqualTo(0u));
            Assert.Throws<ArgumentException>(() => conn.Send(new ClientMessage()));
        }

        [Test]
        public void QueuedAvatarStatesAreCoalesced()
        {
            Make();
            Connect();
            Wait.Until(() => server.Last.Sent.Count >= 2, what: "first ping"); // hello + keepalive ping
            var t = server.Last;
            var gate = new TaskCompletionSource<bool>();
            t.SendGate = gate.Task;
            int calls = t.SendCalls;

            conn.SendChat(ChatChannel.World, "a");
            Wait.Until(() => t.SendCalls == calls + 1, what: "writer blocked on the gate");
            conn.SendAvatarState(new AvatarTransform { Yaw = 1 });
            conn.SendChat(ChatChannel.World, "b");
            conn.SendAvatarState(new AvatarTransform { Yaw = 2 });
            uint last = conn.SendAvatarState(new AvatarTransform { Yaw = 3 });
            t.SendGate = null;
            gate.SetResult(true);

            Wait.Until(() => t.SentExceptPings.Any(m => m.Seq == last));
            var after = t.SentExceptPings.Skip(1).ToList();
            Assert.That(after.Select(m => m.BodyCase.ToString() + (m.Chat?.Text ?? m.AvatarState?.Transform.Yaw.ToString())),
                Is.EqualTo(new[] { "Chata", "Chatb", "AvatarState3" }));
            Assert.That(after.Select(m => m.Seq), Is.Ordered);
        }

        [Test]
        public void RequestAsyncCompletesWithReplyAndEventStillFires()
        {
            Make();
            server.OnMessage = (t, m) =>
            {
                if (m.BodyCase == ClientMessage.BodyOneofCase.ChatHistoryRequest)
                {
                    var h = new ChatHistory();
                    h.Entries.Add(new ChatEntry { Text = "old" });
                    t.ServerSends(new ServerMessage { ChatHistory = new ChatHistory() }); // unrelated, reply_to 0
                    t.ServerSends(new ServerMessage { ReplyTo = m.Seq, ChatHistory = h });
                }
            };
            uint eventReplyTo = 0;
            conn.ChatHistory += (h, r) => { if (r != 0) eventReplyTo = r; };
            Connect();
            var task = conn.RequestAsync(new ClientMessage { ChatHistoryRequest = new ChatHistoryRequest() });
            var reply = Wait.Result(task, conn);
            Assert.That(reply.ChatHistory.Entries[0].Text, Is.EqualTo("old"));
            Assert.That(reply.ReplyTo, Is.Not.EqualTo(0u));
            Wait.Until(() => eventReplyTo == reply.ReplyTo, conn);
        }

        [Test]
        public void RequestAsyncThrowsOnErrorReply()
        {
            Make();
            server.OnMessage = (t, m) =>
                t.ServerSends(new ServerMessage { ReplyTo = m.Seq, Error = new Error { Code = ErrorCode.PermissionDenied, Message = "locked" } });
            int errors = 0;
            conn.Error += (e, r) => errors++;
            Connect();
            var e = Wait.Failure(conn.RequestAsync(new ClientMessage { ObjectDelete = new ObjectDelete { Id = 9 } }), conn);
            Assert.That(e, Is.TypeOf<TinyverseServerException>());
            Assert.That(((TinyverseServerException)e).Code, Is.EqualTo(ErrorCode.PermissionDenied));
            Assert.That(((TinyverseServerException)e).ReplyTo, Is.Not.EqualTo(0u));
            Wait.Until(() => errors == 1, conn);
        }

        [Test]
        public void RequestAsyncTimesOut()
        {
            Make();
            Connect();
            var task = conn.RequestAsync(new ClientMessage { Observe = new Observe() }, TimeSpan.FromSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(2));
            conn.Pump();
            Thread.Sleep(50);
            Assert.That(task.IsCompleted, Is.False);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(Wait.Failure(task, conn), Is.TypeOf<TimeoutException>());
        }

        [Test]
        public void RequestAsyncFailsWhenNotConnected()
        {
            Make();
            var e = Wait.Failure(conn.RequestAsync(new ClientMessage { Stop = new Stop() }));
            Assert.That(e, Is.TypeOf<TinyverseConnectionException>());
        }

        // ---- keepalive ---------------------------------------------------------------------

        [Test]
        public void PingMeasuresRoundTripAndClockOffset()
        {
            Make();
            server.AutoPong = false;
            Connect();
            var t = server.Last;
            Func<List<ClientMessage>> pings = () => t.Sent.Where(m => m.BodyCase == ClientMessage.BodyOneofCase.Ping).ToList();
            Wait.Until(() => pings().Count == 1, what: "ping right after Welcome");
            Assert.That(conn.RoundTripTimeMs, Is.EqualTo(-1));

            var ping = pings()[0].Ping;
            clock.Advance(80);
            t.ServerSends(new ServerMessage { Pong = new Pong { ClientTimeMs = ping.ClientTimeMs, ServerTimeMs = ping.ClientTimeMs + 40 + 5000 } });
            Wait.Until(() => conn.RoundTripTimeMs == 80, what: "rtt");
            Assert.That(conn.ServerClockOffsetMs, Is.EqualTo(5000));
            Assert.That(conn.EstimatedServerTimeMs, Is.EqualTo(clock.UnixTimeMs + 5000));

            clock.Advance(9000);
            Thread.Sleep(50);
            Assert.That(pings().Count, Is.EqualTo(1));
            Wait.AdvanceUntil(clock, () => pings().Count == 2, what: "second ping");
            Assert.That(clock.MonotonicMs, Is.InRange(10000, 11000));
        }

        [Test]
        public void SilentServerIsDetectedAsDead()
        {
            Make();
            server.AutoPong = false;
            string reason = null;
            conn.Disconnected += (r, k) => reason = r;
            Connect();
            clock.Advance(29000);
            Thread.Sleep(100);
            conn.Pump();
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Connected));

            // A frame resets the timer.
            server.Last.ServerSends(new ServerMessage { AvatarStates = new AvatarStates() });
            Wait.Until(() => { conn.Pump(); return true; });
            Thread.Sleep(50);
            clock.Advance(29000);
            Thread.Sleep(100);
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Connected));

            Wait.AdvanceUntil(clock, () => conn.State == ConnectionState.Disconnected, conn, stepMs: 200);
            Wait.Until(() => reason != null, conn);
            Assert.That(reason, Does.Contain("No data"));
            Assert.That(server.Last.Aborted, Is.True);
        }

        // ---- reconnect ---------------------------------------------------------------------

        void ReconnectSettings(ConnectionSettings s, int attempts)
        {
            s.MaxReconnectAttempts = attempts;
            s.ReconnectBaseDelay = TimeSpan.FromSeconds(1);
            s.ReconnectMaxDelay = TimeSpan.FromSeconds(30);
            s.ConnectTimeout = TimeSpan.FromHours(1);
            s.HandshakeTimeout = TimeSpan.FromHours(1);
        }

        [Test]
        public void ReconnectUsesSessionTokenAndBacksOff()
        {
            Make(s => ReconnectSettings(s, 5));
            server.ConnectFailure = i => i == 1 || i == 2 ? new Exception("refused") : null;
            server.OnHello = (t, m) => t.ServerSends(new ServerMessage { ReplyTo = m.Seq, Welcome = FakeServer.MakeWelcome("token-" + (t.Index + 1)) });
            var states = new List<ConnectionState>();
            var delays = new List<TimeSpan>();
            var welcomes = new List<Welcome>();
            bool disconnected = false;
            conn.StateChanged += s => states.Add(s);
            conn.ReconnectScheduled += (n, d) => delays.Add(d);
            conn.Welcomed += w => welcomes.Add(w);
            conn.Disconnected += (r, k) => disconnected = true;

            Connect(Options(token: "stored"));
            Assert.That(server.Connections[0].Hello.Token, Is.EqualTo("stored"));
            conn.Pump();

            server.Last.ServerCloses("restarting");
            Wait.AdvanceUntil(clock, () => welcomes.Count == 2, conn, stepMs: 100);

            Assert.That(server.Connections.Count, Is.EqualTo(4));
            Assert.That(server.Connections[3].Hello.Token, Is.EqualTo("token-1"));
            Assert.That(server.Connections[3].Hello.Name, Is.EqualTo("Alice"));
            Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }));
            Assert.That(states, Is.EqualTo(new[]
            {
                ConnectionState.Connecting, ConnectionState.Joining, ConnectionState.Connected,
                ConnectionState.Reconnecting, ConnectionState.Connected,
            }));
            Assert.That(conn.Welcome.SessionToken, Is.EqualTo("token-4"));
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Connected));
            Assert.That(disconnected, Is.False);

            // The new link works.
            uint s1 = conn.SendChat(ChatChannel.World, "back");
            Wait.Until(() => server.Last.SentExceptPings.Any(m => m.Seq == s1));
        }

        [Test]
        public void ReconnectSendsLatestAppearance()
        {
            Make(s => ReconnectSettings(s, 2));
            var welcomes = 0;
            conn.Welcomed += w => welcomes++;
            Connect();
            conn.SetAppearance(new Appearance { HairStyle = "new" });
            Wait.Until(() => server.Last.SentExceptPings.Any(m => m.BodyCase == ClientMessage.BodyOneofCase.SetAppearance));
            server.Last.ServerCloses();
            Wait.AdvanceUntil(clock, () => welcomes == 2, conn, stepMs: 100);
            Assert.That(server.Last.Hello.Appearance.HairStyle, Is.EqualTo("new"));
        }

        [Test]
        public void ReconnectGivesUpAfterMaxAttempts()
        {
            Make(s => ReconnectSettings(s, 3));
            server.ConnectFailure = i => i >= 1 ? new Exception("refused") : null;
            string reason = null;
            bool? kicked = null;
            var delays = new List<TimeSpan>();
            conn.ReconnectScheduled += (n, d) => delays.Add(d);
            conn.Disconnected += (r, k) =>
            {
                reason = r;
                kicked = k;
            };
            Connect();
            server.Last.ServerCloses();
            Wait.AdvanceUntil(clock, () => reason != null, conn, stepMs: 100);

            Assert.That(server.Connections.Count, Is.EqualTo(4));
            Assert.That(delays.Count, Is.EqualTo(3));
            Assert.That(reason, Does.Contain("3 attempts"));
            Assert.That(kicked, Is.False);
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Disconnected));
            clock.Advance(TimeSpan.FromMinutes(5));
            Thread.Sleep(50);
            Assert.That(server.Connections.Count, Is.EqualTo(4));
        }

        [Test]
        public void ReconnectStopsOnAuthenticationRefusal()
        {
            Make(s => ReconnectSettings(s, 5));
            server.OnHello = (t, m) =>
            {
                if (t.Index == 0)
                {
                    t.ServerSends(new ServerMessage { ReplyTo = m.Seq, Welcome = FakeServer.MakeWelcome("tok") });
                    return;
                }
                t.ServerSends(new ServerMessage { ReplyTo = m.Seq, Error = new Error { Code = ErrorCode.InvalidToken, Message = "unknown token" } });
                t.ServerCloses();
            };
            string reason = null;
            conn.Disconnected += (r, k) => reason = r;
            Connect();
            server.Last.ServerCloses();
            Wait.AdvanceUntil(clock, () => reason != null, conn, stepMs: 100);
            Assert.That(reason, Does.Contain("unknown token"));
            Assert.That(server.Connections.Count, Is.EqualTo(2));
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Disconnected));
        }

        [Test]
        public void NoReconnectAfterKick()
        {
            Make(s => ReconnectSettings(s, 5));
            string reason = null;
            bool wasKicked = false;
            int kickedEvents = 0;
            conn.Kicked += (k, r) => kickedEvents++;
            conn.Disconnected += (r, k) =>
            {
                reason = r;
                wasKicked = k;
            };
            Connect();
            server.Last.ServerSends(new ServerMessage { Kicked = new Kicked { Reason = "be nice" } });
            server.Last.ServerCloses();
            Wait.Until(() => reason != null, conn);
            Assert.That(reason, Is.EqualTo("be nice"));
            Assert.That(wasKicked, Is.True);
            Assert.That(kickedEvents, Is.EqualTo(1));
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Disconnected));
            clock.Advance(TimeSpan.FromMinutes(5));
            Thread.Sleep(50);
            Assert.That(server.Connections.Count, Is.EqualTo(1));
        }

        [Test]
        public void CloseEndsSessionWithoutReconnectAndNeverThrows()
        {
            Make(s => ReconnectSettings(s, 5));
            string reason = null;
            conn.Disconnected += (r, k) => reason = r;
            Connect();
            var pending = conn.RequestAsync(new ClientMessage { Observe = new Observe() });
            Assert.DoesNotThrow(() => conn.Close());
            Assert.DoesNotThrow(() => conn.Close());
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Disconnected));
            Assert.That(Wait.Failure(pending), Is.TypeOf<TinyverseConnectionException>());
            Wait.Until(() => reason != null, conn);
            Wait.Until(() => server.Last.CloseRequested, what: "graceful close");
            Wait.Until(() => server.Last.Disposed, what: "transport disposed");
            Assert.That(conn.SendChat(ChatChannel.World, "x"), Is.EqualTo(0u));
            clock.Advance(TimeSpan.FromMinutes(5));
            Thread.Sleep(50);
            Assert.That(server.Connections.Count, Is.EqualTo(1));
            Assert.DoesNotThrow(() => conn.Dispose());
            Assert.DoesNotThrow(() => conn.Dispose());
        }

        [Test]
        public void CanConnectAgainAfterClose()
        {
            Make();
            Connect();
            conn.Close();
            var w = Connect();
            Assert.That(w, Is.Not.Null);
            Assert.That(server.Connections.Count, Is.EqualTo(2));
        }
    }
}
