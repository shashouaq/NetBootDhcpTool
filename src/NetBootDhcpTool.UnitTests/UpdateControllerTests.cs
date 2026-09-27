using System.Net;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class UpdateControllerTests
{
    private const string ArchiveUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/attach_files/123456";

    [TestMethod]
    public async Task CancelingDownloadUpdatesTypedStateAndAllowsRetryAfterFailure()
    {
        var bytes = new byte[] { 1, 3, 5, 7, 9 };
        var requestCount = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }));
        var controller = new UpdateController(new VersionUpdateService(client), new Version(1, 0, 1));
        var result = new UpdateCheckResult
        {
            CurrentVersion = new Version(1, 0, 1),
            LatestVersion = new Version(1, 0, 2),
            Succeeded = true,
            IsNewVersion = true,
            DownloadUrl = ArchiveUrl,
            DownloadUrls = [ArchiveUrl],
            ArchiveSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ArchiveName = "package.7z"
        };
        var root = Path.Combine(Path.GetTempPath(), "netboot-update-controller-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => controller.DownloadAsync(result, Path.Combine(root, "package.7z")));
            Assert.IsTrue(controller.State.DownloadError?.Contains("503", StringComparison.Ordinal) == true);
            Assert.IsFalse(controller.State.DownloadInProgress);

            var downloaded = await controller.DownloadAsync(result, Path.Combine(root, "package.7z"));
            Assert.AreEqual(Path.Combine(root, "package.7z"), downloaded.FilePath);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(downloaded.FilePath));
            Assert.IsNull(controller.State.DownloadError);
            Assert.IsNotNull(controller.State.DownloadedResult);
        }
        finally
        {
            await controller.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DisposalCancelsAndObservesAnInFlightUpdateCheck()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DelegateHandler(async (_, ct) =>
        {
            requestStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { requestCanceled.TrySetResult(); throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var controller = new UpdateController(new VersionUpdateService(client), new Version(1, 0, 1));
        var check = controller.CheckAsync();
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => check);
        Assert.IsTrue(requestCanceled.Task.IsCompleted);
    }

    [TestMethod]
    public async Task CancelDownloadIsObservedAndDoesNotCommitLateProgress()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DelegateHandler(async (_, ct) =>
        {
            requestStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { requestCanceled.TrySetResult(); throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var controller = new UpdateController(new VersionUpdateService(client), new Version(1, 0, 1));
        var result = new UpdateCheckResult
        {
            CurrentVersion = new Version(1, 0, 1),
            LatestVersion = new Version(1, 0, 2),
            Succeeded = true,
            IsNewVersion = true,
            DownloadUrl = ArchiveUrl,
            DownloadUrls = [ArchiveUrl],
            ArchiveSha256 = new string('a', 64)
        };
        try
        {
            var download = controller.DownloadAsync(result, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cancelled.7z"));
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            controller.CancelDownload();
            Assert.IsTrue(controller.State.DownloadCancellationRequested);
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => download);
            Assert.IsTrue(requestCanceled.Task.IsCompleted);
            Assert.IsTrue(controller.State.DownloadCanceled);
            Assert.IsFalse(controller.State.DownloadInProgress);
        }
        finally { await controller.DisposeAsync(); }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
