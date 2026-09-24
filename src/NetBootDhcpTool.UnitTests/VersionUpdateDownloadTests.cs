using System.Net;
using System.Security.Cryptography;
using NetBootDhcpTool.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class VersionUpdateDownloadTests
{
    [TestMethod]
    public async Task DownloadWritesVerifiesClosesHandlesAndReplacesExistingTarget()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "update.7z");
            await File.WriteAllTextAsync(destination, "old package");
            var package = Enumerable.Range(0, 128 * 1024).Select(x => (byte)(x % 251)).ToArray();
            var handler = new DelegateHandler((request, _) =>
            {
                Assert.IsTrue(request.Headers.UserAgent.Any(x => x.Product?.Name == "NetBootDhcpTool-UpdateDownload"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
            });
            using var client = new HttpClient(handler);
            using var service = new VersionUpdateService(client);

            var result = await service.DownloadAsync(UpdateFor(package), destination);

            Assert.AreEqual(Path.GetFullPath(destination), result.FilePath);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(), result.Sha256);
            CollectionAssert.AreEqual(package, await File.ReadAllBytesAsync(destination));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.download").Length);
            using var exclusive = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void WindowsCannotMoveHashFileWhileReadHandleOmitsDeleteSharing()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("This sharing-mode regression is specific to Windows file replacement.");
        var root = CreateTempDirectory();
        try
        {
            var temp = Path.Combine(root, "package.download");
            var destination = Path.Combine(root, "package.7z");
            File.WriteAllBytes(temp, [1, 2, 3]);
            using (var hashInput = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.ThrowsExactly<IOException>(() => File.Move(temp, destination, overwrite: true),
                    "The old download flow kept its SHA-256 input open without delete sharing while moving the same file.");
            }

            File.Move(temp, destination, overwrite: true);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChecksumMismatchKeepsExistingTargetAndRemovesOwnedTemporaryFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "update.7z");
            await File.WriteAllTextAsync(destination, "keep me");
            var bytes = new byte[] { 1, 2, 3, 4 };
            using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            using var service = new VersionUpdateService(client);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadAsync(UpdateFor(bytes, checksum: new string('0', 64)), destination));

            Assert.AreEqual("keep me", await File.ReadAllTextAsync(destination));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.download").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task HttpAndReadFailuresKeepExistingTargetAndCanRetry()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "update.7z");
            await File.WriteAllTextAsync(destination, "keep me");
            var package = new byte[] { 7, 8, 9, 10, 11 };
            var requestNumber = 0;
            using var client = new HttpClient(new DelegateHandler((_, _) =>
            {
                requestNumber++;
                if (requestNumber == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                if (requestNumber == 2)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailureAfterPrefixStream(package)) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
            }));
            using var service = new VersionUpdateService(client);

            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => service.DownloadAsync(UpdateFor(package), destination));
            Assert.AreEqual("keep me", await File.ReadAllTextAsync(destination));
            await Assert.ThrowsExactlyAsync<IOException>(() => service.DownloadAsync(UpdateFor(package), destination));
            Assert.AreEqual("keep me", await File.ReadAllTextAsync(destination));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.download").Length);

            await service.DownloadAsync(UpdateFor(package), destination);
            CollectionAssert.AreEqual(package, await File.ReadAllBytesAsync(destination));
            Assert.AreEqual(3, requestNumber);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CancellationAfterPartialWriteKeepsOldTargetAndRemovesTemporaryFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "update.7z");
            await File.WriteAllTextAsync(destination, "keep me");
            var blockingStream = new BlockingReadStream([1, 2, 3, 4]);
            using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(blockingStream) })));
            using var service = new VersionUpdateService(client);
            using var cancellation = new CancellationTokenSource();

            var download = service.DownloadAsync(UpdateFor([1, 2, 3, 4, 5]), destination, ct: cancellation.Token);
            await blockingStream.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => download);

            Assert.AreEqual("keep me", await File.ReadAllTextAsync(destination));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.download").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LockedDestinationRetainsOriginalAndRetrySucceedsAfterRelease()
    {
        var root = CreateTempDirectory();
        try
        {
            var destination = Path.Combine(root, "update.7z");
            await File.WriteAllTextAsync(destination, "old package");
            var package = new byte[] { 20, 21, 22 };
            using var client = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) })));
            using var service = new VersionUpdateService(client);

            var locked = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Exception? replacementFailure = null;
            try
            {
                await service.DownloadAsync(UpdateFor(package), destination);
            }
            catch (Exception ex)
            {
                replacementFailure = ex;
            }
            finally
            {
                await locked.DisposeAsync();
            }

            Assert.IsTrue(replacementFailure is IOException or UnauthorizedAccessException,
                "Windows should report the locked destination as a retryable file replacement failure.");
            Assert.AreEqual("old package", await File.ReadAllTextAsync(destination));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.download").Length);

            await service.DownloadAsync(UpdateFor(package), destination);
            CollectionAssert.AreEqual(package, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConcurrentDownloadIsRejectedWithoutSharingTemporaryPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var firstDestination = Path.Combine(root, "first.7z");
            var secondDestination = Path.Combine(root, "second.7z");
            await File.WriteAllTextAsync(firstDestination, "keep first");
            await File.WriteAllTextAsync(secondDestination, "keep second");
            var blockingStream = new BlockingReadStream([1, 2, 3, 4]);
            var requests = 0;
            using var client = new HttpClient(new DelegateHandler((_, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(blockingStream) });
            }));
            using var service = new VersionUpdateService(client);
            using var cancellation = new CancellationTokenSource();

            var first = service.DownloadAsync(UpdateFor([1, 2, 3, 4, 5]), firstDestination, ct: cancellation.Token);
            await blockingStream.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DownloadAsync(UpdateFor([6]), secondDestination));
            Assert.AreEqual(1, requests);
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => first);

            Assert.AreEqual("keep first", await File.ReadAllTextAsync(firstDestination));
            Assert.AreEqual("keep second", await File.ReadAllTextAsync(secondDestination));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.download").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InvalidUrlAndHashAreRejectedBeforeSendingAndInjectedClientRemainsOwnedByCaller()
    {
        var root = CreateTempDirectory();
        try
        {
            var requests = 0;
            using var client = new HttpClient(new DelegateHandler((_, _) =>
            {
                requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
            }));
            var service = new VersionUpdateService(client);
            var destination = Path.Combine(root, "update.7z");

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadAsync(UpdateFor([], url: "https://example.com/releases/file.7z"), destination));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadAsync(UpdateFor([], checksum: "not-a-hash"), destination));
            Assert.AreEqual(0, requests);

            service.Dispose();
            using var response = await client.GetAsync("https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.0/test.7z");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static UpdateCheckResult UpdateFor(byte[] bytes, string? checksum = null, string? url = null) => new()
    {
        Succeeded = true,
        DownloadUrl = url ?? "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.0/test.7z",
        ArchiveSha256 = checksum ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "netboot-update-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FailureAfterPrefixStream(byte[] prefix) : Stream
    {
        private bool _sentPrefix;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sentPrefix) throw new IOException("Synthetic response stream failure.");
            _sentPrefix = true;
            var count = Math.Min(prefix.Length, buffer.Length);
            prefix.AsMemory(0, count).CopyTo(buffer);
            return ValueTask.FromResult(count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream(byte[] prefix) : Stream
    {
        private bool _sentPrefix;
        public TaskCompletionSource SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentPrefix)
            {
                _sentPrefix = true;
                var count = Math.Min(prefix.Length, buffer.Length);
                prefix.AsMemory(0, count).CopyTo(buffer);
                return count;
            }
            SecondReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
