using System.Diagnostics;
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
}

public sealed class UpdateCheckResult
{
    public Version CurrentVersion { get; init; } = new(0, 0);
    public Version? LatestVersion { get; init; }
    public bool Succeeded { get; init; }
    public bool IsNewVersion { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string ReleasePageUrl { get; init; } = "";
    public string ArchiveName { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
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
}

public sealed class UpdateDownloadResult
{
    public string FilePath { get; init; } = "";
    public string Sha256 { get; init; } = "";
}

public sealed class VersionUpdateService : IDisposable
{
    public const string DefaultManifestUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _manifestUri;

    public VersionUpdateService(HttpClient? httpClient = null, string? manifestUrl = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient == null;
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
        _manifestUri = new Uri(manifestUrl ?? DefaultManifestUrl, UriKind.Absolute);
    }

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _manifestUri);
            request.Headers.UserAgent.ParseAdd("NetBootDhcpTool-VersionCheck/1.0");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Evaluate(json, currentVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { CurrentVersion = currentVersion, Succeeded = false, Error = ex.Message };
        }
    }

    public static UpdateCheckResult Evaluate(string json, Version currentVersion)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonStore.Options)
                ?? throw new InvalidDataException("Update manifest is empty");
            if (!TryParseVersion(manifest.Version, out var latest)) throw new InvalidDataException("Update manifest version is invalid");
            if (!IsSafeReleaseUrl(manifest.DownloadUrl, "/releases/")) throw new InvalidDataException("Update download URL is not an approved HTTPS GitHub release URL");
            if (!IsSafeReleaseUrl(manifest.ReleasePageUrl, "/releases/")) throw new InvalidDataException("Update release page URL is not an approved HTTPS GitHub release URL");
            if (!IsSha256(manifest.ArchiveSha256)) throw new InvalidDataException("Update archive checksum is invalid");

            return new UpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = latest,
                Succeeded = true,
                IsNewVersion = latest > currentVersion,
                DownloadUrl = manifest.DownloadUrl!,
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
        if (!update.Succeeded || string.IsNullOrWhiteSpace(update.DownloadUrl) || !IsSafeReleaseUrl(update.DownloadUrl, "/releases/"))
            throw new InvalidDataException("Update download URL is invalid");
        if (!IsSha256(update.ArchiveSha256)) throw new InvalidDataException("Update archive checksum is invalid");

        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Download destination directory is missing", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var tempPath = destinationPath + ".download";
        var completed = false;
        try
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NetBootDhcpTool-UpdateDownload/1.0");
            using var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long received = 0;
                long sampledBytes = 0;
                var stopwatch = Stopwatch.StartNew();
                var sampledAt = stopwatch.Elapsed;
                var lowSpeedDuration = TimeSpan.Zero;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
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
                            LowSpeedDuration = lowSpeedDuration
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
                        LowSpeedDuration = lowSpeedDuration
                    });
                }
            }

            await using var hashInput = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(hashInput, ct)).ToLowerInvariant();
            if (!hash.Equals(update.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Downloaded update checksum mismatch: expected {update.ArchiveSha256}, actual {hash}");
            File.Move(tempPath, destinationPath, overwrite: true);
            completed = true;
            return new UpdateDownloadResult { FilePath = destinationPath, Sha256 = hash };
        }
        finally
        {
            if (!completed)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { }
            }
        }
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

    private static bool IsSafeReleaseUrl(string? text, string requiredPathFragment)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/shashouaq/NetBootDhcpTool/", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains(requiredPathFragment, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? text) => !string.IsNullOrWhiteSpace(text)
        && text.Length == 64
        && text.All(Uri.IsHexDigit);

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
