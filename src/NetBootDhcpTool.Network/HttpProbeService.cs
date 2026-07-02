namespace NetBootDhcpTool.Network;

public sealed class HttpProbeService
{
    public async Task<(bool http, bool https)> ProbeAsync(string ip, int timeoutMs, CancellationToken ct = default)
    {
        return (await ProbeOneAsync("http://" + ip, timeoutMs, ct), await ProbeOneAsync("https://" + ip, timeoutMs, ct));
    }

    private static async Task<bool> ProbeOneAsync(string url, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
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
