using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using NUnit.Framework;
using Tinyverse.Proto;

namespace Tinyverse.Net.Tests
{
    /// <summary>The real ClientWebSocket transport against an in-process TcpListener server.</summary>
    [Timeout(30000)]
    public class ClientWebSocketTransportTests
    {
        static T Run<T>(Task<T> task) => Wait.Result(task, null, 10000);
        static void Run(Task task) => Run(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

        [Test]
        public void EchoesLargeBinaryMessageAndCloses()
        {
            using (var server = new MiniWebSocketServer(data => new[] { data }))
            using (var transport = new ClientWebSocketTransport())
            {
                Run(transport.ConnectAsync(server.Url, CancellationToken.None));
                Assert.That(server.RequestedPath, Is.EqualTo("/ws"));

                var small = new byte[] { 1, 2, 3 };
                Run(transport.SendBinaryAsync(small, CancellationToken.None));
                var echo = Run(transport.ReceiveAsync(CancellationToken.None));
                Assert.That(echo.Type, Is.EqualTo(WebSocketFrameType.Binary));
                Assert.That(echo.Data, Is.EqualTo(small));

                var big = new byte[1536 * 1024];
                new System.Random(42).NextBytes(big);
                Run(transport.SendBinaryAsync(big, CancellationToken.None));
                var bigEcho = Run(transport.ReceiveAsync(CancellationToken.None));
                Assert.That(bigEcho.Data.Length, Is.EqualTo(big.Length));
                Assert.That(bigEcho.Data.SequenceEqual(big), Is.True);

                Run(transport.CloseAsync("bye", CancellationToken.None));
                var closed = Run(transport.ReceiveAsync(CancellationToken.None));
                Assert.That(closed.Type, Is.EqualTo(WebSocketFrameType.Close));
                Assert.That(server.ClosesReceived, Is.EqualTo(1));
            }
        }

        [Test]
        public void RejectsMessagesOverTheLimit()
        {
            using (var server = new MiniWebSocketServer(data => new[] { new byte[200 * 1024] }))
            using (var transport = new ClientWebSocketTransport(maxMessageBytes: 100 * 1024))
            {
                Run(transport.ConnectAsync(server.Url, CancellationToken.None));
                Run(transport.SendBinaryAsync(new byte[] { 1 }, CancellationToken.None));
                Assert.That(Wait.Failure(transport.ReceiveAsync(CancellationToken.None), null, 10000),
                    Is.InstanceOf<System.IO.InvalidDataException>());
            }
        }

        [Test]
        public void ConnectionHandshakesOverTheRealTransport()
        {
            IEnumerable<byte[]> Respond(byte[] data)
            {
                var m = ClientMessage.Parser.ParseFrom(data);
                switch (m.BodyCase)
                {
                    case ClientMessage.BodyOneofCase.Hello:
                        var w = FakeServer.MakeWelcome("real-token");
                        w.ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        yield return new ServerMessage { ReplyTo = m.Seq, Welcome = w }.ToByteArray();
                        yield return new ServerMessage { Snapshot = new Snapshot { Last = true } }.ToByteArray();
                        break;
                    case ClientMessage.BodyOneofCase.Ping:
                        yield return new ServerMessage
                        {
                            ReplyTo = m.Seq,
                            Pong = new Pong { ClientTimeMs = m.Ping.ClientTimeMs, ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                        }.ToByteArray();
                        break;
                    case ClientMessage.BodyOneofCase.Chat:
                        yield return new ServerMessage
                        {
                            ReplyTo = m.Seq,
                            ChatMessage = new ChatMessage { Entry = new ChatEntry { Text = m.Chat.Text } },
                        }.ToByteArray();
                        break;
                }
            }

            using (var server = new MiniWebSocketServer(Respond))
            using (var conn = new TinyverseConnection(new ConnectionSettings { MaxReconnectAttempts = 0 }))
            {
                var welcome = Run(conn.ConnectAsync(new ConnectOptions { Url = server.Url.ToString(), DisplayName = "Real" }));
                Assert.That(welcome.SessionToken, Is.EqualTo("real-token"));

                bool snapshotDone = false;
                string chat = null;
                conn.SnapshotComplete += s => snapshotDone = true;
                conn.ChatMessage += (c, r) => chat = c.Entry.Text;
                Wait.Until(() => snapshotDone, conn, 10000);

                var reply = Wait.Result(conn.RequestAsync(new ClientMessage { Chat = new Chat { Text = "over tcp" } }), conn, 10000);
                Assert.That(reply.ChatMessage.Entry.Text, Is.EqualTo("over tcp"));
                Wait.Until(() => chat == "over tcp", conn, 10000);
                Wait.Until(() => conn.RoundTripTimeMs >= 0, conn, 10000);

                conn.Close();
                Wait.Until(() => server.ClosesReceived == 1, null, 10000, "close frame at the server");
            }
        }
    }
}
