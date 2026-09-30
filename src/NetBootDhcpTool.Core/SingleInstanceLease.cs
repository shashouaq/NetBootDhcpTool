using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NetBootDhcpTool.Core;

/// <summary>Owns the write and recovery responsibility for one normalized data directory.</summary>
public sealed class SingleInstanceLease : IDisposable
{
    private readonly Semaphore _semaphore;
    private int _disposed;

    private SingleInstanceLease(Semaphore semaphore)
    {
        _semaphore = semaphore;
    }

    public static SingleInstanceLease? TryAcquire(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDirectoryIdentity)));
        // Use a new kernel-object name when moving from the legacy named Mutex.
        // Windows does not allow a Semaphore to open an existing Mutex by the same name.
        var semaphore = new Semaphore(initialCount: 1, maximumCount: 1, name: $"Global\\NetBootDhcpTool-LeaseV2-{digest}");
        try
        {
            if (!semaphore.WaitOne(TimeSpan.Zero))
            {
                semaphore.Dispose();
                return null;
            }
            return new SingleInstanceLease(semaphore);
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _semaphore.Release(); }
        finally { _semaphore.Dispose(); }
    }
}
