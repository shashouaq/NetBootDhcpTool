using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class CoalescedRefreshCoordinatorTests
{
    [TestMethod]
    public async Task EventBurstDuringOneReadProducesOnlyOneLatestFollowUp()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = new ConcurrentQueue<string>();
        var applied = new ConcurrentQueue<string>();
        var currentSelection = "99";
        var coordinator = new CoalescedRefreshCoordinator<string, RefreshValue>(async (request, _) =>
        {
            reads.Enqueue(request);
            if (request == "first")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            return new RefreshValue(request);
        }, (request, value) =>
        {
            if (request == currentSelection && value.Value == request) applied.Enqueue(request);
            return Task.CompletedTask;
        });
        try
        {
            coordinator.Request("first");
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 0; i < 100; i++) coordinator.Request(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            releaseFirst.TrySetResult();
            await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
            CollectionAssert.AreEqual(new[] { "first", "99" }, reads.ToArray());
            CollectionAssert.AreEqual(new[] { "99" }, applied.ToArray());
        }
        finally
        {
            releaseFirst.TrySetResult();
            await coordinator.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DisposalCancelsAndObservesAnInFlightReadAndRejectsLaterRequests()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var coordinator = new CoalescedRefreshCoordinator<string, RefreshValue>(async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            return new RefreshValue("unexpected");
        }, (_, _) =>
        {
            Interlocked.Increment(ref commits);
            return Task.CompletedTask;
        });
        coordinator.Request("current");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        coordinator.Request("after-close");
        Assert.IsTrue(canceled.Task.IsCompleted);
        Assert.AreEqual(0, commits);
        Assert.IsFalse(coordinator.IsRunning);
    }

    private sealed record RefreshValue(string Value);
}
