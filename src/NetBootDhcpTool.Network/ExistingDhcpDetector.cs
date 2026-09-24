using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Dhcp;

namespace NetBootDhcpTool.Network;

public sealed class ExistingDhcpDetector
{
    private const SocketOptionName IpInterfaceList = (SocketOptionName)28;
    private const SocketOptionName IpAddInterfaceToList = (SocketOptionName)29;
    private const SocketOptionName IpUnicastInterface = (SocketOptionName)31;
    private readonly ILogger _logger;

    public ExistingDhcpDetector(ILogger logger) => _logger = logger;

    public Task<(bool found, string server, string offeredIp)> DetectAsync(IPAddress localIp, int timeoutMs, CancellationToken ct = default) =>
        DetectAsync(localIp, 0, timeoutMs, ct);

    public async Task<(bool found, string server, string offeredIp)> DetectAsync(IPAddress localIp, int interfaceIndex, int timeoutMs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(localIp);
        if (localIp.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("DHCP detection requires an IPv4 local address.", nameof(localIp));
        if (interfaceIndex < 0) throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        var xidBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4);
        var xid = BinaryPrimitives.ReadUInt32BigEndian(xidBytes);
        var mac = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6);
        mac[0] = (byte)((mac[0] | 0x02) & 0xFE);
        var discover = BuildDiscover(xid, mac);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        if (interfaceIndex > 0)
        {
            udp.Client.SetSocketOption(SocketOptionLevel.IP, IpInterfaceList, true);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, IpAddInterfaceToList, interfaceIndex);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, IpUnicastInterface, IPAddress.HostToNetworkOrder(interfaceIndex));
        }
        udp.Client.Bind(new IPEndPoint(localIp, 68));
        udp.EnableBroadcast = true;
        await udp.SendAsync(discover, new IPEndPoint(IPAddress.Broadcast, 67), ct);
        _logger.Info($"Existing DHCP detection sent Discover from {localIp} interfaceIndex={interfaceIndex} xid=0x{xid:X8}");

        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = Math.Max(1, timeoutMs - (int)timer.ElapsedMilliseconds);
            try
            {
                var buffer = new byte[2048];
                var receiveTask = udp.Client.ReceiveMessageFromAsync(
                    buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                var response = await receiveTask.AsTask().WaitAsync(TimeSpan.FromMilliseconds(remaining), ct);
                if (interfaceIndex > 0 && response.PacketInformation.Interface != interfaceIndex)
                {
                    _logger.Info($"Existing DHCP detection ignored response from non-target interface {response.PacketInformation.Interface}");
                    continue;
                }
                if (response.RemoteEndPoint is not IPEndPoint remote || remote.Port != 67)
                {
                    _logger.Info($"Existing DHCP detection ignored response from non-server UDP port {response.RemoteEndPoint}");
                    continue;
                }

                DhcpPacket packet;
                try { packet = DhcpPacketParser.Parse(buffer.AsSpan(0, response.ReceivedBytes).ToArray()); }
                catch (InvalidDataException ex)
                {
                    _logger.Info($"Existing DHCP detection ignored malformed response: {ex.Message}");
                    continue;
                }
                if (!IsMatchingOffer(packet, xid, mac))
                {
                    _logger.Info($"Existing DHCP detection ignored unrelated response from {response.RemoteEndPoint}");
                    continue;
                }

                var server = packet.ServerIdentifier!.ToString();
                var offered = packet.YiAddr.ToString();
                _logger.Warn($"Existing DHCP detected: server={server} offered={offered} xid=0x{xid:X8} interfaceIndex={interfaceIndex}");
                return (true, server, offered);
            }
            catch (TimeoutException) { break; }
        }
        _logger.Info($"Existing DHCP detection timeout after {timeoutMs} ms interfaceIndex={interfaceIndex}");
        return (false, "", "");
    }

    public static bool IsMatchingOffer(DhcpPacket packet, uint transactionId, ReadOnlySpan<byte> clientHardwareAddress) =>
        packet.Op == 2
        && packet.HType == 1
        && packet.HLen == 6
        && packet.MessageType == DhcpMessageType.Offer
        && packet.Xid == transactionId
        && clientHardwareAddress.Length == 6
        && packet.ChAddr.Length >= 6
        && packet.ChAddr.AsSpan(0, 6).SequenceEqual(clientHardwareAddress)
        && packet.ServerIdentifier != null
        && !packet.ServerIdentifier.Equals(IPAddress.Any)
        && !packet.YiAddr.Equals(IPAddress.Any);

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
        data[i++] = (byte)DhcpMessageType.Discover;
        data[i++] = 61;
        data[i++] = 7;
        data[i++] = 1;
        Array.Copy(mac, 0, data, i, mac.Length);
        i += mac.Length;
        data[i++] = 12;
        var hostname = System.Text.Encoding.ASCII.GetBytes("netboot-check");
        data[i++] = checked((byte)hostname.Length);
        Array.Copy(hostname, 0, data, i, hostname.Length);
        i += hostname.Length;
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
}
