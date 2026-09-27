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
    private const string GithubArchiveV18Url = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.18.7z";
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
            if (request.RequestUri.AbsoluteUri == VersionUpdateService.DefaultManifestUrl)
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
        Assert.AreEqual("https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/v1.0.14", result.ReleasePageUrl,
            "An equal version keeps the first valid release source.");
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
        Assert.AreEqual(5, requests.Count);
        Assert.AreEqual(0, requests.Count(request => request.Headers.Range != null));
    }

    [TestMethod]
    public async Task GiteeCanonicalReleaseDownloadUrlsSupportIndependentDiscovery()
    {
        const string giteeManifestUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/latest.json";
        const string giteeArchiveUrl = "https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.18.7z";
        const string githubArchiveUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.18.7z";
        var requests = new ConcurrentQueue<string>();
        var manifest = $$"""
            {
              "version": "1.0.18",
              "archiveName": "NetBootDhcpTool-v1.0.18.7z",
              "archiveSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "downloadUrl": "{{giteeArchiveUrl}}",
              "downloadMirrors": ["{{githubArchiveUrl}}"],
              "releasePageUrl": "https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/v1.0.18"
            }
            """;
        var githubManifest = $$"""
            {
              "version": "1.0.18",
              "archiveName": "NetBootDhcpTool-v1.0.18.7z",
              "archiveSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "downloadUrl": "{{githubArchiveUrl}}",
              "releasePageUrl": "https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v1.0.18"
            }
            """;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            requests.Enqueue(uri.AbsoluteUri);
            if (uri.AbsoluteUri == VersionUpdateService.GiteeLatestReleaseApiUrl)
                return Task.FromResult(Json("{\"id\":123,\"tag_name\":\"v1.0.18\",\"prerelease\":false}"));
            if (uri.AbsolutePath.EndsWith("/attach_files", StringComparison.Ordinal))
                return Task.FromResult(Json($"[{{\"name\":\"latest.json\",\"browser_download_url\":\"{giteeManifestUrl}\"}}]"));
            if (uri.AbsoluteUri == giteeManifestUrl) return Task.FromResult(Json(manifest));
            if (uri.AbsoluteUri == VersionUpdateService.DefaultManifestUrl) return Task.FromResult(Json(githubManifest));
            if (request.Headers.Range?.Ranges.SingleOrDefault() is { From: 0, To: 65535 })
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Enumerable.Repeat((byte)42, 65536).ToArray()) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        using var service = new VersionUpdateService(client, manifestUrl: VersionUpdateService.GiteeLatestReleaseApiUrl);

        var result = await service.CheckAsync(new Version(1, 0, 17));

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual(new Version(1, 0, 18), result.LatestVersion);
        Assert.IsTrue(result.IsNewVersion);
        CollectionAssert.AreEquivalent(new[] { giteeArchiveUrl, githubArchiveUrl }, result.DownloadUrls.ToArray());
        Assert.IsTrue(requests.Contains(giteeManifestUrl), "The Gitee API's canonical latest.json download URL should be fetched directly.");
        Assert.IsFalse(requests.Contains(VersionUpdateService.DefaultManifestUrl), "A Gitee-only check must succeed without the GitHub fallback manifest.");
    }

    [TestMethod]
    [DataRow("https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.17/NetBootDhcpTool-v1.0.18.7z")]
    [DataRow("https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.17.7z")]
    [DataRow("https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.17/NetBootDhcpTool-v1.0.17.7z")]
    [DataRow("https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.18.7z/extra")]
    [DataRow("https://gitee.com.evil.example/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.18.7z")]
    [DataRow("https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.18.7z?mirror=elsewhere")]
    [DataRow("https://attacker@gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.18.7z")]
    [DataRow("https://gitee.com:8443/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.18.7z")]
    [DataRow("https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/NetBootDhcpTool-v1.0.18.7z#other")]
    [DataRow("https://gitee.com/joel20230302/NetBootDhcpTool/releases/download/v1.0.18/%2e%2e/NetBootDhcpTool-v1.0.18.7z")]
    public void EvaluateRejectsNonCanonicalOrMismatchedGiteeReleasePaths(string giteeUrl)
    {
        var json = $$"""
            {
              "version": "1.0.18",
              "archiveName": "NetBootDhcpTool-v1.0.18.7z",
              "archiveSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "downloadUrl": "{{giteeUrl}}",
              "downloadMirrors": ["{{GithubArchiveV18Url}}"],
              "releasePageUrl": "https://gitee.com/joel20230302/NetBootDhcpTool/releases/tag/v1.0.18"
            }
            """;

        var result = VersionUpdateService.Evaluate(json, new Version(1, 0, 17));

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "not an approved");
    }

    [TestMethod]
    public async Task NewerGitHubManifestWinsOverStaleGiteeLatestRelease()
    {
        var requests = new ConcurrentQueue<string>();
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Enqueue(url);
            if (url == VersionUpdateService.GiteeLatestReleaseApiUrl)
                return Task.FromResult(Json("{\"id\":123,\"tag_name\":\"v1.0.13\",\"prerelease\":false}"));
            if (request.RequestUri.AbsolutePath.EndsWith("/attach_files", StringComparison.Ordinal))
                return Task.FromResult(Json($"[{{\"name\":\"latest.json\",\"browser_download_url\":\"{GiteeManifestUrl}\"}}]"));
            if (url == GiteeManifestUrl) return Task.FromResult(Json(Manifest("1.0.13")));
            if (url == VersionUpdateService.DefaultManifestUrl) return Task.FromResult(Json(GithubManifest()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }));
        using var service = new VersionUpdateService(client);

        var result = await service.CheckAsync(new Version(1, 0, 13));

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual(new Version(1, 0, 14), result.LatestVersion);
        Assert.IsTrue(result.IsNewVersion);
        Assert.AreEqual(GithubArchiveUrl, result.DownloadUrl);
        Assert.AreEqual("https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v1.0.14", result.ReleasePageUrl);
        Assert.IsTrue(requests.Contains(VersionUpdateService.DefaultManifestUrl));
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
        Assert.AreEqual(VersionUpdateService.DefaultManifestUrl + ".sig", requests.Last());
        Assert.AreEqual(3, requests.Count);
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

    private static string GithubManifest() => $$"""
        {
          "version": "1.0.14",
          "archiveName": "NetBootDhcpTool-v1.0.14.7z",
          "archiveSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "downloadUrl": "{{GithubArchiveUrl}}",
          "releasePageUrl": "https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v1.0.14"
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
