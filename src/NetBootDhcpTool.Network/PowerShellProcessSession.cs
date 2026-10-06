using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NetBootDhcpTool.Network;

// Private, workflow-owned pipes only. Commands remain sequential; callers commit each result
// before submitting the next write. A failed request is never replayed by this transport.
internal sealed class PowerShellProcessSession : IAsyncDisposable
{
    private const string Prefix = "NETBOOT_PS_RESULT:";
    private readonly SemaphoreSlim _gate = new(1);
    private readonly TimeSpan _timeout;
    private readonly Action<int>? _processStarted;
    private Process? _process;
    private Task? _errorDrain;
    private bool _disposed;

    internal PowerShellProcessSession(TimeSpan timeout, Action<int>? processStarted)
    {
        _timeout = timeout;
        _processStarted = processStarted;
    }

    // Only starts module initialization; it never submits a network command.
    internal void Prewarm() => EnsureStarted();

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null) return;
        var startInfo = PowerShellProcessRunner.CreateStartInfo(Bootstrap);
        startInfo.RedirectStandardInput = true;
        startInfo.StandardInputEncoding = new UTF8Encoding(false);
        _process = new Process { StartInfo = startInfo };
        if (!_process.Start()) throw new InvalidOperationException("PowerShell session did not start.");
        _processStarted?.Invoke(_process.Id);
        _errorDrain = DrainErrorsAsync(_process.StandardError);
    }

    internal async Task<string> RunAsync(string script, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        cancellationToken.ThrowIfCancellationRequested();
        try { await _gate.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("PowerShell session queue wait was canceled.", error, cancellationToken);
            throw new TimeoutException($"PowerShell session queue wait exceeded the {_timeout.TotalSeconds:0.#}-second limit.", error);
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            deadline.Token.ThrowIfCancellationRequested();
            // Drain concurrently, including large diagnostic streams, without retaining secrets/logs.
            EnsureStarted();

            var id = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(new { Id = id, Script = script });
            await _process!.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            var line = await _process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("PowerShell session ended or returned an invalid response; command was not replayed.");
            using var response = JsonDocument.Parse(Convert.FromBase64String(line[Prefix.Length..]));
            var value = response.RootElement;
            if (value.GetProperty("Id").GetString() != id)
                throw new InvalidOperationException("PowerShell session response identity mismatch; command was not replayed.");
            if (!value.GetProperty("Succeeded").GetBoolean())
                throw new InvalidOperationException(value.GetProperty("Error").GetString());
            return value.GetProperty("Output").GetString()?.Trim() ?? string.Empty;
        }
        catch (Exception error)
        {
            var cleanup = await ReleaseProcessAsync(graceful: false).ConfigureAwait(false);
            if (cleanup is not null)
                throw new AggregateException("PowerShell session failed and its process tree could not be confirmed stopped.", error, cleanup);
            if (error is OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("PowerShell session command was canceled.", error, cancellationToken);
                throw new TimeoutException($"PowerShell session command exceeded the {_timeout.TotalSeconds:0.#}-second limit.", error);
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            var error = await ReleaseProcessAsync(graceful: true).ConfigureAwait(false);
            if (error is not null) throw new InvalidOperationException("PowerShell session could not be confirmed stopped.", error);
        }
        finally { _gate.Release(); }
    }

    private async Task<Exception?> ReleaseProcessAsync(bool graceful)
    {
        var process = _process;
        if (process is null) return null;
        _process = null;
        Exception? failure = null;
        try
        {
            if (graceful && !process.HasExited)
            {
                process.StandardInput.Close(); // EOF ends the loop; no permanent worker remains.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var reapDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(reapDeadline.Token).ConfigureAwait(false);
            if (_errorDrain is not null) await _errorDrain.WaitAsync(reapDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; }
        finally { process.Dispose(); _errorDrain = null; }
        return failure;
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { }
    }

    private const string Bootstrap = """
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
# Global module state is reused; request variables stay in each child script scope.
Import-Module NetAdapter,NetTCPIP,NetSecurity -Global -ErrorAction Stop
while ($null -ne ($requestLine=[Console]::In.ReadLine())) {
  $request=ConvertFrom-Json -InputObject $requestLine -ErrorAction Stop
  $result=@{ Id=[string]$request.Id; Succeeded=$false; Output=''; Error='' }
  try {
    $block=[scriptblock]::Create([string]$request.Script)
    $result.Output=(& $block | Out-String -Width 4096)
    $result.Succeeded=$true
  } catch { $result.Error=$_.ToString() }
  $json=ConvertTo-Json -InputObject $result -Compress -Depth 4
  [Console]::Out.WriteLine('NETBOOT_PS_RESULT:'+ [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)))
}
""";
}
