using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Dhcp;
using NetBootDhcpTool.Network;

if (args.Contains("--adapters"))
{
    var paths = new AppPaths(AppContext.BaseDirectory);
    var logger = new FileLogger(paths);
    var adapters = new NetworkAdapterService(logger).GetAdapters();
    foreach (var adapter in adapters)
    {
        Console.WriteLine($"{adapter.Name}|{adapter.InterfaceIndex}|{adapter.IPv4Address}|{adapter.MacAddress}|wifi={adapter.IsWifi}|virtual={adapter.IsVirtual}|gateway={adapter.Gateway}");
    }
    Console.WriteLine($"COUNT={adapters.Count}");
    return;
}

var routeSmokeIndex = Array.IndexOf(args, "--route-smoke");
if (routeSmokeIndex >= 0)
{
    if (args.Length < routeSmokeIndex + 3) throw new ArgumentException("--route-smoke requires two interface indexes");
    await RunRouteSmokeAsync(args[routeSmokeIndex + 1], args[routeSmokeIndex + 2]);
    return;
}

var mask = IPAddress.Parse("255.255.255.0");
var hosts = IpNetwork.Hosts(IPAddress.Parse("192.168.1.10"), mask);
Assert(hosts.Count == 254, "host count");
Assert(IpNetwork.SameSubnet(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.1.200"), mask), "same subnet");
Assert(IpNetwork.BroadcastAddress(IPAddress.Parse("192.168.100.1"), mask).ToString() == "192.168.100.255", "broadcast address");
Assert(IpNetwork.ParseCidr("192.168.10.42/24") == "192.168.10.0/24", "cidr canonicalization");
Assert(IpNetwork.ParseCidr("0.0.0.0/0") == "0.0.0.0/0", "default route cidr");
var directRoute = StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "192.168.10.42/24", RouteMetric = 10 });
Assert(directRoute.DestinationPrefix == "192.168.10.0/24" && directRoute.NextHop == "0.0.0.0" && !directRoute.IsDefaultRoute, "direct route normalization");
var gatewayRoute = StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "10.20.0.0/16", NextHop = "10.20.0.1", RouteMetric = 20 });
Assert(gatewayRoute.NextHop == "10.20.0.1" && gatewayRoute.RouteMetric == 20, "gateway route normalization");
Assert(StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "0.0.0.0/0", NextHop = "192.168.100.1", RouteMetric = 5 }).IsDefaultRoute, "default route allowed");
AssertThrows(() => StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "10.0.0.0/8", NextHop = "::1", RouteMetric = 10 }), "ipv6 next hop rejected");
AssertThrows(() => StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "10.0.0.0/8", RouteMetric = 0 }), "invalid metric rejected");
var snapshot = new AdapterIpv4Snapshot { IpAddress = "192.168.10.2", PrefixLength = 24, Gateway = "192.168.10.1" };
snapshot.NormalizeLegacyFields();
Assert(snapshot.Addresses.Count == 1 && snapshot.Routes.Count == 1, "legacy adapter snapshot normalization");
var settings = new DhcpServerSettings();
var leases = new DhcpLeaseManager(settings);
var l1 = leases.Allocate("AA-BB-CC-DD-EE-FF", "dev");
var l2 = leases.Allocate("AA-BB-CC-DD-EE-FF", "dev");
Assert(l1.IpAddress == l2.IpAddress, "stable lease");
var smallPool = new DhcpLeaseManager(new DhcpServerSettings
{
    ServerIp = IPAddress.Parse("192.168.50.1"),
    SubnetMask = mask,
    PoolStart = IPAddress.Parse("192.168.50.2"),
    PoolEnd = IPAddress.Parse("192.168.50.3"),
    LeaseSeconds = 60
});
smallPool.Allocate("00-00-00-00-00-01", "one");
smallPool.Allocate("00-00-00-00-00-02", "two");
AssertThrowsAny(() => smallPool.Allocate("00-00-00-00-00-03", "three"), "lease pool exhaustion");
TestVersionUpdates();
TestFavoriteStorage();
await TestDhcpServerAsync();
TestDhcpServerStartFailureRecovery();
var tmp = Path.Combine(Path.GetTempPath(), "netboot-test-" + Guid.NewGuid().ToString("N") + ".json");
JsonStore.Save(tmp, new AppSettings());
Assert(File.Exists(tmp), "json save");
File.Delete(tmp);
Console.WriteLine("OK");

static void Assert(bool value, string name)
{
    if (!value) throw new Exception("Failed: " + name);
}

static void AssertThrows(Action action, string name)
{
    try
    {
        action();
    }
    catch (FormatException)
    {
        return;
    }
    throw new Exception("Failed: " + name);
}

static void AssertThrowsAny(Action action, string name)
{
    try
    {
        action();
    }
    catch
    {
        return;
    }
    throw new Exception("Failed: " + name);
}

static void TestVersionUpdates()
{
    var json = """
    {
      "version": "v1.0.8",
      "releasedAt": "2026-08-27T10:00:00Z",
      "archiveName": "NetBootDhcpTool-v1.0.8.7z",
      "archiveSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "downloadUrl": "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.8/NetBootDhcpTool-v1.0.8.7z",
      "releasePageUrl": "https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v1.0.8"
    }
    """;
    var result = VersionUpdateService.Evaluate(json, new Version(1, 0, 7));
    Assert(result.Succeeded && result.IsNewVersion && result.LatestVersion == new Version(1, 0, 8), "new version manifest");
    Assert(result.DownloadUrl.EndsWith("NetBootDhcpTool-v1.0.8.7z", StringComparison.Ordinal), "direct download URL");
    var current = VersionUpdateService.Evaluate(json, new Version(1, 0, 8));
    Assert(current.Succeeded && !current.IsNewVersion, "current version manifest");
    var unsafeManifest = json.Replace("https://github.com", "http://github.com", StringComparison.Ordinal);
    Assert(!VersionUpdateService.Evaluate(unsafeManifest, new Version(1, 0, 7)).Succeeded, "unsafe update URL rejected");
}

static void TestFavoriteStorage()
{
    if (!OperatingSystem.IsWindows()) return;
    var path = Path.Combine(Path.GetTempPath(), "netboot-favorite-" + Guid.NewGuid().ToString("N") + ".json");
    var exportPath = path + ".export.json";
    try
    {
        File.WriteAllText(path, "[{\"Name\":\"Legacy\",\"LocalIp\":\"192.168.10.2\",\"SubnetMask\":\"255.255.255.0\",\"Password\":\"dpapi-test-value\"}]");
        var loaded = FavoriteStore.Load(path, migrateLegacy: true);
        Assert(loaded.Count == 1 && loaded[0].Password == "dpapi-test-value", "legacy favorite load");
        var persisted = File.ReadAllText(path);
        Assert(!persisted.Contains("\"Password\":", StringComparison.Ordinal) && persisted.Contains("\"ProtectedPassword\":", StringComparison.Ordinal), "favorite password migration");
        Assert(!File.Exists(path + ".bak"), "legacy favorite backup removed");
        FavoriteStore.SaveCredentialFreeExport(exportPath, loaded);
        var exported = File.ReadAllText(exportPath);
        Assert(!exported.Contains("dpapi-test-value", StringComparison.Ordinal) && !exported.Contains("\"Password\":", StringComparison.Ordinal), "credential-free favorite export");
    }
    finally
    {
        foreach (var file in new[] { path, path + ".bak", path + ".tmp", exportPath, exportPath + ".bak", exportPath + ".tmp" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}

static async Task TestDhcpServerAsync()
{
    var logger = new TestLogger();
    using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    using var server = new DhcpServer(logger, listenPort: 0, clientPort: clientPort, replyAddress: IPAddress.Loopback);
    var settings = new DhcpServerSettings
    {
        ServerIp = IPAddress.Parse("192.168.60.1"),
        SubnetMask = IPAddress.Parse("255.255.255.0"),
        PoolStart = IPAddress.Parse("192.168.60.100"),
        PoolEnd = IPAddress.Parse("192.168.60.101"),
        LeaseSeconds = 60
    };
    await server.StartAsync(settings);
    Assert(server.IsRunning && server.ListenPort > 0, "DHCP server started on test port");
    var mac = new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 };
    var discover = BuildDhcpClientPacket(0x01020304, mac, DhcpMessageType.Discover);
    await client.SendAsync(discover, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    var offer = DhcpPacketParser.Parse((await ReceiveWithTimeoutAsync(client)).Buffer);
    Assert(offer.MessageType == DhcpMessageType.Offer && offer.YiAddr.ToString() == "192.168.60.100", "DHCP discover offer");
    var request = BuildDhcpClientPacket(0x01020304, mac, DhcpMessageType.Request, offer.YiAddr);
    await client.SendAsync(request, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    var ack = DhcpPacketParser.Parse((await ReceiveWithTimeoutAsync(client)).Buffer);
    Assert(ack.MessageType == DhcpMessageType.Ack && ack.YiAddr.Equals(offer.YiAddr), "DHCP request ack");
    server.Stop();
    Assert(!server.IsRunning, "DHCP server stopped cleanly");
}

static void TestDhcpServerStartFailureRecovery()
{
    var logger = new TestLogger();
    using var occupied = new UdpClient(AddressFamily.InterNetwork);
    occupied.Client.ExclusiveAddressUse = true;
    occupied.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
    var occupiedPort = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
    using var server = new DhcpServer(logger, listenPort: occupiedPort);
    var settings = new DhcpServerSettings();
    AssertThrowsAny(() => server.StartAsync(settings).GetAwaiter().GetResult(), "DHCP bind failure surfaced");
    Assert(!server.IsRunning, "DHCP failed start leaves stopped state");
}

static byte[] BuildDhcpClientPacket(uint xid, byte[] mac, DhcpMessageType type, IPAddress? requestedIp = null)
{
    var data = new byte[260];
    data[0] = 1;
    data[1] = 1;
    data[2] = 6;
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), xid);
    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10, 2), 0x8000);
    Array.Copy(mac, 0, data, 28, Math.Min(16, mac.Length));
    data[236] = 99;
    data[237] = 130;
    data[238] = 83;
    data[239] = 99;
    var index = 240;
    data[index++] = 53;
    data[index++] = 1;
    data[index++] = (byte)type;
    if (requestedIp != null)
    {
        data[index++] = 50;
        data[index++] = 4;
        requestedIp.GetAddressBytes().CopyTo(data, index);
        index += 4;
    }
    data[index++] = 255;
    Array.Resize(ref data, index);
    return data;
}

static async Task<UdpReceiveResult> ReceiveWithTimeoutAsync(UdpClient client)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    return await client.ReceiveAsync(timeout.Token);
}

static async Task RunRouteSmokeAsync(string firstIndex, string secondIndex)
{
    if (!int.TryParse(firstIndex, out var first) || !int.TryParse(secondIndex, out var second) || first <= 0 || second <= 0 || first == second)
    {
        throw new ArgumentException("Two distinct positive interface indexes are required");
    }

    var paths = new AppPaths(AppContext.BaseDirectory);
    var logger = new FileLogger(paths);
    var service = new StaticRouteService(logger);
    var adapterA = new NetworkAdapterInfo { Id = "route-smoke-a", Name = "Route Smoke A", Description = "Test Ethernet", InterfaceIndex = first.ToString(), Status = "Up", MacAddress = "00-00-00-00-00-A1" };
    var adapterB = new NetworkAdapterInfo { Id = "route-smoke-b", Name = "Route Smoke B", Description = "Test Ethernet", InterfaceIndex = second.ToString(), Status = "Up", MacAddress = "00-00-00-00-00-B1" };
    var ruleA = new StaticRouteRule { Id = "route-smoke-rule-a", DestinationPrefix = "10.250.10.0/24", AdapterId = adapterA.Id, RouteMetric = 10 };
    var ruleB = new StaticRouteRule { Id = "route-smoke-rule-b", DestinationPrefix = "10.250.20.0/24", AdapterId = adapterB.Id, NextHop = "10.250.2.1", RouteMetric = 20 };
    var targets = new List<StaticRouteTarget>
    {
        new(ruleA, adapterA, StaticRouteValidator.Normalize(ruleA)),
        new(ruleB, adapterB, StaticRouteValidator.Normalize(ruleB))
    };
    var results = await service.ApplyAsync(targets);
    var applied = results.Where(x => x.Created && x.Applied != null).Select(x => (x.Applied!, x.Rule.AdapterId == adapterA.Id ? adapterA : adapterB)).ToList();
    Assert(applied.Count == 2, "route smoke created two routes");
    foreach (var item in applied) Assert(await service.ExistsAsync(item.Item1, item.Item2), "route smoke route exists");
    foreach (var item in applied) await service.RemoveAsync(item.Item1, item.Item2);
    foreach (var item in applied) Assert(!await service.ExistsAsync(item.Item1, item.Item2), "route smoke route removed");
    Console.WriteLine($"ROUTE_SMOKE_OK interfaces={first},{second}");
}

sealed class TestLogger : ILogger
{
    public event Action<string>? LineWritten;
    public void Info(string message) => LineWritten?.Invoke(message);
    public void Warn(string message) => LineWritten?.Invoke(message);
    public void Error(string message, Exception? exception = null) => LineWritten?.Invoke(message);
}
