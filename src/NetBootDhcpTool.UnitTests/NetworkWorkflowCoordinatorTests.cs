using NetBootDhcpTool.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class NetworkWorkflowCoordinatorTests
{
    [TestMethod]
    public async Task AllowsOnlyOneOwnerAndCompletesOnlyAfterThatOwnerReleases()
    {
        var coordinator = new NetworkWorkflowCoordinator();
        var owner = coordinator.TryAcquire("manual-ip-scan");
        Assert.IsNotNull(owner);
        Assert.IsTrue(coordinator.IsActive);
        Assert.IsNull(coordinator.TryAcquire("dhcp-start"));
        Assert.IsFalse(owner.Completion.IsCompleted);

        owner.Dispose();

        await owner.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(coordinator.IsActive);
        using var next = coordinator.TryAcquire("route-apply");
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public void CancellationBelongsOnlyToTheLeaseBeingCancelled()
    {
        var coordinator = new NetworkWorkflowCoordinator();
        var first = coordinator.TryAcquire("restart-adapter");
        Assert.IsNotNull(first);
        using var cancellationSignal = CancellationTokenSource.CreateLinkedTokenSource(first.Token);

        first.Cancel();
        Assert.IsTrue(first.Token.IsCancellationRequested);
        Assert.ThrowsExactly<OperationCanceledException>(() => cancellationSignal.Token.ThrowIfCancellationRequested());
        first.Dispose();

        using var second = coordinator.TryAcquire("mac-change");
        Assert.IsNotNull(second);
        Assert.IsFalse(second.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void StaleOrDuplicateOwnerCannotReleaseANewerWorkflow()
    {
        var coordinator = new NetworkWorkflowCoordinator();
        var first = coordinator.TryAcquire("route-clear");
        Assert.IsNotNull(first);
        first.Dispose();
        var second = coordinator.TryAcquire("adapter-restore");
        Assert.IsNotNull(second);

        first.Dispose();

        Assert.IsTrue(coordinator.IsActive);
        Assert.IsNull(coordinator.TryAcquire("dhcp-start"));
        second.Dispose();
        Assert.IsFalse(coordinator.IsActive);
    }

    [TestMethod]
    public async Task ConcurrentCallersNeverOwnMoreThanOneWorkflow()
    {
        var coordinator = new NetworkWorkflowCoordinator();
        using var start = new ManualResetEventSlim(false);
        var active = 0;
        var maxActive = 0;
        var entered = 0;
        const int callerCount = 8;
        var tasks = Enumerable.Range(0, callerCount).Select(index => Task.Run(() =>
        {
            start.Wait();
            using var lease = coordinator.TryAcquire("parallel-" + index);
            if (lease is null) return;
            var current = Interlocked.Increment(ref active);
            Interlocked.Increment(ref entered);
            RecordMaximum(ref maxActive, current);
            Assert.IsNull(coordinator.TryAcquire("nested-" + index));
            Thread.Sleep(30);
            Interlocked.Decrement(ref active);
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        Assert.IsTrue(entered > 0, "At least one simultaneous caller should enter the workflow.");
        Assert.AreEqual(1, maxActive, "No two callers may own network workflows at the same time.");
        Assert.AreEqual(0, active, "No workflow owner should remain after all callers finish.");
        Assert.IsFalse(coordinator.IsActive);
    }

    [TestMethod]
    public async Task CancellationKeepsGateUntilIndependentCompensationCompletes()
    {
        var coordinator = new NetworkWorkflowCoordinator();
        var lease = coordinator.TryAcquire("partial-network-change");
        Assert.IsNotNull(lease);
        var compensationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCompensation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, lease.Token);
            }
            catch (OperationCanceledException)
            {
                compensationStarted.TrySetResult();
                // Compensation deliberately does not use the canceled workflow token.
                await finishCompensation.Task;
            }
            finally
            {
                lease.Dispose();
            }
        });

        lease.Cancel();
        await compensationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(coordinator.IsActive, "The network gate stays owned while compensation is running.");
        Assert.IsFalse(lease.Completion.IsCompleted, "Close must continue waiting for compensation.");
        Assert.IsNull(coordinator.TryAcquire("second-network-change"));

        finishCompensation.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(1));
        await lease.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(coordinator.IsActive);
        using var next = coordinator.TryAcquire("after-compensation");
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public void FailurePathReleasesOnlyItsOwnWorkflow()
    {
        var coordinator = new NetworkWorkflowCoordinator();
        var failed = coordinator.TryAcquire("controlled-failure");
        Assert.IsNotNull(failed);

        try
        {
            throw new InvalidOperationException("controlled network failure");
        }
        catch (InvalidOperationException)
        {
            failed.Dispose();
        }

        Assert.IsTrue(failed.Completion.IsCompleted);
        Assert.IsFalse(coordinator.IsActive);
        using var next = coordinator.TryAcquire("after-failure");
        Assert.IsNotNull(next);
    }

    private static void RecordMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }
}
