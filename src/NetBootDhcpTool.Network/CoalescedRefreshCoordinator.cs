namespace NetBootDhcpTool.Network;

/// <summary>Runs one refresh at a time and folds arrivals during a read into one latest follow-up request.</summary>
public sealed class CoalescedRefreshCoordinator<TRequest, TResult> : IAsyncDisposable where TResult : class
{
    private readonly object _sync = new();
    private readonly Func<TRequest, CancellationToken, Task<TResult?>> _readAsync;
    private readonly Func<TRequest, TResult, Task> _commitAsync;
    private readonly Action<TRequest, Exception>? _onError;
    private readonly CancellationTokenSource _lifetime = new();
    private TRequest? _pending;
    private Task? _worker;
    private bool _hasPending;
    private bool _disposed;

    public CoalescedRefreshCoordinator(
        Func<TRequest, CancellationToken, Task<TResult?>> readAsync,
        Func<TRequest, TResult, Task> commitAsync,
        Action<TRequest, Exception>? onError = null)
    {
        _readAsync = readAsync ?? throw new ArgumentNullException(nameof(readAsync));
        _commitAsync = commitAsync ?? throw new ArgumentNullException(nameof(commitAsync));
        _onError = onError;
    }

    public bool IsRunning { get { lock (_sync) return _worker != null; } }

    public void Request(TRequest request)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _pending = request;
            _hasPending = true;
            _worker ??= Task.Run(ProcessAsync);
        }
    }

    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task? worker;
            lock (_sync) worker = _worker;
            if (worker == null) return;
            await worker.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_sync)
        {
            if (_disposed)
            {
                worker = _worker;
            }
            else
            {
                _disposed = true;
                _hasPending = false;
                _pending = default;
                worker = _worker;
                _lifetime.Cancel();
            }
        }
        if (worker != null) await worker.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            TRequest request;
            lock (_sync)
            {
                if (_disposed || !_hasPending)
                {
                    _worker = null;
                    return;
                }
                request = _pending!;
                _pending = default;
                _hasPending = false;
            }

            try
            {
                var result = await _readAsync(request, _lifetime.Token).ConfigureAwait(false);
                if (result != null && !_lifetime.IsCancellationRequested)
                    await _commitAsync(request, result).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                try { _onError?.Invoke(request, ex); }
                catch { /* Error reporting must not keep the refresh loop stuck. */ }
            }
        }
    }
}
