using System;
using System.Net;
using System.Net.Sockets;

namespace Tinyverse.Host
{
    /// <summary>Checks whether a TCP port can be listened on right now, so the UI can warn before starting.</summary>
    public static class PortCheck
    {
        /// <summary>True if <paramref name="port"/> can be bound on <paramref name="bindAddress"/> at this moment.</summary>
        public static bool IsAvailable(int port, string bindAddress = HostSettings.DefaultBindAddress) =>
            Check(port, bindAddress) == null;

        /// <summary>Null if the port can be bound; otherwise a sentence for the player.</summary>
        public static string Check(int port, string bindAddress = HostSettings.DefaultBindAddress)
        {
            if (port < 1 || port > 65535) return $"The port must be between 1 and 65535 (got {port}).";
            string b = string.IsNullOrWhiteSpace(bindAddress) ? HostSettings.DefaultBindAddress : bindAddress.Trim().Trim('[', ']');
            IPAddress address;
            if (string.Equals(b, "localhost", StringComparison.OrdinalIgnoreCase)) address = IPAddress.Loopback;
            else if (!IPAddress.TryParse(b, out address)) return $"\"{bindAddress}\" is not an IP address.";

            Socket socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                // The server (tokio/std on Unix) sets SO_REUSEADDR, so a port in TIME_WAIT is fine for it too.
                // On Linux this still fails if anything is listening on the port.
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(address, port));
                socket.Listen(1);
                return null;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return $"Port {port} is already in use by another program.";
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AccessDenied)
            {
                return $"Port {port} needs special permission; choose a port above 1023.";
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressNotAvailable)
            {
                return $"{b} is not an address of this computer.";
            }
            catch (Exception e)
            {
                return $"Port {port} cannot be used: {e.Message}";
            }
            finally
            {
                socket?.Close();
            }
        }
    }
}
