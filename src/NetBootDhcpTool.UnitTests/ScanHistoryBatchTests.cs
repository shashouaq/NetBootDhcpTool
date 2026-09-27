using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class ScanHistoryBatchTests
{
    [TestMethod]
    public void BoundedAccumulatorRetainsEveryAcceptedResultAndMonotonicProgress()
    {
        const int targetCount = 4096;
        var accumulator = new ScanProgressAccumulator(targetCount);
        Parallel.For(1, targetCount + 1, done => accumulator.Report((done, targetCount,
            new ScanResult { IpAddress = done.ToString(System.Globalization.CultureInfo.InvariantCulture) })));
        accumulator.Complete();
        accumulator.Report((targetCount, targetCount, new ScanResult { IpAddress = "too-late" }));

        var results = new List<ScanResult>(targetCount);
        var priorDone = 0;
        while (accumulator.TryTakeBatch(128, out var batch))
        {
            Assert.IsTrue(batch.Done >= priorDone, "Progress must never move backward.");
            Assert.AreEqual(targetCount, batch.Total);
            Assert.IsTrue(batch.Results.Count <= 128);
            priorDone = batch.Done;
            results.AddRange(batch.Results);
        }
        Assert.AreEqual(targetCount, priorDone);
        Assert.AreEqual(targetCount, results.Count);
        Assert.AreEqual(targetCount, results.Select(x => x.IpAddress).Distinct(StringComparer.Ordinal).Count());
        Assert.IsFalse(accumulator.TryTakeBatch(128, out _));
    }

    [TestMethod]
    public async Task SnapshotWriterCoalescesPendingSnapshotsAndNeverOverlapsWrites()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new ConcurrentQueue<int[]>();
        var active = 0;
        var maxActive = 0;
        var writer = new CoalescingSnapshotWriter<int>(async snapshot =>
        {
            var nowActive = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maxActive, nowActive);
            try
            {
                if (saved.IsEmpty)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
                saved.Enqueue(snapshot.ToArray());
            }
            finally { Interlocked.Decrement(ref active); }
        });
        try
        {
            var first = writer.SaveAsync([1]);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = new List<Task>();
            for (var latest = 2; latest <= 129; latest++)
                queued.Add(writer.SaveAsync(Enumerable.Range(1, latest).ToArray()));
            releaseFirst.TrySetResult();
            await Task.WhenAll(queued.Append(first)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(1, maxActive);
            Assert.AreEqual(2, saved.Count, "The writer should save the active snapshot and then only the newest pending snapshot.");
            CollectionAssert.AreEqual(Enumerable.Range(1, 129).ToArray(), saved.Last());
            Assert.IsNull(writer.LastFailedSnapshot);
        }
        finally
        {
            releaseFirst.TrySetResult();
            await writer.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SnapshotWriterRetainsFailureAndAllowsAnExplicitRetry()
    {
        var failuresRemaining = 1;
        var writer = new CoalescingSnapshotWriter<string>(snapshot =>
        {
            if (Interlocked.Exchange(ref failuresRemaining, 0) == 1) throw new IOException("synthetic save failure");
            return Task.CompletedTask;
        });
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => writer.SaveAsync(["scan row"]));
            Assert.AreEqual("scan row", writer.LastFailedSnapshot!.Single());
            await writer.SaveAsync(["scan row", "retry row"]);
            Assert.IsNull(writer.LastFailedSnapshot);
        }
        finally { await writer.DisposeAsync(); }
    }

    [TestMethod]
    public async Task ClearSnapshotSupersedesAnOlderPendingHistoryBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-history-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "history.json");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        var writer = new CoalescingSnapshotWriter<int>(async snapshot =>
        {
            var saveIndex = Interlocked.Increment(ref saves);
            if (saveIndex == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            JsonStore.Save(path, snapshot.ToList());
        });
        try
        {
            var first = writer.SaveAsync([1]);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var oldPendingBatch = writer.SaveAsync([1, 2, 3]);
            var clear = writer.SaveAsync([]);
            releaseFirst.TrySetResult();
            await Task.WhenAll(first, oldPendingBatch, clear).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.AreEqual(2, saves, "An active write may finish, but an older queued snapshot must be replaced by the clear snapshot.");
            var loaded = JsonStore.Load<List<int>>(path);
            Assert.IsTrue(loaded.HasData);
            Assert.AreEqual(0, loaded.Value!.Count);
        }
        finally
        {
            releaseFirst.TrySetResult();
            await writer.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
