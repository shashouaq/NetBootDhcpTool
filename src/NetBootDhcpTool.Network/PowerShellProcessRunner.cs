using System.Diagnostics;
using System.Text;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

internal sealed class PowerShellProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly PowerShellScriptExecutor? _scriptExecutor;
    private readonly TimeSpan _timeout;
    private readonly Action<int>? _processStarted;

    public PowerShellProcessRunner(
        ILogger logger,
        PowerShellScriptExecutor? scriptExecutor = null,
        TimeSpan? timeout = null,
        Action<int>? processStarted = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scriptExecutor = scriptExecutor;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _processStarted = processStarted;
    }

    public async Task<string> RunAsync(string script, string summary, CancellationToken cancellationToken, bool logOutput)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        var timer = Stopwatch.StartNew();
        _logger.Info(summary);

        if (_scriptExecutor is not null)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = await _scriptExecutor(script, summary, cancellationToken, logOutput).ConfigureAwait(false);
                _logger.Info($"{summary} completed by injected executor: elapsedMs={timer.Elapsed.TotalMilliseconds:0}");
                if (logOutput) _logger.Info($"{summary} result: exit=0 detail={Summarize(output.Trim())}");
                return output;
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"{summary} canceled by injected executor: elapsedMs={timer.Elapsed.TotalMilliseconds:0}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"{summary} failed in injected executor: elapsedMs={timer.Elapsed.TotalMilliseconds:0} detail={Summarize(ex.Message)}");
                throw;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var process = new Process();
        var processStarted = false;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            process.StartInfo = CreateStartInfo(script);
            if (!process.Start()) throw new InvalidOperationException("PowerShell process did not start.");
            processStarted = true;
            _processStarted?.Invoke(process.Id);

            var (output, error) = await PowerShellProcessOutput.ReadStandardStreamsAsync(process, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            output = output.Trim();
            error = error.Trim();
            if (process.ExitCode != 0)
            {
                var detail = Summarize(error);
                _logger.Warn($"{summary} failed: exit={process.ExitCode} elapsedMs={timer.Elapsed.TotalMilliseconds:0} detail={detail}");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"PowerShell exit {process.ExitCode}" : error);
            }

            _logger.Info(logOutput
                ? $"{summary} completed: exit={process.ExitCode} elapsedMs={timer.Elapsed.TotalMilliseconds:0} detail={Summarize(output)}"
                : $"{summary} completed: exit={process.ExitCode} elapsedMs={timer.Elapsed.TotalMilliseconds:0}");
            return output;
        }
        catch (OperationCanceledException ex)
        {
            var cleanupError = processStarted ? await StopAndReapAsync(process).ConfigureAwait(false) : null;
            _logger.Warn($"{summary} {(cancellationToken.IsCancellationRequested ? "canceled" : "timed out")}: elapsedMs={timer.Elapsed.TotalMilliseconds:0}");
            if (cleanupError is not null)
                throw new AggregateException($"{summary} was interrupted and its PowerShell process tree could not be confirmed stopped.", ex, cleanupError);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException($"{summary} was canceled.", ex, cancellationToken);
            throw new TimeoutException($"{summary} exceeded the {_timeout.TotalSeconds:0.#}-second PowerShell limit.", ex);
        }
        catch (Exception operationError)
        {
            var cleanupError = processStarted && !HasExited(process)
                ? await StopAndReapAsync(process).ConfigureAwait(false)
                : null;
            if (cleanupError is not null)
                throw new AggregateException($"{summary} failed and its PowerShell process tree could not be confirmed stopped.", operationError, cleanupError);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string script)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("Windows PowerShell was not found / 未找到 Windows PowerShell");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(ConfigureUtf8Output(script))));
        return startInfo;
    }

    private static string ConfigureUtf8Output(string script)
    {
        const string utf8Prelude = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding;";
        return utf8Prelude + Environment.NewLine + script;
    }

    private static async Task<Exception?> StopAndReapAsync(Process process)
    {
        Exception? terminationError = null;
        try
        {
            if (!HasExited(process)) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (HasExited(process)) { }
        catch (Exception ex) { terminationError = ex; }

        try
        {
            using var wait = new CancellationTokenSource(TerminationWait);
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            terminationError = terminationError is null ? ex : new AggregateException(terminationError, ex);
        }
        return terminationError;
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static string Summarize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "OK";
        var normalized = string.Join(" | ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length > 240 ? normalized[..240] + "..." : normalized;
    }
}
