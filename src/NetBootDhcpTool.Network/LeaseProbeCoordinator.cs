namespace NetBootDhcpTool.Network;

public readonly record struct LeaseProbeBindingKey(string SessionId, string ClientKey);

public sealed record LeaseProbeIdentity(string SessionId, string ClientKey, string IpAddress, long Generation)
{
    public LeaseProbeBindingKey BindingKey => new(SessionId, ClientKey);
}

public sealed record LeaseProbeRequest(LeaseProbeIdentity Identity, bool IncludeWeb);

public sealed record LeaseProbeResult(LeaseProbeIdentity Identity, long PingLatencyMs, bool? HttpOk, bool? HttpsOk);

public enum LeaseProbeScheduleResult
{
    StartedOrQueued,
    Coalesced,
    QueueFull,
    SessionCancelled
}

public sealed class LeaseProbeCoordinator : IAsyncDisposable
{
    public const int DefaultMaximumConcurrency = 8;
    public const int DefaultMaximumQueued = 64;

    private readonly Func<LeaseProbeRequest, CancellationToken, Task<LeaseProbeResult>> _probeAsync;
    private readonly Func<LeaseProbeResult, CancellationToken, Task> _applyResultAsync;
    private readonly Action<LeaseProbeIdentity, Exception>? _probeFailed;
    private readonly int _maximumConcurrency;
    private readonly int _maximumQueued;
    private readonly object _sync = new();
    private readonly LinkedList<LeaseProbeBindingKey> _queue = new();
    private readonly Dictionary<LeaseProbeBindingKey, PendingProbe> _pending = [];
    private readonly Dictionary<LeaseProbeBindingKey, ActiveProbe> _activeByBinding = [];
    private readonly HashSet<string> _cancelledSessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposeStarted;

    public LeaseProbeCoordinator(
        Func<LeaseProbeRequest, CancellationToken, Task<LeaseProbeResult>> probeAsync,
        Func<LeaseProbeResult, CancellationToken, Task> applyResultAsync,
        Action<LeaseProbeIdentity, Exception>? probeFailed = null,
        int maximumConcurrency = DefaultMaximumConcurrency,
        int maximumQueued = DefaultMaximumQueued)
    {
        _probeAsync = probeAsync ?? throw new ArgumentNullException(nameof(probeAsync));
        _applyResultAsync = applyResultAsync ?? throw new ArgumentNullException(nameof(applyResultAsync));
        _probeFailed = probeFailed;
        if (maximumConcurrency is < 1 or > DefaultMaximumConcurrency) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        if (maximumQueued is < 1 or > DefaultMaximumQueued) throw new ArgumentOutOfRangeException(nameof(maximumQueued));
        _maximumConcurrency = maximumConcurrency;
        _maximumQueued = maximumQueued;
    }

    public int ActiveCount { get { lock (_sync) return _activeByBinding.Count; } }
    public int PendingCount { get { lock (_sync) return _pending.Count; } }

    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task[] activeTasks;
            lock (_sync)
            {
                if (_activeByBinding.Count == 0 && _pending.Count == 0) return;
                activeTasks = _activeByBinding.Values.Select(item => item.Completion.Task).ToArray();
            }
            if (activeTasks.Length == 0) continue;
            await Task.WhenAll(activeTasks).ConfigureAwait(false);
        }
    }

    public LeaseProbeScheduleResult Schedule(LeaseProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Identity);
        CancellationTokenSource? superseded = null;
        LeaseProbeScheduleResult result;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            var identity = request.Identity;
            if (_cancelledSessions.Contains(identity.SessionId)) return LeaseProbeScheduleResult.SessionCancelled;

            _activeByBinding.TryGetValue(identity.BindingKey, out var active);
            if (active is not null && active.Request.Identity == identity)
            {
                if (!request.IncludeWeb || active.Request.IncludeWeb) return LeaseProbeScheduleResult.Coalesced;
                superseded = active.Cancellation;
            }
            else if (active is not null)
            {
                superseded = active.Cancellation;
            }

            if (_pending.TryGetValue(identity.BindingKey, out var pending))
            {
                pending.Request = request;
                result = LeaseProbeScheduleResult.Coalesced;
            }
            else if (_pending.Count >= _maximumQueued)
            {
                result = LeaseProbeScheduleResult.QueueFull;
            }
            else
            {
                var item = new PendingProbe(request);
                item.QueueNode = _queue.AddLast(identity.BindingKey);
                _pending.Add(identity.BindingKey, item);
                result = LeaseProbeScheduleResult.StartedOrQueued;
            }

            StartAvailableLocked();
        }

        if (superseded is not null)
        {
            try { superseded.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return result;
    }

    public void InvalidateBinding(LeaseProbeBindingKey bindingKey)
    {
        CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            RemovePendingLocked(bindingKey);
            if (_activeByBinding.TryGetValue(bindingKey, out var active)) cancellation = active.Cancellation;
        }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task CancelSessionAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ActiveProbe[] active;
        lock (_sync)
        {
            _cancelledSessions.Add(sessionId);
            foreach (var key in _pending.Keys.Where(key => key.SessionId.Equals(sessionId, StringComparison.Ordinal)).ToArray())
                RemovePendingLocked(key);
            active = _activeByBinding.Values
                .Where(item => item.Request.Identity.SessionId.Equals(sessionId, StringComparison.Ordinal))
                .ToArray();
        }

        foreach (var item in active)
        {
            try { item.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        await Task.WhenAll(active.Select(item => item.Completion.Task)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        ActiveProbe[] active;
        lock (_sync)
        {
            if (_disposeStarted)
            {
                active = _activeByBinding.Values.ToArray();
            }
            else
            {
                _disposeStarted = true;
                _pending.Clear();
                _queue.Clear();
                active = _activeByBinding.Values.ToArray();
            }
        }

        try { _lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
        foreach (var item in active)
        {
            try { item.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        await Task.WhenAll(active.Select(item => item.Completion.Task)).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void StartAvailableLocked()
    {
        while (!_disposeStarted && _activeByBinding.Count < _maximumConcurrency)
        {
            var node = _queue.First;
            while (node is not null && _activeByBinding.ContainsKey(node.Value)) node = node.Next;
            if (node is null) return;
            _queue.Remove(node);
            if (!_pending.Remove(node.Value, out var pending)) continue;
            pending.QueueNode = null;
            if (_cancelledSessions.Contains(pending.Request.Identity.SessionId)) continue;

            var active = new ActiveProbe(pending.Request, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _activeByBinding.Add(node.Value, active);
            _ = Task.Run(() => RunProbeAsync(node.Value, active));
        }
    }

    private async Task RunProbeAsync(LeaseProbeBindingKey bindingKey, ActiveProbe active)
    {
        await Task.Yield();
        try
        {
            var result = await _probeAsync(active.Request, active.Cancellation.Token).ConfigureAwait(false);
            if (result.Identity != active.Request.Identity)
                throw new InvalidOperationException("Lease probe returned a result for a different binding generation.");
            active.Cancellation.Token.ThrowIfCancellationRequested();
            await _applyResultAsync(result, active.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            try { _probeFailed?.Invoke(active.Request.Identity, ex); }
            catch { }
        }
        finally
        {
            active.Cancellation.Dispose();
            lock (_sync)
            {
                if (_activeByBinding.TryGetValue(bindingKey, out var current) && ReferenceEquals(current, active))
                    _activeByBinding.Remove(bindingKey);
                active.Completion.TrySetResult();
                StartAvailableLocked();
            }
        }
    }

    private void RemovePendingLocked(LeaseProbeBindingKey bindingKey)
    {
        if (!_pending.Remove(bindingKey, out var pending)) return;
        if (pending.QueueNode is not null) _queue.Remove(pending.QueueNode);
        pending.QueueNode = null;
    }

    private static void Validate(LeaseProbeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ClientKey);
        if (!System.Net.IPAddress.TryParse(identity.IpAddress, out _)) throw new ArgumentException("A lease probe identity must contain a valid IP address.", nameof(identity));
        if (identity.Generation <= 0) throw new ArgumentOutOfRangeException(nameof(identity), "A lease probe generation must be positive.");
    }

    private sealed class PendingProbe(LeaseProbeRequest request)
    {
        public LeaseProbeRequest Request { get; set; } = request;
        public LinkedListNode<LeaseProbeBindingKey>? QueueNode { get; set; }
    }

    private sealed class ActiveProbe(LeaseProbeRequest request, CancellationTokenSource cancellation)
    {
        public LeaseProbeRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
