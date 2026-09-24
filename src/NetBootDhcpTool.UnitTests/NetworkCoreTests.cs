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
        var hosts = IpNetwork.Hosts(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0")).ToList();
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
    public void OfferIsNotCommittedAndSelectingRequestCommitsStableBinding()
    {
        var manager = new DhcpLeaseManager(CreateSettings());
        var first = manager.Process(Packet(DhcpMessageType.Discover));
        var repeated = manager.Process(Packet(DhcpMessageType.Discover));

        Assert.IsTrue(first.ResponseType == DhcpMessageType.Offer);
        Assert.AreEqual(first.Address, repeated.Address);
        Assert.AreEqual(0, manager.Leases.Count, "DHCPOFFER must not create a committed binding.");

        var ack = manager.Process(Packet(DhcpMessageType.Request, requestedIp: first.Address, serverId: CreateSettings().ServerIp));
        Assert.AreEqual(DhcpMessageType.Ack, ack.ResponseType);
        Assert.AreEqual(first.Address?.ToString(), manager.Leases.Single().IpAddress);
        Assert.AreEqual(1, manager.Leases.Count);

        var repeatedAck = manager.Process(Packet(DhcpMessageType.Request, requestedIp: first.Address, serverId: CreateSettings().ServerIp));
        Assert.AreEqual(DhcpMessageType.Ack, repeatedAck.ResponseType);
        Assert.AreEqual(1, manager.Leases.Count, "repeated selection for the same owner remains one committed binding");
    }

    [TestMethod]
    public void SelectingOtherServerIsIgnoredAndInvalidAddressIsNaked()
    {
        var settings = CreateSettings();
        var manager = new DhcpLeaseManager(settings);
        var offer = manager.Process(Packet(DhcpMessageType.Discover));
        var otherServer = manager.Process(Packet(DhcpMessageType.Request, requestedIp: offer.Address, serverId: IPAddress.Parse("192.168.50.9")));
        Assert.IsNull(otherServer.ResponseType);

        var invalid = manager.Process(Packet(DhcpMessageType.Request,
            requestedIp: IPAddress.Parse("192.168.50.200"), serverId: settings.ServerIp));
        Assert.AreEqual(DhcpMessageType.Nak, invalid.ResponseType);
        Assert.AreEqual(0, manager.Leases.Count);
    }

    [TestMethod]
    public void InitRebootAndRenewRequireTheOwnersBinding()
    {
        var settings = CreateSettings();
        var manager = new DhcpLeaseManager(settings);
        var offer = manager.Process(Packet(DhcpMessageType.Discover));
        var committed = manager.Process(Packet(DhcpMessageType.Request, requestedIp: offer.Address, serverId: settings.ServerIp));
        Assert.AreEqual(DhcpMessageType.Ack, committed.ResponseType);

        var reboot = manager.Process(Packet(DhcpMessageType.Request, requestedIp: offer.Address));
        Assert.AreEqual(DhcpMessageType.Ack, reboot.ResponseType);
        var wrongClient = manager.Process(Packet(DhcpMessageType.Request, requestedIp: offer.Address, mac: "02-11-22-33-44-66"));
        Assert.AreEqual(DhcpMessageType.Nak, wrongClient.ResponseType);

        var renewal = Packet(DhcpMessageType.Request);
        renewal.CiAddr = offer.Address!;
        var renewed = manager.Process(renewal);
        Assert.AreEqual(DhcpMessageType.Ack, renewed.ResponseType);
        Assert.AreEqual(offer.Address, renewed.Address);
    }

    [TestMethod]
    public void ReleaseAndDeclineAreOwnerCheckedAndConflictIsQuarantined()
    {
        var settings = CreateSettings();
        var manager = new DhcpLeaseManager(settings);
        var offer = manager.Process(Packet(DhcpMessageType.Discover));
        manager.Process(Packet(DhcpMessageType.Request, requestedIp: offer.Address, serverId: settings.ServerIp));
        var spoofedRelease = Packet(DhcpMessageType.Release, serverId: settings.ServerIp, mac: "02-11-22-33-44-66");
        spoofedRelease.CiAddr = offer.Address!;
        manager.Process(spoofedRelease);
        Assert.AreEqual(1, manager.Leases.Count);

        var spoofedDecline = Packet(DhcpMessageType.Decline, requestedIp: offer.Address, serverId: settings.ServerIp, mac: "02-11-22-33-44-66");
        manager.Process(spoofedDecline);
        Assert.AreEqual(1, manager.Leases.Count, "a client without the offer or binding cannot quarantine an owner's address");

        var decline = Packet(DhcpMessageType.Decline, requestedIp: offer.Address, serverId: settings.ServerIp);
        manager.Process(decline);
        Assert.AreEqual(0, manager.Leases.Count);
        var nextOffer = manager.Process(Packet(DhcpMessageType.Discover, mac: "02-11-22-33-44-66"));
        Assert.AreEqual("192.168.50.101", nextOffer.Address?.ToString());

        manager.Process(Packet(DhcpMessageType.Request, requestedIp: nextOffer.Address, serverId: settings.ServerIp, mac: "02-11-22-33-44-66"));
        var release = Packet(DhcpMessageType.Release, serverId: settings.ServerIp, mac: "02-11-22-33-44-66");
        release.CiAddr = nextOffer.Address!;
        manager.Process(release);
        Assert.AreEqual(0, manager.Leases.Count);
    }

    [TestMethod]
    public void ExpiredLeaseAddressCanBeReusedWithInjectedClock()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-23T00:00:00Z"));
        var settings = CreateSettings(leaseSeconds: 60);
        var manager = new DhcpLeaseManager(settings, timeProvider: time);
        var offer = manager.Process(Packet(DhcpMessageType.Discover));
        manager.Process(Packet(DhcpMessageType.Request, requestedIp: offer.Address, serverId: settings.ServerIp));
        time.Advance(TimeSpan.FromSeconds(61));
        var nextOffer = manager.Process(Packet(DhcpMessageType.Discover, mac: "02-11-22-33-44-66"));
        Assert.AreEqual(0, manager.Leases.Count);
        Assert.AreEqual("192.168.50.100", nextOffer.Address?.ToString());
    }

    [TestMethod]
    public void ExpiredPendingOfferCannotBeCommittedAndCanBeOfferedAgain()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-23T00:00:00Z"));
        var settings = CreateSettings();
        var manager = new DhcpLeaseManager(settings, timeProvider: time);
        var offer = manager.Process(Packet(DhcpMessageType.Discover));
        time.Advance(DhcpLeaseManager.OfferLifetime + TimeSpan.FromSeconds(1));

        var expiredRequest = manager.Process(Packet(DhcpMessageType.Request, requestedIp: offer.Address, serverId: settings.ServerIp));
        var freshOffer = manager.Process(Packet(DhcpMessageType.Discover));

        Assert.AreEqual(DhcpMessageType.Nak, expiredRequest.ResponseType);
        Assert.AreEqual(0, manager.Leases.Count, "an expired Offer cannot create a lease");
        Assert.AreEqual(offer.Address, freshOffer.Address, "the released pending address can be offered again");
    }

    [TestMethod]
    public void DeclinedAddressReturnsToPoolOnlyAfterInjectedHoldExpires()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-23T00:00:00Z"));
        var settings = CreateSettings(start: "192.168.50.100", end: "192.168.50.100");
        var manager = new DhcpLeaseManager(settings, timeProvider: time);
        var offer = manager.Process(Packet(DhcpMessageType.Discover));
        var decline = Packet(DhcpMessageType.Decline, requestedIp: offer.Address, serverId: settings.ServerIp);
        manager.Process(decline);

        var whileQuarantined = manager.Process(Packet(DhcpMessageType.Discover, mac: "02-11-22-33-44-66"));
        time.Advance(DhcpLeaseManager.DeclineHold + TimeSpan.FromSeconds(1));
        var afterHold = manager.Process(Packet(DhcpMessageType.Discover, mac: "02-11-22-33-44-66"));

        Assert.IsNull(whileQuarantined.ResponseType, "declined address is withheld during its conflict hold");
        Assert.AreEqual(offer.Address, afterHold.Address, "declined address returns to the pool after expiry");
    }

    [TestMethod]
    public void PersistenceFailurePreventsAckAndRestoredBindingIsReserved()
    {
        var settings = CreateSettings();
        var fail = true;
        var manager = new DhcpLeaseManager(settings, persist: _ => { if (fail) throw new IOException("injected journal failure"); });
        var offer = manager.Process(Packet(DhcpMessageType.Discover));
        Assert.Throws<IOException>(() => manager.Process(Packet(DhcpMessageType.Request, requestedIp: offer.Address, serverId: settings.ServerIp)));
        Assert.AreEqual(0, manager.Leases.Count);

        fail = false;
        var recoveredManager = new DhcpLeaseManager(settings, restoredBindings:
        [
            new DhcpLeaseBinding
            {
                ClientKey = "MAC:02-11-22-33-44-55",
                MacAddress = "02-11-22-33-44-55",
                IpAddress = "192.168.50.100",
                LeaseStart = DateTimeOffset.UtcNow,
                LeaseEnd = DateTimeOffset.UtcNow.AddMinutes(10)
            }
        ]);
        var existingOwner = recoveredManager.Process(Packet(DhcpMessageType.Discover));
        var otherOwner = recoveredManager.Process(Packet(DhcpMessageType.Discover, mac: "02-11-22-33-44-66"));
        Assert.AreEqual("192.168.50.100", existingOwner.Address?.ToString());
        Assert.AreEqual("192.168.50.101", otherOwner.Address?.ToString());
    }

    private static DhcpPacket Packet(DhcpMessageType type, IPAddress? requestedIp = null, IPAddress? serverId = null, string mac = "02-11-22-33-44-55") => new()
    {
        Op = 1,
        HType = 1,
        HLen = 6,
        ChAddr = Convert.FromHexString(mac.Replace("-", "" )).Concat(new byte[10]).ToArray(),
        MessageType = type,
        RequestedIp = requestedIp,
        ServerIdentifier = serverId,
        Hostname = "device"
    };

    private static DhcpServerSettings CreateSettings(string start = "192.168.50.100", string end = "192.168.50.101", int leaseSeconds = 60) => new()
    {
        ServerIp = IPAddress.Parse("192.168.50.1"),
        SubnetMask = IPAddress.Parse("255.255.255.0"),
        PoolStart = IPAddress.Parse(start),
        PoolEnd = IPAddress.Parse(end),
        LeaseSeconds = leaseSeconds
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
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
