using System.Net;
using System.Net.Sockets;

namespace NetBootDhcpTool.Network;

public sealed class HttpProbeService
{
    public async Task<(bool http, bool https)> ProbeAsync(string ip, int timeoutMs, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ip?.Trim(), out var address)
            || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return (false, false);
        }

        var host = address.ToString();
        return (await ProbeOneAsync("http://" + host, timeoutMs, ct), await ProbeOneAsync("https://" + host, timeoutMs, ct));
    }

    private static async Task<bool> ProbeOneAsync(string url, int timeoutMs, CancellationToken ct)
    {
        try
        {
            // Use the platform certificate store and validation policy. A device with a
            // self-signed certificate may not pass HTTPS probing, but accepting every
            // certificate would make this reachability check an avoidable MITM bypass.
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
