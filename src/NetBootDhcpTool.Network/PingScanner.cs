using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public sealed class PingScanner
{
    private readonly ILogger _logger;
    private readonly HttpProbeService _probe;

    public PingScanner(ILogger logger, HttpProbeService probe)
    {
        _logger = logger;
        _probe = probe;
    }

    public async Task<IReadOnlyList<ScanResult>> ScanAsync(IPAddress localIp, IPAddress mask, int concurrency, int pingTimeoutMs, int httpTimeoutMs, IProgress<(int done, int total, ScanResult? result)> progress, CancellationToken ct)
    {
        var hosts = IpNetwork.Hosts(localIp, mask).Where(x => !x.Equals(localIp)).ToList();
        return await ScanHostsAsync(hosts, $"{localIp}/{IpNetwork.PrefixLength(mask)}", concurrency, pingTimeoutMs, httpTimeoutMs, progress, ct);
    }

    public async Task<IReadOnlyList<ScanResult>> ScanTargetsAsync(IReadOnlyList<IPAddress> targets, int concurrency, int pingTimeoutMs, int httpTimeoutMs, IProgress<(int done, int total, ScanResult? result)> progress, CancellationToken ct)
    {
        return await ScanHostsAsync(targets.Distinct().ToList(), string.Join(",", targets.Select(x => x.ToString())), concurrency, pingTimeoutMs, httpTimeoutMs, progress, ct);
    }

    private async Task<IReadOnlyList<ScanResult>> ScanHostsAsync(IReadOnlyList<IPAddress> hosts, string scope, int concurrency, int pingTimeoutMs, int httpTimeoutMs, IProgress<(int done, int total, ScanResult? result)> progress, CancellationToken ct)
    {
        var results = new ConcurrentBag<ScanResult>();
        var done = 0;
        _logger.Info($"Scan start: {scope} hosts={hosts.Count}");
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct }, async (ip, token) =>
        {
            ScanResult? result = null;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, pingTimeoutMs);
                if (reply.Status == IPStatus.Success)
                {
                    result = new ScanResult
                    {
                        IpAddress = ip.ToString(),
                        PingOk = true,
                        LatencyMs = reply.RoundtripTime,
                        Hostname = ResolveHost(ip),
                        LastSeen = DateTime.Now
                    };
                    var probes = await _probe.ProbeAsync(ip.ToString(), httpTimeoutMs, token);
                    result.HttpOk = probes.http;
                    result.HttpsOk = probes.https;
                    results.Add(result);
                    _logger.Info($"Scan hit: {result.IpAddress} {result.LatencyMs}ms http={result.HttpOk} https={result.HttpsOk}");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error($"Scan failed: {ip}", ex);
            }
            finally
            {
                progress.Report((Interlocked.Increment(ref done), hosts.Count, result));
            }
        });
        _logger.Info("Scan stop");
        return results.OrderBy(x => IPAddress.Parse(x.IpAddress).GetAddressBytes(), ByteArrayComparer.Instance).ToList();
    }

    private static string ResolveHost(IPAddress ip)
    {
        try { return Dns.GetHostEntry(ip).HostName; }
        catch { return ""; }
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
