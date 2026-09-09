using System.Buffers.Binary;
using System.Diagnostics;
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
Assert(hosts.Count == 254, "host count");
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
await TestHttpProbeInputBoundaryAsync();
var publicPresets = Defaults.DefaultFavorites();
Assert(publicPresets.Count >= 10 && publicPresets.All(x => x.IsPublicDefault && !string.IsNullOrWhiteSpace(x.Password)), "public BMC presets");
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
        var persisted = File.ReadAllText(path);
        Assert(!persisted.Contains("\"Password\":", StringComparison.Ordinal) && persisted.Contains("\"ProtectedPassword\":", StringComparison.Ordinal), "favorite password migration");
        Assert(!File.Exists(path + ".bak"), "legacy favorite backup removed");
        FavoriteStore.SaveCredentialFreeExport(exportPath, loaded);
        var exported = File.ReadAllText(exportPath);
        Assert(!exported.Contains("dpapi-test-value", StringComparison.Ordinal) && !exported.Contains("\"Password\":", StringComparison.Ordinal), "credential-free favorite export");

        var publicPath = path + ".public.json";
        var publicFavorite = new FavoriteConfig { Id = "public", Name = "Public preset", IsPublicDefault = true, Password = "PUBLIC_DEFAULT" };
        FavoriteStore.Save(publicPath, [publicFavorite]);
        var publicJson = File.ReadAllText(publicPath);
        Assert(publicJson.Contains("PUBLIC_DEFAULT", StringComparison.Ordinal) && publicJson.Contains("\"PublicPassword\"", StringComparison.Ordinal), "public favorite plaintext credential");
        var publicLoaded = FavoriteStore.Load(publicPath);
        Assert(publicLoaded.Count == 1 && publicLoaded[0].Password == "PUBLIC_DEFAULT", "public favorite load");
    }
    finally
    {
        foreach (var file in new[] { path, path + ".bak", path + ".tmp", exportPath, exportPath + ".bak", exportPath + ".tmp", path + ".public.json", path + ".public.json.bak", path + ".public.json.tmp" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
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
    var adapterA = new NetworkAdapterInfo { Id = "route-smoke-a", Name = "Route Smoke A", Description = "Test Ethernet", InterfaceIndex = first.ToString(), Status = "Up" };
    var adapterB = new NetworkAdapterInfo { Id = "route-smoke-b", Name = "Route Smoke B", Description = "Test Ethernet", InterfaceIndex = second.ToString(), Status = "Up" };
    var ruleA = new StaticRouteRule { Id = "route-smoke-rule-a", DestinationPrefix = "10.250.10.0/24", AdapterId = adapterA.Id, RouteMetric = 10 };
    var ruleB = new StaticRouteRule { Id = "route-smoke-rule-b", DestinationPrefix = "10.250.20.0/24", AdapterId = adapterB.Id, NextHop = "198.18.251.254", RouteMetric = 20 };
    var conflictRule = new StaticRouteRule { Id = "route-smoke-rule-conflict", DestinationPrefix = "10.250.30.0/24", AdapterId = adapterA.Id, RouteMetric = 10 };
    var samePrefixRuleA = new StaticRouteRule { Id = "route-smoke-rule-same-a", DestinationPrefix = "10.250.40.0/24", AdapterId = adapterA.Id };
    var samePrefixRuleB = new StaticRouteRule { Id = "route-smoke-rule-same-b", DestinationPrefix = "10.250.40.0/24", AdapterId = adapterB.Id };
    var ipv6Rule = new StaticRouteRule { Id = "route-smoke-rule-ipv6", DestinationPrefix = "fd12:250:252::/64", AdapterId = adapterA.Id, RouteMetric = 10 };
    const string conflictDestination = "10.250.30.0/24";
    await RunPowerShellCommandAsync($"New-NetRoute -DestinationPrefix '{conflictDestination}' -InterfaceIndex {second} -AddressFamily IPv4 -NextHop '0.0.0.0' -RouteMetric 200 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null");
    var targets = new List<StaticRouteTarget>
    {
        new(ruleA, adapterA, StaticRouteValidator.Normalize(ruleA)),
        new(ruleB, adapterB, StaticRouteValidator.Normalize(ruleB)),
        new(conflictRule, adapterA, StaticRouteValidator.Normalize(conflictRule)),
        new(samePrefixRuleA, adapterA, StaticRouteValidator.Normalize(samePrefixRuleA)),
        new(samePrefixRuleB, adapterB, StaticRouteValidator.Normalize(samePrefixRuleB)),
        new(ipv6Rule, adapterA, StaticRouteValidator.Normalize(ipv6Rule))
    };
    try
    {
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
        await RunPowerShellCommandAsync($"Get-NetRoute -InterfaceIndex {second} -AddressFamily IPv4 -DestinationPrefix '{conflictDestination}' -NextHop '0.0.0.0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue");
    }
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
        var adapter = new NetworkAdapterInfo
        {
            Id = "route-virtual-smoke",
            Name = "Route Virtual Smoke",
            Description = "Virtual adapter route test",
            InterfaceIndex = interfaceIndex.ToString(),
            Status = "Up",
            IsVirtual = true,
        };
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
