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
    public async Task DnsAndHttpStartTogetherAndCallerCancellationStopsBoth()
    {
        var dnsStarted = NewSignal();
        var httpStarted = NewSignal();
        var dnsStopped = NewSignal();
        var httpStopped = NewSignal();
        var probe = new NetworkScanTargetProbe(
            (_, _, _) => Task.FromResult((true, 3L)),
            async (_, cancellationToken) =>
            {
                dnsStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return "never";
                }
                finally { dnsStopped.TrySetResult(); }
            },
            async (_, _, cancellationToken) =>
            {
                httpStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return (true, true);
                }
                finally { httpStopped.TrySetResult(); }
            },
            TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();

        var task = probe.ProbeAsync(IPAddress.Loopback, 1000, 1000, cancellation.Token);
        await Task.WhenAll(dnsStarted.Task, httpStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await AssertCancellationAsync(task);
        await Task.WhenAll(dnsStopped.Task, httpStopped.Task).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task ReachabilityDetailsStartDnsAndWebTogetherAndReturnBothResults()
    {
        var dnsStarted = NewSignal();
        var webStarted = NewSignal();
        var release = NewSignal();
        var probe = new NetworkScanTargetProbe(
            (_, _, _) => Task.FromResult((true, 1L)),
            async (_, cancellationToken) =>
            {
                dnsStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return "device.example.test";
            },
            async (_, _, cancellationToken) =>
            {
                webStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return (true, false);
            },
            TimeSpan.FromSeconds(1));

        var task = probe.ProbeReachableDetailsAsync(IPAddress.Loopback, 1000, CancellationToken.None);
        await Task.WhenAll(dnsStarted.Task, webStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        var details = await task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual("device.example.test", details.Hostname);
        Assert.IsTrue(details.HttpOk);
        Assert.IsFalse(details.HttpsOk);
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
        await using var service = new HttpProbeService(() => new BlockingHandler(requestStarted));
        using var cancellation = new CancellationTokenSource();

        var task = service.ProbeAsync("127.0.0.1", 5000, cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await AssertCancellationAsync(task);
    }

    [TestMethod]
    public async Task HttpAndHttpsRequestsStartConcurrently()
    {
        var handler = new ConcurrentStartHandler();
        await using var service = new HttpProbeService(() => handler);

        var task = service.ProbeAsync("127.0.0.1", 2000);
        await handler.BothRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEquivalent(new[] { "http", "https" }, handler.Schemes.ToArray());
        handler.Release.TrySetResult();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(result.http && result.https);
    }

    [TestMethod]
    public async Task SharedHandlerLimitsAllCallersToSixtyFourRequests()
    {
        var factoryCalls = 0;
        var handler = new ConcurrencyLimitHandler();
        await using var service = new HttpProbeService(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            return handler;
        });

        var probes = Enumerable.Range(0, 40)
            .Select(index => service.ProbeAsync($"192.0.2.{index + 1}", 5000))
            .ToArray();
        await handler.SixtyFourRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(64, handler.MaximumConcurrency);
        handler.Release.TrySetResult();
        var results = await Task.WhenAll(probes).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual(1, factoryCalls, "The handler and connection pool belong to one service lifetime.");
        Assert.AreEqual(80, handler.RequestCount);
        Assert.IsTrue(handler.MaximumConcurrency <= HttpProbeService.DefaultMaximumConcurrentRequests);
        Assert.IsTrue(results.All(result => result.http && result.https));
    }

    [TestMethod]
    public async Task OneProtocolTimeoutDoesNotPoisonTheNextProbe()
    {
        var handler = new OneTimeoutHandler();
        await using var service = new HttpProbeService(() => handler);

        var first = await service.ProbeAsync("127.0.0.1", 100).WaitAsync(TimeSpan.FromSeconds(2));
        var second = await service.ProbeAsync("127.0.0.1", 1000).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(first.http);
        Assert.IsFalse(first.https);
        Assert.IsTrue(second.http && second.https);
        Assert.AreEqual(2, handler.HttpRequests);
        Assert.AreEqual(2, handler.HttpsRequests);
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

    private sealed class ConcurrentStartHandler : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _schemes = [];
        private int _started;
        public TaskCompletionSource BothRequestsStarted { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public IReadOnlyCollection<string> Schemes => _schemes.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _schemes.Add(request.RequestUri!.Scheme);
            if (Interlocked.Increment(ref _started) == 2) BothRequestsStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ConcurrencyLimitHandler : HttpMessageHandler
    {
        private int _active;
        private int _maximumConcurrency;
        private int _requestCount;
        public TaskCompletionSource SixtyFourRequestsStarted { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maximumConcurrency, active);
            if (active == HttpProbeService.DefaultMaximumConcurrentRequests) SixtyFourRequestsStarted.TrySetResult();
            Interlocked.Increment(ref _requestCount);
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        private static void UpdateMaximum(ref int maximum, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref maximum)) && Interlocked.CompareExchange(ref maximum, value, current) != current) { }
        }
    }

    private sealed class OneTimeoutHandler : HttpMessageHandler
    {
        private int _httpRequests;
        private int _httpsRequests;
        public int HttpRequests => Volatile.Read(ref _httpRequests);
        public int HttpsRequests => Volatile.Read(ref _httpsRequests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Scheme == "http")
            {
                Interlocked.Increment(ref _httpRequests);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (Interlocked.Increment(ref _httpsRequests) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
