using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class LeaseProbeCoordinatorTests
{
    [TestMethod]
    public async Task ProbeStartedOnUiLikeSynchronizationContextDoesNotRequireThatContextToPump()
    {
        var originalContext = SynchronizationContext.Current;
        var probeStarted = NewSignal();
        var coordinator = new LeaseProbeCoordinator(
            (request, _) => { probeStarted.TrySetResult(); return Task.FromResult(Result(request)); },
            (_, _) => Task.CompletedTask);
        try
        {
            SynchronizationContext.SetSynchronizationContext(new BlockingSynchronizationContext());
            coordinator.Schedule(Request(1));
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
            await coordinator.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task OneHundredTwentyEightBindingsStayWithinEightActiveAndSixtyFourQueuedThenResumeOnNextRound()
    {
        var release = NewSignal();
        var startedEight = NewSignal();
        var drainedFirstRound = NewSignal();
        var drainedDeferredRound = NewSignal();
        var active = 0;
        var maximumActive = 0;
        var totalStarted = 0;
        var totalApplied = 0;
        await using var coordinator = new LeaseProbeCoordinator(
            async (request, token) =>
            {
                var count = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, count);
                var started = Interlocked.Increment(ref totalStarted);
                if (started == 8) startedEight.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(token);
                    return Result(request);
                }
                finally { Interlocked.Decrement(ref active); }
            },
            (_, _) =>
            {
                var count = Interlocked.Increment(ref totalApplied);
                if (count == 72) drainedFirstRound.TrySetResult();
                if (count == 128) drainedDeferredRound.TrySetResult();
                return Task.CompletedTask;
            });

        var requests = Enumerable.Range(1, 128).Select(index => Request(index)).ToArray();
        var outcomes = requests.Select(coordinator.Schedule).ToArray();
        await startedEight.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(8, coordinator.ActiveCount);
        Assert.AreEqual(64, coordinator.PendingCount);
        Assert.AreEqual(72, outcomes.Count(value => value is LeaseProbeScheduleResult.StartedOrQueued));
        Assert.AreEqual(56, outcomes.Count(value => value is LeaseProbeScheduleResult.QueueFull));
        Assert.IsTrue(maximumActive <= 8);

        release.TrySetResult();
        await drainedFirstRound.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.WaitForIdleAsync();
        Assert.AreEqual(0, coordinator.ActiveCount);
        Assert.AreEqual(0, coordinator.PendingCount);

        var deferredRequests = requests.Skip(72).ToArray();
        foreach (var request in deferredRequests) coordinator.Schedule(request);
        await drainedDeferredRound.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.WaitForIdleAsync();

        Assert.AreEqual(128, totalApplied);
        Assert.IsTrue(maximumActive <= 8);
    }

    [TestMethod]
    public async Task NewGenerationCancelsOldProbeAndOnlyAppliesCurrentIdentity()
    {
        var oldStarted = NewSignal();
        var oldStopped = NewSignal();
        var newStarted = NewSignal();
        var applied = new ConcurrentQueue<LeaseProbeResult>();
        await using var coordinator = new LeaseProbeCoordinator(
            async (request, token) =>
            {
                if (request.Identity.Generation == 1)
                {
                    oldStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return Result(request);
                    }
                    finally { oldStopped.TrySetResult(); }
                }

                newStarted.TrySetResult();
                return Result(request);
            },
            (result, _) => { applied.Enqueue(result); return Task.CompletedTask; });

        coordinator.Schedule(Request(1, generation: 1));
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Schedule(Request(1, generation: 2, includeWeb: true));
        await Task.WhenAll(oldStopped.Task, newStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.WaitForIdleAsync();

        Assert.AreEqual(1, applied.Count);
        Assert.AreEqual(2, applied.Single().Identity.Generation);
        Assert.IsTrue(applied.Single().HttpOk.HasValue, "The replacement event probe keeps its HTTP request.");
    }

    [TestMethod]
    public async Task TerminalInvalidationAndSessionStopCancelWorkAndRejectLateSchedules()
    {
        var activeStarted = NewSignal();
        var activeStopped = NewSignal();
        var appliedCount = 0;
        await using var coordinator = new LeaseProbeCoordinator(
            async (request, token) =>
            {
                activeStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Result(request);
                }
                finally { activeStopped.TrySetResult(); }
            },
            (_, _) => { Interlocked.Increment(ref appliedCount); return Task.CompletedTask; });

        var activeRequest = Request(1, generation: 1);
        coordinator.Schedule(activeRequest);
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Schedule(Request(1, generation: 2));
        coordinator.InvalidateBinding(activeRequest.Identity.BindingKey);
        await activeStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, coordinator.PendingCount, "Invalidating a binding also removes its queued replacement.");

        coordinator.Schedule(Request(3, sessionId: "session-2"));
        await coordinator.CancelSessionAsync("session-2");
        Assert.AreEqual(LeaseProbeScheduleResult.SessionCancelled,
            coordinator.Schedule(Request(4, sessionId: "session-2")));
        await coordinator.CancelSessionAsync("session-1");
        Assert.AreEqual(0, coordinator.ActiveCount);
        Assert.AreEqual(0, coordinator.PendingCount);
        Assert.AreEqual(0, appliedCount);
    }

    [TestMethod]
    public async Task DuplicateRequestsCoalesceAndProbeFailuresAreObserved()
    {
        var started = NewSignal();
        var failed = NewSignal();
        var failure = new ConcurrentQueue<(LeaseProbeIdentity Identity, Exception Error)>();
        var invocationCount = 0;
        await using var coordinator = new LeaseProbeCoordinator(
            async (request, token) =>
            {
                Interlocked.Increment(ref invocationCount);
                if (request.Identity.ClientKey == "client-2") throw new IOException("injected probe failure");
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Result(request);
            },
            (_, _) => Task.CompletedTask,
            (identity, error) => { failure.Enqueue((identity, error)); failed.TrySetResult(); });

        var request = Request(1);
        coordinator.Schedule(request);
        Assert.AreEqual(LeaseProbeScheduleResult.Coalesced, coordinator.Schedule(request));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Schedule(Request(2));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, invocationCount);
        await coordinator.CancelSessionAsync("session-1");
        Assert.AreEqual("client-2", failure.Single().Identity.ClientKey);
        StringAssert.Contains(failure.Single().Error.Message, "injected probe failure");
    }

    private static LeaseProbeRequest Request(int index, string sessionId = "session-1", long generation = 1, bool includeWeb = false) =>
        new(new LeaseProbeIdentity(sessionId, $"client-{index}", $"192.0.2.{index}", generation), includeWeb);

    private static LeaseProbeResult Result(LeaseProbeRequest request) =>
        new(request.Identity, 1, request.IncludeWeb ? true : null, request.IncludeWeb ? false : null);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref maximum)) && Interlocked.CompareExchange(ref maximum, value, current) != current) { }
    }

    private sealed class BlockingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }
}
