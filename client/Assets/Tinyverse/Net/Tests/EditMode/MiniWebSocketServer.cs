using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tinyverse.Net.Tests
{
    /// <summary>
    /// Just enough of RFC 6455 to test a real client: the opening handshake, (fragmented) binary
    /// messages in, unfragmented binary messages out, ping and close. Each message is handed to
    /// <c>onMessage</c>, which returns the messages to send back.
    /// </summary>
    public sealed class MiniWebSocketServer : IDisposable
    {
        const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        readonly TcpListener listener;
        readonly Func<byte[], IEnumerable<byte[]>> onMessage;
        readonly List<TcpClient> clients = new List<TcpClient>();
        volatile bool stopped;

        public string RequestedPath { get; private set; }
        public int ClosesReceived;

        public MiniWebSocketServer(Func<byte[], IEnumerable<byte[]>> onMessage)
        {
            this.onMessage = onMessage;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Uri Url => new Uri($"ws://127.0.0.1:{Port}/ws");

        async Task AcceptLoop()
        {
            while (!stopped)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
                lock (clients) clients.Add(client);
                var thread = new Thread(() => Serve(client)) { IsBackground = true };
                thread.Start();
            }
        }

        void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    Handshake(stream);
                    var message = new MemoryStream();
                    while (!stopped)
                    {
                        var (fin, opcode, payload) = ReadFrame(stream);
                        switch (opcode)
                        {
                            case 0x0: // continuation
                            case 0x1:
                            case 0x2:
                                message.Write(payload, 0, payload.Length);
                                if (!fin) break;
                                var data = message.ToArray();
                                message.SetLength(0);
                                foreach (var reply in onMessage(data)) WriteFrame(stream, 0x2, reply);
                                break;
                            case 0x8:
                                Interlocked.Increment(ref ClosesReceived);
                                WriteFrame(stream, 0x8, payload.Length >= 2 ? new[] { payload[0], payload[1] } : new byte[0]);
                                return;
                            case 0x9:
                                WriteFrame(stream, 0xA, payload);
                                break;
                        }
                    }
                }
            }
            catch
            {
                // Client went away.
            }
        }

        void Handshake(NetworkStream stream)
        {
            var header = new StringBuilder();
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                int b = stream.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                header.Append((char)b);
            }
            string key = null;
            var lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            RequestedPath = lines[0].Split(' ')[1];
            foreach (var line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                    key = line.Substring(colon + 1).Trim();
            }
            string accept;
            using (var sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + Magic)));
            var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(response);
            stream.Write(bytes, 0, bytes.Length);
        }

        static (bool fin, int opcode, byte[] payload) ReadFrame(Stream s)
        {
            var head = ReadExactly(s, 2);
            bool fin = (head[0] & 0x80) != 0;
            int opcode = head[0] & 0x0F;
            bool masked = (head[1] & 0x80) != 0;
            long len = head[1] & 0x7F;
            if (len == 126)
            {
                var ext = ReadExactly(s, 2);
                len = (ext[0] << 8) | ext[1];
            }
            else if (len == 127)
            {
                var ext = ReadExactly(s, 8);
                len = 0;
                for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
            }
            byte[] mask = masked ? ReadExactly(s, 4) : null;
            var payload = ReadExactly(s, (int)len);
            if (mask != null)
                for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
            return (fin, opcode, payload);
        }

        static void WriteFrame(Stream s, int opcode, byte[] payload)
        {
            var head = new List<byte> { (byte)(0x80 | opcode) };
            if (payload.Length < 126)
            {
                head.Add((byte)payload.Length);
            }
            else if (payload.Length <= 0xFFFF)
            {
                head.Add(126);
                head.Add((byte)(payload.Length >> 8));
                head.Add((byte)payload.Length);
            }
            else
            {
                head.Add(127);
                long len = payload.Length;
                for (int i = 7; i >= 0; i--) head.Add((byte)(len >> (8 * i)));
            }
            lock (s)
            {
                s.Write(head.ToArray(), 0, head.Count);
                s.Write(payload, 0, payload.Length);
                s.Flush();
            }
        }

        static byte[] ReadExactly(Stream s, int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
            }
            return buf;
        }

        public void Dispose()
        {
            stopped = true;
            try { listener.Stop(); } catch { }
            lock (clients)
                foreach (var c in clients)
                    try { c.Close(); } catch { }
        }
    }
}
