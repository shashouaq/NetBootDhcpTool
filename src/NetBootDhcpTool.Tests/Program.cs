using System.Net;
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
var settings = new DhcpServerSettings();
var leases = new DhcpLeaseManager(settings);
var l1 = leases.Allocate("AA-BB-CC-DD-EE-FF", "dev");
var l2 = leases.Allocate("AA-BB-CC-DD-EE-FF", "dev");
Assert(l1.IpAddress == l2.IpAddress, "stable lease");
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
