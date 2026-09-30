using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Updater;

internal static class Program
{
    private static IDisposable? _transactionLease;
    private static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows()) _ = SetErrorMode(0x8003); // Suppress OS error dialogs; the transaction protocol reports the failure.
        try { return RunMain(args); }
        finally { _transactionLease?.Dispose(); }
    }

    private static int RunMain(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--apply" or "--recover")) return (int)InstallExitCode.PreflightFailure;
        UpdateProcessRequest? request = null;
        var requestValidated = false;
        try
        {
            var requestPath = Path.GetFullPath(args[1]);
            if (!File.Exists(requestPath) || new FileInfo(requestPath).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("更新请求文件不存在或超出大小限制。 / Update request is missing or too large.");
            request = JsonSerializer.Deserialize<UpdateProcessRequest>(File.ReadAllBytes(requestPath), JsonStore.Options)
                ?? throw new InvalidDataException("更新请求文件格式无效。 / Update request is invalid.");
            if (!request.RequestFilePath.Equals(requestPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新请求路径与其内容不一致。 / Update request path does not match its content.");
            ValidateRequest(request);
            requestValidated = true;
            _transactionLease = UpdateTransactionLease.Acquire(request.UserDataDirectory);
            if (args[0] == "--recover")
            {
                using var recoveryLease = WaitForDataLeaseAsync(request.UserDataDirectory, request.Apply.InstallRoot).GetAwaiter().GetResult();
                UpdatePackageApplier.RecoverInterruptedAsync(request.Apply.InstallRoot).GetAwaiter().GetResult();
                recoveryLease.Dispose();
                StartPreviousVersionAsync(request.Apply.InstallRoot).GetAwaiter().GetResult();
                WriteFinal(request, InstallExitCode.RolledBack, "rolled-back", "Interrupted transaction recovered; previous installation restored.");
                return (int)InstallExitCode.RolledBack;
            }
            using (request.IsFullInstall
                ? GetExpectedSetupParent(request.ParentProcessId)
                : GetExpectedParent(request.ParentProcessId, Path.GetFullPath(request.Apply.InstallRoot))) { }
            new UpdatePackageApplier().ApplyAsync(request.Apply, validateOnly: true).GetAwaiter().GetResult();
            WriteAccepted(request);
        }
        catch (Exception ex)
        {
            var code = UpdateResultProtocol.Classify(ex);
            if (requestValidated && request is not null)
            {
                TryLog(request.UpdateLogPath, "FAILED", ex.ToString());
                WriteFinal(request, code, code == InstallExitCode.RollbackFailed ? "rollback-failed" : "failed", ex.Message);
            }
            if (request?.Silent != true) ShowFailureMessage(ex.Message);
            return (int)code;
        }

        try
        {
            RunTransactionAsync(request!).GetAwaiter().GetResult();
            return (int)InstallExitCode.Success;
        }
        catch (Exception ex)
        {
            TryLog(request!.UpdateLogPath, "FAILED", ex.ToString());
            var journal = UpdatePackageApplier.GetJournalState(request.Apply.InstallRoot, request.Apply.RequestId);
            var code = ex is UpdateOperationException operation ? operation.Code
                : journal == "rolled-back" ? InstallExitCode.RolledBack
                : journal == "rollback-incomplete" || ex is AggregateException ? InstallExitCode.RollbackFailed
                : UpdateResultProtocol.Classify(ex);
            WriteFinal(request, code, code is InstallExitCode.RolledBack or InstallExitCode.HealthCheckRolledBack ? "rolled-back"
                : code == InstallExitCode.RollbackFailed ? "rollback-failed" : "failed", ex.Message);
            if (!request.Silent) ShowFailureMessage(ex.Message);
            return (int)code;
        }
    }

    private static async Task RunTransactionAsync(UpdateProcessRequest request)
    {
        var apply = request.Apply;
        var root = Path.GetFullPath(apply.InstallRoot);
        var status = new UpdateTransactionStatus(request.Apply.RequestId, apply.CurrentVersion, apply.TargetVersion, "waiting-for-exit", "正在等待主程序正常退出 / Waiting for the application to close normally", DateTimeOffset.UtcNow);
        WriteStatus(request, status);
        TryLog(request.UpdateLogPath, "WAITING_FOR_EXIT", $"pid={request.ParentProcessId} root={root}");

        if (request.WaitForParentExit)
        {
        var parent = request.IsFullInstall
            ? GetExpectedSetupParent(request.ParentProcessId)
            : GetExpectedParent(request.ParentProcessId, root);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
        {
            try { await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException("主程序未能在 4 分钟内正常退出；更新器没有修改安装目录。 / The application did not exit within four minutes; no installation files were changed.");
            }
            finally { parent.Dispose(); }
        }
        }

        Process? launched = null;
        UpdateApplyResult? appliedResult = null;
        var preserveInstallationForRecovery = false;
        var previousRestartAttempted = false;
        try
        {
            using var lease = await WaitForDataLeaseAsync(request.UserDataDirectory, root).ConfigureAwait(false);
            await UpdatePackageApplier.RecoverInterruptedAsync(root).ConfigureAwait(false);
            status = status with { Phase = "applying", Message = "正在验证并替换程序文件 / Verifying and replacing program files", UpdatedAt = DateTimeOffset.UtcNow };
            WriteStatus(request, status);
            TryLog(request.UpdateLogPath, "APPLYING", "Validating signed package and applying file transaction.");
            UpdateApplyResult result;
            try { result = await new UpdatePackageApplier().ApplyAsync(apply).ConfigureAwait(false); }
            catch (Exception applyFailure)
            {
                if (applyFailure is AggregateException) preserveInstallationForRecovery = true;
                throw;
            }
            appliedResult = result;
            status = status with { Phase = "starting", Message = "正在启动并验证新版本 / Starting and validating the new version", UpdatedAt = DateTimeOffset.UtcNow };
            WriteStatus(request, status);
            TryLog(request.UpdateLogPath, "FILES_VERIFIED", $"backup={result.BackupDirectory}");

            try { await UpdatePackageApplier.VerifyInstalledFilesAsync(root, apply.TargetVersion).ConfigureAwait(false); }
            catch (Exception verificationFailure)
            {
                previousRestartAttempted = true;
                await RollbackAndRestartPreviousAsync(result.BackupDirectory, root, apply.RequestId).ConfigureAwait(false);
                throw new UpdateOperationException(InstallExitCode.HealthCheckRolledBack, "Installed inventory verification failed; previous installation restored.", verificationFailure);
            }
            lease.Dispose();
            var executable = Path.Combine(root, "NetBootDhcpTool.exe");
            try { launched = StartTarget(executable, root, request); }
            catch (Exception startFailure)
            {
                previousRestartAttempted = true;
                await RollbackAndRestartPreviousAsync(result.BackupDirectory, root, apply.RequestId).ConfigureAwait(false);
                throw new UpdateOperationException(InstallExitCode.HealthCheckRolledBack, "新版无法启动，已恢复旧版本并尝试重新启动。 / The new version could not start; the old version was restored and a restart was attempted.", startFailure);
            }

            var health = await WaitForHealthAsync(request, launched).ConfigureAwait(false);
            UpdateTestEnvironment.Checkpoint("health-check", root);
            if (health)
            {
                await UpdatePackageApplier.VerifyInstalledFilesAsync(root, apply.TargetVersion).ConfigureAwait(false);
                UpdatePackageApplier.MarkHealthy(result.BackupDirectory, apply.RequestId);
                RememberInstallLocation(root, request.UpdateLogPath);
                WriteFinal(request, InstallExitCode.Success, "completed", "新版已启动并通过校验 / New version started and passed its health check");
                TryLog(request.UpdateLogPath, "HEALTHY", $"version={apply.TargetVersion}");
                TryDelete(request.RequestFilePath);
                TryDelete(request.AcceptedFilePath);
                TryDelete(request.HealthFilePath);
                TryDelete(apply.PackagePath);
                return;
            }

            if (launched is { HasExited: false })
            {
                // This exact process was started by this transaction; stop it before restoring its files.
                launched.CloseMainWindow();
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await launched.WaitForExitAsync(stopTimeout.Token); }
                catch (OperationCanceledException)
                {
                    launched.Kill();
                    using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await launched.WaitForExitAsync(killTimeout.Token);
                }
            }

            previousRestartAttempted = true;
            await RollbackAndRestartPreviousAsync(result.BackupDirectory, root, apply.RequestId).ConfigureAwait(false);
            WriteStatus(request, status with { Phase = "rolled-back", Message = "新版启动校验失败，已恢复旧版本 / New version health check failed; the previous version was restored", UpdatedAt = DateTimeOffset.UtcNow });
            TryLog(request.UpdateLogPath, "ROLLED_BACK", "New version exited before health confirmation; restored the previous installation.");
            throw new UpdateOperationException(InstallExitCode.HealthCheckRolledBack, "新版未能通过启动校验，已恢复旧版本并尝试重新启动。 / The new version failed its startup check; the old version was restored and a restart was attempted.");
        }
        catch (Exception transactionFailure)
        {
            if (appliedResult is not null && !preserveInstallationForRecovery && !previousRestartAttempted)
            {
                if (launched is { HasExited: false })
                {
                    launched.Kill();
                    using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await launched.WaitForExitAsync(killTimeout.Token);
                }
                previousRestartAttempted = true;
                await RollbackAndRestartPreviousAsync(appliedResult.BackupDirectory, root, apply.RequestId);
                throw new UpdateOperationException(InstallExitCode.HealthCheckRolledBack, "Post-replacement validation failed; previous installation restored.", transactionFailure);
            }
            if (!preserveInstallationForRecovery && !previousRestartAttempted && (launched is null || launched.HasExited))
            {
                try { await StartPreviousVersionAsync(root).ConfigureAwait(false); }
                catch (Exception restartFailure) { throw new AggregateException("The previous application could not be restarted after the update failed.", transactionFailure, restartFailure); }
            }
            throw;
        }
    }

    private static void RememberInstallLocation(string root, string logPath)
    {
        if (UpdateTestEnvironment.IsActive) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\NetBootDhcpTool", writable: true);
            key?.SetValue("InstallPath", Path.GetFullPath(root), RegistryValueKind.String);
            key?.Flush();
            TryLog(logPath, "INSTALL_LOCATION_REGISTERED", $"path={Path.GetFullPath(root)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            TryLog(logPath, "WARN_INSTALL_LOCATION_REGISTRATION", ex.Message);
        }
    }

    private static async Task RollbackAndRestartPreviousAsync(string backupDirectory, string root, string requestId)
    {
        Exception? rollbackFailure = null;
        try { await UpdatePackageApplier.RollbackAsync(backupDirectory, root, requestId).ConfigureAwait(false); }
        catch (Exception ex) { rollbackFailure = ex; }
        try { await StartPreviousVersionAsync(root).ConfigureAwait(false); }
        catch (Exception ex) { rollbackFailure = rollbackFailure is null ? ex : new AggregateException(rollbackFailure, ex); }
        if (rollbackFailure is not null)
            throw new AggregateException("The previous files were restored or retained, but their inventory verification or restart needs attention.", rollbackFailure);
    }

    private static void ValidateRequest(UpdateProcessRequest request)
    {
        var apply = request.Apply;
        UpdateTestEnvironment.RequirePath(apply.InstallRoot);
        UpdateTestEnvironment.RequirePath(request.UserDataDirectory);
        var expectedDefaultData = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBootDhcpTool"));
        var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.UserDataDirectory));
        var injected = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");
        var testData = UpdateTestEnvironment.Root is { } testRoot ? Path.Combine(testRoot, "user-data") : "";
        if (!dataRoot.Equals(expectedDefaultData, StringComparison.OrdinalIgnoreCase)
            && !dataRoot.Equals(testData, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(injected) || !dataRoot.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(injected)), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("更新数据目录不在应用数据目录中。 / Update data directory is not approved.");
        var updates = Path.Combine(dataRoot, "updates");
        RequireChildPath(updates, request.RequestFilePath);
        RequireChildPath(Path.Combine(updates, "health"), request.AcceptedFilePath);
        RequireChildPath(Path.Combine(updates, "health"), request.HealthFilePath);
        RequireChildPath(Path.Combine(updates, "staging"), apply.PackagePath);
        if (!IsToken(request.HealthToken) || request.ParentProcessId <= 0 || string.IsNullOrWhiteSpace(apply.RequestId)
            || apply.RequestId.Length > 80 || apply.RequestId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            throw new InvalidDataException("更新请求标识无效。 / Update request identifiers are invalid.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(apply.InstallRoot));
        if (root.Equals(dataRoot, StringComparison.OrdinalIgnoreCase)
            || (!Directory.Exists(root) && !request.IsFullInstall)
            || request.IsFullInstall != apply.IsFullInstall)
            throw new InvalidDataException("应用安装目录无效。 / Application installation directory is invalid.");
        if (!request.IsFullInstall && !request.WaitForParentExit)
            throw new InvalidDataException("Automatic updates must wait for the application to exit.");
        var expectedLog = Path.Combine(updates, "update.log");
        if (!Path.GetFullPath(request.UpdateLogPath).Equals(expectedLog, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新日志路径无效。 / Update log path is invalid.");
    }

    private static Process GetExpectedParent(int processId, string installRoot)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            var expectedExe = Path.GetFullPath(Path.Combine(installRoot, "NetBootDhcpTool.exe"));
            var actualExe = Path.GetFullPath(process.MainModule?.FileName ?? "");
            if (!actualExe.Equals(expectedExe, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("等待退出的进程不是目标安装目录中的 NetBoot DHCP Tool。 / The process is not the application from the target installation directory.");
            return process;
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }

    private static Process GetExpectedSetupParent(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            var actualExe = Path.GetFileName(process.MainModule?.FileName ?? "");
            if (!actualExe.Equals("NetBootDhcpTool.SetupHelper.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("完整安装事务不是由产品安装助手发起。 / The full-install transaction was not started by the product setup helper.");
            return process;
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }

    private static async Task<SingleInstanceLease> WaitForDataLeaseAsync(string dataDirectory, string installRoot)
    {
        var paths = new AppPaths(installRoot);
        if (!Path.GetFullPath(paths.DataDirectory).Equals(Path.GetFullPath(dataDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Application data directory changed while the update was waiting.");
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            var lease = SingleInstanceLease.TryAcquire(paths);
            if (lease is not null) return lease;
            await Task.Delay(250).ConfigureAwait(false);
        }
        throw new TimeoutException("主程序已退出，但单实例锁未释放；没有修改安装文件。 / The application exited but its single-instance lock remained held; no files were changed.");
    }

    private static Process? StartTarget(string executable, string root, UpdateProcessRequest request)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Target application executable is missing.", executable);
        using (var header = File.OpenRead(executable))
        {
            if (header.Length < 64 || header.ReadByte() != 'M' || header.ReadByte() != 'Z')
                throw new InvalidDataException("The target application is not a Windows executable.");
        }
        TryLog(request.UpdateLogPath, "LAUNCH_START", $"root={root} target={request.Apply.TargetVersion}");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--update-health-file");
        start.ArgumentList.Add(request.HealthFilePath);
        start.ArgumentList.Add("--update-health-token");
        start.ArgumentList.Add(request.HealthToken);
        start.ArgumentList.Add("--update-target-version");
        start.ArgumentList.Add(request.Apply.TargetVersion);
        var process = Process.Start(start);
        TryLog(request.UpdateLogPath, "LAUNCH_STARTED", $"pid={process?.Id}");
        return process;
    }

    private static async Task<bool> WaitForHealthAsync(UpdateProcessRequest request, Process? launched)
    {
        var deadline = Stopwatch.StartNew();
        var healthTimeout = UpdateTestEnvironment.IsActive && Environment.GetEnvironmentVariable("NETBOOT_TEST_HEALTH_TIMEOUT") == "short"
            ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(90);
        while (deadline.Elapsed < healthTimeout)
        {
            if (File.Exists(request.HealthFilePath))
            {
                var token = await File.ReadAllTextAsync(request.HealthFilePath).ConfigureAwait(false);
                if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(request.HealthToken)))
                    return true;
            }
            if (launched is { HasExited: true }) return false;
            await Task.Delay(200).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task StartPreviousVersionAsync(string root)
    {
        var executable = Path.Combine(root, "NetBootDhcpTool.exe");
        if (!File.Exists(executable)) return;
        try
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
            Process.Start(start);
            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (Exception ex) { throw new IOException("Previous version was restored, but could not be started automatically.", ex); }
    }

    private static void WriteAccepted(UpdateProcessRequest request)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.AcceptedFilePath)!);
        WriteAtomically(request.AcceptedFilePath, request.HealthToken);
        TryLog(request.UpdateLogPath, "ACCEPTED", $"request={request.Apply.RequestId}");
    }

    private static void TryWriteStatus(UpdateProcessRequest request, string phase, string message)
    {
        try { WriteStatus(request, new UpdateTransactionStatus(request.Apply.RequestId, request.Apply.CurrentVersion, request.Apply.TargetVersion, phase, message, DateTimeOffset.UtcNow)); }
        catch { }
    }

    private static void WriteStatus(UpdateProcessRequest request, UpdateTransactionStatus status)
    {
        UpdateResultProtocol.Write(request, status);
    }

    private static void WriteFinal(UpdateProcessRequest request, InstallExitCode code, string phase, string message)
        => WriteStatus(request, new UpdateTransactionStatus(request.Apply.RequestId, request.Apply.CurrentVersion,
            request.Apply.TargetVersion, phase, message, DateTimeOffset.UtcNow, code));

    private static void ShowFailureMessage(string details)
    {
        try { _ = MessageBoxW(IntPtr.Zero, $"更新未能完成，现有安装已保留或已恢复。请查看更新日志。\nThe update could not be completed. The existing installation was preserved or restored. See the update log.\n\n{details}", "NetBoot DHCP Tool Update", 0x00000010); }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW", SetLastError = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    private static void TryLog(string path, string phase, string details)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}\t{phase}\t{details}{Environment.NewLine}");
        }
        catch { }
    }

    private static void WriteAtomically(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, fullPath, overwrite: true);
    }

    private static void RequireChildPath(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新请求包含越界文件路径。 / Update request contains a path outside its data directory.");
    }

    private static bool IsToken(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

}
