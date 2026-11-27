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

        /// <summary>
        /// This machine's IPv4 addresses for people on the same network, best first (ordering: <see cref="OrderLan"/>).
        /// Only this reading part touches the OS.
        /// </summary>
        public static List<string> LanIPv4()
        {
            IPAddress viaDefault = DefaultRouteSource();
            var found = new List<InterfaceAddress>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                        found.Add(new InterfaceAddress(nic.Name, nic.NetworkInterfaceType, nic.OperationalStatus,
                            u.Address, viaDefault != null && u.Address.Equals(viaDefault)));
                }
            }
            catch (Exception)
            {
                // Some Mono builds throw on exotic interfaces; fall back to DNS below.
            }

            if (found.Count == 0)
            {
                try
                {
                    foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
                        found.Add(new InterfaceAddress("", NetworkInterfaceType.Unknown, OperationalStatus.Up, a,
                            viaDefault != null && a.Equals(viaDefault)));
                }
                catch (Exception)
                {
                    // No addresses known.
                }
            }
            return OrderLan(found);
        }

        /// <summary>
        /// The local address the kernel would use to reach the internet, i.e. that of the default-route interface.
        /// Connecting a UDP socket sends nothing; 192.0.2.1 is TEST-NET-1 and never routed. Null without a route.
        /// </summary>
        public static IPAddress DefaultRouteSource()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
                    var local = s.LocalEndPoint as IPEndPoint;
                    if (local == null || local.Address.Equals(IPAddress.Any) || IPAddress.IsLoopback(local.Address)) return null;
                    return local.Address;
                }
            }
            catch (Exception)
            {
                return null; // no route (offline): no preference
            }
        }

        private static readonly string[] VirtualPrefixes =
        {
            "docker", "br-", "veth", "virbr", "tun", "tap", "wg", "tailscale", "zt", "vmnet", "lo",
        };

        /// <summary>
        /// Orders candidate addresses best first, dropping non-IPv4, loopback and 169.254/16, each address once.
        /// Order: the default-route interface; then real, up interfaces before virtual ones (docker, br-, veth,
        /// virbr, tun, tap, wg, tailscale, zt, vmnet, lo, or loopback/tunnel type) and ones not up; within each
        /// group 192.168/16, then 10/8, then 172.16/12, then anything else; ties keep the input order.
        /// </summary>
        public static List<string> OrderLan(IEnumerable<InterfaceAddress> candidates)
        {
            var keyed = new List<KeyValuePair<long, string>>();
            int index = 0;
            foreach (InterfaceAddress c in candidates)
            {
                index++;
                IPAddress a = c.Address;
                if (a == null || a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                byte[] b = a.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                long route = c.HasDefaultRoute ? 0 : 1;
                long virt = IsVirtual(c) ? 1 : 0;
                long range = b[0] == 192 && b[1] == 168 ? 0
                    : b[0] == 10 ? 1
                    : b[0] == 172 && b[1] >= 16 && b[1] <= 31 ? 2
                    : 3;
                long key = (route << 40) | (virt << 36) | (range << 32) | (uint)index;
                keyed.Add(new KeyValuePair<long, string>(key, a.ToString()));
            }
            keyed.Sort((x, y) => x.Key.CompareTo(y.Key));
            var seen = new HashSet<string>();
            var result = new List<string>();
            foreach (KeyValuePair<long, string> kv in keyed)
                if (seen.Add(kv.Value)) result.Add(kv.Value);
            return result;
        }

        private static bool IsVirtual(InterfaceAddress c)
        {
            if (c.Status != OperationalStatus.Up && c.Status != OperationalStatus.Unknown) return true;
            if (c.Type == NetworkInterfaceType.Loopback || c.Type == NetworkInterfaceType.Tunnel) return true;
            string name = c.Name ?? "";
            foreach (string p in VirtualPrefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>One address of one interface: the input of <see cref="Addresses.OrderLan"/>.</summary>
    internal readonly struct InterfaceAddress
    {
        public InterfaceAddress(string name, NetworkInterfaceType type, OperationalStatus status, IPAddress address,
            bool hasDefaultRoute)
        {
            Name = name;
            Type = type;
            Status = status;
            Address = address;
            HasDefaultRoute = hasDefaultRoute;
        }

        public string Name { get; }
        public NetworkInterfaceType Type { get; }
        public OperationalStatus Status { get; }
        public IPAddress Address { get; }
        public bool HasDefaultRoute { get; }

        public override string ToString() => $"{Name} {Address}{(HasDefaultRoute ? " (default route)" : "")}";
    }
}
