namespace NetBootDhcpTool.Core;

/// <summary>Serializes immutable snapshots and replaces pending work with the newest complete state.</summary>
public sealed class CoalescingSnapshotWriter<T>(Func<IReadOnlyList<T>, Task> save) : IAsyncDisposable
{
    private sealed record Pending(long Version, IReadOnlyList<T> Snapshot);
    private sealed record Waiter(long Version, TaskCompletionSource Completion);

    private readonly object _sync = new();
    private readonly Func<IReadOnlyList<T>, Task> _save = save ?? throw new ArgumentNullException(nameof(save));
    private readonly List<Waiter> _waiters = [];
    private Pending? _pending;
    private Task? _pump;
    private Task? _disposeTask;
    private long _nextVersion;
    private bool _disposed;
    private IReadOnlyList<T>? _lastFailedSnapshot;

    public IReadOnlyList<T>? LastFailedSnapshot
    {
        get { lock (_sync) return _lastFailedSnapshot; }
    }

    public Task SaveAsync(IEnumerable<T> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        TaskCompletionSource completion;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var version = ++_nextVersion;
            _pending = new Pending(version, Array.AsReadOnly(snapshot.ToArray()));
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new Waiter(version, completion));
            _pump ??= Task.Run(PumpAsync);
        }
        return completion.Task;
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask != null) return new ValueTask(_disposeTask);
            _disposed = true;
            _disposeTask = _pump ?? Task.CompletedTask;
            return new ValueTask(_disposeTask);
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Pending current;
            lock (_sync)
            {
                if (_pending == null)
                {
                    _pump = null;
                    return;
                }
                current = _pending;
                _pending = null;
            }

            Exception? failure = null;
            try { await _save(current.Snapshot).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }

            List<Waiter> completed;
            bool hasNewerPending;
            bool stopPump;
            lock (_sync)
            {
                hasNewerPending = _pending is { } pending && pending.Version > current.Version;
                completed = _waiters.Where(x => x.Version <= current.Version).ToList();
                _waiters.RemoveAll(x => x.Version <= current.Version);
                if (failure != null) _lastFailedSnapshot = current.Snapshot;
                else _lastFailedSnapshot = null;

                stopPump = failure != null ? !hasNewerPending : _pending == null;
                if (stopPump) _pump = null;
            }

            foreach (var waiter in completed)
            {
                if (failure == null) waiter.Completion.TrySetResult();
                else waiter.Completion.TrySetException(failure);
            }
            if (stopPump) return;
        }
    }
}
