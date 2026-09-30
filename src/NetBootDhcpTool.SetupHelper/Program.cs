using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.SetupHelper;

internal static class Program
{
    private static bool _silent;
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0].Equals("--detect-install", StringComparison.Ordinal))
            {
                var resultPath = Path.GetFullPath(args[1]);
                UpdateTestEnvironment.RequirePath(resultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
                File.WriteAllText(resultPath, (FindExistingInstall() ?? "") + Environment.NewLine, new UTF8Encoding(false));
                return 0;
            }
            _silent = args.Contains("--silent", StringComparer.Ordinal);
            if (args.Length is not (5 or 6) || !args[0].Equals("--full-install", StringComparison.Ordinal)
                || args.Length == 6 && !_silent)
                throw new ArgumentException("Invalid setup invocation.");
            if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("管理员权限不足，请重新运行安装程序。 / Administrator access is required; run the setup program again.");
            var result = RunFullInstallAsync(args[1], args[2], args[3], args[4])
                .WaitAsync(UpdateResultProtocol.InstallationTimeout).GetAwaiter().GetResult();
            if (!_silent && result != InstallExitCode.Success)
                ShowError($"安装未通过最终验收，结果为 {result}，退出码 {(int)result}。请查看安装事务状态和日志。\r\nInstallation did not pass final acceptance: {result}, exit code {(int)result}. Review the transaction status and setup log.", "NetBoot DHCP Tool Setup");
            return (int)result;
        }
        catch (Exception ex)
        {
            TryLog("FAILED", ex.ToString());
            if (!_silent) ShowError(
                $"完整安装未能完成；用户数据保留在 LocalAppData。请结合事务状态确认安装或回退结果。\r\nFull installation did not complete; user data remains in LocalAppData. Review the transaction status for the installation or recovery result.\r\n\r\n{ex.Message}",
                "NetBoot DHCP Tool Setup");
            return (int)UpdateResultProtocol.Classify(ex);
        }
    }

    private static void ShowError(string message, string title)
    {
        _ = MessageBox(IntPtr.Zero, message, title, 0x00000010);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

    private static string? FindExistingInstall()
    {
        if (UpdateTestEnvironment.IsActive)
        {
            var isolated = Environment.GetEnvironmentVariable("NETBOOT_TEST_INSTALL_ROOT");
            if (string.IsNullOrWhiteSpace(isolated)) return null;
            UpdateTestEnvironment.RequirePath(isolated);
            return Path.GetFullPath(isolated);
        }
        // The registered install is the user's selected installation. A portable/test
        // copy may also be running, so process enumeration must not silently override it.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\NetBootDhcpTool", writable: false);
            var registered = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(registered))
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered));
                if (File.Exists(Path.Combine(root, "NetBootDhcpTool.exe"))) return root;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // Registry discovery is advisory; process discovery and the directory page
            // remain available for portable installs.
        }

        foreach (var process in Process.GetProcessesByName("NetBootDhcpTool"))
        {
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) continue;
                    var root = Path.GetDirectoryName(Path.GetFullPath(executable));
                    if (root is not null) return root;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Do not guess a path when Windows will not let setup identify the running process.
                }
            }
        }

        return null;
    }

    private static async Task<InstallExitCode> RunFullInstallAsync(string packageSource, string manifestPath, string signaturePath, string installRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var manifestFullPath = Path.GetFullPath(manifestPath);
        var signatureFullPath = Path.GetFullPath(signaturePath);
        var packageFullPath = string.IsNullOrWhiteSpace(packageSource) ? "" : Path.GetFullPath(packageSource);
        UpdateTestEnvironment.RequirePath(root);
        UpdateTestEnvironment.RequirePath(manifestFullPath);
        UpdateTestEnvironment.RequirePath(signatureFullPath);
        if (packageFullPath.Length > 0) UpdateTestEnvironment.RequirePath(packageFullPath);
        if (!File.Exists(manifestFullPath) || !File.Exists(signatureFullPath))
            throw new FileNotFoundException("Setup is missing its signed V2 manifest.");
        if (root.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(new AppPaths(root).DataDirectory)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The application cannot be installed into its user-data directory.");

        var manifestJson = await File.ReadAllTextAsync(manifestFullPath, new UTF8Encoding(false, true));
        var signature = (await File.ReadAllTextAsync(signatureFullPath, new UTF8Encoding(false, true))).Trim();
        var evaluation = VersionUpdateService.Evaluate(manifestJson, signature, new Version(0, 0));
        if (!evaluation.Succeeded || !evaluation.SignatureVerified || evaluation.LatestVersion is null)
            throw new InvalidDataException($"正式更新清单或签名无效。 / The signed update manifest is invalid: {(string.IsNullOrWhiteSpace(evaluation.Error) ? "RSA-PSS-SHA256 signature verification failed; the manifest bytes, signature and trusted key must match." : evaluation.Error)}");
        var package = evaluation.Packages.SingleOrDefault(item => item.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase)
            && item.Format.Equals(nameof(UpdatePackageFormat.SevenZip), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The signed manifest does not contain exactly one Full 7z package.");
        if (!Path.GetFileName(package.FileName).Equals(package.FileName, StringComparison.Ordinal))
            throw new InvalidDataException("The Full 7z filename in the signed manifest is unsafe.");

        var dataPaths = new AppPaths(root);
        using var preparationLease = UpdateTransactionLease.Acquire(dataPaths.DataDirectory);
        // Recovery runs before trusting a possibly half-replaced installation manifest.
        if (Directory.Exists(Path.Combine(root, "updates", "rollback")))
        {
            CloseExistingApplications(root);
            using var recoveryLease = SingleInstanceLease.TryAcquire(dataPaths)
                ?? throw new IOException("An application still owns the installation data lease.");
            await UpdatePackageApplier.RecoverInterruptedAsync(root);
        }
        var installManifestPath = Path.Combine(root, UpdatePackageApplier.InstallManifestName);
        var currentVersion = "0.0.0";
        var baseManifestHash = "";
        if (File.Exists(installManifestPath))
        {
            var currentBytes = await File.ReadAllBytesAsync(installManifestPath);
            var currentManifest = JsonSerializer.Deserialize<UpdateInstallManifest>(currentBytes, JsonStore.Options)
                ?? throw new InvalidDataException("The existing installation manifest is invalid; setup will not overwrite an installation it cannot roll back.");
            if (currentManifest.SchemaVersion != 1 || currentManifest.ProductId != UpdatePackageApplier.ProductId
                || !VersionUpdateService.TryParseVersion(currentManifest.Version, out var installedVersion)
                || !UpdatePackageApplier.IsValidFileInventory(currentManifest.Files)
                || !currentManifest.Files.Any(file => file.Path.Equals(currentManifest.EntryPoint, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The existing installation manifest is invalid; setup will not overwrite an installation it cannot roll back.");
            currentVersion = installedVersion.ToString(3);
            baseManifestHash = UpdatePackageApplier.Sha256(currentBytes);
        }

        if (evaluation.LatestVersion <= Version.Parse(currentVersion))
            throw new InvalidDataException("The setup version is not newer than the existing installation.");

        var updateRoot = Path.Combine(dataPaths.DataDirectory, "updates");
        var requestId = Guid.NewGuid().ToString("N");
        var packageDirectory = Path.Combine(updateRoot, "staging", "setup-" + requestId);
        Directory.CreateDirectory(packageDirectory);
        UpdateTestEnvironment.RequireSpace("download", packageDirectory, package.Size);
        var stagedPackage = Path.Combine(packageDirectory, package.FileName);
        var useLocalPackage = false;
        if (!string.IsNullOrWhiteSpace(packageSource) && File.Exists(packageFullPath))
        {
            try
            {
                if (!Path.GetFileName(packageFullPath).Equals(package.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Local Full 7z filename differs from the signed manifest.");
                var localInfo = new FileInfo(packageFullPath);
                if (localInfo.Length != package.Size)
                    throw new InvalidDataException($"Local Full 7z size mismatch: expected {package.Size}, actual {localInfo.Length}.");
                var localHash = await HashFileAsync(packageFullPath);
                if (!localHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Local Full 7z SHA-256 mismatch: expected {package.Sha256}, actual {localHash}.");
                var partial = stagedPackage + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    File.Copy(packageFullPath, partial, overwrite: false);
                    File.Move(partial, stagedPackage, overwrite: false);
                }
                finally { TryDelete(partial); }
                useLocalPackage = true;
                TryLog("LOCAL_PACKAGE_VERIFIED", $"filename={package.FileName} bytes={localInfo.Length} sha256={localHash}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                TryLog("LOCAL_PACKAGE_REJECTED", $"filename={Path.GetFileName(packageFullPath)} reason={ex.Message}");
                TryDelete(stagedPackage);
            }
        }
        else TryLog("LOCAL_PACKAGE_MISSING", $"expected={package.FileName}; online download will be attempted");

        if (!useLocalPackage)
        {
            if (UpdateTestEnvironment.IsActive && Environment.GetEnvironmentVariable("NETBOOT_TEST_OFFLINE") == "1")
                throw new InvalidDataException("The isolated offline package is missing or invalid.");
            try { await DownloadFullPackageAsync(evaluation, package, currentVersion, stagedPackage); }
            catch (Exception ex)
            {
                var failures = ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [ex];
                var code = failures.Any(failure => failure is InvalidDataException) ? InstallExitCode.VerificationFailure : InstallExitCode.DownloadFailure;
                throw new UpdateOperationException(code, "Full 7z download failed.", ex);
            }
        }

        var finalPackageInfo = new FileInfo(stagedPackage);
        if (!finalPackageInfo.Exists || finalPackageInfo.Length != package.Size)
            throw new InvalidDataException($"Prepared Full 7z size mismatch: expected {package.Size}, actual {(finalPackageInfo.Exists ? finalPackageInfo.Length : 0)}.");
        var finalPackageHash = await HashFileAsync(stagedPackage);
        if (!finalPackageHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prepared Full 7z SHA-256 does not match the signed manifest.");

        var apply = new UpdateApplyRequest(root, stagedPackage, package.Sha256, currentVersion, evaluation.LatestVersion.ToString(3),
            package.Kind, baseManifestHash, Convert.ToBase64String(Encoding.UTF8.GetBytes(manifestJson)), signature, requestId,
            nameof(UpdatePackageFormat.SevenZip))
        {
            IsFullInstall = true,
            KeepValidatedStaging = true
        };

        TryLog("VERIFYING", $"version={evaluation.LatestVersion.ToString(3)} bytes={package.Size} filename={package.FileName}");
        // The helper and updater both extract a validation staging tree. Give them distinct transaction IDs.
        var validationRequest = apply with { RequestId = requestId + "-setup-preflight" };
        var validation = await new UpdatePackageApplier().ApplyAsync(validationRequest, validateOnly: true);
        try
        {
            var targetManifestPath = Path.Combine(validation.StagingDirectory, UpdatePackageApplier.InstallManifestName);
            var targetManifest = JsonSerializer.Deserialize<UpdateInstallManifest>(await File.ReadAllBytesAsync(targetManifestPath), JsonStore.Options)
                ?? throw new InvalidDataException("Validated package has no installation manifest.");
            var updaterRecord = targetManifest.Files.SingleOrDefault(file => file.Path.Equals("NetBootDhcpTool.Updater.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Validated package does not contain the managed updater.");
            var updaterSource = Path.Combine(validation.StagingDirectory, "NetBootDhcpTool.Updater.exe");
            if (!File.Exists(updaterSource) || new FileInfo(updaterSource).Length != updaterRecord.Size
                || !updaterRecord.Sha256.Equals(await HashFileAsync(updaterSource), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The updater extracted from the signed Full 7z failed its payload verification.");

            CloseExistingApplications(root);

            // Copy legacy settings/favourites/logs without creating or changing product files in the install root.
            dataPaths.EnsureUserDataForMigration();
            if (dataPaths.MigrationWarnings.Count > 0)
                throw new IOException("旧配置迁移未能完整验证；安装文件尚未更改。 / Legacy user-data migration could not be verified; installation files were not changed. "
                    + string.Join("; ", dataPaths.MigrationWarnings));
            var updaterDirectory = Path.Combine(dataPaths.DataDirectory, "Updater");
            Directory.CreateDirectory(updaterDirectory);
            var updaterPath = Path.Combine(updaterDirectory, "NetBootDhcpTool.Updater.exe");
            var updaterPartial = updaterPath + "." + requestId + ".new";
            File.Copy(updaterSource, updaterPartial, overwrite: false);
            File.Move(updaterPartial, updaterPath, overwrite: true);

            var healthDirectory = Path.Combine(updateRoot, "health");
            var requestDirectory = Path.Combine(updateRoot, "requests");
            Directory.CreateDirectory(healthDirectory);
            Directory.CreateDirectory(requestDirectory);
            var healthToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var requestPath = Path.Combine(requestDirectory, requestId + ".json");
            var acceptedPath = Path.Combine(healthDirectory, requestId + ".accepted");
            var healthPath = Path.Combine(healthDirectory, requestId + ".health");
            var logPath = Path.Combine(updateRoot, "update.log");
            var processRequest = new UpdateProcessRequest(apply with { KeepValidatedStaging = false }, Environment.ProcessId,
                Path.GetFullPath(dataPaths.DataDirectory), requestPath, acceptedPath, healthPath, healthToken, logPath)
            {
                IsFullInstall = true,
                WaitForParentExit = false,
                Silent = _silent
            };
            await File.WriteAllTextAsync(requestPath + ".tmp", JsonSerializer.Serialize(processRequest, JsonStore.Options));
            File.Move(requestPath + ".tmp", requestPath, overwrite: false);

            var start = new ProcessStartInfo(updaterPath)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--apply");
            start.ArgumentList.Add(requestPath);
            preparationLease.Dispose(); // The cached updater takes the same lock and retains it through health confirmation.
            using var updater = Process.Start(start) ?? throw new InvalidOperationException("The new updater process could not start.");
            TryLog("UPDATER_STARTED", $"pid={updater.Id} version={evaluation.LatestVersion.ToString(3)}");
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (File.Exists(acceptedPath))
                {
                    var accepted = await File.ReadAllTextAsync(acceptedPath);
                    if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(accepted), Encoding.UTF8.GetBytes(healthToken)))
                    {
                        TryLog("ACCEPTED", $"version={evaluation.LatestVersion.ToString(3)}");
                        var finalCode = await UpdateResultProtocol.WaitForFinalResultAsync(processRequest, updater);
                        TryLog("FINAL_RESULT", $"request={requestId} code={(int)finalCode} result={finalCode}");
                        return finalCode;
                    }
                }
                if (updater.HasExited) return await UpdateResultProtocol.WaitForFinalResultAsync(processRequest, updater);
                await Task.Delay(100);
            }
            throw new TimeoutException("The updater did not accept the full-install transaction within 30 seconds; the installation directory was not changed.");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(validation.StagingDirectory)!, recursive: true); }
            catch { }
        }
    }

    private static void CloseExistingApplications(string root)
    {
        foreach (var process in FindApplicationProcesses(root))
        {
            using (process)
            {
                TryLog("CLOSING_OLD_APP", $"pid={process.Id}");
                if (!RequestGracefulShutdown(process))
                    throw new InvalidOperationException($"请先正常关闭旧程序后重试。 / Close the existing application normally and retry (PID {process.Id}).");
                if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
                    throw new TimeoutException("旧程序未能在两分钟内正常关闭；安装目录尚未修改。 / The existing application did not close within two minutes; installation files were not changed.");
            }
        }
    }

    private static bool RequestGracefulShutdown(Process process)
    {
        // WPF can expose more than one top-level HWND. Target the actual product window,
        // rather than whichever window Process.MainWindowHandle found first.
        process.Refresh();
        TryLog("CLOSE_WINDOW_DISCOVERY", $"pid={process.Id} default_handle={process.MainWindowHandle} default_title={process.MainWindowTitle}");
        var sent = false;
        _ = EnumWindows((window, unusedState) =>
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId != process.Id || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(512);
            var windowClass = new StringBuilder(256);
            _ = GetWindowText(window, title, title.Capacity);
            _ = GetClassName(window, windowClass, windowClass.Capacity);
            TryLog("CLOSE_WINDOW_CANDIDATE", $"pid={process.Id} handle={window} class={windowClass} title={title}");
            if (!title.ToString().StartsWith("NetBoot DHCP Tool", StringComparison.Ordinal)
                || !windowClass.ToString().StartsWith("HwndWrapper", StringComparison.Ordinal)) return true;
            if (!process.HasExited && PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero)) sent = true;
            return true;
        }, IntPtr.Zero);
        return sent;
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] private static extern int GetClassName(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static async Task DownloadFullPackageAsync(UpdateCheckResult embeddedManifest, UpdatePackageMetadata embeddedPackage,
        string installedVersion, string destinationPath)
    {
        var current = Version.Parse(installedVersion);
        using var testClient = UpdateTestEnvironment.CreateHttpClient();
        using var service = new VersionUpdateService(testClient);
        UpdateCheckResult selectedManifest = embeddedManifest;
        UpdatePackageMetadata selectedPackage = embeddedPackage;
        try
        {
            var remote = testClient is null ? await service.CheckAsync(current) : new UpdateCheckResult();
            if (remote.Succeeded && remote.SignatureVerified && remote.LatestVersion == embeddedManifest.LatestVersion)
            {
                var matchingPackage = remote.Packages.SingleOrDefault(item =>
                    item.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase)
                    && item.Format.Equals(nameof(UpdatePackageFormat.SevenZip), StringComparison.OrdinalIgnoreCase)
                    && item.FileName.Equals(embeddedPackage.FileName, StringComparison.Ordinal)
                    && item.Size == embeddedPackage.Size
                    && item.Sha256.Equals(embeddedPackage.Sha256, StringComparison.OrdinalIgnoreCase));
                if (matchingPackage is not null)
                {
                    selectedManifest = remote;
                    selectedPackage = matchingPackage;
                    TryLog("MIRROR_SELECTION", $"version={remote.LatestVersion} sources={string.Join(',', remote.DownloadSpeeds.Select(speed => $"{new Uri(speed.Url).Host}:{(speed.BytesPerSecond is null ? "unavailable" : Math.Round(speed.BytesPerSecond.Value))}"))}");
                }
                else TryLog("REMOTE_PACKAGE_MISMATCH", $"version={remote.LatestVersion}; falling back to URLs in the embedded signed manifest");
            }
            else TryLog("REMOTE_MANIFEST_UNAVAILABLE", $"embedded_version={embeddedManifest.LatestVersion}; detail={remote.Error}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
        {
            TryLog("REMOTE_MANIFEST_ERROR", ex.Message);
        }

        TryLog("PACKAGE_DOWNLOAD_START", $"filename={selectedPackage.FileName} expected_bytes={selectedPackage.Size} source_count={selectedPackage.DownloadMirrors.Count + 1}");
        var progress = new SetupDownloadProgress();
        var downloaded = await service.DownloadPackageAsync(selectedManifest, selectedPackage, destinationPath, progress);
        var info = new FileInfo(downloaded.FilePath);
        if (info.Length != selectedPackage.Size || !downloaded.Sha256.Equals(selectedPackage.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded Full 7z size or SHA-256 differs from its signed metadata.");
        TryLog("PACKAGE_DOWNLOAD_VERIFIED", $"filename={selectedPackage.FileName} bytes={info.Length} sha256={downloaded.Sha256} host={new Uri(downloaded.DownloadUrl).Host}");
    }

    private sealed class SetupDownloadProgress : IProgress<UpdateDownloadProgress>
    {
        private long _lastLoggedBytes = -1;
        private DateTimeOffset _lastLoggedAt = DateTimeOffset.MinValue;

        public void Report(UpdateDownloadProgress value)
        {
            if (value.BytesReceived - _lastLoggedBytes < 4 * 1024 * 1024
                && DateTimeOffset.UtcNow - _lastLoggedAt < TimeSpan.FromSeconds(5)
                && !value.IsSourceFallback) return;
            _lastLoggedBytes = value.BytesReceived;
            _lastLoggedAt = DateTimeOffset.UtcNow;
            TryLog("PACKAGE_DOWNLOAD_PROGRESS", $"received={value.BytesReceived} total={value.TotalBytes?.ToString() ?? "unknown"} bytes_per_second={Math.Round(value.BytesPerSecond)} fallback={value.IsSourceFallback} host={(Uri.TryCreate(value.DownloadUrl, UriKind.Absolute, out var uri) ? uri.Host : "unknown")}");
        }
    }

    private static IReadOnlyList<Process> FindApplicationProcesses(string root)
    {
        var expected = Path.GetFullPath(Path.Combine(root, "NetBootDhcpTool.exe"));
        var processes = new List<Process>();
        foreach (var process in Process.GetProcessesByName("NetBootDhcpTool"))
        {
            try
            {
                if (Path.GetFullPath(process.MainModule?.FileName ?? "").Equals(expected, StringComparison.OrdinalIgnoreCase)) processes.Add(process);
                else process.Dispose();
            }
            catch
            {
                process.Dispose();
                throw new InvalidOperationException("安装程序无法安全识别正在运行的 NetBootDhcpTool 进程；请先关闭程序后重试。 / Setup could not safely identify a running application process; close the application and retry.");
            }
        }
        return processes;
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static void TryLog(string phase, string details)
    {
        try
        {
            var configuredDataDirectory = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");
            var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
                ? UpdateTestEnvironment.Root is { } testRoot ? Path.Combine(testRoot, "user-data")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBootDhcpTool")
                : Path.GetFullPath(configuredDataDirectory);
            UpdateTestEnvironment.RequirePath(dataDirectory);
            var path = Path.Combine(dataDirectory, "updates", "setup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}\t{phase}\t{details}{Environment.NewLine}");
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
