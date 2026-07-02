using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public sealed class ExistingDhcpDetector
{
    private readonly ILogger _logger;

    public ExistingDhcpDetector(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<(bool found, string server, string offeredIp)> DetectAsync(IPAddress localIp, int timeoutMs, CancellationToken ct = default)
    {
        var xid = (uint)Random.Shared.Next();
        var mac = new byte[] { 0x02, 0x4E, 0x42, 0x44, 0x48, 0x43 };
        var discover = BuildDiscover(xid, mac);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        udp.Client.Bind(new IPEndPoint(localIp, 68));
        udp.EnableBroadcast = true;
        await udp.SendAsync(discover, new IPEndPoint(IPAddress.Broadcast, 67), ct);
        _logger.Info($"Existing DHCP detection sent Discover from {localIp}");
        try
        {
            var response = await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
            var type = GetMessageType(response.Buffer);
            if (type == 2)
            {
                var offered = new IPAddress(response.Buffer.AsSpan(16, 4)).ToString();
                var server = GetServerId(response.Buffer);
                _logger.Warn($"Existing DHCP detected: server={server} offered={offered}");
                return (true, server, offered);
            }
        }
        catch (TimeoutException)
        {
            _logger.Info("Existing DHCP detection timeout");
        }
        return (false, "", "");
    }

    private static byte[] BuildDiscover(uint xid, byte[] mac)
    {
        var data = new byte[300];
        data[0] = 1;
        data[1] = 1;
        data[2] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), xid);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10, 2), 0x8000);
        Array.Copy(mac, 0, data, 28, mac.Length);
        data[236] = 99;
        data[237] = 130;
        data[238] = 83;
        data[239] = 99;
        var i = 240;
        data[i++] = 53;
        data[i++] = 1;
        data[i++] = 1;
        data[i++] = 12;
        data[i++] = 18;
        foreach (var b in System.Text.Encoding.ASCII.GetBytes("netboot-dhcp-check")) data[i++] = b;
        data[i++] = 55;
        data[i++] = 4;
        data[i++] = 1;
        data[i++] = 3;
        data[i++] = 6;
        data[i++] = 51;
        data[i++] = 255;
        Array.Resize(ref data, i);
        return data;
    }

    private static byte GetMessageType(byte[] data)
    {
        var i = 240;
        while (i < data.Length)
        {
            var code = data[i++];
            if (code == 255) break;
            if (code == 0) continue;
            var len = data[i++];
            if (code == 53 && len > 0) return data[i];
            i += len;
        }
        return 0;
    }

    private static string GetServerId(byte[] data)
    {
        var i = 240;
        while (i < data.Length)
        {
            var code = data[i++];
            if (code == 255) break;
            if (code == 0) continue;
            var len = data[i++];
            if (code == 54 && len == 4) return new IPAddress(data.AsSpan(i, 4)).ToString();
            i += len;
        }
        return "";
    }
}
