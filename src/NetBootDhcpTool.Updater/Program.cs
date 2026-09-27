using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Updater;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2 || !args[0].Equals("--apply", StringComparison.Ordinal)) return 2;
        UpdateProcessRequest? request = null;
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
            using (GetExpectedParent(request.ParentProcessId, Path.GetFullPath(request.Apply.InstallRoot))) { }
            new UpdatePackageApplier().ApplyAsync(request.Apply, validateOnly: true).GetAwaiter().GetResult();
            WriteAccepted(request);
        }
        catch (Exception ex)
        {
            if (request is not null)
            {
                TryLog(request.UpdateLogPath, "FAILED", ex.ToString());
                TryWriteStatus(request, "failed", ex.Message);
            }
            ShowFailureMessage();
            return 1;
        }

        var exitCode = 0;
        using var window = new UpdateProgressWindow();
        window.Shown += async (_, _) =>
        {
            try
            {
                await RunTransactionAsync(request!, window.ShowStatus).ConfigureAwait(true);
                window.ShowStatus("completed", "新版已启动并通过校验 / New version started and passed its health check");
                await Task.Delay(900).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                TryLog(request!.UpdateLogPath, "FAILED", ex.ToString());
                TryWriteStatus(request, "failed", ex.Message);
                window.ShowFailure(ex.Message);
                ShowFailureMessage();
                exitCode = 1;
            }
            finally { window.Close(); }
        };
        Application.Run(window);
        return exitCode;
    }

    private static async Task RunTransactionAsync(UpdateProcessRequest request, Action<string, string>? showStatus = null)
    {
        var apply = request.Apply;
        var root = Path.GetFullPath(apply.InstallRoot);
        var status = new UpdateStatus(request.Apply.RequestId, apply.CurrentVersion, apply.TargetVersion, "waiting-for-exit", "正在等待主程序正常退出 / Waiting for the application to close normally", DateTimeOffset.UtcNow);
        WriteStatus(request, status, showStatus);
        TryLog(request.UpdateLogPath, "WAITING_FOR_EXIT", $"pid={request.ParentProcessId} root={root}");

        var parent = GetExpectedParent(request.ParentProcessId, root);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
        {
            try { await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException("主程序未能在 4 分钟内正常退出；更新器没有修改安装目录。 / The application did not exit within four minutes; no installation files were changed.");
            }
            finally { parent.Dispose(); }
        }

        Process? launched = null;
        var preserveInstallationForRecovery = false;
        var previousRestartAttempted = false;
        try
        {
            using var lease = await WaitForDataLeaseAsync(request.UserDataDirectory, root).ConfigureAwait(false);
            status = status with { Phase = "applying", Message = "正在验证并替换程序文件 / Verifying and replacing program files", UpdatedAt = DateTimeOffset.UtcNow };
            WriteStatus(request, status, showStatus);
            TryLog(request.UpdateLogPath, "APPLYING", "Validating signed package and applying file transaction.");
            UpdateApplyResult result;
            try { result = await new UpdatePackageApplier().ApplyAsync(apply).ConfigureAwait(false); }
            catch (Exception applyFailure)
            {
                if (applyFailure is AggregateException) preserveInstallationForRecovery = true;
                throw;
            }
            status = status with { Phase = "starting", Message = "正在启动并验证新版本 / Starting and validating the new version", UpdatedAt = DateTimeOffset.UtcNow };
            WriteStatus(request, status, showStatus);
            TryLog(request.UpdateLogPath, "FILES_VERIFIED", $"backup={result.BackupDirectory}");

            try { await UpdatePackageApplier.VerifyInstalledFilesAsync(root, apply.TargetVersion).ConfigureAwait(false); }
            catch
            {
                previousRestartAttempted = true;
                await RollbackAndRestartPreviousAsync(result.BackupDirectory, root, apply.RequestId).ConfigureAwait(false);
                throw;
            }
            lease.Dispose();
            var executable = Path.Combine(root, "NetBootDhcpTool.exe");
            try { launched = StartTarget(executable, root, request); }
            catch (Exception startFailure)
            {
                previousRestartAttempted = true;
                await RollbackAndRestartPreviousAsync(result.BackupDirectory, root, apply.RequestId).ConfigureAwait(false);
                throw new InvalidOperationException("新版无法启动，已恢复旧版本并尝试重新启动。 / The new version could not start; the old version was restored and a restart was attempted.", startFailure);
            }

            var health = await WaitForHealthAsync(request, launched).ConfigureAwait(false);
            if (health)
            {
                UpdatePackageApplier.MarkHealthy(result.BackupDirectory, apply.RequestId);
                WriteStatus(request, status with { Phase = "completed", Message = "新版已启动并通过校验 / New version started and passed its health check", UpdatedAt = DateTimeOffset.UtcNow }, showStatus);
                TryLog(request.UpdateLogPath, "HEALTHY", $"version={apply.TargetVersion}");
                TryDelete(request.RequestFilePath);
                TryDelete(request.AcceptedFilePath);
                TryDelete(request.HealthFilePath);
                TryDelete(apply.PackagePath);
                return;
            }

            if (launched is { HasExited: false })
            {
                preserveInstallationForRecovery = true;
                WriteStatus(request, status with { Phase = "awaiting-startup", Message = "新版进程仍在运行但未完成健康确认；保留新文件与回滚备份，不强制结束进程。 / The new process is still running without a health confirmation; files and rollback backup are preserved, and the process will not be terminated.", UpdatedAt = DateTimeOffset.UtcNow }, showStatus);
                throw new TimeoutException("新版未在 90 秒内回报启动健康状态；更新器保留现场供恢复。 / The new version did not report healthy startup within 90 seconds; the transaction was preserved for recovery.");
            }

            previousRestartAttempted = true;
            await RollbackAndRestartPreviousAsync(result.BackupDirectory, root, apply.RequestId).ConfigureAwait(false);
            WriteStatus(request, status with { Phase = "rolled-back", Message = "新版启动校验失败，已恢复旧版本 / New version health check failed; the previous version was restored", UpdatedAt = DateTimeOffset.UtcNow }, showStatus);
            TryLog(request.UpdateLogPath, "ROLLED_BACK", "New version exited before health confirmation; restored the previous installation.");
            throw new InvalidOperationException("新版未能通过启动校验，已恢复旧版本并尝试重新启动。 / The new version failed its startup check; the old version was restored and a restart was attempted.");
        }
        catch (Exception transactionFailure)
        {
            if (!preserveInstallationForRecovery && !previousRestartAttempted && (launched is null || launched.HasExited))
            {
                try { await StartPreviousVersionAsync(root).ConfigureAwait(false); }
                catch (Exception restartFailure) { throw new AggregateException("The previous application could not be restarted after the update failed.", transactionFailure, restartFailure); }
            }
            throw;
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
        var expectedDefaultData = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBootDhcpTool"));
        var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.UserDataDirectory));
        var injected = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");
        if (!dataRoot.Equals(expectedDefaultData, StringComparison.OrdinalIgnoreCase)
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
        if (root.Equals(dataRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root))
            throw new InvalidDataException("应用安装目录无效。 / Application installation directory is invalid.");
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
        return Process.Start(start);
    }

    private static async Task<bool> WaitForHealthAsync(UpdateProcessRequest request, Process? launched)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(90))
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
        try { WriteStatus(request, new UpdateStatus(request.Apply.RequestId, request.Apply.CurrentVersion, request.Apply.TargetVersion, phase, message, DateTimeOffset.UtcNow)); }
        catch { }
    }

    private static void WriteStatus(UpdateProcessRequest request, UpdateStatus status, Action<string, string>? showStatus = null)
    {
        var path = Path.Combine(Path.GetDirectoryName(request.UpdateLogPath)!, "update-status.json");
        WriteAtomically(path, JsonSerializer.Serialize(status, JsonStore.Options));
        showStatus?.Invoke(status.Phase, status.Message);
    }

    private static void ShowFailureMessage()
    {
        try
        {
            MessageBox.Show(
                "更新未能完成，现有安装已保留或已恢复。请查看更新日志。\nThe update could not be completed. The existing installation was preserved or restored. See the update log.",
                "NetBoot DHCP Tool Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch { }
    }

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

    private sealed record UpdateStatus(string RequestId, string CurrentVersion, string TargetVersion, string Phase, string Message, DateTimeOffset UpdatedAt);

    private sealed class UpdateProgressWindow : Form
    {
        private readonly Label _message;
        private readonly ProgressBar _progress;

        public UpdateProgressWindow()
        {
            Text = "NetBoot DHCP Tool Update / 程序升级";
            Width = 540;
            Height = 175;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;

            _message = new Label
            {
                Left = 22,
                Top = 20,
                Width = 480,
                Height = 70,
                Text = "正在等待主程序正常关闭 / Waiting for the application to close normally",
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
            _progress = new ProgressBar
            {
                Left = 22,
                Top = 102,
                Width = 480,
                Height = 22,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 24
            };
            Controls.Add(_message);
            Controls.Add(_progress);
        }

        public void ShowStatus(string phase, string message)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => ShowStatus(phase, message))); } catch (InvalidOperationException) { }
                return;
            }
            _message.Text = $"{phase}\n{message}";
            _progress.Style = phase is "completed" or "rolled-back" ? ProgressBarStyle.Blocks : ProgressBarStyle.Marquee;
            if (_progress.Style == ProgressBarStyle.Blocks) _progress.Value = 100;
        }

        public void ShowFailure(string message)
        {
            ShowStatus("failed", message);
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Value = 0;
        }
    }
}
