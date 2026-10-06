using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class AdapterAddressTests
{
    private static AdapterAddressDraft Draft(string ip = "198.51.100.10") => new() { AdapterId = "adapter-a", IpAddress = ip, SubnetMask = "255.255.255.0" };
    [TestMethod]
    public void RejectInvalidMaskNetworkAndDuplicate()
    {
        foreach (var ip in new[] { "198.51.100.0", "198.51.100.255", "127.0.0.1", "224.0.0.1" })
            Assert.ThrowsExactly<InvalidDataException>(() => AdapterAddressPlan.Validate([Draft(ip)]));
        var bad = Draft(); bad.SubnetMask = "255.0.255.0";
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterAddressPlan.Validate([bad]));
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterAddressPlan.Validate([Draft(), Draft()]));
        var point = Draft(); point.SubnetMask = "255.255.255.254";
        Assert.AreEqual(1, AdapterAddressPlan.Validate([point]).Count);
    }
    [TestMethod]
    public async Task AddAndRemoveRetainBusinessGatewayDnsMetricAndExternalAddress()
    {
        var backend = new FakeBackend(); var baseline = backend.Snapshot();
        var savedBeforeWrite = false;
        var manager = new AdapterAddressManager(backend, new(), j => { savedBeforeWrite |= j.Addresses.Any(x => x.State == "Intent"); });
        backend.BeforeAdd = () => Assert.IsTrue(savedBeforeWrite);
        await manager.ApplyAsync([Draft()], CancellationToken.None);
        Assert.IsTrue(AdapterAddressManager.PreservesConfiguration(baseline, backend.Snapshot(), manager.Journal.Addresses));
        backend.AddObserved("203.0.113.10");
        await manager.RemoveAsync(manager.Journal.Addresses.Single(), CancellationToken.None);
        Assert.IsTrue(backend.Rows.Any(x => x.IpAddress == "203.0.113.10"));
        Assert.IsTrue(backend.Rows.Any(x => x.IpAddress == "192.0.2.10"));
        Assert.AreEqual(0, manager.Journal.Addresses.Count);
        Assert.AreEqual("192.0.2.1", backend.State.Routes.Single().NextHop);
        Assert.AreEqual("192.0.2.53", backend.State.Dns.Single());
        Assert.AreEqual(25, backend.State.InterfaceMetric);
    }
    [TestMethod]
    public async Task ExistingMatchingAddressNeverAcquiresOwnership()
    {
        var backend = new FakeBackend(); backend.AddObserved(Draft().IpAddress, persistent: true);
        var manager = new AdapterAddressManager(backend, new(), _ => { });
        await manager.ApplyAsync([Draft()], CancellationToken.None);
        Assert.AreEqual(0, manager.Journal.Addresses.Count); Assert.AreEqual(0, backend.AddCount);
    }
    [TestMethod]
    public async Task ChangedPrefixOrPersistencePreventsDelete()
    {
        var backend = new FakeBackend(); var manager = new AdapterAddressManager(backend, new(), _ => { });
        await manager.ApplyAsync([Draft()], CancellationToken.None);
        backend.Rows.Last().PrefixLength = 25;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.RemoveAsync(manager.Journal.Addresses.Single(), CancellationToken.None));
        Assert.AreEqual(0, backend.RemoveCount);
        backend.Rows.Last().PrefixLength = 24; backend.Rows.Last().Persistent = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.RemoveAsync(manager.Journal.Addresses.Single(), CancellationToken.None));
        Assert.AreEqual(1, manager.Journal.Addresses.Count);
    }
    [TestMethod]
    public async Task PartialFailureCompensatesConfirmedWritesButRetainsUncertainIntent()
    {
        var backend = new FakeBackend { FailAddNumber = 2 }; var manager = new AdapterAddressManager(backend, new(), _ => { });
        await Assert.ThrowsExactlyAsync<IOException>(() => manager.ApplyAsync([Draft(), Draft("203.0.113.10")], CancellationToken.None));
        Assert.AreEqual(1, backend.RemoveCount);
        Assert.AreEqual("Intent", manager.Journal.Addresses.Single().State);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.RemoveAsync(manager.Journal.Addresses.Single(), CancellationToken.None));
        await manager.RemoveAsync(manager.Journal.Addresses.Single(), CancellationToken.None, true);
        Assert.AreEqual(0, manager.Journal.Addresses.Count);
    }
    [TestMethod]
    public async Task UncertainWriteThatAddedAnAddressIsNeverAutomaticallyReplayedOrDeleted()
    {
        var backend = new FakeBackend { FailAfterAdd = true }; var manager = new AdapterAddressManager(backend, new(), _ => { });
        await Assert.ThrowsExactlyAsync<IOException>(() => manager.ApplyAsync([Draft()], CancellationToken.None));
        Assert.AreEqual(1, backend.AddCount); Assert.AreEqual(0, backend.RemoveCount);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.ApplyAsync([Draft()], CancellationToken.None));
        await manager.RemoveAsync(manager.Journal.Addresses.Single(), CancellationToken.None, true);
        Assert.AreEqual(1, backend.RemoveCount);
    }
    [TestMethod]
    public async Task PersistenceFailureBlocksMutation()
    {
        var backend = new FakeBackend(); var manager = new AdapterAddressManager(backend, new(), _ => throw new IOException("journal"));
        await Assert.ThrowsExactlyAsync<IOException>(() => manager.ApplyAsync([Draft()], CancellationToken.None));
        Assert.AreEqual(0, backend.AddCount);
    }
    [TestMethod]
    public async Task RoutePreflightRejectsBeforeJournalOrCoexistenceMutation()
    {
        var backend = new FakeBackend { RejectPreflight = true }; backend.State.DhcpEnabled = true;
        var commits = 0;
        var manager = new AdapterAddressManager(backend, new(), _ => commits++);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.ApplyAsync([Draft()], CancellationToken.None));
        Assert.AreEqual(0, commits); Assert.AreEqual(0, backend.AddCount); Assert.IsFalse(backend.Coexistence);
    }
    [TestMethod]
    public async Task CancellationCompensatesConfirmedAddressUnderIndependentToken()
    {
        var backend = new FakeBackend(); using var cancel = new CancellationTokenSource();
        backend.BeforeAdd = () => { if (backend.AddCount == 1) cancel.Cancel(); };
        var manager = new AdapterAddressManager(backend, new(), _ => { });
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => manager.ApplyAsync([Draft(), Draft("203.0.113.10")], cancel.Token));
        Assert.AreEqual(1, backend.RemoveCount); Assert.AreEqual("Intent", manager.Journal.Addresses.Single().State);
        Assert.IsFalse(backend.Rows.Any(x => x.IpAddress == Draft().IpAddress));
    }
    [TestMethod]
    public void NullAndMultipleAdapterProfileAddressesCannotBecomeExecutable()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterAddressPlan.Validate([null!]));
        var other = Draft("203.0.113.10"); other.AdapterId = "adapter-b";
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterAddressPlan.Validate([Draft(), other]));
        var draft = Draft(); draft.IpAddress = null!;
        Assert.ThrowsExactly<InvalidDataException>(() => AdapterAddressPlan.Validate([draft]));
        Assert.ThrowsExactly<InvalidDataException>(() => ProfileStore.Clone(new() { Addresses = [null!] }));
    }
    [TestMethod]
    public async Task DhcpCoexistenceIsReadBeforeWriteAndRestoredAfterLastOwnedAddress()
    {
        var backend = new FakeBackend(); backend.State.DhcpEnabled = true; backend.Rows.Clear();
        backend.AddObserved("192.0.2.10", origin: "Dhcp");
        var baseline = backend.Snapshot(); var manager = new AdapterAddressManager(backend, new(), _ => { });
        await manager.ApplyAsync([Draft(), Draft("203.0.113.10")], CancellationToken.None);
        Assert.IsTrue(backend.Coexistence); Assert.IsTrue(backend.State.DhcpEnabled);
        await manager.RemoveAsync(manager.Journal.Addresses.First(), CancellationToken.None);
        Assert.IsTrue(backend.Coexistence);
        await manager.RemoveAsync(manager.Journal.Addresses.First(), CancellationToken.None);
        Assert.IsFalse(backend.Coexistence); Assert.IsTrue(AdapterIpv4SnapshotComparer.Equivalent(baseline, backend.Snapshot()));
        Assert.AreEqual(0, manager.Journal.Coexistence.Count);
    }
    [TestMethod]
    public async Task ExistingCoexistenceIsNotOwnedAndUnknownStateBlocksAddressWrite()
    {
        var backend = new FakeBackend { Coexistence = true }; backend.State.DhcpEnabled = true;
        var manager = new AdapterAddressManager(backend, new(), _ => { });
        await manager.ApplyAsync([Draft()], CancellationToken.None);
        Assert.AreEqual(0, manager.Journal.Coexistence.Count);
        await manager.RemoveAsync(manager.Journal.Addresses.Single(), CancellationToken.None);
        Assert.IsTrue(backend.Coexistence);
        backend = new FakeBackend { UnknownCoexistence = true }; backend.State.DhcpEnabled = true;
        manager = new AdapterAddressManager(backend, new(), _ => { });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.ApplyAsync([Draft()], CancellationToken.None));
        Assert.AreEqual(0, backend.AddCount);
    }
    [TestMethod]
    public async Task ExternalStaticAddressPreventsDisablingCoexistence()
    {
        var backend = new FakeBackend(); backend.State.DhcpEnabled = true; backend.Rows.Clear(); backend.AddObserved("192.0.2.10", origin: "Dhcp");
        var manager = new AdapterAddressManager(backend, new(), _ => { }); await manager.ApplyAsync([Draft()], CancellationToken.None);
        backend.AddObserved("203.0.113.11");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.RemoveAsync(manager.Journal.Addresses.Single(), CancellationToken.None));
        Assert.IsTrue(backend.Coexistence); Assert.AreEqual(0, manager.Journal.Addresses.Count); Assert.AreEqual(1, manager.Journal.Coexistence.Count);
    }
    [TestMethod]
    public async Task StableIdentityLossPreventsCleanupAndUnknownDnsPreventsApply()
    {
        var backend = new FakeBackend(); var manager = new AdapterAddressManager(backend, new(), _ => { });
        await manager.ApplyAsync([Draft()], CancellationToken.None); backend.IdentityMissing = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.RemoveAsync(manager.Journal.Addresses.Single(), CancellationToken.None));
        Assert.AreEqual(0, backend.RemoveCount);
        backend = new FakeBackend(); backend.State.DnsMode = AdapterDnsMode.Unknown; manager = new(backend, new(), _ => { });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.ApplyAsync([Draft()], CancellationToken.None)); Assert.AreEqual(0, backend.AddCount);
    }
    [TestMethod]
    public void InvalidJournalAndProfileMigrationAreBounded()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => new AdapterAddressManager(new FakeBackend(), new() { SchemaVersion = 2 }, _ => { }));
        var old = JsonSerializer.Deserialize<NetworkProfile>("{\"Name\":\"old\",\"ManualIp\":\"192.0.2.10\"}")!;
        Assert.AreEqual(0, old.Addresses.Count); Assert.AreEqual("192.0.2.10", old.ManualIp);
        var profile = new NetworkProfile { Addresses = [Draft()], DhcpPreserveAddresses = true };
        var copy = ProfileStore.Clone(profile); Assert.AreEqual(0, ProfileComparer.Compare(profile, copy).Count);
        copy.Addresses[0].SubnetMask = "255.255.255.128";
        Assert.IsTrue(ProfileComparer.Compare(profile, copy).Any(x => x.Field == "Addresses"));
        copy.Addresses[0].SubnetMask = "255.0.255.0";
        Assert.AreEqual(ProfileSectionStatus.Invalid, ProfileValidation.Assess(copy).OverallStatus);
        Assert.AreEqual("255.255.255.0", profile.Addresses[0].SubnetMask);
    }

    private sealed class FakeBackend : IAdapterAddressBackend
    {
        public List<AddressObservation> Rows { get; } = [];
        public AdapterIpv4Snapshot State { get; } = new() { DnsMode = AdapterDnsMode.Static, Dns = ["192.0.2.53"], AdapterEnabled = true,
            InterfaceMetric = 25, Routes = [new() { DestinationPrefix = "0.0.0.0/0", NextHop = "192.0.2.1", RouteMetric = 10, Protocol = "NetMgmt", PolicyStore = "PersistentStore" }] };
        public bool Coexistence, UnknownCoexistence, IdentityMissing, FailAfterAdd, RejectPreflight;
        public int AddCount, RemoveCount, FailAddNumber;
        public Action? BeforeAdd;
        public FakeBackend() => AddObserved("192.0.2.10", persistent: true);
        public void AddObserved(string ip, bool persistent = false, string origin = "Manual") => Rows.Add(new() { IpAddress = ip, PrefixLength = 24, AddressState = "Preferred", PrefixOrigin = origin, Persistent = persistent });
        public NetworkAdapterInfo Resolve(string id) => IdentityMissing || id != "adapter-a" ? throw new InvalidOperationException("identity") : new() { Id = id, Name = "Ethernet", InterfaceIndex = "15", Status = "Up" };
        public AdapterIpv4Snapshot Snapshot()
        {
            var state = JsonSerializer.Deserialize<AdapterIpv4Snapshot>(JsonSerializer.Serialize(State))!;
            state.Addresses = Rows.Select(x => new AdapterIpv4AddressSnapshot { IpAddress = x.IpAddress, PrefixLength = x.PrefixLength, SkipAsSource = x.SkipAsSource }).ToList();
            state.IpAddress = state.Addresses.FirstOrDefault()?.IpAddress ?? "";
            return state;
        }
        public Task<AdapterIpv4Snapshot> CaptureAsync(NetworkAdapterInfo adapter, CancellationToken ct) => Task.FromResult(Snapshot());
        public Task<List<AddressObservation>> ReadAsync(NetworkAdapterInfo adapter, CancellationToken ct) => Task.FromResult(Rows.ToList());
        public Task<bool> ReadCoexistenceAsync(NetworkAdapterInfo adapter, CancellationToken ct) => UnknownCoexistence ? throw new InvalidOperationException("unknown") : Task.FromResult(Coexistence);
        public Task SetCoexistenceAsync(NetworkAdapterInfo adapter, bool enabled, CancellationToken ct) { Coexistence = enabled; return Task.CompletedTask; }
        public Task ValidateAppendAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct) => RejectPreflight ? throw new InvalidOperationException("route conflict") : Task.CompletedTask;
        public Task AddAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct)
        {
            BeforeAdd?.Invoke(); AddCount++; ct.ThrowIfCancellationRequested();
            if (FailAddNumber == AddCount) throw new IOException("add");
            AddObserved(address.IpAddress);
            if (FailAfterAdd) throw new IOException("uncertain");
            return Task.CompletedTask;
        }
        public Task RemoveAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct) { RemoveCount++; Rows.RemoveAll(x => x.IpAddress == address.IpAddress); return Task.CompletedTask; }
    }
}
