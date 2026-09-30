using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
    public List<UpdatePackageMetadata> Packages { get; set; } = [];
    public List<UpdatePackageMetadata> SevenZipPackages { get; set; } = [];
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
    public bool SignatureVerified { get; init; }
    public string SignedManifestJson { get; init; } = "";
    public string ManifestSignature { get; init; } = "";
    public IReadOnlyList<UpdatePackageMetadata> Packages { get; init; } = [];
    public UpdatePackageMetadata? SelectedPackage { get; init; }
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
    public bool ReadyToInstall { get; init; }
    public UpdatePackageMetadata? Package { get; init; }
    public bool FellBackToFullPackage { get; init; }
    public string SignedManifestJson { get; init; } = "";
    public string ManifestSignature { get; init; } = "";
}

public sealed class VersionUpdateService : IDisposable
{
    private sealed record ManifestPayload(string Json, string Signature, string SourceUrl);
    private sealed record ManifestCandidate(Uri SourceUri, UpdateCheckResult Result);

    public const string ManifestFileName = "latest-v2.json";
    public const string ManifestSignatureFileName = "latest-v2.json.sig";
    public const string DefaultManifestUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest-v2.json";
    public const string GiteeLatestReleaseApiUrl = "https://gitee.com/api/v5/repos/joel20230302/NetBootDhcpTool/releases/latest";
    private const string GiteeRepositoryApiPrefix = "https://gitee.com/api/v5/repos/joel20230302/NetBootDhcpTool";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IReadOnlyList<Uri> _manifestUris;
    private readonly TimeSpan _downloadIdleTimeout;
    private readonly string _trustedPublicKeyPem;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public VersionUpdateService(HttpClient? httpClient = null, string? manifestUrl = null, TimeSpan? downloadIdleTimeout = null)
        : this(httpClient, manifestUrl, downloadIdleTimeout, UpdateManifestSignature.TrustedPublicKeyPem)
    {
    }

    internal VersionUpdateService(HttpClient? httpClient, string? manifestUrl, TimeSpan? downloadIdleTimeout, string trustedPublicKeyPem)
    {
        _downloadIdleTimeout = downloadIdleTimeout ?? TimeSpan.FromSeconds(30);
        if (_downloadIdleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadIdleTimeout));
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPublicKeyPem);
        _trustedPublicKeyPem = trustedPublicKeyPem;
        _httpClient = httpClient ?? UpdateTestEnvironment.CreateHttpClient() ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = httpClient == null;
        _manifestUris = string.IsNullOrWhiteSpace(manifestUrl)
            ? [new Uri(GiteeLatestReleaseApiUrl, UriKind.Absolute), new Uri(DefaultManifestUrl, UriKind.Absolute)]
            : [new Uri(manifestUrl, UriKind.Absolute)];
    }

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken ct = default, IProgress<UpdateSourceSpeed>? speedProgress = null)
    {
        var errors = new List<string>();
        var candidates = new List<ManifestCandidate>();
        foreach (var manifestUri in _manifestUris)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                var response = IsGiteeLatestReleaseEndpoint(manifestUri)
                    ? await FetchLatestGiteeManifestAsync(manifestUri, linkedCancellation.Token)
                    : await FetchManifestAsync(manifestUri, linkedCancellation.Token);
                var result = Evaluate(response.Json, response.Signature, currentVersion, _trustedPublicKeyPem);
                if (result.Succeeded)
                {
                    candidates.Add(new ManifestCandidate(new Uri(response.SourceUrl, UriKind.Absolute), result));
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

        if (candidates.Count == 0)
            return new UpdateCheckResult { CurrentVersion = currentVersion, Succeeded = false, Error = string.Join("; ", errors) };
        var candidateSource = candidates
            .Where(item => item.SourceUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Result.LatestVersion)
            .FirstOrDefault()
            ?? candidates.OrderByDescending(item => item.Result.LatestVersion).First();
        var candidate = RestrictToMatchingMirrors(candidateSource.Result, candidates);
        if (candidate.DownloadUrls.Count == 0)
            return new UpdateCheckResult { CurrentVersion = currentVersion, Succeeded = false, Error = "No mirror has a manifest matching the selected version, filename, and SHA-256." };
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

    private static UpdateCheckResult RestrictToMatchingMirrors(UpdateCheckResult selected, IReadOnlyList<ManifestCandidate> candidates)
    {
        var matchingSources = candidates
            .Where(item => SameReleaseIdentity(selected, item.Result))
            .ToArray();
        var isCanonicalSourceSet = matchingSources.All(item =>
            item.SourceUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || item.SourceUri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase));
        if (!isCanonicalSourceSet)
            return selected;

        var archiveUrls = matchingSources
            .SelectMany(item => item.Result.DownloadUrls.Where(url => IsFromHost(url, item.SourceUri.Host)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packages = new List<UpdatePackageMetadata>();
        foreach (var selectedPackage in selected.Packages)
        {
            var packageUrls = matchingSources
                .SelectMany(item => item.Result.Packages
                    .Where(package => SamePackageIdentity(selectedPackage, package))
                    .SelectMany(package => GetSafePackageUrls(package, item.Result.LatestVersion)
                        .Where(url => IsFromHost(url, item.SourceUri.Host))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (packageUrls.Length == 0) continue;
            packages.Add(new UpdatePackageMetadata
            {
                Kind = selectedPackage.Kind,
                Format = selectedPackage.Format,
                FileName = selectedPackage.FileName,
                Sha256 = selectedPackage.Sha256,
                Size = selectedPackage.Size,
                DownloadUrl = packageUrls[0],
                DownloadMirrors = packageUrls.Skip(1).ToList(),
                BaseVersion = selectedPackage.BaseVersion,
                BaseInstallManifestSha256 = selectedPackage.BaseInstallManifestSha256
            });
        }

        return new UpdateCheckResult
        {
            CurrentVersion = selected.CurrentVersion,
            LatestVersion = selected.LatestVersion,
            Succeeded = selected.Succeeded,
            IsNewVersion = selected.IsNewVersion,
            DownloadUrl = archiveUrls.FirstOrDefault() ?? "",
            DownloadUrls = archiveUrls,
            ReleasePageUrl = selected.ReleasePageUrl,
            ArchiveName = selected.ArchiveName,
            ArchiveSha256 = selected.ArchiveSha256,
            ReleaseNotes = selected.ReleaseNotes,
            Changes = selected.Changes,
            SignatureVerified = selected.SignatureVerified,
            SignedManifestJson = selected.SignedManifestJson,
            ManifestSignature = selected.ManifestSignature,
            Packages = packages,
            Error = selected.Error
        };
    }

    private static bool SameReleaseIdentity(UpdateCheckResult left, UpdateCheckResult right) =>
        left.LatestVersion == right.LatestVersion
        && left.ArchiveName.Equals(right.ArchiveName, StringComparison.Ordinal)
        && left.ArchiveSha256.Equals(right.ArchiveSha256, StringComparison.OrdinalIgnoreCase);

    private static bool SamePackageIdentity(UpdatePackageMetadata left, UpdatePackageMetadata right) =>
        left.Kind.Equals(right.Kind, StringComparison.OrdinalIgnoreCase)
        && left.Format.Equals(right.Format, StringComparison.OrdinalIgnoreCase)
        && left.FileName.Equals(right.FileName, StringComparison.Ordinal)
        && left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase)
        && left.Size == right.Size
        && string.Equals(left.BaseVersion, right.BaseVersion, StringComparison.Ordinal)
        && string.Equals(left.BaseInstallManifestSha256, right.BaseInstallManifestSha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsFromHost(string url, string host) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);

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
        SignatureVerified = source.SignatureVerified,
        SignedManifestJson = source.SignedManifestJson,
        ManifestSignature = source.ManifestSignature,
        Packages = source.Packages,
        SelectedPackage = source.SelectedPackage,
        Error = source.Error
    };

    public static UpdateCheckResult Evaluate(string json, Version currentVersion)
        => Evaluate(json, "", currentVersion);

    public static UpdateCheckResult Evaluate(string json, string? signature, Version currentVersion, string? trustedPublicKeyPem = null)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonStore.Options)
                ?? throw new InvalidDataException("Update manifest is empty");
            if (!TryParseVersion(manifest.Version, out var latest)) throw new InvalidDataException("Update manifest version is invalid");
            var downloadUrls = GetSafeDownloadUrls(manifest, latest);
            if (!IsSafeReleasePageUrl(manifest.ReleasePageUrl)) throw new InvalidDataException("Update release page URL is not an approved HTTPS release URL");
            if (!IsSha256(manifest.ArchiveSha256)) throw new InvalidDataException("Update archive checksum is invalid");

            var signed = UpdateManifestSignature.Verify(Encoding.UTF8.GetBytes(json), signature, trustedPublicKeyPem);
            var packages = signed
                ? ValidatePackages(manifest.Packages, latest, UpdatePackageFormat.Zip)
                    .Concat(ValidatePackages(manifest.SevenZipPackages, latest, UpdatePackageFormat.SevenZip)).ToList()
                : [];

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
                Changes = manifest.Changes ?? [],
                SignatureVerified = signed,
                SignedManifestJson = signed ? json : "",
                ManifestSignature = signed ? signature!.Trim() : "",
                Packages = packages
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

    private async Task<ManifestPayload> FetchManifestAsync(Uri manifestUri, CancellationToken ct)
    {
        var json = await GetJsonAsync(manifestUri, ct).ConfigureAwait(false);
        var signatureUri = new Uri(manifestUri.AbsoluteUri + ".sig", UriKind.Absolute);
        var signature = await GetOptionalTextAsync(signatureUri, ct).ConfigureAwait(false);
        return new ManifestPayload(json, signature, manifestUri.AbsoluteUri);
    }

    private async Task<ManifestPayload> FetchLatestGiteeManifestAsync(Uri releaseEndpoint, CancellationToken ct)
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

        string? manifestUrl = null;
        string? signatureUrl = null;
        foreach (var attachment in attachmentsDocument.RootElement.EnumerateArray())
        {
            if (!attachment.TryGetProperty("name", out var nameElement)
                || !attachment.TryGetProperty("browser_download_url", out var urlElement))
                continue;
            var name = nameElement.GetString();
            var url = urlElement.GetString();
            if (string.Equals(name, ManifestFileName, StringComparison.OrdinalIgnoreCase)) manifestUrl = url;
            else if (string.Equals(name, ManifestSignatureFileName, StringComparison.OrdinalIgnoreCase)) signatureUrl = url;
        }

        if (manifestUrl is null) throw new FileNotFoundException($"Gitee latest release does not contain {ManifestFileName}");
        if (!IsSafeManifestAssetUrl(manifestUrl, releaseVersion, ManifestFileName))
            throw new InvalidDataException("Gitee latest manifest attachment URL is not approved");
        var manifestJson = await GetJsonAsync(new Uri(manifestUrl, UriKind.Absolute), ct).ConfigureAwait(false);
        using var manifestDocument = JsonDocument.Parse(manifestJson);
        if (!manifestDocument.RootElement.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.String
            || !TryParseVersion(versionElement.GetString(), out var manifestVersion)
            || manifestVersion != releaseVersion)
            throw new InvalidDataException("Gitee latest manifest version does not match its Release tag");
        var signature = "";
        if (signatureUrl is not null && IsSafeManifestAssetUrl(signatureUrl, releaseVersion, ManifestSignatureFileName))
            signature = await GetOptionalTextAsync(new Uri(signatureUrl, UriKind.Absolute), ct).ConfigureAwait(false);
        return new ManifestPayload(manifestJson, signature, manifestUrl);
    }

    private async Task<string> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NetBootDhcpTool-VersionCheck/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> GetOptionalTextAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("NetBootDhcpTool-ManifestSignature/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return "";
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
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

    private static IReadOnlyList<UpdatePackageMetadata> ValidatePackages(IReadOnlyList<UpdatePackageMetadata>? packages, Version expectedVersion, UpdatePackageFormat format)
    {
        if (packages is null || packages.Count == 0 || packages.Count > 8) return [];
        var valid = new List<UpdatePackageMetadata>();
        var hasFull = false;
        foreach (var package in packages)
        {
            if (package is null || !IsSha256(package.Sha256) || package.Size <= 0 || package.Size > UpdatePackageApplier.MaximumPackageBytes)
                return [];
            if (package.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase))
            {
                var extension = format == UpdatePackageFormat.SevenZip ? ".7z" : ".zip";
                if (hasFull || package.FileName != $"NetBootDhcpTool-full-v{expectedVersion.ToString(3)}{extension}") return [];
                hasFull = true;
            }
            else if (package.Kind.Equals("Ota", StringComparison.OrdinalIgnoreCase))
            {
                var extension = format == UpdatePackageFormat.SevenZip ? ".7z" : ".zip";
                if (!TryParseVersion(package.BaseVersion, out var baseVersion) || baseVersion >= expectedVersion
                    || package.FileName != $"NetBootDhcpTool-ota-v{baseVersion.ToString(3)}-to-v{expectedVersion.ToString(3)}{extension}"
                    || !IsSha256(package.BaseInstallManifestSha256)) return [];
            }
            else return [];

            if (GetSafePackageUrls(package, expectedVersion).Count == 0) return [];
            if (valid.Any(existing => existing.Kind.Equals(package.Kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.BaseVersion, package.BaseVersion, StringComparison.Ordinal))) return [];
            valid.Add(new UpdatePackageMetadata
            {
                Kind = package.Kind,
                Format = format.ToString(),
                FileName = package.FileName,
                Sha256 = package.Sha256,
                Size = package.Size,
                DownloadUrl = package.DownloadUrl,
                DownloadMirrors = package.DownloadMirrors,
                BaseVersion = package.BaseVersion,
                BaseInstallManifestSha256 = package.BaseInstallManifestSha256
            });
        }
        return hasFull ? valid : [];
    }

    private static List<string> GetSafePackageUrls(UpdatePackageMetadata package, Version? expectedVersion)
    {
        var candidates = new List<string>();
        if (IsSafePackageUrl(package.DownloadUrl, package, expectedVersion)) candidates.Add(package.DownloadUrl!);
        foreach (var mirror in package.DownloadMirrors ?? [])
        {
            if (IsSafePackageUrl(mirror, package, expectedVersion)
                && Uri.TryCreate(mirror, UriKind.Absolute, out var mirrorUri)
                && !candidates.Any(url => Uri.TryCreate(url, UriKind.Absolute, out var existingUri)
                    && existingUri.Host.Equals(mirrorUri.Host, StringComparison.OrdinalIgnoreCase))) candidates.Add(mirror);
        }
        return candidates;
    }

    private static bool IsSafePackageUrl(string? text, UpdatePackageMetadata package, Version? expectedVersion)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
                    @"^/joel20230302/NetBootDhcpTool/attach_files/[0-9]+(?:/download)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return true;
            return IsSafeManifestAssetUrl(uri.AbsoluteUri, expectedVersion, package.FileName);
        }
        return IsSafeManifestAssetUrl(uri.AbsoluteUri, expectedVersion, package.FileName);
    }

    private static bool IsSafeManifestAssetUrl(string? text, Version? expectedVersion, string fileName)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var prefix = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "/shashouaq/NetBootDhcpTool/releases/download/"
            : uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
                ? "/joel20230302/NetBootDhcpTool/releases/download/"
                : "";
        if (uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
            && System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
                @"^/joel20230302/NetBootDhcpTool/attach_files/[0-9]+(?:/download)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return true;
        if (prefix.Length == 0 || !uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = uri.AbsolutePath[prefix.Length..].Split('/');
        return tail.Length == 2 && tail[0].StartsWith('v')
            && TryParseVersion(tail[0], out var tagVersion)
            && (expectedVersion is null || tagVersion == expectedVersion)
            && tail[1].Equals(fileName, StringComparison.Ordinal);
    }

    private static int SourcePriority(string url, IReadOnlyList<UpdateSourceSpeed> speeds)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        var index = speeds.Where(speed => Uri.TryCreate(speed.Url, UriKind.Absolute, out _))
            .OrderByDescending(speed => speed.BytesPerSecond ?? 0)
            .Select((speed, position) => (speed, position))
            .FirstOrDefault(item => Uri.TryCreate(item.speed.Url, UriKind.Absolute, out var sourceUri)
                && sourceUri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        return index.speed is null ? int.MaxValue : index.position;
    }

    private static bool IsSafeDownloadUrl(string? text, Version? expectedVersion = null)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return IsApprovedReleaseAssetPath(uri.AbsolutePath, "/shashouaq/NetBootDhcpTool/releases/download/", expectedVersion);

        if (uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return false;

            var path = uri.AbsolutePath;
            if (System.Text.RegularExpressions.Regex.IsMatch(path,
                    @"^/joel20230302/NetBootDhcpTool/attach_files/[0-9]+(?:/download)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                return true;

            return IsApprovedReleaseAssetPath(path, "/joel20230302/NetBootDhcpTool/releases/download/", expectedVersion);
        }

        return false;
    }

    private static bool IsApprovedReleaseAssetPath(string path, string prefix, Version? expectedVersion)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var tail = path[prefix.Length..].Split('/');
        if (tail.Length != 2 || !tail[0].StartsWith('v') || !TryParseVersion(tail[0], out var tagVersion)
            || (expectedVersion is not null && tagVersion != expectedVersion)) return false;
        var name = tail[1];
        if (name is "latest.json" or "latest.json.sig" or "latest-v2.json" or "latest-v2.json.sig") return true;
        if (name.Equals($"NetBootDhcpTool-v{tagVersion.ToString(3)}.7z", StringComparison.Ordinal)) return true;
        if (name.Equals($"NetBootDhcpTool-v{tagVersion.ToString(3)}.7z.sha256", StringComparison.Ordinal)) return true;
        if (name.Equals($"NetBootDhcpTool-full-v{tagVersion.ToString(3)}.zip", StringComparison.Ordinal)
            || name.Equals($"NetBootDhcpTool-full-v{tagVersion.ToString(3)}.zip.sha256", StringComparison.Ordinal)
            || name.Equals($"NetBootDhcpTool-full-v{tagVersion.ToString(3)}.7z", StringComparison.Ordinal)
            || name.Equals($"NetBootDhcpTool-full-v{tagVersion.ToString(3)}.7z.sha256", StringComparison.Ordinal)) return true;
        var ota = System.Text.RegularExpressions.Regex.Match(name,
            @"^NetBootDhcpTool-ota-v(?<base>[0-9]+\.[0-9]+\.[0-9]+)-to-v(?<target>[0-9]+\.[0-9]+\.[0-9]+)\.(?:zip|7z)(?:\.sha256)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return ota.Success && ota.Groups["target"].Value.Equals(tagVersion.ToString(3), StringComparison.Ordinal)
            && TryParseVersion(ota.Groups["base"].Value, out var baseVersion) && baseVersion < tagVersion;
    }

    public async Task<UpdateDownloadResult> DownloadPackageAsync(UpdateCheckResult update, UpdatePackageMetadata package, string destinationPath, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(package);
        if (!update.SignatureVerified || string.IsNullOrWhiteSpace(update.SignedManifestJson)
            || !update.Packages.Any(item => ReferenceEquals(item, package)
                || item.Kind.Equals(package.Kind, StringComparison.OrdinalIgnoreCase)
                && item.FileName.Equals(package.FileName, StringComparison.Ordinal)
                && item.Sha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Only a package from a verified update manifest can be installed.");
        var urls = GetSafePackageUrls(package, update.LatestVersion)
            .OrderBy(url => SourcePriority(url, update.DownloadSpeeds))
            .ToArray();
        if (urls.Length == 0 || !IsSha256(package.Sha256)) throw new InvalidDataException("Signed update package metadata is invalid.");
        var packageResult = await DownloadAsync(new UpdateCheckResult
        {
            CurrentVersion = update.CurrentVersion,
            LatestVersion = update.LatestVersion,
            Succeeded = update.Succeeded,
            IsNewVersion = update.IsNewVersion,
            DownloadUrl = urls[0],
            DownloadUrls = urls,
            ArchiveName = package.FileName,
            ArchiveSha256 = package.Sha256
        }, destinationPath, progress, ct).ConfigureAwait(false);
        return new UpdateDownloadResult
        {
            FilePath = packageResult.FilePath,
            Sha256 = packageResult.Sha256,
            DownloadUrl = packageResult.DownloadUrl,
            ReadyToInstall = true,
            Package = package,
            SignedManifestJson = update.SignedManifestJson,
            ManifestSignature = update.ManifestSignature
        };
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
