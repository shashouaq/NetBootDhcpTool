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
}

public sealed class UpdateCheckResult
{
    public Version CurrentVersion { get; init; } = new(0, 0);
    public Version? LatestVersion { get; init; }
    public bool Succeeded { get; init; }
    public bool IsNewVersion { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string ReleasePageUrl { get; init; } = "";
    public string Error { get; init; } = "";
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
                ReleasePageUrl = manifest.ReleasePageUrl!
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { CurrentVersion = currentVersion, Succeeded = false, Error = ex.Message };
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
