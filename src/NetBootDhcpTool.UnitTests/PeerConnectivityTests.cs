using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Dhcp;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class PeerConnectivityTests
{
    [TestMethod]
    public void FreshWebResponseCanProveReachabilityWithoutFabricatingPingLatency()
    {
        var peer = new PeerConnectivity();
        var at = DateTime.Now;
        peer.ObserveReachability(-1, true, at);
        Assert.AreEqual("Online", peer.State);
        peer.ObserveReachability(-1, false, at.AddSeconds(2));
        Assert.AreEqual("Offline", peer.State);
        peer.Stop(at.AddSeconds(3));
        peer.ObserveReachability(-1, true, at.AddSeconds(4));
        Assert.AreEqual("Stopped", peer.State);
    }

    [TestMethod]
    public void FreshObservationsTrackTransitionsSeparatelyFromEveryCheck()
    {
        var peer = new PeerConnectivity();
        var at = new DateTime(2026, 10, 6, 10, 0, 0);
        Assert.AreEqual("Pending", peer.State);
        Assert.IsNull(peer.LastCheckedAt);
        peer.Observe(12, at);
        Assert.AreEqual("Online", peer.State);
        peer.Observe(15, at.AddSeconds(2));
        Assert.AreEqual(at, peer.ChangedAt);
        Assert.AreEqual(at.AddSeconds(2), peer.LastCheckedAt);
        peer.Observe(-1, at.AddSeconds(4));
        Assert.AreEqual("Offline", peer.State);
        Assert.AreEqual(at.AddSeconds(4), peer.ChangedAt);
        peer.Observe(0, at.AddSeconds(6));
        Assert.AreEqual("Online", peer.State);
        Assert.AreEqual(at.AddSeconds(6), peer.ChangedAt);
    }

    [TestMethod]
    public void StoppedPeerRejectsLateObservationUntilExplicitReset()
    {
        var peer = new PeerConnectivity();
        var at = DateTime.Now;
        peer.Observe(1, at);
        peer.Stop(at.AddSeconds(1));
        peer.Observe(1, at.AddSeconds(2));
        Assert.AreEqual("Stopped", peer.State);
        Assert.AreEqual(at, peer.LastCheckedAt);
        Assert.AreEqual(at.AddSeconds(1), peer.ChangedAt);
        peer.Reset();
        Assert.AreEqual("Pending", peer.State);
        Assert.IsNull(peer.LastCheckedAt);
        peer.Observe(-1, at.AddSeconds(3));
        Assert.AreEqual("Offline", peer.State);
    }

    [TestMethod]
    public void LeaseProtocolAndHistoryDoNotInheritCurrentConnectivity()
    {
        var lease = new DhcpLease { Status = "Assigned" };
        lease.Connectivity.Observe(1, DateTime.Now);
        lease.Connectivity.Observe(-1, DateTime.Now);
        Assert.AreEqual("Assigned", lease.Status);
        Assert.AreEqual("Offline", lease.Connectivity.State);
        lease.IsCurrentSession = false;
        lease.Connectivity.Observe(1, DateTime.Now);
        Assert.AreEqual("Stopped", lease.Connectivity.State);
        var reused = new DhcpLease { IpAddress = lease.IpAddress };
        Assert.AreEqual("Pending", reused.Connectivity.State);
        Assert.IsNull(reused.Connectivity.LastCheckedAt);
        reused.IsActiveLease = false;
        Assert.AreEqual("Stopped", reused.Connectivity.State);
    }
}
