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

var mask = IPAddress.Parse("255.255.255.0");
var hosts = IpNetwork.Hosts(IPAddress.Parse("192.168.1.10"), mask);
Assert(hosts.Count == 254, "host count");
Assert(IpNetwork.SameSubnet(IPAddress.Parse("192.168.1.1"), IPAddress.Parse("192.168.1.200"), mask), "same subnet");
Assert(IpNetwork.BroadcastAddress(IPAddress.Parse("192.168.100.1"), mask).ToString() == "192.168.100.255", "broadcast address");
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
