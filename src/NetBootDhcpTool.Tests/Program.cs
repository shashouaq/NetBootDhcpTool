using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
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

var isolatedAdapterCancelSmokeIndex = Array.IndexOf(args, "--isolated-adapter-cancel-smoke");
if (isolatedAdapterCancelSmokeIndex >= 0)
{
    if (args.Length < isolatedAdapterCancelSmokeIndex + 3)
        throw new ArgumentException("--isolated-adapter-cancel-smoke requires the designated adapter GUID and a result path");
    await RunIsolatedAdapterCancelSmokeAsync(args[isolatedAdapterCancelSmokeIndex + 1], args[isolatedAdapterCancelSmokeIndex + 2]);
    return;
}

var virtualRouteSmokeIndex = Array.IndexOf(args, "--route-virtual-smoke");
if (virtualRouteSmokeIndex >= 0)
{
    if (args.Length < virtualRouteSmokeIndex + 2) throw new ArgumentException("--route-virtual-smoke requires an interface index");
    var resultPath = args.Length > virtualRouteSmokeIndex + 2 ? args[virtualRouteSmokeIndex + 2] : Path.Combine(Path.GetTempPath(), "netboot-virtual-route-smoke.result");
    await RunVirtualRouteSmokeAsync(args[virtualRouteSmokeIndex + 1], resultPath);
    return;
}

var mask = IPAddress.Parse("255.255.255.0");
var hosts = IpNetwork.Hosts(IPAddress.Parse("192.168.1.10"), mask);
Assert(hosts.Count() == 254, "host count");
Assert(IpNetwork.SameSubnet(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.1.200"), mask), "same subnet");
Assert(IpNetwork.BroadcastAddress(IPAddress.Parse("192.168.100.1"), mask).ToString() == "192.168.100.255", "broadcast address");
Assert(IpNetwork.ParseCidr("192.168.10.42/24") == "192.168.10.0/24", "cidr canonicalization");
Assert(IpNetwork.ParseCidr("192.168.10.42") == "192.168.10.42/32", "single IPv4 host canonicalization");
Assert(IpNetwork.ParseCidr("2001:db8:10::42/64") == "2001:db8:10::/64", "IPv6 cidr canonicalization");
Assert(IpNetwork.ParseCidr("0.0.0.0/0") == "0.0.0.0/0", "default route cidr");
var directRoute = StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "192.168.10.42/24", RouteMetric = 10 });
Assert(directRoute.DestinationPrefix == "192.168.10.0/24" && directRoute.NextHop == "0.0.0.0" && directRoute.AddressFamily == AddressFamily.InterNetwork && !directRoute.IsDefaultRoute, "direct route normalization");
var gatewayRoute = StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "10.20.0.0/16", NextHop = "10.20.0.1", RouteMetric = 20 });
Assert(gatewayRoute.NextHop == "10.20.0.1" && gatewayRoute.RouteMetric == 20, "gateway route normalization");
var ipv6Route = StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "2001:db8:10::42/64", NextHop = "2001:db8:10::1", RouteMetric = 10 });
Assert(ipv6Route.DestinationPrefix == "2001:db8:10::/64" && ipv6Route.NextHop == "2001:db8:10::1" && ipv6Route.AddressFamily == AddressFamily.InterNetworkV6, "IPv6 route normalization");
TestRouteSmokeAdapterResolution();
AssertThrows(() => StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "0.0.0.0/0", NextHop = "192.168.100.1", RouteMetric = 5 }), "IPv4 default route rejected");
AssertThrows(() => StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "::/0", NextHop = "2001:db8::1", RouteMetric = 5 }), "IPv6 default route rejected");
AssertThrows(() => StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "10.0.0.0/8", NextHop = "::1", RouteMetric = 10 }), "IPv6 next hop rejected for IPv4 route");
AssertThrows(() => StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "2001:db8::/32", NextHop = "10.0.0.1", RouteMetric = 10 }), "IPv4 next hop rejected for IPv6 route");
AssertThrows(() => StaticRouteValidator.Normalize(new StaticRouteRule { DestinationPrefix = "10.0.0.0/8", RouteMetric = 0 }), "invalid metric rejected");
Assert(NetworkAdapterService.NormalizeMacAddress("02:11:22:33:44:55") == "02-11-22-33-44-55", "MAC normalization");
var randomMac = NetworkAdapterService.GenerateRandomMacAddress();
Assert(NetworkAdapterService.NormalizeMacAddress(randomMac) == randomMac, "random MAC format");
AssertThrows(() => NetworkAdapterService.NormalizeMacAddress("01-11-22-33-44-55"), "multicast MAC rejected");
var snapshot = new AdapterIpv4Snapshot { IpAddress = "192.168.10.2", PrefixLength = 24, Gateway = "192.168.10.1" };
snapshot.NormalizeLegacyFields();
Assert(snapshot.Addresses.Count == 1 && snapshot.Routes.Count == 1, "legacy adapter snapshot normalization");
var settings = new DhcpServerSettings();
var leases = new DhcpLeaseManager(settings);
var testMac = new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 };
var offered = leases.Process(CreateDhcpPacket(DhcpMessageType.Discover, testMac));
var repeatedOffer = leases.Process(CreateDhcpPacket(DhcpMessageType.Discover, testMac));
Assert(offered.ResponseType == DhcpMessageType.Offer && offered.Address?.Equals(repeatedOffer.Address) == true && leases.Leases.Count == 0,
    $"stable uncommitted offer ({offered.ResponseType}:{offered.Address}; {repeatedOffer.ResponseType}:{repeatedOffer.Address}; leases={leases.Leases.Count})");
var ackDecision = leases.Process(CreateDhcpPacket(DhcpMessageType.Request, testMac, offered.Address, settings.ServerIp));
Assert(ackDecision.ResponseType == DhcpMessageType.Ack && leases.Leases.Count == 1, "DORA request commits a lease");
var wrongServer = leases.Process(CreateDhcpPacket(DhcpMessageType.Request, testMac, offered.Address, IPAddress.Parse("192.168.100.9")));
Assert(wrongServer.ResponseType == null && leases.Leases.Count == 1, "selecting another server receives no acknowledgement");
var smallPoolSettings = new DhcpServerSettings
{
    ServerIp = IPAddress.Parse("192.168.50.1"),
    SubnetMask = mask,
    PoolStart = IPAddress.Parse("192.168.50.2"),
    PoolEnd = IPAddress.Parse("192.168.50.3"),
    LeaseSeconds = 60
};
var smallPool = new DhcpLeaseManager(smallPoolSettings);
var poolOffer1 = smallPool.Process(CreateDhcpPacket(DhcpMessageType.Discover, [0x02, 0, 0, 0, 0, 1]));
var poolOffer2 = smallPool.Process(CreateDhcpPacket(DhcpMessageType.Discover, [0x02, 0, 0, 0, 0, 2]));
var poolOffer3 = smallPool.Process(CreateDhcpPacket(DhcpMessageType.Discover, [0x02, 0, 0, 0, 0, 3]));
Assert(poolOffer1.Address != null && poolOffer2.Address != null && !poolOffer1.Address.Equals(poolOffer2.Address) && poolOffer3.ResponseType == null, "offer pool exhaustion");
TestVersionUpdates();
TestSupportDataRedaction();
TestFavoriteStorage();
TestProfileStorage();
TestJsonRecovery();
await TestHttpProbeInputBoundaryAsync();
var publicPresets = Defaults.DefaultFavorites();
Assert(publicPresets.Count >= 10 && publicPresets.All(x => x.IsPublicDefault && !string.IsNullOrWhiteSpace(x.Password)), "public BMC presets");
await TestDhcpServerAsync();
TestDhcpServerStartFailureRecovery();
var tmp = Path.Combine(Path.GetTempPath(), "netboot-test-" + Guid.NewGuid().ToString("N") + ".json");
var settingsPath = tmp + ".settings";
JsonStore.Save(tmp, new AppSettings());
Assert(File.Exists(tmp), "json save");
var expectedSettings = new AppSettings
{
    WindowWidth = 1400,
    WindowHeight = 900,
    WindowLeft = 12,
    WindowTop = 34,
    WindowState = "Maximized",
    LastTab = "TabHistory",
    LogPanelExpanded = true,
    LogAutoScroll = false
};
JsonStore.Save(settingsPath, expectedSettings);
var loadedSettings = JsonStore.LoadOrDefault(settingsPath, new AppSettings());
Assert(loadedSettings.WindowWidth == 1400 && loadedSettings.WindowHeight == 900
    && loadedSettings.WindowLeft == 12 && loadedSettings.WindowTop == 34
    && loadedSettings.WindowState == "Maximized" && loadedSettings.LastTab == "TabHistory"
    && loadedSettings.LogPanelExpanded && !loadedSettings.LogAutoScroll, "app settings layout roundtrip");
foreach (var file in new[] { tmp, settingsPath })
{
    if (File.Exists(file)) File.Delete(file);
}
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

static void TestSupportDataRedaction()
{
    var source = "IP=192.168.10.25 MAC=02-11-22-33-44-55 IPv6=2001:db8:10::42";
    var redacted = SupportDataRedactor.RedactNetworkValues(source);
    Assert(!redacted.Contains("192.168.10.25", StringComparison.Ordinal)
        && !redacted.Contains("02-11-22-33-44-55", StringComparison.Ordinal)
        && !redacted.Contains("2001:db8:10::42", StringComparison.Ordinal), "support network redaction");
    Assert(redacted.Contains("x.x.x.x", StringComparison.Ordinal)
        && redacted.Contains("XX-XX-XX-XX-XX-XX", StringComparison.Ordinal)
        && redacted.Contains("xxxx:xxxx::xxxx", StringComparison.Ordinal), "support redaction placeholders");
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
      "releasePageUrl": "https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v1.0.8",
      "releaseNotes": "## v1.0.8\n- Changes",
      "changes": ["Changes"]
    }
    """;
    var result = VersionUpdateService.Evaluate(json, new Version(1, 0, 7));
    Assert(result.Succeeded && result.IsNewVersion && result.LatestVersion == new Version(1, 0, 8), "new version manifest");
    Assert(result.DownloadUrl.EndsWith("NetBootDhcpTool-v1.0.8.7z", StringComparison.Ordinal), "direct download URL");
    Assert(result.Changes.Count == 1 && result.ReleaseNotes.Contains("Changes", StringComparison.Ordinal), "release notes in manifest");
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
        Assert(loaded[0].PasswordDisplay == "••••••", "favorite password masked by default");
        var persisted = File.ReadAllText(path);
        Assert(!persisted.Contains("\"Password\":", StringComparison.Ordinal) && persisted.Contains("\"ProtectedPassword\":", StringComparison.Ordinal), "favorite password migration");
        var protectedBackup = File.ReadAllText(path + ".bak");
        Assert(!protectedBackup.Contains("dpapi-test-value", StringComparison.Ordinal)
            && !protectedBackup.Contains("\"Password\":", StringComparison.Ordinal)
            && protectedBackup.Contains("\"ProtectedPassword\":", StringComparison.Ordinal), "legacy favorite backup contains only DPAPI-protected credentials");
        FavoriteStore.SaveCredentialFreeExport(exportPath, loaded);
        var exported = File.ReadAllText(exportPath);
        Assert(!exported.Contains("dpapi-test-value", StringComparison.Ordinal) && !exported.Contains("\"Password\":", StringComparison.Ordinal), "credential-free favorite export");
        var exportedBackup = File.ReadAllText(exportPath + ".bak");
        Assert(!exportedBackup.Contains("dpapi-test-value", StringComparison.Ordinal) && !exportedBackup.Contains("\"Password\":", StringComparison.Ordinal), "credential-free favorite export backup");

        var publicPath = path + ".public.json";
        var publicFavorite = Defaults.DefaultFavorites().First(x => x.IsPublicDefault);
        FavoriteStore.Save(publicPath, [publicFavorite]);
        var publicJson = File.ReadAllText(publicPath);
        Assert(publicJson.Contains(publicFavorite.Password, StringComparison.Ordinal) && publicJson.Contains("\"PublicPassword\"", StringComparison.Ordinal), "shipped public favorite plaintext credential");
        var publicLoaded = FavoriteStore.Load(publicPath);
        Assert(publicLoaded.Count == 1 && publicLoaded[0].Password == publicFavorite.Password && publicLoaded[0].PasswordDisplay == "••••••", "public favorite load and mask");

        var forgedPath = path + ".forged-public.json";
        const string forgedSecret = "SYNTHETIC_FORGED_PUBLIC_SECRET";
        FavoriteStore.Save(forgedPath,
        [new FavoriteConfig { Id = "not-a-shipped-id", Name = "Forged public marker", IsPublicDefault = true, Password = forgedSecret }]);
        var forgedJson = File.ReadAllText(forgedPath);
        Assert(!forgedJson.Contains(forgedSecret, StringComparison.Ordinal)
            && forgedJson.Contains("\"ProtectedPassword\"", StringComparison.Ordinal)
            && !FavoriteStore.Load(forgedPath)[0].IsPublicDefault, "untrusted public marker remains a protected personal credential");
    }
    finally
    {
        foreach (var file in new[] { path, path + ".bak", path + ".tmp", exportPath, exportPath + ".bak", exportPath + ".tmp", path + ".public.json", path + ".public.json.bak", path + ".public.json.tmp", path + ".forged-public.json", path + ".forged-public.json.bak", path + ".forged-public.json.tmp" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}

static void TestProfileStorage()
{
    var path = Path.Combine(Path.GetTempPath(), "netboot-profile-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var profile = new NetworkProfile
        {
            Name = "isolated-lab",
            Description = "credential-free test profile",
            ManualIp = "192.168.77.2",
            ManualMask = "255.255.255.0",
            ManualTargetIp = "192.168.77.3",
            Routes = [new StaticRouteRule { DestinationPrefix = "192.168.88.0/24", AdapterId = "adapter-a", NextHop = "0.0.0.0" }]
        };
        ProfileStore.Save(path, [profile]);
        var loaded = ProfileStore.Load(path);
        Assert(loaded.Count == 1 && loaded[0].Name == profile.Name && loaded[0].Routes.Count == 1, "network profile storage");
        var json = File.ReadAllText(path);
        Assert(!json.Contains("Password", StringComparison.OrdinalIgnoreCase), "network profile has no credentials");
    }
    finally
    {
        foreach (var file in new[] { path, path + ".bak", path + ".tmp" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}

static void TestJsonRecovery()
{
    var directory = Path.Combine(Path.GetTempPath(), "netboot-recovery-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "settings.json");
    try
    {
        Assert(JsonStore.Load<List<string>>(path).Status == DataLoadStatus.Missing, "missing JSON status");
        File.WriteAllText(path, "[]");
        Assert(JsonStore.Load<List<string>>(path).Status == DataLoadStatus.LoadedEmpty, "valid empty JSON status");
        File.Delete(path);
        JsonStore.Save(path, new AppSettings { Language = "en-US" });
        JsonStore.Save(path, new AppSettings { Language = "zh-CN" });
        File.WriteAllText(path, "damaged primary");
        var damaged = File.ReadAllText(path);
        var restored = JsonStore.Load<AppSettings>(path);
        Assert(restored.Status == DataLoadStatus.RestoredFromBackup && restored.Value!.Language == "en-US", "restore from valid backup");
        Assert(File.ReadAllText(path) == damaged, "corrupt primary preserved");
        File.WriteAllText(path + ".bak", "damaged backup");
        Assert(JsonStore.Load<AppSettings>(path).Status == DataLoadStatus.Failed, "both corrupt status");
        try { JsonStore.Save(path, new AppSettings()); throw new Exception("corrupt JSON save should fail"); }
        catch (InvalidDataException) { }
        Assert(File.ReadAllText(path) == damaged, "failed save retained corrupt primary");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task TestHttpProbeInputBoundaryAsync()
{
    var result = await new HttpProbeService().ProbeAsync("127.0.0.1;Get-Process", 50);
    Assert(!result.http && !result.https, "HTTP probe rejects non-IP input");
}

static async Task TestDhcpServerAsync()
{
    var logger = new TestLogger();
    using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    var loopback = NetworkInterface.GetAllNetworkInterfaces().Single(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
    var loopbackIndex = loopback.GetIPProperties().GetIPv4Properties()?.Index ?? throw new InvalidOperationException("Loopback IPv4 interface index unavailable.");
    using var server = new DhcpServer(logger, listenPort: 0, clientPort: clientPort, replyAddress: IPAddress.Loopback);
    var settings = new DhcpServerSettings
    {
        AdapterId = loopback.Id,
        InterfaceIndex = loopbackIndex,
        ServerIp = IPAddress.Loopback,
        SubnetMask = IPAddress.Parse("255.0.0.0"),
        PoolStart = IPAddress.Parse("127.0.0.100"),
        PoolEnd = IPAddress.Parse("127.0.0.101"),
        LeaseSeconds = 60
    };
    var persistedBindings = new List<DhcpLeaseBinding>();
    await server.StartAsync(settings, persistLeaseTable: snapshot => persistedBindings = snapshot.Bindings);
    Assert(server.IsRunning && server.ListenPort > 0, "DHCP server started on test port");
    var mac = new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 };
    var discover = BuildDhcpClientPacket(0x01020304, mac, DhcpMessageType.Discover);
    var malformedDiscover = (byte[])discover.Clone();
    malformedDiscover[239] = 0;
    await client.SendAsync(malformedDiscover, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    Assert(!await HasDhcpPacketAsync(client, 100), "malformed packet gets no DHCP response");
    await client.SendAsync(discover, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    var offer = DhcpPacketParser.Parse((await ReceiveWithTimeoutAsync(client)).Buffer);
    Assert(offer.MessageType == DhcpMessageType.Offer && offer.YiAddr.ToString() == "127.0.0.100", "DHCP discover offer scoped to the selected loopback test interface");
    Assert(persistedBindings.Count == 0, "DHCP offer not yet persisted as a committed lease");
    await client.SendAsync(discover, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    var repeatedOffer = DhcpPacketParser.Parse((await ReceiveWithTimeoutAsync(client)).Buffer);
    Assert(repeatedOffer.YiAddr.Equals(offer.YiAddr), "repeat discover retains the pending offer");
    var wrongServerRequest = BuildDhcpClientPacket(0x01020304, mac, DhcpMessageType.Request, offer.YiAddr, IPAddress.Parse("127.0.0.2"));
    await client.SendAsync(wrongServerRequest, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    Assert(!await HasDhcpPacketAsync(client, 200), "request selecting another DHCP server is ignored");
    await client.SendAsync(discover, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    offer = DhcpPacketParser.Parse((await ReceiveWithTimeoutAsync(client)).Buffer);
    var request = BuildDhcpClientPacket(0x01020304, mac, DhcpMessageType.Request, offer.YiAddr, settings.ServerIp);
    await client.SendAsync(request, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    var ack = DhcpPacketParser.Parse((await ReceiveWithTimeoutAsync(client)).Buffer);
    Assert(ack.MessageType == DhcpMessageType.Ack && ack.YiAddr.Equals(offer.YiAddr), "DHCP request ack");
    Assert(persistedBindings.Count == 1 && persistedBindings[0].IpAddress == offer.YiAddr.ToString(), "lease is journaled before ACK");
    var invalidRequest = BuildDhcpClientPacket(0x01020304, mac, DhcpMessageType.Request, IPAddress.Parse("127.0.0.102"), settings.ServerIp);
    await client.SendAsync(invalidRequest, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    var nak = DhcpPacketParser.Parse((await ReceiveWithTimeoutAsync(client)).Buffer);
    Assert(nak.MessageType == DhcpMessageType.Nak && nak.YiAddr.Equals(IPAddress.Any), "invalid requested address receives addressless NAK");
    var release = BuildDhcpClientPacket(0x01020304, mac, DhcpMessageType.Release, serverIdentifier: settings.ServerIp, ciAddr: offer.YiAddr);
    await client.SendAsync(release, new IPEndPoint(IPAddress.Loopback, server.ListenPort));
    var releaseDeadline = Stopwatch.StartNew();
    while (persistedBindings.Count != 0 && releaseDeadline.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
    Assert(persistedBindings.Count == 0, "owner release clears the committed binding journal");
    server.Stop();
    Assert(!server.IsRunning, "DHCP server stopped cleanly");
}

static void TestDhcpServerStartFailureRecovery()
{
    var logger = new TestLogger();
    using var occupied = new UdpClient(AddressFamily.InterNetwork);
    occupied.Client.ExclusiveAddressUse = true;
    occupied.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    var occupiedPort = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
    using var server = new DhcpServer(logger, listenPort: occupiedPort, allowUnscopedTestBinding: true);
    var settings = new DhcpServerSettings { ServerIp = IPAddress.Loopback };
    AssertThrowsAny(() => server.StartAsync(settings).GetAwaiter().GetResult(), "DHCP bind failure surfaced");
    Assert(!server.IsRunning, "DHCP failed start leaves stopped state");
}

static byte[] BuildDhcpClientPacket(uint xid, byte[] mac, DhcpMessageType type, IPAddress? requestedIp = null, IPAddress? serverIdentifier = null, IPAddress? ciAddr = null)
{
    var data = new byte[260];
    data[0] = 1;
    data[1] = 1;
    data[2] = 6;
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), xid);
    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10, 2), 0x8000);
    if (ciAddr != null) ciAddr.GetAddressBytes().CopyTo(data, 12);
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
    if (serverIdentifier != null)
    {
        data[index++] = 54;
        data[index++] = 4;
        serverIdentifier.GetAddressBytes().CopyTo(data, index);
        index += 4;
    }
    data[index++] = 255;
    Array.Resize(ref data, index);
    return data;
}

static DhcpPacket CreateDhcpPacket(DhcpMessageType type, byte[] mac, IPAddress? requestedIp = null, IPAddress? serverIdentifier = null) => new()
{
    Op = 1,
    HType = 1,
    HLen = 6,
    Xid = 0x01020304,
    ChAddr = mac.Concat(new byte[10]).ToArray(),
    MessageType = type,
    RequestedIp = requestedIp,
    ServerIdentifier = serverIdentifier,
    Hostname = "smoke-client"
};

static async Task<UdpReceiveResult> ReceiveWithTimeoutAsync(UdpClient client)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    return await client.ReceiveAsync(timeout.Token);
}

static async Task<bool> HasDhcpPacketAsync(UdpClient client, int timeoutMs)
{
    using var timeout = new CancellationTokenSource(timeoutMs);
    try { _ = await client.ReceiveAsync(timeout.Token); return true; }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return false; }
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
    var discoveredAdapters = new NetworkAdapterService(logger).GetAdapters(logAdapters: false);
    var adapterA = ResolveRouteSmokeAdapter(discoveredAdapters, first, "first route-smoke", requireVirtual: true);
    var adapterB = ResolveRouteSmokeAdapter(discoveredAdapters, second, "second route-smoke", requireVirtual: true);
    var secondAdapterGuid = Guid.Parse(adapterB.Id).ToString("D");
    var ruleA = new StaticRouteRule { Id = "route-smoke-rule-a", DestinationPrefix = "10.250.10.0/24", AdapterId = adapterA.Id, RouteMetric = 10 };
    var ruleB = new StaticRouteRule { Id = "route-smoke-rule-b", DestinationPrefix = "10.250.20.0/24", AdapterId = adapterB.Id, NextHop = "198.18.251.254", RouteMetric = 20 };
    var conflictRule = new StaticRouteRule { Id = "route-smoke-rule-conflict", DestinationPrefix = "10.250.30.0/24", AdapterId = adapterA.Id, RouteMetric = 10 };
    var samePrefixRuleA = new StaticRouteRule { Id = "route-smoke-rule-same-a", DestinationPrefix = "10.250.40.0/24", AdapterId = adapterA.Id };
    var samePrefixRuleB = new StaticRouteRule { Id = "route-smoke-rule-same-b", DestinationPrefix = "10.250.40.0/24", AdapterId = adapterB.Id };
    var ipv6Rule = new StaticRouteRule { Id = "route-smoke-rule-ipv6", DestinationPrefix = "fd12:250:252::/64", AdapterId = adapterA.Id, RouteMetric = 10 };
    const string conflictDestination = "10.250.30.0/24";
    const string conflictNextHop = "198.18.251.254";
    var targets = new List<StaticRouteTarget>
    {
        new(ruleA, adapterA, StaticRouteValidator.Normalize(ruleA)),
        new(ruleB, adapterB, StaticRouteValidator.Normalize(ruleB)),
        new(conflictRule, adapterA, StaticRouteValidator.Normalize(conflictRule)),
        new(samePrefixRuleA, adapterA, StaticRouteValidator.Normalize(samePrefixRuleA)),
        new(samePrefixRuleB, adapterB, StaticRouteValidator.Normalize(samePrefixRuleB)),
        new(ipv6Rule, adapterA, StaticRouteValidator.Normalize(ipv6Rule))
    };
    var seedRouteCreated = false;
    try
    {
        var setupScript = $$"""
$idx={{second}}
$expectedGuid=[guid]'{{secondAdapterGuid}}'
$netAdapter=Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop
if ([guid]$netAdapter.InterfaceGuid -ne $expectedGuid) { throw 'Route-smoke adapter identity changed before setup' }
$existing=@(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '{{conflictDestination}}' -ErrorAction SilentlyContinue | Where-Object { [string]$_.NextHop -eq '{{conflictNextHop}}' -and [int]$_.RouteMetric -eq 200 })
if ($existing.Count -gt 0) { throw 'The isolated route-smoke seed route already exists; refusing to claim it' }
New-NetRoute -DestinationPrefix '{{conflictDestination}}' -InterfaceIndex $idx -AddressFamily IPv4 -NextHop '{{conflictNextHop}}' -RouteMetric 200 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
""";
        await RunPowerShellCommandAsync(setupScript);
        seedRouteCreated = true;
        var conflictRoutes = (await service.GetCurrentStaticRoutesAsync())
            .Where(route => route.DestinationPrefix.StartsWith("10.250.30.", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var route in conflictRoutes)
            Console.WriteLine($"ROUTE_SMOKE_SETUP prefix={route.DestinationPrefix} interface={route.InterfaceIndex} nextHop={route.NextHop} routeMetric={route.RouteMetric} interfaceMetric={route.InterfaceMetric} store={route.PolicyStore}");

        var results = await service.ApplyAsync(targets);
        var applied = results.Where(x => x.Created && x.Applied != null).Select(x => (x.Applied!, x.Rule.AdapterId == adapterA.Id ? adapterA : adapterB)).ToList();
        Assert(applied.Count == 6, "route smoke created IPv4, IPv6, and multi-adapter priority routes");
        var conflictApplied = applied.Single(x => x.Item1.DestinationPrefix == conflictDestination);
        Assert(conflictApplied.Item1.RouteMetric < 200, "same-prefix route received higher priority");
        var samePrefixApplied = applied.Where(x => x.Item1.DestinationPrefix == "10.250.40.0/24").ToList();
        Assert(samePrefixApplied.Count == 2 && samePrefixApplied.Select(x => x.Item1.RouteMetric).Distinct().Count() == 2, "same-prefix routes on different adapters received distinct automatic metrics");
        Assert(applied.Any(x => x.Item1.AddressFamily == "IPv6"), "IPv6 route created");
        foreach (var item in applied) Assert(await service.ExistsAsync(item.Item1, item.Item2), "route smoke route exists");
        foreach (var item in applied) await service.RemoveAsync(item.Item1, item.Item2);
        foreach (var item in applied) Assert(!await service.ExistsAsync(item.Item1, item.Item2), "route smoke route removed");
        Console.WriteLine($"ROUTE_SMOKE_OK interfaces={first},{second}");
    }
    finally
    {
        if (seedRouteCreated)
        {
            var cleanupScript = $$"""
$idx={{second}}
$expectedGuid=[guid]'{{secondAdapterGuid}}'
$netAdapter=Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop
if ([guid]$netAdapter.InterfaceGuid -ne $expectedGuid) { throw 'Route-smoke adapter identity changed before seed-route cleanup' }
$owned=@(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '{{conflictDestination}}' -ErrorAction SilentlyContinue | Where-Object { [string]$_.NextHop -eq '{{conflictNextHop}}' -and [int]$_.RouteMetric -eq 200 })
$owned | Remove-NetRoute -Confirm:$false -ErrorAction Stop
$remaining=@(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '{{conflictDestination}}' -ErrorAction SilentlyContinue | Where-Object { [string]$_.NextHop -eq '{{conflictNextHop}}' -and [int]$_.RouteMetric -eq 200 })
if ($remaining.Count -gt 0) { throw 'The route-smoke seed route remains after cleanup' }
""";
            await RunPowerShellCommandAsync(cleanupScript);
        }
    }
}

static NetworkAdapterInfo ResolveRouteSmokeAdapter(
    IReadOnlyList<NetworkAdapterInfo> discoveredAdapters,
    int interfaceIndex,
    string description,
    bool requireVirtual)
{
    var matches = discoveredAdapters
        .Where(adapter => int.TryParse(adapter.InterfaceIndex, out var candidateIndex) && candidateIndex == interfaceIndex)
        .ToList();
    if (matches.Count != 1)
        throw new InvalidOperationException($"Expected exactly one {description} adapter at interface index {interfaceIndex}; found {matches.Count}.");

    var adapter = matches[0];
    if (!Guid.TryParse(adapter.Id, out _))
        throw new InvalidOperationException($"The {description} adapter at interface index {interfaceIndex} has no stable GUID.");
    if (requireVirtual && !adapter.IsVirtual)
        throw new InvalidOperationException($"The {description} adapter at interface index {interfaceIndex} is not identified as a virtual adapter.");
    return adapter;
}

static void TestRouteSmokeAdapterResolution()
{
    var virtualAdapter = new NetworkAdapterInfo
    {
        Id = Guid.NewGuid().ToString("B"),
        Name = "vEthernet (Route Smoke Test)",
        InterfaceIndex = "51001",
        IsVirtual = true
    };
    var resolved = ResolveRouteSmokeAdapter([virtualAdapter], 51001, "test", requireVirtual: true);
    Assert(resolved.Id == virtualAdapter.Id, "route smoke resolves the stable adapter GUID by interface index");
    AssertThrowsAny(() => ResolveRouteSmokeAdapter([], 51001, "missing test", requireVirtual: true), "route smoke rejects missing adapter identity");
    AssertThrowsAny(() => ResolveRouteSmokeAdapter([virtualAdapter, virtualAdapter], 51001, "ambiguous test", requireVirtual: true), "route smoke rejects ambiguous interface indexes");
    AssertThrowsAny(() => ResolveRouteSmokeAdapter([new NetworkAdapterInfo { Id = "route-smoke-test", InterfaceIndex = "51001", IsVirtual = true }], 51001, "invalid GUID test", requireVirtual: true), "route smoke rejects non-GUID adapter identity");
    AssertThrowsAny(() => ResolveRouteSmokeAdapter([new NetworkAdapterInfo { Id = Guid.NewGuid().ToString("B"), InterfaceIndex = "51001", IsVirtual = false }], 51001, "physical adapter test", requireVirtual: true), "route smoke rejects a physical adapter");
}

static async Task RunIsolatedAdapterCancelSmokeAsync(string adapterGuidText, string resultPath)
{
    var designatedGuid = Guid.Parse("863A3E86-475B-4BA1-AE14-8D7C654DF9C1");
    if (!Guid.TryParse(adapterGuidText, out var requestedGuid) || requestedGuid != designatedGuid)
        throw new InvalidOperationException($"The isolated adapter smoke only accepts the designated X722 GUID {designatedGuid:B}.");

    var fullResultPath = Path.GetFullPath(resultPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath)!);
    var recoveryPath = fullResultPath + ".recovery.json";
    if (File.Exists(recoveryPath))
        throw new IOException($"A prior isolated adapter recovery record already exists: {recoveryPath}");

    var logger = new TestLogger();
    NetworkAdapterInfo? adapter = null;
    AdapterIpv4Snapshot? originalSnapshot = null;
    string? originalMacAddress = null;
    var disabledReadbackObserved = false;
    var cancellationRequested = false;
    var delayInvocation = 0;
    using var operationCancellation = new CancellationTokenSource();
    NetworkAdapterService? service = null;
    service = new NetworkAdapterService(logger, delayAsync: async (delay, cancellationToken) =>
    {
        if (delay == TimeSpan.FromMilliseconds(750) && Interlocked.Increment(ref delayInvocation) == 1)
        {
            var target = adapter ?? throw new InvalidOperationException("The designated adapter was not resolved before restart.");
            if (await service!.IsAdapterEnabledAsync(target, CancellationToken.None).ConfigureAwait(false))
                throw new InvalidOperationException("The adapter did not read back AdminStatus Down after the disable command.");

            disabledReadbackObserved = true;
            cancellationRequested = true;
            operationCancellation.Cancel();
        }

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    });

    var cleanupRestored = false;
    try
    {
        if (!service.IsAdministrator())
            throw new InvalidOperationException("Run the isolated adapter smoke from an elevated PowerShell session.");

        var matches = service.GetAdapters(logAdapters: false)
            .Where(candidate => Guid.TryParse(candidate.Id, out var id) && id == designatedGuid)
            .ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected exactly one adapter with designated GUID {designatedGuid:B}; found {matches.Count}.");

        var selected = matches[0];
        adapter = service.GetAdapterByIdentity(selected.Id, selected.InterfaceIndex)
            ?? throw new InvalidOperationException("The selected adapter identity changed during preflight.");
        if (adapter.IsWifi || adapter.IsVirtual
            || !adapter.Description.Contains("X722", StringComparison.OrdinalIgnoreCase)
            || !adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)
            || adapter.HasGateway)
            throw new InvalidOperationException("The designated X722 adapter failed its physical, connected, or no-gateway preflight.");

        var interfaceIndex = int.Parse(adapter.InterfaceIndex, System.Globalization.CultureInfo.InvariantCulture);
        var defaultRoutesBefore = await GetAdapterDefaultRouteCountAsync(interfaceIndex).ConfigureAwait(false);
        if (defaultRoutesBefore != 0)
            throw new InvalidOperationException($"The designated isolated adapter has {defaultRoutesBefore} default route(s); no write was attempted.");
        if (!await service.IsAdapterEnabledAsync(adapter, CancellationToken.None).ConfigureAwait(false))
            throw new InvalidOperationException("The designated adapter is not administratively enabled; no restart was attempted.");

        originalSnapshot = await service.CaptureIPv4ConfigAsync(adapter, CancellationToken.None).ConfigureAwait(false);
        if (originalSnapshot.AdapterEnabled != true)
            throw new InvalidDataException("The adapter's original administrative state could not be captured as enabled.");
        originalMacAddress = GetCurrentMacAddress(adapter.Id);
        File.WriteAllText(recoveryPath, JsonSerializer.Serialize(new
        {
            adapterGuid = designatedGuid.ToString("B"),
            interfaceIndex = adapter.InterfaceIndex,
            originalMacAddress,
            originalSnapshot,
            createdUtc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));

        var cancellationCaught = false;
        try
        {
            await service.RestartAdapterAsync(
                adapter,
                allowAnyAdapter: true,
                ct: operationCancellation.Token,
                expectedEnabledBefore: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested && disabledReadbackObserved)
        {
            cancellationCaught = true;
        }

        if (!cancellationCaught || !cancellationRequested || !disabledReadbackObserved)
            throw new InvalidOperationException("Cancellation was not observed after the real adapter disable readback.");

        var enabledAfterCompensation = await service.IsAdapterEnabledAsync(adapter, CancellationToken.None).ConfigureAwait(false);
        var restoredSnapshot = await service.CaptureIPv4ConfigAsync(adapter, CancellationToken.None).ConfigureAwait(false);
        var restoredMacAddress = GetCurrentMacAddress(adapter.Id);
        var defaultRoutesAfter = await GetAdapterDefaultRouteCountAsync(interfaceIndex).ConfigureAwait(false);
        var snapshotEquivalent = AdapterIpv4SnapshotComparer.Equivalent(originalSnapshot, restoredSnapshot);
        var macRestored = NetworkAdapterService.NormalizeMacAddress(originalMacAddress)
            .Equals(NetworkAdapterService.NormalizeMacAddress(restoredMacAddress), StringComparison.OrdinalIgnoreCase);
        if (!enabledAfterCompensation || !snapshotEquivalent || !macRestored || defaultRoutesAfter != 0)
            throw new InvalidOperationException("The isolated adapter did not return to its complete pre-test state.");

        File.Delete(recoveryPath);
        WriteIsolatedAdapterSmokeResult(fullResultPath, new
        {
            passed = true,
            adapterGuid = designatedGuid.ToString("B"),
            interfaceIndex = adapter.InterfaceIndex,
            cancellationRequestedAfterDisableReadback = true,
            independentCompensationRestoredAdminStatus = enabledAfterCompensation,
            ipv4SnapshotEquivalent = snapshotEquivalent,
            macRestored,
            defaultRoutesBefore,
            defaultRoutesAfter,
            recoveryRecordRemoved = !File.Exists(recoveryPath)
        });
        Console.WriteLine($"T21_PHYSICAL_CANCEL_SMOKE_OK interface={interfaceIndex} cancellation=after-disable-readback compensation=verified ipv4=unchanged recovery=cleared");
    }
    catch (Exception operationError)
    {
        Exception? cleanupError = null;
        if (adapter is not null && originalSnapshot is not null && originalMacAddress is not null)
        {
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                if (!await service.IsAdapterEnabledAsync(adapter, cleanupTimeout.Token).ConfigureAwait(false))
                    await service.EnsureAdapterEnabledAsync(adapter, allowAnyAdapter: true, cleanupTimeout.Token).ConfigureAwait(false);
                var currentMac = GetCurrentMacAddress(adapter.Id);
                if (!NetworkAdapterService.NormalizeMacAddress(currentMac).Equals(
                        NetworkAdapterService.NormalizeMacAddress(originalMacAddress), StringComparison.OrdinalIgnoreCase))
                    await service.RestoreMacAddressAsync(adapter, originalMacAddress, cleanupTimeout.Token).ConfigureAwait(false);
                var currentSnapshot = await service.CaptureIPv4ConfigAsync(adapter, cleanupTimeout.Token).ConfigureAwait(false);
                if (!AdapterIpv4SnapshotComparer.Equivalent(originalSnapshot, currentSnapshot))
                    await service.RestoreIPv4ConfigAsync(adapter, originalSnapshot, cleanupTimeout.Token).ConfigureAwait(false);
                var finalSnapshot = await service.CaptureIPv4ConfigAsync(adapter, cleanupTimeout.Token).ConfigureAwait(false);
                cleanupRestored = await service.IsAdapterEnabledAsync(adapter, cleanupTimeout.Token).ConfigureAwait(false)
                    && AdapterIpv4SnapshotComparer.Equivalent(originalSnapshot, finalSnapshot)
                    && NetworkAdapterService.NormalizeMacAddress(GetCurrentMacAddress(adapter.Id))
                        .Equals(NetworkAdapterService.NormalizeMacAddress(originalMacAddress), StringComparison.OrdinalIgnoreCase)
                    && await GetAdapterDefaultRouteCountAsync(int.Parse(adapter.InterfaceIndex, System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false) == 0;
                if (!cleanupRestored) throw new InvalidOperationException("Fallback cleanup could not verify the original adapter state.");
                if (File.Exists(recoveryPath)) File.Delete(recoveryPath);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
            }
        }

        WriteIsolatedAdapterSmokeResult(fullResultPath, new
        {
            passed = false,
            adapterGuid = designatedGuid.ToString("B"),
            interfaceIndex = adapter?.InterfaceIndex,
            cancellationRequestedAfterDisableReadback = cancellationRequested && disabledReadbackObserved,
            fallbackCleanupRestored = cleanupRestored,
            recoveryRecord = File.Exists(recoveryPath) ? recoveryPath : null,
            error = operationError.GetType().Name,
            errorMessage = operationError.Message,
            cleanupError = cleanupError?.GetType().Name
        });
        if (cleanupError is not null)
            throw new AggregateException("Isolated adapter cancellation smoke failed and fallback cleanup could not be verified.", operationError, cleanupError);
        throw;
    }
}

static async Task<int> GetAdapterDefaultRouteCountAsync(int interfaceIndex)
{
    var script = $$"""
$routes = @(Get-NetRoute -InterfaceIndex {{interfaceIndex}} -ErrorAction Stop | Where-Object { [string]$_.DestinationPrefix -in @('0.0.0.0/0', '::/0') })
[Console]::WriteLine($routes.Count)
""";
    var result = await RunPowerShellCommandAsync(script).ConfigureAwait(false);
    if (!int.TryParse(result, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) || count < 0)
        throw new InvalidDataException("Could not read the adapter's IPv4/IPv6 default-route count.");
    return count;
}

static string GetCurrentMacAddress(string adapterId)
{
    var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
        .SingleOrDefault(candidate => candidate.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("The adapter identity disappeared during the isolated smoke.");
    return NetworkAdapterService.NormalizeMacAddress(networkInterface.GetPhysicalAddress().ToString());
}

static void WriteIsolatedAdapterSmokeResult<T>(string resultPath, T result)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
    File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
}

static async Task RunVirtualRouteSmokeAsync(string interfaceIndexText, string resultPath)
{
    const string ipv4Destination = "198.18.240.0/24";
    const string ipv6Destination = "fd12:250:25::/64";
    try
    {
        if (!int.TryParse(interfaceIndexText, out var interfaceIndex) || interfaceIndex <= 0)
            throw new ArgumentException("A positive virtual adapter interface index is required");

        var paths = new AppPaths(AppContext.BaseDirectory);
        var logger = new FileLogger(paths);
        var service = new StaticRouteService(logger);
        var discoveredAdapters = new NetworkAdapterService(logger).GetAdapters(logAdapters: false);
        var adapter = ResolveRouteSmokeAdapter(discoveredAdapters, interfaceIndex, "virtual route-smoke", requireVirtual: true);
        var ipv4Rule = new StaticRouteRule { Id = "route-virtual-smoke-v4", DestinationPrefix = ipv4Destination, AdapterId = adapter.Id };
        var ipv6Rule = new StaticRouteRule { Id = "route-virtual-smoke-v6", DestinationPrefix = ipv6Destination, AdapterId = adapter.Id };
        var targets = new List<StaticRouteTarget>
        {
            new(ipv4Rule, adapter, StaticRouteValidator.Normalize(ipv4Rule)),
            new(ipv6Rule, adapter, StaticRouteValidator.Normalize(ipv6Rule))
        };

        var results = await service.ApplyAsync(targets);
        var applied = results.Where(x => x.Created && x.Applied != null).Select(x => x.Applied!).ToList();
        Assert(applied.Count == 2, "virtual adapter created IPv4 and IPv6 routes");
        foreach (var route in applied)
        {
            Assert(await service.ExistsAsync(route, adapter), "virtual adapter route exists");
            await service.RemoveAsync(route, adapter);
            Assert(!await service.ExistsAsync(route, adapter), "virtual adapter route removed");
        }

        WriteRouteSmokeResult(resultPath, $"PASS interface={interfaceIndex} routes=IPv4,IPv6");
        Console.WriteLine($"ROUTE_VIRTUAL_SMOKE_OK interface={interfaceIndex}");
    }
    catch (Exception ex)
    {
        WriteRouteSmokeResult(resultPath, $"FAIL type={ex.GetType().Name} message={ex.Message} inner={ex.InnerException?.Message}");
        throw;
    }
    finally
    {
        try
        {
            await RunPowerShellCommandAsync($"Get-NetRoute -InterfaceIndex {interfaceIndexText} -AddressFamily IPv4 -DestinationPrefix '{ipv4Destination}' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
            await RunPowerShellCommandAsync($"Get-NetRoute -InterfaceIndex {interfaceIndexText} -AddressFamily IPv6 -DestinationPrefix '{ipv6Destination}' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
        }
        catch
        {
            // The primary failure (for example, a non-elevated test) is already recorded above.
        }
    }
}

static void WriteRouteSmokeResult(string resultPath, string result)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
    File.WriteAllText(resultPath, result, Encoding.UTF8);
}

static async Task<string> RunPowerShellCommandAsync(string script)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        }
    };
    process.Start();
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await Task.WhenAll(outputTask, errorTask);
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException(errorTask.Result.Trim());
    return outputTask.Result.Trim();
}

sealed class TestLogger : ILogger
{
    public event Action<string>? LineWritten;
    public void Info(string message) => LineWritten?.Invoke(message);
    public void Warn(string message) => LineWritten?.Invoke(message);
    public void Error(string message, Exception? exception = null) => LineWritten?.Invoke(message);
}
