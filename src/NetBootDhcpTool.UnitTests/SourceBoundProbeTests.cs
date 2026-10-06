using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class SourceBoundProbeTests
{
    private static ProbeSource LoopbackSource()
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().Single(x => x.NetworkInterfaceType == NetworkInterfaceType.Loopback);
        return new(adapter.Id, adapter.GetIPProperties().GetIPv4Properties()!.Index, IPAddress.Loopback);
    }
    [TestMethod]
    public async Task NativeIcmpUsesAssignedSourceAndRejectsChangedIdentity()
    {
        var source = LoopbackSource();
        Assert.IsNotNull(await source.PingAsync(IPAddress.Loopback, 800, CancellationToken.None));
        var parallel = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => source.PingAsync(IPAddress.Loopback, 800, CancellationToken.None)));
        Assert.IsTrue(parallel.All(x => x.HasValue));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => source.PingAsync(IPAddress.Loopback, 800, new CancellationToken(true)));
        Assert.ThrowsExactly<InvalidOperationException>(() => (source with { AdapterId = Guid.NewGuid().ToString() }).Validate(IPAddress.Loopback));
        Assert.ThrowsExactly<InvalidOperationException>(() => (source with { LocalAddress = IPAddress.Parse("127.0.0.2") }).Validate(IPAddress.Loopback));
    }
    [TestMethod]
    public async Task HttpBindsSourceAndDoesNotFollowRedirect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            Assert.AreEqual(IPAddress.Loopback, ((IPEndPoint)peer.Client.RemoteEndPoint!).Address);
            var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://192.0.2.200/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
        });
        using var client = new HttpClient(LoopbackSource().CreateHttpHandler());
        using var result = await client.GetAsync($"http://127.0.0.1:{port}", timeout.Token);
        Assert.AreEqual(HttpStatusCode.Found, result.StatusCode);
        await server;
    }
}
