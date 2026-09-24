using System.Collections.Concurrent;
using System.Net;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class ScanRangePlanTests
{
    [TestMethod]
    [DataRow("10.1.2.3", "0.0.0.0")]
    [DataRow("10.1.2.3", "255.0.0.0")]
    public void VeryLargeRangesAreRejectedWithoutEnumeration(string local, string mask)
    {
        Assert.IsFalse(ScanRangePlan.TryCreate(local, mask, "", out var plan, out var error));
        Assert.IsNull(plan);
        Assert.AreEqual(ScanRangeError.TooManyTargets, error);
    }

    [TestMethod]
    public void NonemptyInvalidOrOutOfSubnetTargetNeverBecomesSubnetScan()
    {
        Assert.IsFalse(ScanRangePlan.TryCreate("192.168.10.2", "255.255.255.0", "not-an-ip", out _, out var invalid));
        Assert.AreEqual(ScanRangeError.InvalidTargetIp, invalid);

        Assert.IsFalse(ScanRangePlan.TryCreate("192.168.10.2", "255.255.255.0", "192.168.11.3", out _, out var outside));
        Assert.AreEqual(ScanRangeError.TargetOutsideSubnet, outside);
    }

    [TestMethod]
    public void NormalSubnetCountExcludesLocalNetworkAndBroadcast()
    {
        Assert.IsTrue(ScanRangePlan.TryCreate("192.168.1.10", "255.255.255.0", "", out var plan, out _));
        Assert.AreEqual(253, plan!.TargetCount);
        var first = plan.EnumerateTargets().First().ToString();
        var last = plan.EnumerateTargets().Last().ToString();
        Assert.AreEqual("192.168.1.1", first);
        Assert.AreEqual("192.168.1.254", last);
    }

    [TestMethod]
    public void Slash31HasOnePeerAndSlash32NeedsAnExplicitTarget()
    {
        Assert.IsTrue(ScanRangePlan.TryCreate("192.0.2.10", "255.255.255.254", "", out var p31, out _));
        Assert.AreEqual(1, p31!.TargetCount);
        Assert.AreEqual("192.0.2.11", p31.EnumerateTargets().Single().ToString());
        Assert.AreEqual(2, IpNetwork.Hosts(IPAddress.Parse("192.0.2.10"), IPAddress.Parse("255.255.255.254")).Count());

        Assert.IsFalse(ScanRangePlan.TryCreate("192.0.2.10", "255.255.255.255", "", out _, out var noPeer));
        Assert.AreEqual(ScanRangeError.NoSubnetTargets, noPeer);
        Assert.IsTrue(ScanRangePlan.TryCreate("192.0.2.10", "255.255.255.255", "192.0.2.10", out var p32, out _));
        Assert.AreEqual(1, p32!.TargetCount);
    }

    [TestMethod]
    public void RejectsNoncontiguousMasksAndNetworkOrBroadcastAddresses()
    {
        Assert.IsFalse(ScanRangePlan.TryCreate("10.0.0.5", "255.0.255.0", "", out _, out var maskError));
        Assert.AreEqual(ScanRangeError.InvalidSubnetMask, maskError);
        Assert.IsFalse(ScanRangePlan.TryCreate("192.168.1.0", "255.255.255.0", "", out _, out var localError));
        Assert.AreEqual(ScanRangeError.InvalidLocalHost, localError);
        Assert.IsFalse(ScanRangePlan.TryCreate("192.168.1.10", "255.255.255.0", "192.168.1.255", out _, out var targetError));
        Assert.AreEqual(ScanRangeError.TargetNotUsableHost, targetError);
    }

    [TestMethod]
    public void HostEnumerationIsLazyAndSupportsIpv4SlashZeroWithoutPreallocation()
    {
        var firstFew = IpNetwork.Hosts(IPAddress.Parse("10.1.2.3"), IPAddress.Parse("0.0.0.0")).Take(3).Select(x => x.ToString()).ToArray();
        CollectionAssert.AreEqual(new[] { "0.0.0.1", "0.0.0.2", "0.0.0.3" }, firstFew);
    }
}

[TestClass]
public sealed class PingScannerBoundsTests
{
    [TestMethod]
    public async Task ExactlyMaximumTargetsAreAcceptedAndOneMoreIsRejectedBeforeProbing()
    {
        var probe = new CountingProbe((ip, _, _, _) => Task.FromResult<ScanResult?>(null));
        var scanner = new PingScanner(new NullLogger(), probe);
        var targets = Targets(ScanRangePlan.MaxProbeTargets);
        var progress = new InlineProgress<(int done, int total, ScanResult? result)>();

        await scanner.ScanTargetsAsync(targets, 16, 100, 100, progress, CancellationToken.None);
        Assert.AreEqual(ScanRangePlan.MaxProbeTargets, probe.Started);

        var tooMany = Targets(ScanRangePlan.MaxProbeTargets + 1);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await scanner.ScanTargetsAsync(tooMany, 16, 100, 100, progress, CancellationToken.None));
        Assert.AreEqual(ScanRangePlan.MaxProbeTargets, probe.Started, "The rejected scan must not call its probe.");
    }

    [TestMethod]
    public async Task ProbeConcurrencyIsBoundedByRequestedDegree()
    {
        var active = 0;
        var maximumActive = 0;
        var probe = new CountingProbe(async (ip, _, _, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMax(ref maximumActive, current);
            try { await Task.Delay(5, ct); return null; }
            finally { Interlocked.Decrement(ref active); }
        });
        var scanner = new PingScanner(new NullLogger(), probe);

        await scanner.ScanTargetsAsync(Targets(64), 3, 100, 100,
            new InlineProgress<(int done, int total, ScanResult? result)>(), CancellationToken.None);

        Assert.AreEqual(64, probe.Started);
        Assert.AreEqual(3, maximumActive);
    }

    [TestMethod]
    public async Task TimedOutProbeDoesNotHidePartialResultsAndProgressUsesActualCount()
    {
        var reports = new ConcurrentBag<(int done, int total, ScanResult? result)>();
        var targets = Targets(4);
        var probe = new CountingProbe(async (ip, _, _, ct) =>
        {
            if (ip.Equals(targets[3])) throw new TimeoutException("injected probe timeout");
            await Task.Delay(1, ct);
            return ip.Equals(targets[1]) ? null : new ScanResult { IpAddress = ip.ToString(), PingOk = true };
        });
        var scanner = new PingScanner(new NullLogger(), probe);

        var results = await scanner.ScanTargetsAsync(targets, 2, 100, 100,
            new InlineProgress<(int done, int total, ScanResult? result)>(reports.Add), CancellationToken.None);

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual(4, probe.Started);
        Assert.AreEqual(4, reports.Count);
        Assert.IsTrue(reports.All(x => x.total == 4));
        Assert.AreEqual(4, reports.Max(x => x.done));
    }

    [TestMethod]
    public async Task CancellationStopsActiveProbesAndDoesNotQueueTheRemainingTargets()
    {
        using var cts = new CancellationTokenSource();
        var started = 0;
        var allWorkersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new CountingProbe(async (_, _, _, ct) =>
        {
            if (Interlocked.Increment(ref started) == 3) allWorkersStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
        var scanner = new PingScanner(new NullLogger(), probe);
        var scanTask = scanner.ScanTargetsAsync(Targets(ScanRangePlan.MaxProbeTargets), 3, 100, 100,
            new InlineProgress<(int done, int total, ScanResult? result)>(), cts.Token);

        await allWorkersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => scanTask);
        Assert.AreEqual(3, probe.Started, "No queued target should begin after cancellation.");
    }

    private static List<IPAddress> Targets(int count) => Enumerable.Range(1, count)
        .Select(offset => IpNetwork.FromUInt32(0x0A000000u + (uint)offset)).ToList();

    private static void UpdateMax(ref int target, int value)
    {
        int old;
        while (value > (old = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, old) != old) { }
    }

    private sealed class CountingProbe(Func<IPAddress, int, int, CancellationToken, Task<ScanResult?>> action) : IScanTargetProbe
    {
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public Task<ScanResult?> ProbeAsync(IPAddress ip, int pingTimeoutMs, int httpTimeoutMs, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            return action(ip, pingTimeoutMs, httpTimeoutMs, cancellationToken);
        }
    }

    private sealed class InlineProgress<T>(Action<T>? report = null) : IProgress<T>
    {
        public void Report(T value) => report?.Invoke(value);
    }

    private sealed class NullLogger : ILogger
    {
        public event Action<string>? LineWritten { add { } remove { } }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
