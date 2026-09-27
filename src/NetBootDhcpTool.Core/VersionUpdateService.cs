using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetBootDhcpTool.Core;

public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string ReleasedAt { get; set; } = "";
    public string ArchiveName { get; set; } = "";
    public string ArchiveSha256 { get; set; } = "";
    public string? DownloadUrl { get; set; }
    public string? ReleasePageUrl { get; set; }
    public string? MinimumSupportedVersion { get; set; }
    public string? ReleaseNotes { get; set; }
    public List<string> Changes { get; set; } = [];
    public List<string> DownloadMirrors { get; set; } = [];
}

public sealed class UpdateCheckResult
{
    public Version CurrentVersion { get; init; } = new(0, 0);
    public Version? LatestVersion { get; init; }
    public bool Succeeded { get; init; }
    public bool IsNewVersion { get; init; }
    public string DownloadUrl { get; init; } = "";
    public IReadOnlyList<string> DownloadUrls { get; init; } = [];
    public string ReleasePageUrl { get; init; } = "";
    public string ArchiveName { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public IReadOnlyList<UpdateSourceSpeed> DownloadSpeeds { get; init; } = [];
    public string ReleaseNotes { get; init; } = "";
    public IReadOnlyList<string> Changes { get; init; } = [];
    public string Error { get; init; } = "";
}

public sealed class UpdateDownloadProgress
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan LowSpeedDuration { get; init; }
    public string DownloadUrl { get; init; } = "";
    public bool IsSourceFallback { get; init; }
}

public sealed record UpdateSourceSpeed(string Url, double? BytesPerSecond, bool IsChecking = false, string Error = "")
{
    public string SourceName => Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
        ? "Gitee"
        : "GitHub";
}

public sealed class UpdateDownloadResult
{
    public string FilePath { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
}

public sealed class VersionUpdateService : IDisposable
{
    public const string DefaultManifestUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json";
    public const string GiteeLatestReleaseApiUrl = "https://gitee.com/api/v5/repos/joel20230302/NetBootDhcpTool/releases/latest";
    private const string GiteeRepositoryApiPrefix = "https://gitee.com/api/v5/repos/joel20230302/NetBootDhcpTool";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<Uri> _manifestUris;
    private readonly TimeSpan _downloadIdleTimeout;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public VersionUpdateService(HttpClient? httpClient = null, string? manifestUrl = null, TimeSpan? downloadIdleTimeout = null)
    {
        _downloadIdleTimeout = downloadIdleTimeout ?? TimeSpan.FromSeconds(30);
        if (_downloadIdleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadIdleTimeout));
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = httpClient == null;
        _manifestUris = string.IsNullOrWhiteSpace(manifestUrl)
            ? [new Uri(GiteeLatestReleaseApiUrl, UriKind.Absolute), new Uri(DefaultManifestUrl, UriKind.Absolute)]
            : [new Uri(manifestUrl, UriKind.Absolute)];
    }

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken ct = default, IProgress<UpdateSourceSpeed>? speedProgress = null)
    {
        var errors = new List<string>();
        UpdateCheckResult? candidate = null;
        foreach (var manifestUri in _manifestUris)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                var json = IsGiteeLatestReleaseEndpoint(manifestUri)
                    ? await FetchLatestGiteeManifestAsync(manifestUri, linkedCancellation.Token)
                    : await GetJsonAsync(manifestUri, linkedCancellation.Token);
                var result = Evaluate(json, currentVersion);
                if (result.Succeeded)
                {
                    if (candidate?.LatestVersion is null || result.LatestVersion > candidate.LatestVersion)
                        candidate = result;
                    continue;
                }
                errors.Add($"{manifestUri.Host}: {result.Error}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{manifestUri.Host}: {ex.Message}");
            }
        }

        if (candidate is null)
            return new UpdateCheckResult { CurrentVersion = currentVersion, Succeeded = false, Error = string.Join("; ", errors) };
        if (!candidate.IsNewVersion) return candidate;

        var measured = await MeasureDownloadSourcesAsync(candidate.DownloadUrls, speedProgress, ct);
        var fastest = measured.Where(x => x.BytesPerSecond is > 0)
            .OrderByDescending(x => x.BytesPerSecond)
            .Select(x => x.Url)
            .Concat(measured.Where(x => x.BytesPerSecond is null or <= 0).Select(x => x.Url))
            .ToArray();
        var prioritizedUrls = fastest.Length > 0 ? fastest : candidate.DownloadUrls;
        return CopyWithDownloadSources(candidate, prioritizedUrls, measured);
    }

    private async Task<IReadOnlyList<UpdateSourceSpeed>> MeasureDownloadSourcesAsync(IReadOnlyList<string> urls, IProgress<UpdateSourceSpeed>? progress, CancellationToken ct)
    {
        foreach (var url in urls) progress?.Report(new UpdateSourceSpeed(url, null, IsChecking: true));
        return await Task.WhenAll(urls.Select(async url =>
        {
            var source = await MeasureDownloadSourceAsync(url, ct);
            progress?.Report(source);
            return source;
        }));
    }

    private async Task<UpdateSourceSpeed> MeasureDownloadSourceAsync(string url, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("NetBootDhcpTool-UpdateSpeedCheck/1.0");
            request.Headers.Range = new RangeHeaderValue(0, 64 * 1024 - 1);
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCancellation.Token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(linkedCancellation.Token);
            var buffer = new byte[16 * 1024];
            var remaining = 64 * 1024;
            var received = 0;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), linkedCancellation.Token);
                if (read == 0) break;
                received += read;
                remaining -= read;
            }
            stopwatch.Stop();
            if (received == 0) return new UpdateSourceSpeed(url, null, Error: "No data received");
            var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            return new UpdateSourceSpeed(url, received / seconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UpdateSourceSpeed(url, null, Error: "Probe timed out");
        }
        catch (Exception ex)
        {
            var error = ex is HttpRequestException { StatusCode: { } statusCode }
                ? $"HTTP {(int)statusCode}"
                : ex.GetType().Name;
            return new UpdateSourceSpeed(url, null, Error: error);
        }
    }

    private static UpdateCheckResult CopyWithDownloadSources(UpdateCheckResult source, IReadOnlyList<string> urls, IReadOnlyList<UpdateSourceSpeed> speeds) => new()
    {
        CurrentVersion = source.CurrentVersion,
        LatestVersion = source.LatestVersion,
        Succeeded = source.Succeeded,
        IsNewVersion = source.IsNewVersion,
        DownloadUrl = urls.FirstOrDefault() ?? source.DownloadUrl,
        DownloadUrls = urls,
        ReleasePageUrl = source.ReleasePageUrl,
        ArchiveName = source.ArchiveName,
        ArchiveSha256 = source.ArchiveSha256,
        DownloadSpeeds = speeds,
        ReleaseNotes = source.ReleaseNotes,
        Changes = source.Changes,
        Error = source.Error
    };

    public static UpdateCheckResult Evaluate(string json, Version currentVersion)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonStore.Options)
                ?? throw new InvalidDataException("Update manifest is empty");
            if (!TryParseVersion(manifest.Version, out var latest)) throw new InvalidDataException("Update manifest version is invalid");
            var downloadUrls = GetSafeDownloadUrls(manifest, latest);
            if (!IsSafeReleasePageUrl(manifest.ReleasePageUrl)) throw new InvalidDataException("Update release page URL is not an approved HTTPS release URL");
            if (!IsSha256(manifest.ArchiveSha256)) throw new InvalidDataException("Update archive checksum is invalid");

            return new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = latest,
                Succeeded = true,
                IsNewVersion = latest > currentVersion,
                DownloadUrl = downloadUrls[0],
                DownloadUrls = downloadUrls,
                ReleasePageUrl = manifest.ReleasePageUrl!,
                ArchiveName = manifest.ArchiveName,
                ArchiveSha256 = manifest.ArchiveSha256,
                ReleaseNotes = manifest.ReleaseNotes ?? "",
                Changes = manifest.Changes ?? []
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { CurrentVersion = currentVersion, Succeeded = false, Error = ex.Message };
        }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(UpdateCheckResult update, string destinationPath, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        IReadOnlyList<string> downloadUrls = update.DownloadUrls.Count > 0 ? update.DownloadUrls : [update.DownloadUrl];
        if (!update.Succeeded || downloadUrls.Count == 0 || downloadUrls.Any(url => !IsSafeDownloadUrl(url, update.LatestVersion)))
            throw new InvalidDataException("Update download URL is invalid");
        if (!IsSha256(update.ArchiveSha256)) throw new InvalidDataException("Update archive checksum is invalid");

        if (!await _downloadGate.WaitAsync(0, ct))
            throw new InvalidOperationException("An update download is already in progress.");

        try
        {
            var fullDestinationPath = Path.GetFullPath(destinationPath);
            var directory = Path.GetDirectoryName(fullDestinationPath);
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Download destination directory is missing", nameof(destinationPath));
            Directory.CreateDirectory(directory);
            var failures = new List<Exception>();
            var attemptedSources = 0;
            foreach (var downloadUrl in downloadUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var isFallback = attemptedSources++ > 0;
                progress?.Report(new UpdateDownloadProgress { DownloadUrl = downloadUrl, IsSourceFallback = isFallback });
                try
                {
                    return await DownloadFromUrlAsync(downloadUrl, fullDestinationPath, update.ArchiveSha256, progress, isFallback, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    failures.Add(ex);
                }
            }

            if (failures.Count == 1) throw failures[0];
            throw new AggregateException("All approved update download sources failed.", failures);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private async Task<UpdateDownloadResult> DownloadFromUrlAsync(string downloadUrl, string fullDestinationPath, string expectedSha256, IProgress<UpdateDownloadProgress>? progress, bool isFallback, CancellationToken ct)
    {
        var tempPath = fullDestinationPath + "." + Guid.NewGuid().ToString("N") + ".download";
        var ownsTempFile = false;
        using var idleTimeout = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, idleTimeout.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.UserAgent.ParseAdd("NetBootDhcpTool-UpdateDownload/1.0");
            idleTimeout.CancelAfter(_downloadIdleTimeout);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCancellation.Token);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(linkedCancellation.Token);
            idleTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                ownsTempFile = true;
                var buffer = new byte[64 * 1024];
                long received = 0;
                long sampledBytes = 0;
                var stopwatch = Stopwatch.StartNew();
                var sampledAt = stopwatch.Elapsed;
                var lowSpeedDuration = TimeSpan.Zero;
                int read;
                while (true)
                {
                    idleTimeout.CancelAfter(_downloadIdleTimeout);
                    try { read = await input.ReadAsync(buffer.AsMemory(), linkedCancellation.Token); }
                    finally { idleTimeout.CancelAfter(Timeout.InfiniteTimeSpan); }
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    var now = stopwatch.Elapsed;
                    var sampleElapsed = now - sampledAt;
                    if (sampleElapsed >= TimeSpan.FromSeconds(1))
                    {
                        var bytesPerSecond = (received - sampledBytes) / sampleElapsed.TotalSeconds;
                        if (bytesPerSecond < 3 * 1024) lowSpeedDuration += sampleElapsed;
                        else lowSpeedDuration = TimeSpan.Zero;
                        progress?.Report(new UpdateDownloadProgress
                        {
                            BytesReceived = received,
                            TotalBytes = totalBytes,
                            BytesPerSecond = bytesPerSecond,
                            LowSpeedDuration = lowSpeedDuration,
                            DownloadUrl = downloadUrl,
                            IsSourceFallback = isFallback
                        });
                        sampledBytes = received;
                        sampledAt = now;
                    }
                }
                var finalElapsed = stopwatch.Elapsed - sampledAt;
                if (finalElapsed > TimeSpan.Zero)
                {
                    progress?.Report(new UpdateDownloadProgress
                    {
                        BytesReceived = received,
                        TotalBytes = totalBytes,
                        BytesPerSecond = finalElapsed.TotalSeconds <= 0 ? 0 : (received - sampledBytes) / finalElapsed.TotalSeconds,
                        LowSpeedDuration = lowSpeedDuration,
                        DownloadUrl = downloadUrl,
                        IsSourceFallback = isFallback
                    });
                }
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }

            string hash;
            await using (var hashInput = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, ct)).ToLowerInvariant();
            }
            if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Downloaded update checksum mismatch: expected {expectedSha256}, actual {hash}");

            File.Move(tempPath, fullDestinationPath, overwrite: true);
            ownsTempFile = false;
            return new UpdateDownloadResult { FilePath = fullDestinationPath, Sha256 = hash, DownloadUrl = downloadUrl };
        }
        catch (Exception downloadFailure)
        {
            if (ownsTempFile)
            {
                try { File.Delete(tempPath); }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Update download failed and its temporary file could not be removed.", downloadFailure, cleanupFailure);
                }
            }
            if (downloadFailure is OperationCanceledException && !ct.IsCancellationRequested && idleTimeout.IsCancellationRequested)
                throw new IOException("Update download source was idle for too long.", downloadFailure);
            throw;
        }
    }

    private async Task<string> FetchLatestGiteeManifestAsync(Uri releaseEndpoint, CancellationToken ct)
    {
        var releaseJson = await GetJsonAsync(releaseEndpoint, ct);
        using var releaseDocument = JsonDocument.Parse(releaseJson);
        if (!releaseDocument.RootElement.TryGetProperty("id", out var releaseIdElement))
            throw new InvalidDataException("Gitee latest release response has no release ID");
        if (releaseDocument.RootElement.TryGetProperty("prerelease", out var prereleaseElement)
            && prereleaseElement.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("Gitee latest release is a prerelease");
        if (!releaseDocument.RootElement.TryGetProperty("tag_name", out var tagElement)
            || tagElement.ValueKind != JsonValueKind.String
            || !TryParseVersion(tagElement.GetString(), out var releaseVersion))
            throw new InvalidDataException("Gitee latest release tag is invalid");

        var releaseId = releaseIdElement.ValueKind switch
        {
            JsonValueKind.Number when releaseIdElement.TryGetInt64(out var numericId) && numericId > 0 => numericId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.String when long.TryParse(releaseIdElement.GetString(), out var parsedId) && parsedId > 0 => parsedId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException("Gitee latest release ID is invalid")
        };

        var attachmentsUri = new Uri($"{GiteeRepositoryApiPrefix}/releases/{releaseId}/attach_files?page=1&per_page=100");
        var attachmentsJson = await GetJsonAsync(attachmentsUri, ct);
        using var attachmentsDocument = JsonDocument.Parse(attachmentsJson);
        if (attachmentsDocument.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Gitee release attachments response is not an array");

        foreach (var attachment in attachmentsDocument.RootElement.EnumerateArray())
        {
            if (!attachment.TryGetProperty("name", out var nameElement)
                || !string.Equals(nameElement.GetString(), "latest.json", StringComparison.OrdinalIgnoreCase)
                || !attachment.TryGetProperty("browser_download_url", out var urlElement))
                continue;

            var manifestUrl = urlElement.GetString();
            if (!IsSafeDownloadUrl(manifestUrl, releaseVersion))
                throw new InvalidDataException("Gitee latest manifest attachment URL is not approved");
            var manifestJson = await GetJsonAsync(new Uri(manifestUrl!, UriKind.Absolute), ct);
            using var manifestDocument = JsonDocument.Parse(manifestJson);
            if (!manifestDocument.RootElement.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String
                || !TryParseVersion(versionElement.GetString(), out var manifestVersion)
                || manifestVersion != releaseVersion)
                throw new InvalidDataException("Gitee latest manifest version does not match its Release tag");
            return manifestJson;
        }

        throw new FileNotFoundException("Gitee latest release does not contain latest.json");
    }

    private async Task<string> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NetBootDhcpTool-VersionCheck/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static bool IsGiteeLatestReleaseEndpoint(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals("/api/v5/repos/joel20230302/NetBootDhcpTool/releases/latest", StringComparison.OrdinalIgnoreCase);

    private static List<string> GetSafeDownloadUrls(UpdateManifest manifest, Version expectedVersion)
    {
        if (!IsSafeDownloadUrl(manifest.DownloadUrl, expectedVersion))
            throw new InvalidDataException("Update download URL is not an approved HTTPS GitHub or Gitee release asset URL");

        var urls = new List<string> { manifest.DownloadUrl! };
        foreach (var mirror in manifest.DownloadMirrors ?? [])
        {
            if (IsSafeDownloadUrl(mirror, expectedVersion)
                && Uri.TryCreate(mirror, UriKind.Absolute, out var mirrorUri)
                && !urls.Any(url => Uri.TryCreate(url, UriKind.Absolute, out var existingUri)
                    && existingUri.Host.Equals(mirrorUri.Host, StringComparison.OrdinalIgnoreCase)))
                urls.Add(mirror);
        }
        return urls;
    }

    private static bool IsSafeDownloadUrl(string? text, Version? expectedVersion = null)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/shashouaq/NetBootDhcpTool/releases/download/", StringComparison.OrdinalIgnoreCase);

        if (uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return false;

            var path = uri.AbsolutePath;
            if (System.Text.RegularExpressions.Regex.IsMatch(path,
                    @"^/joel20230302/NetBootDhcpTool/attach_files/[0-9]+(?:/download)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                return true;

            var releaseAsset = System.Text.RegularExpressions.Regex.Match(path,
                @"^/joel20230302/NetBootDhcpTool/releases/download/v(?<version>[0-9]+\.[0-9]+\.[0-9]+)/(?<name>latest\.json|NetBootDhcpTool-v(?<archiveVersion>[0-9]+\.[0-9]+\.[0-9]+)\.7z)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return releaseAsset.Success
                && (!releaseAsset.Groups["archiveVersion"].Success
                    || releaseAsset.Groups["archiveVersion"].Value.Equals(releaseAsset.Groups["version"].Value, StringComparison.OrdinalIgnoreCase))
                && (expectedVersion is null
                    || releaseAsset.Groups["version"].Value.Equals(expectedVersion.ToString(3), StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsSafeReleasePageUrl(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        return (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/shashouaq/NetBootDhcpTool/releases/", StringComparison.OrdinalIgnoreCase))
            || (uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/joel20230302/NetBootDhcpTool/releases/", StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
        var prereleaseIndex = normalized.IndexOf('-');
        if (prereleaseIndex >= 0) normalized = normalized[..prereleaseIndex];
        if (!Version.TryParse(normalized, out var parsed)) return false;
        version = parsed;
        return true;
    }

    private static bool IsSha256(string? text) => !string.IsNullOrWhiteSpace(text)
        && text.Length == 64
        && text.All(Uri.IsHexDigit);

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
