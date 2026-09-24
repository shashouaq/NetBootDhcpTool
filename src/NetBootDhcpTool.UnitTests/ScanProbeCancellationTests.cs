using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class ScanProbeCancellationTests
{
    [TestMethod]
    public async Task PingCancellationStopsProbeBeforeDnsOrHttp()
    {
        var pingStarted = NewSignal();
        var dnsCalls = 0;
        var httpCalls = 0;
        var probe = new NetworkScanTargetProbe(
            async (_, _, cancellationToken) =>
            {
                pingStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return (true, 1L);
            },
            (_, _) => { Interlocked.Increment(ref dnsCalls); return Task.FromResult("host"); },
            (_, _, _) => { Interlocked.Increment(ref httpCalls); return Task.FromResult((true, true)); },
            TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();

        var task = probe.ProbeAsync(IPAddress.Loopback, 1000, 1000, cancellation.Token);
        await pingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await AssertCancellationAsync(task);
        Assert.AreEqual(0, dnsCalls);
        Assert.AreEqual(0, httpCalls);
    }

    [TestMethod]
    public async Task DnsCancellationStopsProbeBeforeHttp()
    {
        var dnsStarted = NewSignal();
        var httpCalls = 0;
        var probe = new NetworkScanTargetProbe(
            (_, _, _) => Task.FromResult((true, 3L)),
            async (_, cancellationToken) =>
            {
                dnsStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "never";
            },
            (_, _, _) => { Interlocked.Increment(ref httpCalls); return Task.FromResult((true, true)); },
            TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        var task = probe.ProbeAsync(IPAddress.Loopback, 1000, 1000, cancellation.Token);
        await dnsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await AssertCancellationAsync(task);
        Assert.AreEqual(0, httpCalls);
    }

    [TestMethod]
    public async Task DnsTimeoutIsBoundedAndAllowsHttpProbeToFinish()
    {
        var httpCalls = 0;
        var probe = new NetworkScanTargetProbe(
            (_, _, _) => Task.FromResult((true, 7L)),
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "never";
            },
            (_, _, _) => { Interlocked.Increment(ref httpCalls); return Task.FromResult((true, false)); },
            TimeSpan.FromMilliseconds(25));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        var result = await probe.ProbeAsync(IPAddress.Loopback, 1000, 1000, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        timer.Stop();
        Assert.IsNotNull(result);
        Assert.AreEqual("", result.Hostname);
        Assert.IsTrue(result.HttpOk);
        Assert.IsFalse(result.HttpsOk);
        Assert.AreEqual(1, httpCalls);
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(1), $"DNS timeout did not stay bounded: {timer.Elapsed}.");
    }

    [TestMethod]
    public async Task HttpProbeCancellationIsPropagatedFromTheActualHttpClientCall()
    {
        var requestStarted = NewSignal();
        var service = new HttpProbeService(() => new BlockingHandler(requestStarted));
        using var cancellation = new CancellationTokenSource();

        var task = service.ProbeAsync("127.0.0.1", 5000, cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await AssertCancellationAsync(task);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Fail("The probe completed instead of propagating cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private static async Task AssertCancellationAsync(Task<(bool http, bool https)> task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Fail("The HTTP request completed instead of propagating cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private static async Task AssertCancellationAsync(Task<NetBootDhcpTool.Core.ScanResult?> task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Fail("The network probe completed instead of propagating cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private sealed class BlockingHandler(TaskCompletionSource requestStarted) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
