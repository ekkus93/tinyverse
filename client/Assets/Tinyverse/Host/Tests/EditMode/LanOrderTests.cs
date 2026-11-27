using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using NUnit.Framework;
using Tinyverse.Host.Internal;

namespace Tinyverse.Host.Tests
{
    public class LanOrderTests
    {
        private static InterfaceAddress A(string name, string ip, bool defaultRoute = false,
            OperationalStatus status = OperationalStatus.Up, NetworkInterfaceType type = NetworkInterfaceType.Ethernet) =>
            new InterfaceAddress(name, type, status, IPAddress.Parse(ip), defaultRoute);

        /// <summary>The development machine the bug was found on: USB Ethernet, Wi-Fi with the default route, Docker.</summary>
        private static List<InterfaceAddress> DevMachine(bool withDefaultRoute) => new List<InterfaceAddress>
        {
            A("lo", "127.0.0.1", type: NetworkInterfaceType.Loopback),
            A("enx8cae4cf966cb", "10.77.77.1"),
            A("wlo1", "192.168.87.210", withDefaultRoute, type: NetworkInterfaceType.Wireless80211),
            A("br-dedcc306f02d", "172.23.0.1"),
            A("br-e3620193105e", "172.20.0.1"),
            A("docker0", "172.17.0.1", status: OperationalStatus.Down),
            A("br-c0ecf5a26c26", "172.18.0.1"),
            A("wlo1", "fe80::1"),
        };

        [Test]
        public void DevMachine_PutsTheWifiAddressFirst()
        {
            List<string> order = Addresses.OrderLan(DevMachine(true));
            Assert.That(order, Is.EqualTo(new[]
            {
                "192.168.87.210", "10.77.77.1", "172.23.0.1", "172.20.0.1", "172.17.0.1", "172.18.0.1",
            }));
        }

        [Test]
        public void DevMachine_WithoutRouteInformation_StillPrefersTheHomeRange()
        {
            Assert.That(Addresses.OrderLan(DevMachine(false))[0], Is.EqualTo("192.168.87.210"));
        }

        [Test]
        public void DefaultRoute_BeatsTheRangePreference()
        {
            var list = new List<InterfaceAddress> { A("eth0", "192.168.1.5"), A("eth1", "10.0.0.7", true) };
            Assert.That(Addresses.OrderLan(list), Is.EqualTo(new[] { "10.0.0.7", "192.168.1.5" }));
        }

        [Test]
        public void SingleInterface()
        {
            Assert.That(Addresses.OrderLan(new[] { A("eth0", "192.168.0.20", true) }), Is.EqualTo(new[] { "192.168.0.20" }));
        }

        [Test]
        public void VpnOnly_IsStillListed()
        {
            var list = new List<InterfaceAddress>
            {
                A("lo", "127.0.0.1", type: NetworkInterfaceType.Loopback),
                A("wg0", "10.8.0.2", type: NetworkInterfaceType.Unknown),
                A("tun0", "10.9.0.2", type: NetworkInterfaceType.Tunnel),
            };
            Assert.That(Addresses.OrderLan(list), Is.EqualTo(new[] { "10.8.0.2", "10.9.0.2" }));
        }

        [Test]
        public void VirtualAndDownInterfaces_GoLast_AndPublicAfterPrivate()
        {
            var list = new List<InterfaceAddress>
            {
                A("tailscale0", "100.101.1.2"),
                A("eth9", "192.168.5.5", status: OperationalStatus.Down),
                A("eth0", "203.0.113.9"),
                A("eth1", "172.16.4.4"),
                A("virbr0", "192.168.122.1"),
                A("eth2", "10.1.1.1"),
            };
            Assert.That(Addresses.OrderLan(list), Is.EqualTo(new[]
            {
                "10.1.1.1", "172.16.4.4", "203.0.113.9", "192.168.5.5", "192.168.122.1", "100.101.1.2",
            }));
        }

        [Test]
        public void NoNetwork_IsEmpty()
        {
            Assert.That(Addresses.OrderLan(new InterfaceAddress[0]), Is.Empty);
            Assert.That(Addresses.OrderLan(new[]
            {
                A("lo", "127.0.0.1", type: NetworkInterfaceType.Loopback),
                A("eth0", "169.254.3.4"),
                A("eth0", "::1"),
            }), Is.Empty);
        }

        [Test]
        public void Duplicates_KeepTheirBestPlace()
        {
            var list = new List<InterfaceAddress> { A("docker0", "192.168.1.9"), A("eth0", "10.0.0.1"), A("eth0", "192.168.1.9", true) };
            Assert.That(Addresses.OrderLan(list), Is.EqualTo(new[] { "192.168.1.9", "10.0.0.1" }));
        }

        [Test]
        public void ThisMachine_DefaultRouteAddressComesFirst()
        {
            List<string> order = Addresses.LanIPv4();
            IPAddress viaDefault = Addresses.DefaultRouteSource();
            TestContext.Progress.WriteLine("LanIPv4 on this machine: " + string.Join(", ", order) +
                                           " (default route source: " + (viaDefault?.ToString() ?? "none") + ")");
            if (viaDefault != null) Assert.That(order[0], Is.EqualTo(viaDefault.ToString()));
        }
    }
}
