using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class AdapterStatusComparerTests
{
    [TestMethod]
    public void UnchangedDisplayedSnapshotIsNotReportedAsAChange()
    {
        var current = CreateAdapter();
        Assert.IsFalse(AdapterStatusComparer.HasChanges(current, Clone(current)));
        Assert.IsFalse(AdapterStatusComparer.HasChanges(current, Clone(current, status: "up")), "Text comparisons are case-insensitive.");
    }

    [TestMethod]
    public void EveryDisplayedStatusFieldParticipatesInChangeDetection()
    {
        var current = CreateAdapter();
        Assert.IsTrue(AdapterStatusComparer.HasChanges(current, Clone(current, status: "Down")));
        Assert.IsTrue(AdapterStatusComparer.HasChanges(current, Clone(current, ipv4Address: "192.0.2.11")));
        Assert.IsTrue(AdapterStatusComparer.HasChanges(current, Clone(current, subnetMask: "255.255.255.128")));
        Assert.IsTrue(AdapterStatusComparer.HasChanges(current, Clone(current, gateway: "192.0.2.1")));
        Assert.IsTrue(AdapterStatusComparer.HasChanges(current, Clone(current, dns: "192.0.2.53")));
        Assert.IsTrue(AdapterStatusComparer.HasChanges(current, Clone(current, macAddress: "02-00-00-00-00-11")));
        Assert.IsTrue(AdapterStatusComparer.HasChanges(current, Clone(current, linkSpeedMbps: 1000)));
    }

    private static NetworkAdapterInfo CreateAdapter() => new()
    {
        Id = "adapter-id",
        Name = "Adapter",
        InterfaceIndex = "11",
        Status = "Up",
        IPv4Address = "192.0.2.10",
        SubnetMask = "255.255.255.0",
        Gateway = "",
        Dns = "",
        MacAddress = "02-00-00-00-00-10",
        LinkSpeedMbps = 100
    };

    private static NetworkAdapterInfo Clone(
        NetworkAdapterInfo source,
        string? status = null,
        string? ipv4Address = null,
        string? subnetMask = null,
        string? gateway = null,
        string? dns = null,
        string? macAddress = null,
        long? linkSpeedMbps = null) => new()
        {
            Id = source.Id,
            Name = source.Name,
            InterfaceIndex = source.InterfaceIndex,
            Status = status ?? source.Status,
            IPv4Address = ipv4Address ?? source.IPv4Address,
            SubnetMask = subnetMask ?? source.SubnetMask,
            Gateway = gateway ?? source.Gateway,
            Dns = dns ?? source.Dns,
            MacAddress = macAddress ?? source.MacAddress,
            LinkSpeedMbps = linkSpeedMbps ?? source.LinkSpeedMbps
        };
}
