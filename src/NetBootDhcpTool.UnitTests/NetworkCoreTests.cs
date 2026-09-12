using System.Net;
using System.Net.Sockets;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Dhcp;
using NetBootDhcpTool.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class IpNetworkTests
{
    [TestMethod]
    public void HostsReturnsUsableAddressesOnly()
    {
        var hosts = IpNetwork.Hosts(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0"));
        Assert.AreEqual(254, hosts.Count);
        Assert.AreEqual("192.168.1.1", hosts[0].ToString());
        Assert.AreEqual("192.168.1.254", hosts[^1].ToString());
    }

    [TestMethod]
    public void SameSubnetAndBroadcastAreCalculatedFromMask()
    {
        var mask = IPAddress.Parse("255.255.255.0");
        Assert.IsTrue(IpNetwork.SameSubnet(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.1.200"), mask));
        Assert.IsFalse(IpNetwork.SameSubnet(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.2.1"), mask));
        Assert.AreEqual("192.168.100.255", IpNetwork.BroadcastAddress(IPAddress.Parse("192.168.100.1"), mask).ToString());
    }

    [TestMethod]
    [DataRow("192.168.10.42/24", "192.168.10.0/24")]
    [DataRow("192.168.10.42", "192.168.10.42/32")]
    [DataRow("2001:db8:10::42/64", "2001:db8:10::/64")]
    [DataRow("2001:db8::42", "2001:db8::42/128")]
    public void ParseCidrCanonicalizesAddress(string input, string expected) =>
        Assert.AreEqual(expected, IpNetwork.ParseCidr(input));

    [TestMethod]
    public void ParseCidrRejectsInvalidPrefix()
    {
        Assert.Throws<FormatException>(() => IpNetwork.ParseCidr("192.168.1.1/33"));
        Assert.Throws<FormatException>(() => IpNetwork.ParseCidr("not-an-address"));
    }
}

[TestClass]
public sealed class StaticRouteValidatorTests
{
    [TestMethod]
    public void NormalizesIpv4DirectRoute()
    {
        var route = StaticRouteValidator.Normalize(new StaticRouteRule
        {
            DestinationPrefix = "192.168.10.42/24",
            RouteMetric = 10
        });

        Assert.AreEqual("192.168.10.0/24", route.DestinationPrefix);
        Assert.AreEqual("0.0.0.0", route.NextHop);
        Assert.AreEqual(AddressFamily.InterNetwork, route.AddressFamily);
        Assert.IsFalse(route.IsDefaultRoute);
    }

    [TestMethod]
    public void NormalizesIpv6GatewayRoute()
    {
        var route = StaticRouteValidator.Normalize(new StaticRouteRule
        {
            DestinationPrefix = "2001:db8:10::42/64",
            NextHop = "2001:db8:10::1",
            RouteMetric = 20
        });

        Assert.AreEqual("2001:db8:10::/64", route.DestinationPrefix);
        Assert.AreEqual("2001:db8:10::1", route.NextHop);
        Assert.AreEqual(AddressFamily.InterNetworkV6, route.AddressFamily);
    }

    [TestMethod]
    public void RejectsDefaultRoutesInvalidMetricsAndMismatchedNextHop()
    {
        Assert.Throws<FormatException>(() => Normalize("0.0.0.0/0", "192.168.1.1", 5));
        Assert.Throws<FormatException>(() => Normalize("::/0", "2001:db8::1", 5));
        Assert.Throws<FormatException>(() => Normalize("10.0.0.0/8", "::1", 10));
        Assert.Throws<FormatException>(() => Normalize("10.0.0.0/8", "", 0));
    }

    private static ValidatedStaticRoute Normalize(string destination, string gateway, int metric) =>
        StaticRouteValidator.Normalize(new StaticRouteRule
        {
            DestinationPrefix = destination,
            NextHop = gateway,
            RouteMetric = metric
        });
}

[TestClass]
public sealed class DhcpLeaseManagerTests
{
    [TestMethod]
    public void AllocationIsStableForClientMac()
    {
        var manager = new DhcpLeaseManager(CreateSettings());
        var first = manager.Allocate("AA-BB-CC-DD-EE-FF", "device");
        var second = manager.Allocate("AA-BB-CC-DD-EE-FF", "renamed-device");

        Assert.AreEqual(first.IpAddress, second.IpAddress);
        Assert.AreEqual("renamed-device", second.Hostname);
        Assert.AreEqual(1, manager.Leases.Count);
    }

    [TestMethod]
    public void ExhaustedPoolReportsAnActionableFailure()
    {
        var manager = new DhcpLeaseManager(CreateSettings("192.168.50.2", "192.168.50.3"));
        manager.Allocate("00-00-00-00-00-01", "one");
        manager.Allocate("00-00-00-00-00-02", "two");

        Assert.Throws<InvalidOperationException>(() => manager.Allocate("00-00-00-00-00-03", "three"));
    }

    private static DhcpServerSettings CreateSettings(string start = "192.168.50.100", string end = "192.168.50.101") => new()
    {
        ServerIp = IPAddress.Parse("192.168.50.1"),
        SubnetMask = IPAddress.Parse("255.255.255.0"),
        PoolStart = IPAddress.Parse(start),
        PoolEnd = IPAddress.Parse(end),
        LeaseSeconds = 60
    };
}

[TestClass]
public sealed class JsonStoreTests
{
    [TestMethod]
    public void SaveUsesBackupAndLoadsCurrentSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "netboot-json-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            JsonStore.Save(path, new AppSettings { Language = "en-US" });
            JsonStore.Save(path, new AppSettings { Language = "zh-CN", WindowWidth = 1120 });

            var loaded = JsonStore.LoadOrDefault(path, new AppSettings());
            Assert.AreEqual("zh-CN", loaded.Language);
            Assert.AreEqual(1120, loaded.WindowWidth);
            Assert.IsTrue(File.Exists(path + ".bak"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
