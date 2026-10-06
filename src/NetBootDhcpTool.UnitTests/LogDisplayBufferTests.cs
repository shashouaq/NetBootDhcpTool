using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class LogDisplayBufferTests
{
    [TestMethod]
    public void BurstRetainsNewestRowsInOrderAndDrainsInFourBoundedBatches()
    {
        var buffer = new LogDisplayBuffer();
        for (var i = 0; i < 1_000; i++) buffer.Enqueue($"row={i} 中文");
        Assert.AreEqual(500, buffer.PendingCount);
        Assert.AreEqual(500L, buffer.OmittedCount);
        var rows = new List<string>();
        var sizes = new List<int>();
        while (buffer.PendingCount > 0)
        {
            var batch = buffer.Drain();
            sizes.Add(batch.Count);
            rows.AddRange(batch);
        }
        CollectionAssert.AreEqual(new[] { 128, 128, 128, 116 }, sizes);
        CollectionAssert.AreEqual(Enumerable.Range(500, 500).Select(i => $"row={i} 中文").ToArray(), rows);
        Assert.AreEqual(0, buffer.Drain().Count);
    }

    [TestMethod]
    public void ClearResetsPendingAndBothOmissionSourcesButAcceptsNewRows()
    {
        var buffer = new LogDisplayBuffer();
        for (var i = 0; i < 501; i++) buffer.Enqueue(i.ToString());
        buffer.RecordVisibleOmission();
        Assert.AreEqual(2L, buffer.OmittedCount);
        buffer.Clear();
        Assert.AreEqual(0, buffer.PendingCount);
        Assert.AreEqual(0L, buffer.OmittedCount);
        buffer.Enqueue("after clear");
        CollectionAssert.AreEqual(new[] { "after clear" }, buffer.Drain().ToArray());
    }

    [TestMethod]
    public void CloseRejectsLateRowsAndAllowsOnlyForcedFinalDrain()
    {
        var buffer = new LogDisplayBuffer();
        for (var i = 0; i < 200; i++) buffer.Enqueue(i.ToString());
        buffer.Close();
        buffer.Close();
        buffer.Enqueue("late");
        Assert.AreEqual(0, buffer.Drain().Count);
        Assert.AreEqual(200, buffer.PendingCount);
        CollectionAssert.AreEqual(Enumerable.Range(0, 200).Select(i => i.ToString()).ToArray(), buffer.Drain(force: true).ToArray());
        buffer.Clear();
        buffer.Enqueue("still closed");
        Assert.AreEqual(0, buffer.PendingCount);
    }

    [TestMethod]
    public async Task ConcurrentProducersAndConsumerAccountForEveryRowWithoutDuplicates()
    {
        var buffer = new LogDisplayBuffer();
        var rows = new ConcurrentQueue<string>();
        var producers = Task.Run(() => Parallel.For(0, 10_000, i => buffer.Enqueue(i.ToString())));
        var consumer = Task.Run(async () =>
        {
            do
            {
                foreach (var row in buffer.Drain()) rows.Enqueue(row);
                await Task.Yield();
            } while (!producers.IsCompleted);
        });
        await Task.WhenAll(producers, consumer);
        buffer.Close();
        foreach (var row in buffer.Drain(force: true)) rows.Enqueue(row);
        Assert.AreEqual(10_000L, rows.Count + buffer.OmittedCount);
        Assert.AreEqual(rows.Count, rows.Distinct().Count());
        Assert.AreEqual(0, buffer.PendingCount);
    }
}
