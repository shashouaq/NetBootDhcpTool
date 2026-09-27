using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.App;

internal sealed class UpdateActivationService
{
    public async Task ActivateAsync(AppPaths paths, UpdateInstallContext install, UpdateDownloadResult downloaded, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(downloaded);
        if (!downloaded.ReadyToInstall || downloaded.Package is null || string.IsNullOrWhiteSpace(downloaded.SignedManifestJson)
            || string.IsNullOrWhiteSpace(downloaded.ManifestSignature))
            throw new InvalidOperationException("更新包尚未通过完整校验。 / The update package has not passed complete validation.");
        if (!VersionUpdateService.TryParseVersion(downloaded.Package.Kind.Equals("Ota", StringComparison.OrdinalIgnoreCase) ? downloaded.Package.BaseVersion : install.Manifest.Version, out var baseVersion)
            || baseVersion.ToString(3) != install.Manifest.Version)
            throw new InvalidDataException("更新包基线与当前安装版本不匹配。 / Update package baseline does not match the installed version.");
        if (downloaded.Package.Kind.Equals("Ota", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(downloaded.Package.BaseInstallManifestSha256, install.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OTA 包的安装清单基线已变化，请重新检查更新。 / OTA install-manifest baseline has changed; check for updates again.");

        var packagePath = Path.GetFullPath(downloaded.FilePath);
        var stagingRoot = Path.GetFullPath(Path.Combine(paths.DataDirectory, "updates", "staging"));
        RequireChildPath(stagingRoot, packagePath);
        if (!File.Exists(packagePath) || !downloaded.Sha256.Equals(await HashFileAsync(packagePath, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("已下载的更新包校验失败，请重新下载。 / Downloaded package verification failed; download it again.");

        var installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.BaseDirectory));
        var updaterRelative = "NetBootDhcpTool.Updater.exe";
        var updaterEntry = install.Manifest.Files.FirstOrDefault(file => file.Path.Equals(updaterRelative, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("当前安装缺少受管更新器，无法自动升级。 / The current installation does not contain a managed updater.");
        var updaterSource = Path.Combine(installRoot, updaterRelative);
        if (!File.Exists(updaterSource) || new FileInfo(updaterSource).Length != updaterEntry.Size
            || !updaterEntry.Sha256.Equals(await HashFileAsync(updaterSource, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("当前更新器文件与安装清单不一致。 / Installed updater does not match the installation manifest.");

        var updatesRoot = Path.Combine(paths.DataDirectory, "updates");
        var updaterDirectory = Path.Combine(paths.DataDirectory, "Updater");
        Directory.CreateDirectory(updaterDirectory);
        var updaterPath = Path.Combine(updaterDirectory, updaterRelative);
        var copyPath = updaterPath + "." + Guid.NewGuid().ToString("N") + ".new";
        File.Copy(updaterSource, copyPath, overwrite: false);
        try { File.Move(copyPath, updaterPath, overwrite: true); }
        finally { TryDelete(copyPath); }

        var requestId = Guid.NewGuid().ToString("N");
        var healthToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var requestDirectory = Path.Combine(updatesRoot, "requests");
        var healthDirectory = Path.Combine(updatesRoot, "health");
        Directory.CreateDirectory(requestDirectory);
        Directory.CreateDirectory(healthDirectory);
        var requestPath = Path.Combine(requestDirectory, requestId + ".json");
        var acceptedPath = Path.Combine(healthDirectory, requestId + ".accepted");
        var healthPath = Path.Combine(healthDirectory, requestId + ".health");
        var statusPath = Path.Combine(updatesRoot, "update.log");
        var targetVersion = install.Manifest.Version;
        if (!VersionUpdateService.TryParseVersion(downloaded.SignedUpdateManifestVersion(), out var parsedTarget))
            throw new InvalidDataException("签名更新清单中的目标版本无效。 / Signed update manifest target version is invalid.");
        targetVersion = parsedTarget.ToString(3);
        var applyRequest = new UpdateApplyRequest(
            installRoot,
            packagePath,
            downloaded.Package.Sha256,
            install.Manifest.Version,
            targetVersion,
            downloaded.Package.Kind,
            install.ManifestSha256,
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(downloaded.SignedManifestJson)),
            downloaded.ManifestSignature,
            requestId);
        var processRequest = new UpdateProcessRequest(
            applyRequest,
            Environment.ProcessId,
            Path.GetFullPath(paths.DataDirectory),
            requestPath,
            acceptedPath,
            healthPath,
            healthToken,
            statusPath);
        var temporaryRequestPath = requestPath + ".tmp";
        await File.WriteAllTextAsync(temporaryRequestPath, JsonSerializer.Serialize(processRequest, JsonStore.Options), ct).ConfigureAwait(false);
        File.Move(temporaryRequestPath, requestPath, overwrite: false);

        var start = new ProcessStartInfo(updaterPath)
        {
            WorkingDirectory = installRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--apply");
        start.ArgumentList.Add(requestPath);
        if (Process.Start(start) is null) throw new InvalidOperationException("更新器进程未能启动。 / The updater process did not start.");

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(acceptedPath))
            {
                var acceptedToken = await File.ReadAllTextAsync(acceptedPath, ct).ConfigureAwait(false);
                if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(acceptedToken), System.Text.Encoding.UTF8.GetBytes(healthToken))) return;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("更新器未在 20 秒内受理事务；程序保持打开，安装目录未修改。 / The updater did not accept the transaction within 20 seconds; the application remains open and the installation was not changed.");
    }

    private static void RequireChildPath(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Path.GetFullPath(path).StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包不在应用数据目录的暂存区。 / Update package is outside the application staging directory.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

internal static class SignedManifestExtensions
{
    public static string SignedUpdateManifestVersion(this UpdateDownloadResult result)
    {
        using var document = JsonDocument.Parse(result.SignedManifestJson);
        return document.RootElement.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }
}
