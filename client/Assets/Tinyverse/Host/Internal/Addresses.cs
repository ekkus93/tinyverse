using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Tinyverse.Host.Internal
{
    internal static class ReadyLine
    {
        private static readonly Regex Pattern = new Regex(
            @"listening on (?<scheme>wss?)://(?<host>\[[^\]]*\]|[^\s:/]+):(?<port>\d{1,5})/ws",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool TryParse(string line, out bool tls, out string host, out int port)
        {
            tls = false;
            host = null;
            port = 0;
            if (line == null) return false;
            Match m = Pattern.Match(line);
            if (!m.Success) return false;
            if (!int.TryParse(m.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port < 1 || port > 65535)
                return false;
            host = m.Groups["host"].Value;
            tls = m.Groups["scheme"].Value == "wss";
            return true;
        }
    }

    internal static class Addresses
    {
        public static string Url(bool tls, string host, int port)
        {
            if (host.IndexOf(':') >= 0 && !host.StartsWith("[", StringComparison.Ordinal)) host = "[" + host + "]";
            return (tls ? "wss://" : "ws://") + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/ws";
        }

        /// <summary>True for 0.0.0.0, ::, [::] or empty.</summary>
        public static bool IsWildcard(string bind)
        {
            if (string.IsNullOrWhiteSpace(bind)) return true;
            string b = bind.Trim().Trim('[', ']');
            return IPAddress.TryParse(b, out IPAddress ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any));
        }

        public static bool IsLoopback(string bind)
        {
            string b = (bind ?? "").Trim().Trim('[', ']');
            if (string.Equals(b, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            return IPAddress.TryParse(b, out IPAddress ip) && IPAddress.IsLoopback(ip);
        }

        /// <summary>This machine's up, non-loopback, non-link-local IPv4 addresses, sorted, without duplicates.</summary>
        public static List<string> LanIPv4()
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus == OperationalStatus.Down) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                        Consider(u.Address, result);
                }
            }
            catch (Exception)
            {
                // Some Mono builds throw on exotic interfaces; fall back to DNS.
            }

            if (result.Count == 0)
            {
                try
                {
                    foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
                        Consider(a, result);
                }
                catch (Exception)
                {
                    // No addresses known.
                }
            }
            return new List<string>(result);
        }

        private static void Consider(IPAddress a, ISet<string> into)
        {
            if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) return;
            byte[] b = a.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return; // link-local
            into.Add(a.ToString());
        }
    }
}
