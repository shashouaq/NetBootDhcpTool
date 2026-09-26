using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Dhcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class DhcpInterfaceScopeTests
{
    [TestMethod]
    public async Task ProductionServerRequiresStableSelectedInterface()
    {
        using var server = new DhcpServer(NullLogger.Instance, listenPort: 0);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => server.StartAsync(new DhcpServerSettings()));

        Assert.IsFalse(server.IsRunning);
    }

    [TestMethod]
    public async Task ServerRefusesReusedOrMismatchedInterfaceIndexBeforeBinding()
    {
        var loopback = NetworkInterface.GetAllNetworkInterfaces().Single(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var currentIndex = loopback.GetIPProperties().GetIPv4Properties()?.Index ??
            throw new AssertFailedException("Loopback IPv4 index unavailable.");
        using var server = new DhcpServer(NullLogger.Instance, listenPort: 0);
        var settings = new DhcpServerSettings
        {
            AdapterId = loopback.Id,
            InterfaceIndex = currentIndex + 100,
            ServerIp = IPAddress.Loopback
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => server.StartAsync(settings));

        Assert.IsFalse(server.IsRunning);
    }

    [TestMethod]
    public async Task ServerRefusesUnknownAdapterIdentityBeforeBinding()
    {
        using var server = new DhcpServer(NullLogger.Instance, listenPort: 0);
        var settings = new DhcpServerSettings
        {
            AdapterId = "missing-adapter-id",
            InterfaceIndex = 12,
            ServerIp = IPAddress.Parse("192.0.2.1")
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => server.StartAsync(settings));

        Assert.IsFalse(server.IsRunning);
    }

    [TestMethod]
    public async Task ServerRefusesAddressNotAssignedToSelectedAdapterBeforeBinding()
    {
        var loopback = NetworkInterface.GetAllNetworkInterfaces().Single(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var currentIndex = loopback.GetIPProperties().GetIPv4Properties()?.Index ??
            throw new AssertFailedException("Loopback IPv4 index unavailable.");
        using var server = new DhcpServer(NullLogger.Instance, listenPort: 0);
        var settings = new DhcpServerSettings
        {
            AdapterId = loopback.Id,
            InterfaceIndex = currentIndex,
            ServerIp = IPAddress.Parse("192.0.2.1")
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => server.StartAsync(settings));

        Assert.IsFalse(server.IsRunning);
    }

    [TestMethod]
    public void SocketScopeEnablesOnlySelectedIngressAndEgressInterface()
    {
        var loopback = NetworkInterface.GetAllNetworkInterfaces().Single(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var interfaceIndex = loopback.GetIPProperties().GetIPv4Properties()?.Index ??
            throw new AssertFailedException("Loopback IPv4 index unavailable.");
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        DhcpServer.ConfigureInterfaceScope(socket, interfaceIndex);

        var interfaceList = socket.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)33, 64);
        Assert.IsTrue(interfaceList.Length >= sizeof(int));
        var configuredInterfaces = interfaceList
            .Chunk(sizeof(int))
            .Select(bytes => BitConverter.ToInt32(bytes))
            .Where(index => index != 0)
            .ToArray();
        CollectionAssert.AreEqual(new[] { interfaceIndex }, configuredInterfaces, "IP_IFLIST contains only the selected interface");
        var outgoingInterface = (int)socket.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31)!;
        Assert.AreEqual(interfaceIndex, outgoingInterface, "IP_UNICAST_IF selects the same interface for all outgoing IPv4 traffic");
    }

    [TestMethod]
    public async Task StopReleasesExclusivePortForTheNextSession()
    {
        var loopback = NetworkInterface.GetAllNetworkInterfaces().Single(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var interfaceIndex = loopback.GetIPProperties().GetIPv4Properties()?.Index ??
            throw new AssertFailedException("Loopback IPv4 index unavailable.");
        var settings = new DhcpServerSettings
        {
            AdapterId = loopback.Id,
            InterfaceIndex = interfaceIndex,
            ServerIp = IPAddress.Loopback,
            SubnetMask = IPAddress.Parse("255.0.0.0"),
            PoolStart = IPAddress.Parse("127.0.0.100"),
            PoolEnd = IPAddress.Parse("127.0.0.101")
        };
        using var first = new DhcpServer(NullLogger.Instance, listenPort: 0);
        await first.StartAsync(settings);
        var releasedPort = first.ListenPort;
        Assert.AreEqual(IPAddress.Loopback, first.BoundEndpoint?.Address, "the server binds only the selected server address");

        first.Stop();

        using var second = new DhcpServer(NullLogger.Instance, listenPort: releasedPort);
        await second.StartAsync(settings);

        Assert.IsTrue(second.IsRunning, "a new session can bind the released exclusive port");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LeaseJournalFailureStopsServerWithoutSendingAck(bool throwNonIoException)
    {
        var loopback = NetworkInterface.GetAllNetworkInterfaces().Single(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var interfaceIndex = loopback.GetIPProperties().GetIPv4Properties()?.Index ??
            throw new AssertFailedException("Loopback IPv4 index unavailable.");
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        using var server = new DhcpServer(NullLogger.Instance, listenPort: 0, clientPort: clientPort, replyAddress: IPAddress.Loopback);
        var settings = new DhcpServerSettings
        {
            AdapterId = loopback.Id,
            InterfaceIndex = interfaceIndex,
            ServerIp = IPAddress.Loopback,
            SubnetMask = IPAddress.Parse("255.0.0.0"),
            PoolStart = IPAddress.Parse("127.0.0.100"),
            PoolEnd = IPAddress.Parse("127.0.0.101")
        };
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.StoppedUnexpectedly += reason => stopped.TrySetResult(reason);
        await server.StartAsync(settings, persistLeaseTable: _ =>
        {
            if (throwNonIoException) throw new InvalidOperationException("injected unexpected persistence failure");
            throw new IOException("injected lease journal write failure");
        });

        await client.SendAsync(BuildDhcpRequest(DhcpMessageType.Discover), new IPEndPoint(IPAddress.Loopback, server.ListenPort));
        var offer = DhcpPacketParser.Parse((await ReceiveWithTimeoutAsync(client)).Buffer);
        Assert.AreEqual(DhcpMessageType.Offer, offer.MessageType);
        await client.SendAsync(BuildDhcpRequest(DhcpMessageType.Request, offer.YiAddr, settings.ServerIp), new IPEndPoint(IPAddress.Loopback, server.ListenPort));

        var stopReason = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(stopReason.Contains("journal persistence failed", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(server.IsRunning, "the service stops when a committed lease cannot be persisted");
        Assert.IsFalse(await HasDhcpPacketAsync(client, 200), "an unpersisted lease never receives an ACK");
    }

    [TestMethod]
    public async Task LeaseExpiryJournalFailureStopsServer()
    {
        var loopback = NetworkInterface.GetAllNetworkInterfaces().Single(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        var interfaceIndex = loopback.GetIPProperties().GetIPv4Properties()?.Index ??
            throw new AssertFailedException("Loopback IPv4 index unavailable.");
        var settings = new DhcpServerSettings
        {
            AdapterId = loopback.Id,
            InterfaceIndex = interfaceIndex,
            ServerIp = IPAddress.Loopback,
            SubnetMask = IPAddress.Parse("255.0.0.0"),
            PoolStart = IPAddress.Parse("127.0.0.100"),
            PoolEnd = IPAddress.Parse("127.0.0.101")
        };
        var now = DateTimeOffset.UtcNow;
        var binding = new DhcpLeaseBinding
        {
            ClientKey = "MAC:02-11-22-33-44-55",
            MacAddress = "02-11-22-33-44-55",
            IpAddress = "127.0.0.100",
            LeaseStart = now.AddMinutes(-1),
            LeaseEnd = now.AddMilliseconds(500)
        };
        using var server = new DhcpServer(NullLogger.Instance, listenPort: 0, leaseSweepInterval: TimeSpan.FromMilliseconds(20));
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.StoppedUnexpectedly += reason => stopped.TrySetResult(reason);
        await server.StartAsync(settings, restoredBindings: [binding], persistLeaseTable: _ => throw new InvalidOperationException("injected expiry persistence failure"));

        var stopReason = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(stopReason.Contains("expiry sweep failed", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(server.IsRunning);
    }

    private static byte[] BuildDhcpRequest(DhcpMessageType type, IPAddress? requestedIp = null, IPAddress? serverIdentifier = null)
    {
        var packet = new byte[300];
        packet[0] = 1;
        packet[1] = 1;
        packet[2] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), 0x10203040);
        packet[10] = 0x80;
        new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 }.CopyTo(packet, 28);
        packet[236] = 99;
        packet[237] = 130;
        packet[238] = 83;
        packet[239] = 99;
        var offset = 240;
        packet[offset++] = 53;
        packet[offset++] = 1;
        packet[offset++] = (byte)type;
        if (requestedIp != null)
        {
            packet[offset++] = 50;
            packet[offset++] = 4;
            requestedIp.GetAddressBytes().CopyTo(packet, offset);
            offset += 4;
        }
        if (serverIdentifier != null)
        {
            packet[offset++] = 54;
            packet[offset++] = 4;
            serverIdentifier.GetAddressBytes().CopyTo(packet, offset);
            offset += 4;
        }
        packet[offset++] = 255;
        Array.Resize(ref packet, offset);
        return packet;
    }

    private static async Task<UdpReceiveResult> ReceiveWithTimeoutAsync(UdpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await client.ReceiveAsync(timeout.Token);
    }

    private static async Task<bool> HasDhcpPacketAsync(UdpClient client, int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try { _ = await client.ReceiveAsync(timeout.Token); return true; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return false; }
    }

    [TestMethod]
    public void PacketFilterAcceptsOnlyTheSelectedInterface()
    {
        Assert.IsTrue(DhcpServer.IsTargetInterface(12, 12));
        Assert.IsFalse(DhcpServer.IsTargetInterface(12, 13));
        Assert.IsFalse(DhcpServer.IsTargetInterface(0, 12));
        Assert.IsFalse(DhcpServer.IsTargetInterface(12, 0));
    }

    private sealed class NullLogger : ILogger
    {
        public static NullLogger Instance { get; } = new();
        public event Action<string>? LineWritten { add { } remove { } }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
