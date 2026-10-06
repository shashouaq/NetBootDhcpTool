using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public sealed record NetworkProbeDetails(string Hostname, bool HttpOk, bool HttpsOk);

public interface IScanTargetProbe
{
    Task<ScanResult?> ProbeAsync(IPAddress ip, int pingTimeoutMs, int httpTimeoutMs, CancellationToken cancellationToken);
}

public sealed class NetworkScanTargetProbe : IScanTargetProbe
{
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(1.5);
    private readonly Func<IPAddress, TimeSpan, CancellationToken, Task<(bool success, long roundTripTime)>> _pingAsync;
    private readonly Func<IPAddress, CancellationToken, Task<string>> _resolveHostAsync;
    private readonly Func<string, int, CancellationToken, Task<(bool http, bool https)>> _httpProbeAsync;
    private readonly TimeSpan _dnsTimeout;

    public NetworkScanTargetProbe(HttpProbeService httpProbe)
        : this(SendPingAsync, ResolveHostAsync, CreateHttpProbe(httpProbe), DnsTimeout) { }

    internal NetworkScanTargetProbe(
        Func<IPAddress, TimeSpan, CancellationToken, Task<(bool success, long roundTripTime)>> pingAsync,
        Func<IPAddress, CancellationToken, Task<string>> resolveHostAsync,
        Func<string, int, CancellationToken, Task<(bool http, bool https)>> httpProbeAsync,
        TimeSpan dnsTimeout)
    {
        _pingAsync = pingAsync ?? throw new ArgumentNullException(nameof(pingAsync));
        _resolveHostAsync = resolveHostAsync ?? throw new ArgumentNullException(nameof(resolveHostAsync));
        _httpProbeAsync = httpProbeAsync ?? throw new ArgumentNullException(nameof(httpProbeAsync));
        if (dnsTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(dnsTimeout));
        _dnsTimeout = dnsTimeout;
    }

    public async Task<ScanResult?> ProbeAsync(IPAddress ip, int pingTimeoutMs, int httpTimeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ping = await _pingAsync(ip, TimeSpan.FromMilliseconds(Math.Max(1, pingTimeoutMs)), cancellationToken).ConfigureAwait(false);
        if (!ping.success) return null;

        var details = await ProbeReachableDetailsAsync(ip, httpTimeoutMs, cancellationToken).ConfigureAwait(false);
        var result = new ScanResult
        {
            IpAddress = ip.ToString(),
            PingOk = true,
            LatencyMs = ping.roundTripTime,
            Hostname = details.Hostname,
            HttpOk = details.HttpOk,
            HttpsOk = details.HttpsOk,
            LastSeen = DateTime.Now
        };
        result.Connectivity.Observe(ping.roundTripTime, result.LastSeen);
        return result;
    }

    public async Task<NetworkProbeDetails> ProbeReachableDetailsAsync(IPAddress ip, int httpTimeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dnsTask = ResolveHostBoundedAsync(ip, cancellationToken);
        var webTask = _httpProbeAsync(ip.ToString(), httpTimeoutMs, cancellationToken);
        await Task.WhenAll(dnsTask, webTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var web = await webTask.ConfigureAwait(false);
        return new NetworkProbeDetails(await dnsTask.ConfigureAwait(false), web.http, web.https);
    }

    private async Task<string> ResolveHostBoundedAsync(IPAddress ip, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_dnsTimeout);
        try
        {
            return await _resolveHostAsync(ip, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ""; }
        catch (SocketException) { return ""; }
    }

    private static async Task<(bool success, long roundTripTime)> SendPingAsync(IPAddress ip, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(ip, timeout, new byte[32], new PingOptions(), cancellationToken).ConfigureAwait(false);
        return (reply.Status == IPStatus.Success, reply.RoundtripTime);
    }

    private static async Task<string> ResolveHostAsync(IPAddress ip, CancellationToken cancellationToken)
    {
        var entry = await Dns.GetHostEntryAsync(ip.ToString(), AddressFamily.Unspecified, cancellationToken).ConfigureAwait(false);
        return entry.HostName;
    }

    private static Func<string, int, CancellationToken, Task<(bool http, bool https)>> CreateHttpProbe(HttpProbeService httpProbe)
    {
        ArgumentNullException.ThrowIfNull(httpProbe);
        return httpProbe.ProbeAsync;
    }
}

public sealed class PingScanner
{
    public const int MaxConcurrency = 64;
    private readonly ILogger _logger;
    private readonly IScanTargetProbe _targetProbe;

    public PingScanner(ILogger logger, HttpProbeService probe)
        : this(logger, new NetworkScanTargetProbe(probe)) { }

    public PingScanner(ILogger logger, IScanTargetProbe targetProbe)
    {
        _logger = logger;
        _targetProbe = targetProbe;
    }

    public Task<IReadOnlyList<ScanResult>> ScanAsync(IPAddress localIp, IPAddress mask, int concurrency, int pingTimeoutMs,
        int httpTimeoutMs, IProgress<(int done, int total, ScanResult? result)> progress, CancellationToken ct)
    {
        if (!ScanRangePlan.TryCreate(localIp.ToString(), mask.ToString(), "", out var plan, out var error))
            throw new ArgumentException($"Invalid scan range: {error}");
        return ScanPlanAsync(plan!, concurrency, pingTimeoutMs, httpTimeoutMs, progress, ct);
    }

    public Task<IReadOnlyList<ScanResult>> ScanPlanAsync(ScanRangePlan plan, int concurrency, int pingTimeoutMs,
        int httpTimeoutMs, IProgress<(int done, int total, ScanResult? result)> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.TargetCount is < 1 or > ScanRangePlan.MaxProbeTargets)
            throw new ArgumentOutOfRangeException(nameof(plan), "Scan target count is outside the permitted range.");
        var scope = plan.IsSingleTarget
            ? plan.TargetIp!.ToString()
            : $"{plan.LocalIp}/{plan.PrefixLength}";
        return ScanHostsAsync(plan.EnumerateTargets(), plan.TargetCount, scope, concurrency, pingTimeoutMs, httpTimeoutMs, progress, ct);
    }

    public Task<NetworkProbeDetails> ProbeReachableDetailsAsync(IPAddress ip, int httpTimeoutMs, CancellationToken ct)
    {
        if (_targetProbe is not NetworkScanTargetProbe networkProbe)
            throw new InvalidOperationException("Reachability details are available only when the scanner uses the network probe.");
        return networkProbe.ProbeReachableDetailsAsync(ip, httpTimeoutMs, ct);
    }

    public Task<IReadOnlyList<ScanResult>> ScanTargetsAsync(IReadOnlyList<IPAddress> targets, int concurrency, int pingTimeoutMs,
        int httpTimeoutMs, IProgress<(int done, int total, ScanResult? result)> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var unique = new List<IPAddress>(Math.Min(targets.Count, ScanRangePlan.MaxProbeTargets));
        var seen = new HashSet<IPAddress>();
        foreach (var target in targets)
        {
            if (target.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentException("Only IPv4 scan targets are supported.", nameof(targets));
            if (seen.Add(target))
            {
                if (unique.Count == ScanRangePlan.MaxProbeTargets)
                    throw new ArgumentOutOfRangeException(nameof(targets), $"A scan may probe at most {ScanRangePlan.MaxProbeTargets} addresses.");
                unique.Add(target);
            }
        }
        if (unique.Count == 0) throw new ArgumentException("At least one scan target is required.", nameof(targets));
        return ScanHostsAsync(unique, unique.Count, string.Join(",", unique.Select(x => x.ToString())),
            concurrency, pingTimeoutMs, httpTimeoutMs, progress, ct);
    }

    private async Task<IReadOnlyList<ScanResult>> ScanHostsAsync(IEnumerable<IPAddress> hosts, int total, string scope,
        int concurrency, int pingTimeoutMs, int httpTimeoutMs,
        IProgress<(int done, int total, ScanResult? result)> progress, CancellationToken ct)
    {
        var results = new ConcurrentBag<ScanResult>();
        var done = 0;
        var degree = Math.Clamp(concurrency, 1, MaxConcurrency);
        _logger.Info($"Scan start: {scope} hosts={total} concurrency={degree}");
        await Parallel.ForEachAsync(hosts, new ParallelOptions
        {
            MaxDegreeOfParallelism = degree,
            CancellationToken = ct
        }, async (ip, token) =>
        {
            ScanResult? result = null;
            try
            {
                result = await _targetProbe.ProbeAsync(ip, pingTimeoutMs, httpTimeoutMs, token).ConfigureAwait(false);
                if (result is not null)
                {
                    results.Add(result);
                    _logger.Info($"Scan hit: {result.IpAddress} {result.LatencyMs}ms http={result.HttpOk} https={result.HttpsOk}");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.Error($"Scan failed: {ip}", ex);
            }
            finally
            {
                progress.Report((Interlocked.Increment(ref done), total, result));
            }
        }).ConfigureAwait(false);
        _logger.Info("Scan stop");
        return results.OrderBy(x => IPAddress.Parse(x.IpAddress).GetAddressBytes(), ByteArrayComparer.Instance).ToList();
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (x == null || y == null) return 0;
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                var c = x[i].CompareTo(y[i]);
                if (c != 0) return c;
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
