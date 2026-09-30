using System.Diagnostics;
using System.Text.Json;

namespace NetBootDhcpTool.Core;

// Stable deployment exit codes. Zero always means the new installation is healthy.
public enum InstallExitCode
{
    Success = 0,
    PreflightFailure = 10,
    DownloadFailure = 11,
    VerificationFailure = 12,
    InsufficientSpace = 13,
    AccessDenied = 14,
    Cancelled = 16,
    RolledBack = 21,
    RollbackFailed = 22,
    HealthCheckRolledBack = 23,
    UpdaterTerminated = 24,
    TimedOut = 25
}

public sealed class UpdateOperationException(InstallExitCode code, string message, Exception? inner = null) : IOException(message, inner)
{
    public InstallExitCode Code { get; } = code;
}

public sealed record UpdateTransactionStatus(string RequestId, string CurrentVersion, string TargetVersion,
    string Phase, string Message, DateTimeOffset UpdatedAt, InstallExitCode? ExitCode = null)
{
    public bool IsTerminal => ExitCode.HasValue;
}

public static class UpdateResultProtocol
{
    public static readonly TimeSpan InstallationTimeout = TimeSpan.FromMinutes(15);
    public static string StatusPath(UpdateProcessRequest request) => Path.Combine(Path.GetDirectoryName(request.AcceptedFilePath)!, request.Apply.RequestId + ".status.json");

    public static void Write(UpdateProcessRequest request, UpdateTransactionStatus status)
    {
        var json = JsonSerializer.Serialize(status, JsonStore.Options);
        WriteDurably(StatusPath(request), json);
        WriteDurably(Path.Combine(Path.GetDirectoryName(request.UpdateLogPath)!, "update-status.json"), json);
    }

    public static void WriteDurably(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static InstallExitCode Classify(Exception exception) => exception switch
    {
        UpdateOperationException operation => operation.Code,
        UnauthorizedAccessException => InstallExitCode.AccessDenied,
        OperationCanceledException => InstallExitCode.Cancelled,
        TimeoutException => InstallExitCode.TimedOut,
        InvalidDataException or System.Security.Cryptography.CryptographicException or FormatException => InstallExitCode.VerificationFailure,
        _ => InstallExitCode.PreflightFailure
    };

    public static async Task<InstallExitCode> WaitForFinalResultAsync(UpdateProcessRequest request, Process updater,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? InstallationTimeout);
        try { await updater.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Do not kill an installer during replacement. Keep its journal for recovery.
            return InstallExitCode.TimedOut;
        }
        var path = StatusPath(request);
        if (!File.Exists(path)) return InstallExitCode.UpdaterTerminated;
        var result = JsonSerializer.Deserialize<UpdateTransactionStatus>(await File.ReadAllBytesAsync(path, ct), JsonStore.Options);
        if (result is null || result.RequestId != request.Apply.RequestId || result.TargetVersion != request.Apply.TargetVersion
            || !result.IsTerminal || (int)result.ExitCode!.Value != updater.ExitCode)
            return InstallExitCode.UpdaterTerminated;
        if (result.ExitCode == InstallExitCode.Success && result.Phase != "completed") return InstallExitCode.UpdaterTerminated;
        return result.ExitCode!.Value;
    }
}
