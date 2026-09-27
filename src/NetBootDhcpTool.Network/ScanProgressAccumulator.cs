using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public sealed record ScanProgressBatch(int Done, int Total, IReadOnlyList<ScanResult> Results);

/// <summary>Thread-safe bounded buffer for scan callbacks; results are never replaced by newer progress.</summary>
public sealed class ScanProgressAccumulator(int maximumResults)
    : IProgress<(int done, int total, ScanResult? result)>
{
    private readonly object _sync = new();
    private readonly Queue<ScanResult> _results = new();
    private readonly int _maximumResults = Math.Clamp(maximumResults, 1, 4096);
    private int _done;
    private int _total;
    private bool _progressPending;
    private bool _completed;

    public int PendingResultCount
    {
        get { lock (_sync) return _results.Count; }
    }

    public void Report((int done, int total, ScanResult? result) value)
    {
        lock (_sync)
        {
            if (_completed) return;
            if (value.total < 0) throw new ArgumentOutOfRangeException(nameof(value), "Scan total cannot be negative.");
            if (_total != 0 && value.total != _total)
                throw new InvalidOperationException("A scan progress stream cannot change its target count.");
            _total = Math.Max(_total, value.total);
            _done = Math.Max(_done, Math.Clamp(value.done, 0, _total));
            _progressPending = true;
            if (value.result == null) return;
            if (_results.Count >= _maximumResults)
                throw new InvalidOperationException($"Scan progress exceeded its bounded result capacity ({_maximumResults}).");
            _results.Enqueue(value.result);
        }
    }

    public bool TryTakeBatch(int maximumBatchSize, out ScanProgressBatch batch)
    {
        if (maximumBatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBatchSize));
        lock (_sync)
        {
            if (_results.Count == 0 && !_progressPending)
            {
                batch = new ScanProgressBatch(_done, _total, Array.Empty<ScanResult>());
                return false;
            }
            var results = new List<ScanResult>(Math.Min(maximumBatchSize, _results.Count));
            while (results.Count < maximumBatchSize && _results.TryDequeue(out var result)) results.Add(result);
            batch = new ScanProgressBatch(_done, _total, results);
            _progressPending = false;
            return true;
        }
    }

    public void Complete()
    {
        lock (_sync) _completed = true;
    }
}
