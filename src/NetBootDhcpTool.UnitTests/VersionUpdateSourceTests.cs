using System.Collections.Concurrent;
using System.Net;
using System.Text;
using NetBootDhcpTool.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class VersionUpdateSourceTests
{
    private const string GiteeArchiveUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/attach_files/123456";
    private const string GithubArchiveUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.14/NetBootDhcpTool-v1.0.14.7z";
    private const string GiteeManifestUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/attach_files/654321";

    [TestMethod]
    public async Task UpdateCheckMeasuresBothMirrorsAndPrefersTheFasterOne()
    {
        var requests = new ConcurrentQueue<HttpRequestMessage>();
        var progress = new InlineProgress<UpdateSourceSpeed>();
        var packagePrefix = Enumerable.Range(0, 64 * 1024).Select(index => (byte)(index % 251)).ToArray();
        using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            requests.Enqueue(CloneRequest(request));
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return Json("{\"id\":123,\"tag_name\":\"v1.0.14\",\"prerelease\":false}");
            if (path.EndsWith("/attach_files", StringComparison.Ordinal))
                return Json($"[{{\"name\":\"latest.json\",\"browser_download_url\":\"{GiteeManifestUrl}\"}}]");
            if (request.RequestUri.AbsoluteUri == GiteeManifestUrl)
                return Json(Manifest("1.0.14"));
            if (request.Headers.Range?.Ranges.SingleOrDefault() is not { From: 0, To: 65535 })
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            if (request.RequestUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DelayedReadStream(packagePrefix, TimeSpan.FromMilliseconds(180))) };
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packagePrefix) };
        }));
        using var service = new VersionUpdateService(client);

        var result = await service.CheckAsync(new Version(1, 0, 13), speedProgress: progress);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsTrue(result.IsNewVersion);
        Assert.AreEqual(2, result.DownloadSpeeds.Count);
        Assert.AreEqual(GiteeArchiveUrl, result.DownloadUrl);
        Assert.AreEqual(GiteeArchiveUrl, result.DownloadUrls[0]);
        Assert.IsTrue(result.DownloadSpeeds.All(source => source.BytesPerSecond is > 0));
        Assert.IsTrue(result.DownloadSpeeds.Single(source => source.Url == result.DownloadUrl).BytesPerSecond
            > result.DownloadSpeeds.Single(source => source.Url == GithubArchiveUrl).BytesPerSecond);
        Assert.IsTrue(progress.Items.Count >= 4, "Both probing and per-source completion progress should be reported.");
        Assert.IsTrue(requests.Where(request => request.Headers.Range != null).All(request =>
            request.Headers.Range!.Ranges.Single().From == 0 && request.Headers.Range.Ranges.Single().To == 65535));
        Assert.AreEqual(2, requests.Count(request => request.Headers.Range != null));
    }

    [TestMethod]
    public async Task LatestVersionSkipsDownloadSpeedProbes()
    {
        var requests = new ConcurrentQueue<HttpRequestMessage>();
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            requests.Enqueue(CloneRequest(request));
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"id\":123,\"tag_name\":\"v1.0.13\",\"prerelease\":false}"));
            if (path.EndsWith("/attach_files", StringComparison.Ordinal))
                return Task.FromResult(Json($"[{{\"name\":\"latest.json\",\"browser_download_url\":\"{GiteeManifestUrl}\"}}]"));
            return Task.FromResult(Json(Manifest("1.0.13")));
        }));
        using var service = new VersionUpdateService(client);

        var result = await service.CheckAsync(new Version(1, 0, 13));

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsFalse(result.IsNewVersion);
        Assert.AreEqual(0, result.DownloadSpeeds.Count);
        Assert.AreEqual(3, requests.Count);
        Assert.AreEqual(0, requests.Count(request => request.Headers.Range != null));
    }

    [TestMethod]
    public async Task GitHubManifestIsUsedWhenGiteeLatestApiIsUnavailable()
    {
        var requests = new ConcurrentQueue<string>();
        const string githubManifest = """
            {
              "version": "1.0.13",
              "archiveName": "NetBootDhcpTool-v1.0.13.7z",
              "archiveSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "downloadUrl": "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.13/NetBootDhcpTool-v1.0.13.7z",
              "releasePageUrl": "https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v1.0.13"
            }
            """;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var requestUri = request.RequestUri!.AbsoluteUri;
            requests.Enqueue(requestUri);
            return Task.FromResult(requestUri == VersionUpdateService.GiteeLatestReleaseApiUrl
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(githubManifest));
        }));
        using var service = new VersionUpdateService(client);

        var result = await service.CheckAsync(new Version(1, 0, 13));

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsFalse(result.IsNewVersion);
        Assert.AreEqual(VersionUpdateService.DefaultManifestUrl, requests.Last());
        Assert.AreEqual(2, requests.Count);
    }

    private static string Manifest(string version) => $$"""
        {
          "version": "{{version}}",
          "releasedAt": "2026-09-26T00:00:00Z",
          "archiveName": "NetBootDhcpTool-v1.0.14.7z",
          "archiveSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "downloadUrl": "{{GiteeArchiveUrl}}",
          "downloadMirrors": ["{{GithubArchiveUrl}}"],
          "releasePageUrl": "https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/v1.0.14",
          "changes": ["Gitee distribution"]
        }
        """;

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Headers.Range != null) clone.Headers.Range = request.Headers.Range;
        return clone;
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public ConcurrentQueue<T> Items { get; } = new();
        public void Report(T value) => Items.Enqueue(value);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class DelayedReadStream(byte[] data, TimeSpan delay) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);
        private bool _delayApplied;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delayApplied)
            {
                _delayApplied = true;
                await Task.Delay(delay, cancellationToken);
            }
            return await _inner.ReadAsync(buffer, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
