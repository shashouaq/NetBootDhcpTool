using System.Collections.Concurrent;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class FileLoggerTests
{
    [TestMethod]
    public void ConcurrentWritesAreCompleteOrderedReadableAndDisposeReleasesTheFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-file-logger-" + Guid.NewGuid().ToString("N"));
        var prior = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");
        var notifications = new ConcurrentQueue<string>();
        FileLogger? logger = null;
        try
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", Path.Combine(root, "data"));
            logger = new FileLogger(new AppPaths(root));
            logger.LineWritten += _ => throw new InvalidOperationException("Observer failures must not break logging.");
            logger.LineWritten += notifications.Enqueue;
            Parallel.For(0, 10_000, i => logger.Info($"item={i} 中文"));
            logger.Error("multiline fixture", new InvalidOperationException("first detail\nsecond detail 中文"));

            var path = logger.SessionLogPath;
            using (var activeRead = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var activeText = new StreamReader(activeRead, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                Assert.AreEqual(10_000, activeText.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Count(line => line.Contains("[INFO] item=", StringComparison.Ordinal)),
                    "The support-package sharing mode must read every row while the logger still owns the writer handle.");

            logger.Dispose();
            logger.Dispose();
            var bytes = File.ReadAllBytes(path);
            CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble(), bytes.Take(Encoding.UTF8.GetPreamble().Length).ToArray());
            var fileRows = File.ReadLines(path).Where(x => x.Contains("[INFO] item=", StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(10_000, fileRows.Length);
            Assert.IsTrue(File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal)
                .Contains("first detail\nsecond detail 中文", StringComparison.Ordinal));
            Assert.AreEqual(10_001, notifications.Count);
            var observerRows = notifications.Where(x => x.Contains("[INFO] item=", StringComparison.Ordinal)).ToArray();
            CollectionAssert.AreEqual(fileRows, observerRows);
            Assert.AreEqual(10_000, observerRows.Select(ParseItemId).Distinct().Count());
            Assert.ThrowsExactly<ObjectDisposedException>(() => logger.Info("after dispose"));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            logger?.Dispose();
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", prior);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void WriteFailureDoesNotNotifyAsWrittenAndDisposeStillClosesTheStream()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-file-logger-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failedStream = new FailingWriteStream();
        FileLogger? logger = null;
        var notifications = 0;
        try
        {
            logger = new FileLogger(new AppPaths(root), _ => failedStream);
            logger.LineWritten += _ => Interlocked.Increment(ref notifications);
            failedStream.FailWrites();
            Assert.ThrowsExactly<IOException>(() => logger.Error("synthetic file write failure"));
            Assert.AreEqual(0, Volatile.Read(ref notifications));
            Assert.ThrowsExactly<IOException>(() => logger.Dispose());
            Assert.IsTrue(failedStream.IsDisposed, "Dispose must release the stream even when its flush fails.");
            logger.Dispose();
        }
        finally
        {
            try { logger?.Dispose(); } catch (IOException) { }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static int ParseItemId(string row)
    {
        var marker = row.IndexOf("[INFO] item=", StringComparison.Ordinal);
        var start = marker + "[INFO] item=".Length;
        var end = row.IndexOf(' ', start);
        return int.Parse(row[start..end]);
    }

    private sealed class FailingWriteStream : Stream
    {
        private bool _failWrites;
        public bool IsDisposed { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !IsDisposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
            if (_failWrites) throw new IOException("synthetic disk full");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public void FailWrites() => _failWrites = true;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_failWrites) throw new IOException("synthetic disk full");
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_failWrites) throw new IOException("synthetic disk full");
        }
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
