using System.Net;
using System.Net.Sockets;

namespace NetBootDhcpTool.Network;

public sealed class HttpProbeService : IAsyncDisposable
{
    public const int DefaultMaximumConcurrentRequests = 64;

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _requestSlots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifecycleLock = new();
    private TaskCompletionSource _allProbesIdle = CompletedSignal();
    private Task? _disposeTask;
    private int _activeProbes;
    private bool _disposeStarted;

    public HttpProbeService() : this(CreateDefaultHandler, DefaultMaximumConcurrentRequests) { }

    public HttpProbeService(Func<HttpMessageHandler> handlerFactory, int maximumConcurrentRequests = DefaultMaximumConcurrentRequests)
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        if (maximumConcurrentRequests is < 1 or > DefaultMaximumConcurrentRequests)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentRequests), $"The HTTP probe request limit must be between 1 and {DefaultMaximumConcurrentRequests}.");

        _client = new HttpClient(handlerFactory(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _requestSlots = new SemaphoreSlim(maximumConcurrentRequests, maximumConcurrentRequests);
    }

    public async Task<(bool http, bool https)> ProbeAsync(string ip, int timeoutMs, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ip?.Trim(), out var address)
            || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return (false, false);
        }

        BeginProbe();
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var host = address.ToString();
            var httpTask = ProbeOneAsync("http://" + host, timeoutMs, operation.Token);
            var httpsTask = ProbeOneAsync("https://" + host, timeoutMs, operation.Token);
            var results = await Task.WhenAll(httpTask, httpsTask).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            return (results[0], results[1]);
        }
        finally
        {
            EndProbe();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task idleTask;
        TaskCompletionSource cancelIssued = NewSignal();
        Task disposeTask;
        lock (_lifecycleLock)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposeStarted = true;
            idleTask = _activeProbes == 0 ? Task.CompletedTask : _allProbesIdle.Task;
            _disposeTask = DisposeAfterIdleAsync(idleTask, cancelIssued.Task);
            disposeTask = _disposeTask;
        }

        try
        {
            _lifetime.Cancel();
        }
        finally
        {
            cancelIssued.TrySetResult();
        }

        return new ValueTask(disposeTask);
    }

    private async Task<bool> ProbeOneAsync(string url, int timeoutMs, CancellationToken operationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)));
        var acquired = false;
        try
        {
            await _requestSlots.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            timeout.Dispose();
            if (acquired) _requestSlots.Release();
        }
    }

    private void BeginProbe()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            if (_activeProbes++ == 0) _allProbesIdle = NewSignal();
        }
    }

    private void EndProbe()
    {
        lock (_lifecycleLock)
        {
            if (--_activeProbes == 0) _allProbesIdle.TrySetResult();
        }
    }

    private async Task DisposeAfterIdleAsync(Task idleTask, Task cancelIssued)
    {
        await cancelIssued.ConfigureAwait(false);
        await idleTask.ConfigureAwait(false);
        _client.Dispose();
        _requestSlots.Dispose();
        _lifetime.Dispose();
    }

    private static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler
    {
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null
    };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = NewSignal();
        signal.TrySetResult();
        return signal;
    }
}
