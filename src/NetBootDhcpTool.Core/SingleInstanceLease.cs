using System.Security.Cryptography;
using System.Text;

namespace NetBootDhcpTool.Core;

/// <summary>Owns the write and recovery responsibility for one normalized data directory.</summary>
public sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    private SingleInstanceLease(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static SingleInstanceLease? TryAcquire(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDirectoryIdentity)));
        var mutex = new Mutex(initiallyOwned: false, name: $"Global\\NetBootDhcpTool-{digest}");
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.Zero))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // An abnormal exit releases the mutex. Ownership is granted to this waiter.
            }

            return new SingleInstanceLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsMutex)
        {
            _ownsMutex = false;
            try { _mutex.ReleaseMutex(); }
            finally { _mutex.Dispose(); }
        }
        else _mutex.Dispose();
    }
}
