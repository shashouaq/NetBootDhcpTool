using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class DhcpSessionRestoreCoordinatorTests
{
    [TestMethod]
    public async Task RestoreTargetsStableAdapterIdWhenAnotherAdapterReusesItsOldIndex()
    {
        var intended = Adapter("stable-a", "12", "Renamed Ethernet A");
        intended.MacAddress = "00-11-22-33-44-66";
        var replacement = Adapter("different-b", "7", "Ethernet B");
        var recovery = PendingRecovery(Adapter("stable-a", "7", "Ethernet A"), Snapshot("10.0.0.5"), Snapshot("10.0.0.20"));
        var writes = new List<NetworkAdapterInfo>();

        await DhcpSessionRestoreCoordinator.RestoreAsync(
            recovery,
            [replacement, intended],
            (_, _) => Task.FromResult(Snapshot("10.0.0.20")),
            (target, _, _) => { writes.Add(target); return Task.CompletedTask; });

        CollectionAssert.AreEqual(new[] { intended }, writes);
        Assert.AreEqual(DhcpSessionRestoreState.Restored, recovery.State);
    }

    [TestMethod]
    public async Task MissingOriginalAdapterDoesNotWriteToReplacementAtReusedIndex()
    {
        var replacement = Adapter("different-b", "7", "Ethernet B");
        var recovery = PendingRecovery(Adapter("stable-a", "7", "Ethernet A"), Snapshot("10.0.0.5"), Snapshot("10.0.0.20"));
        var captureCalls = 0;
        var writes = 0;

        var error = await Assert.ThrowsExactlyAsync<DhcpSessionRestoreException>(() => DhcpSessionRestoreCoordinator.RestoreAsync(
            recovery,
            [replacement],
            (_, _) => { captureCalls++; return Task.FromResult(Snapshot("10.0.0.20")); },
            (_, _, _) => { writes++; return Task.CompletedTask; }));

        Assert.AreEqual(DhcpSessionRestoreFailure.AdapterUnavailable, error.Reason);
        Assert.AreEqual(0, captureCalls);
        Assert.AreEqual(0, writes);
        Assert.AreEqual(DhcpSessionRestoreState.RestoreFailed, recovery.State);
    }

    [TestMethod]
    public async Task AmbiguousStableIdentityFailsClosed()
    {
        var first = Adapter("stable-a", "7", "Ethernet A");
        var duplicate = Adapter("stable-a", "12", "Ethernet A Copy");
        var recovery = PendingRecovery(first, Snapshot("10.0.0.5"), Snapshot("10.0.0.20"));
        var writes = 0;

        var error = await Assert.ThrowsExactlyAsync<DhcpSessionRestoreException>(() => DhcpSessionRestoreCoordinator.RestoreAsync(
            recovery,
            [first, duplicate],
            (_, _) => Task.FromResult(Snapshot("10.0.0.20")),
            (_, _, _) => { writes++; return Task.CompletedTask; }));

        Assert.AreEqual(DhcpSessionRestoreFailure.AdapterIdentityAmbiguous, error.Reason);
        Assert.AreEqual(0, writes);
    }

    [TestMethod]
    public async Task ExternalConfigurationChangeIsPreservedForManualRecovery()
    {
        var adapter = Adapter("stable-a", "7", "Ethernet A");
        var recovery = PendingRecovery(adapter, Snapshot("10.0.0.5"), Snapshot("10.0.0.20"));
        var writes = 0;

        var error = await Assert.ThrowsExactlyAsync<DhcpSessionRestoreException>(() => DhcpSessionRestoreCoordinator.RestoreAsync(
            recovery,
            [adapter],
            (_, _) => Task.FromResult(Snapshot("10.0.0.99")),
            (_, _, _) => { writes++; return Task.CompletedTask; }));

        Assert.AreEqual(DhcpSessionRestoreFailure.ExternalConfigurationChanged, error.Reason);
        Assert.AreEqual(0, writes);
        Assert.AreEqual(DhcpSessionRestoreState.RestoreFailed, recovery.State);
    }

    [TestMethod]
    public async Task RepeatedRestoreAfterSuccessDoesNotWriteTwice()
    {
        var adapter = Adapter("stable-a", "7", "Ethernet A");
        var recovery = PendingRecovery(adapter, Snapshot("10.0.0.5"), Snapshot("10.0.0.20"));
        var writes = 0;
        var restore = new Func<NetworkAdapterInfo, AdapterIpv4Snapshot, CancellationToken, Task>((_, _, _) =>
        {
            writes++;
            return Task.CompletedTask;
        });
        Func<NetworkAdapterInfo, CancellationToken, Task<AdapterIpv4Snapshot>> capture = (_, _) => Task.FromResult(Snapshot("10.0.0.20"));

        await DhcpSessionRestoreCoordinator.RestoreAsync(recovery, [adapter], capture, restore);
        await DhcpSessionRestoreCoordinator.RestoreAsync(recovery, [adapter], capture, restore);

        Assert.AreEqual(1, writes);
        Assert.AreEqual(DhcpSessionRestoreState.Restored, recovery.State);
    }

    [TestMethod]
    public async Task OriginalStateAfterCanceledStartCompletesWithoutAnotherWrite()
    {
        var adapter = Adapter("stable-a", "7", "Ethernet A");
        var recovery = PendingRecovery(adapter, Snapshot("10.0.0.5"), expected: null);
        var writes = 0;

        await DhcpSessionRestoreCoordinator.RestoreAsync(
            recovery,
            [adapter],
            (_, _) => Task.FromResult(Snapshot("10.0.0.5")),
            (_, _, _) => { writes++; return Task.CompletedTask; });

        Assert.AreEqual(0, writes);
        Assert.AreEqual(DhcpSessionRestoreState.Restored, recovery.State);
    }

    [TestMethod]
    public async Task MissingExpectedStateDoesNotGuessOrWrite()
    {
        var adapter = Adapter("stable-a", "7", "Ethernet A");
        var recovery = PendingRecovery(adapter, Snapshot("10.0.0.5"), expected: null);
        var writes = 0;

        var error = await Assert.ThrowsExactlyAsync<DhcpSessionRestoreException>(() => DhcpSessionRestoreCoordinator.RestoreAsync(
            recovery,
            [adapter],
            (_, _) => Task.FromResult(Snapshot("10.0.0.20")),
            (_, _, _) => { writes++; return Task.CompletedTask; }));

        Assert.AreEqual(DhcpSessionRestoreFailure.ExpectedSnapshotUnavailable, error.Reason);
        Assert.AreEqual(0, writes);
        Assert.AreEqual(DhcpSessionRestoreState.RestoreFailed, recovery.State);
    }

    [TestMethod]
    public async Task FailedRestoreCanBeRetriedAndOnlyCompletesAfterCallbackSucceeds()
    {
        var adapter = Adapter("stable-a", "7", "Ethernet A");
        var recovery = PendingRecovery(adapter, Snapshot("10.0.0.5"), Snapshot("10.0.0.20"));
        var writes = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => DhcpSessionRestoreCoordinator.RestoreAsync(
            recovery,
            [adapter],
            (_, _) => Task.FromResult(Snapshot("10.0.0.20")),
            (_, _, _) => { writes++; throw new InvalidOperationException("simulated restore failure"); }));

        Assert.AreEqual(DhcpSessionRestoreState.RestoreFailed, recovery.State);
        await DhcpSessionRestoreCoordinator.RestoreAsync(
            recovery,
            [adapter],
            (_, _) => Task.FromResult(Snapshot("10.0.0.20")),
            (_, _, _) => { writes++; return Task.CompletedTask; });

        Assert.AreEqual(2, writes);
        Assert.AreEqual(DhcpSessionRestoreState.Restored, recovery.State);
    }

    [TestMethod]
    public void SnapshotComparerIncludesRoutesDnsAndMetric()
    {
        var baseline = Snapshot("10.0.0.20");
        var equivalent = Snapshot("10.0.0.20");
        equivalent.Dns = ["1.1.1.1"];
        equivalent.DnsMode = AdapterDnsMode.Static;
        equivalent.InterfaceMetric = 10;
        equivalent.Routes = [new AdapterRouteSnapshot { DestinationPrefix = "0.0.0.0/0", NextHop = "10.0.0.1", RouteMetric = 5 }];
        baseline.Dns = ["1.1.1.1"];
        baseline.DnsMode = AdapterDnsMode.Static;
        baseline.InterfaceMetric = 10;
        baseline.Routes = [new AdapterRouteSnapshot { DestinationPrefix = "0.0.0.0/0", NextHop = "10.0.0.1", RouteMetric = 5 }];

        Assert.IsTrue(AdapterIpv4SnapshotComparer.Equivalent(baseline, equivalent));
        equivalent.Dns = ["8.8.8.8"];
        Assert.IsFalse(AdapterIpv4SnapshotComparer.Equivalent(baseline, equivalent));
    }

    [TestMethod]
    public void AppliedDhcpHostSnapshotMustMatchExactlyTheToolOwnedAddressAndMetric()
    {
        var snapshot = Snapshot("10.0.0.20");
        snapshot.AutomaticMetric = false;
        snapshot.InterfaceMetric = NetworkAdapterService.DhcpHostAdapterMetric;

        Assert.IsTrue(DhcpSessionRestoreCoordinator.MatchesAppliedHostConfiguration(snapshot, "10.0.0.20", 24));
        snapshot.Addresses.Add(new AdapterIpv4AddressSnapshot { IpAddress = "10.0.0.21", PrefixLength = 24 });
        Assert.IsFalse(DhcpSessionRestoreCoordinator.MatchesAppliedHostConfiguration(snapshot, "10.0.0.20", 24));
    }

    private static DhcpSessionRecovery PendingRecovery(NetworkAdapterInfo adapter, AdapterIpv4Snapshot original, AdapterIpv4Snapshot? expected)
    {
        var recovery = new DhcpSessionRecovery(new AdapterConfigBackup
        {
            AdapterId = adapter.Id,
            InterfaceIndex = adapter.InterfaceIndex,
            AdapterName = adapter.Name,
            AdapterMac = adapter.MacAddress
        }, original);
        recovery.MarkModificationPending();
        if (expected != null) recovery.SetExpectedSessionSnapshot(expected);
        return recovery;
    }

    private static NetworkAdapterInfo Adapter(string id, string index, string name) => new()
    {
        Id = id,
        InterfaceIndex = index,
        Name = name,
        MacAddress = "00-11-22-33-44-55"
    };

    private static AdapterIpv4Snapshot Snapshot(string ip) => new()
    {
        DhcpEnabled = false,
        IpAddress = ip,
        PrefixLength = 24,
        Addresses = [new AdapterIpv4AddressSnapshot { IpAddress = ip, PrefixLength = 24 }],
        DnsMode = AdapterDnsMode.Automatic,
        AdapterEnabled = true,
        AutomaticMetric = true
    };
}
