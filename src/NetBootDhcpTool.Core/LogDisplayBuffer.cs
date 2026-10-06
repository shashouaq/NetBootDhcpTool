namespace NetBootDhcpTool.Core;

/// <summary>
/// Bounded display backlog only. FileLogger remains responsible for the complete file log.
/// Closing stops producers; a forced drain can still consume the final pending rows.
/// </summary>
internal sealed class LogDisplayBuffer
{
    public const int Capacity = 500;
    public const int BatchSize = 128;

    private readonly object _sync = new();
    private readonly Queue<string> _pending = new();
    private long _omitted;
    private bool _closed;

    public int PendingCount { get { lock (_sync) return _pending.Count; } }
    public long OmittedCount { get { lock (_sync) return _omitted; } }

    public void Enqueue(string line)
    {
        lock (_sync)
        {
            if (_closed) return;
            while (_pending.Count >= Capacity)
            {
                _pending.Dequeue();
                _omitted++;
            }
            _pending.Enqueue(line);
        }
    }

    public IReadOnlyList<string> Drain(bool force = false)
    {
        lock (_sync)
        {
            if (_closed && !force) return Array.Empty<string>();
            var lines = new List<string>(Math.Min(_pending.Count, force ? Capacity : BatchSize));
            var limit = force ? Capacity : BatchSize;
            while (lines.Count < limit && _pending.Count > 0) lines.Add(_pending.Dequeue());
            return lines;
        }
    }

    // The view also trims already-rendered rows; include those in the same summary.
    public void RecordVisibleOmission()
    {
        lock (_sync) _omitted++;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _pending.Clear();
            _omitted = 0;
        }
    }

    public void Close()
    {
        lock (_sync) _closed = true;
    }
}
