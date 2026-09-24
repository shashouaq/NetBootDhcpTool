namespace NetBootDhcpTool.Core;

/// <summary>Owns a single cancellable network workflow and releases it only for its owner.</summary>
public sealed class NetworkWorkflowCoordinator
{
    private readonly object _sync = new();
    private NetworkWorkflowLease? _activeLease;

    public bool IsActive
    {
        get { lock (_sync) return _activeLease is not null; }
    }

    public NetworkWorkflowLease? TryAcquire(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An operation name is required.", nameof(name));
        lock (_sync)
        {
            if (_activeLease is not null) return null;
            _activeLease = new NetworkWorkflowLease(this, name);
            return _activeLease;
        }
    }

    internal void Release(NetworkWorkflowLease lease)
    {
        var released = false;
        lock (_sync)
        {
            if (ReferenceEquals(_activeLease, lease))
            {
                _activeLease = null;
                released = true;
            }
        }
        if (released) lease.MarkReleased();
    }
}

public sealed class NetworkWorkflowLease : IDisposable
{
    private readonly NetworkWorkflowCoordinator _owner;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal NetworkWorkflowLease(NetworkWorkflowCoordinator owner, string name)
    {
        _owner = owner;
        Name = name;
    }

    public string Name { get; }
    public CancellationToken Token => _cancellation.Token;
    public Task Completion => _completion.Task;

    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) == 0) _cancellation.Cancel();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner.Release(this);
        _cancellation.Dispose();
    }

    internal void MarkReleased() => _completion.TrySetResult();
}
