using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetBootDhcpTool.Dhcp;

var server = args.Length > 0 ? IPAddress.Parse(args[0]) : IPAddress.Broadcast;
var xidBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4);
var xid = BinaryPrimitives.ReadUInt32BigEndian(xidBytes);
var mac = new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 };
var discover = BuildDiscover(xid, mac);
using var udp = new UdpClient(AddressFamily.InterNetwork);
udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
udp.Client.Bind(new IPEndPoint(IPAddress.Any, 68));
udp.EnableBroadcast = true;
await udp.SendAsync(discover, new IPEndPoint(server, 67));
Console.WriteLine($"DISCOVER sent xid=0x{xid:X8} to {server}:67");

var timer = Stopwatch.StartNew();
while (timer.Elapsed < TimeSpan.FromSeconds(5))
{
    try
    {
        var remaining = TimeSpan.FromSeconds(5) - timer.Elapsed;
        var buffer = new byte[2048];
        var response = await udp.Client.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None,
            new IPEndPoint(IPAddress.Any, 0)).AsTask().WaitAsync(remaining);
        if (response.RemoteEndPoint is not IPEndPoint remote || remote.Port != 67)
        {
            Console.WriteLine($"Ignored packet from non-server UDP port {response.RemoteEndPoint}");
            continue;
        }
        DhcpPacket packet;
        try { packet = DhcpPacketParser.Parse(buffer.AsSpan(0, response.ReceivedBytes).ToArray()); }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"Ignored malformed packet: {ex.Message}");
            continue;
        }
        if (packet.Op != 2 || packet.MessageType != DhcpMessageType.Offer || packet.Xid != xid
            || !packet.ChAddr.AsSpan(0, 6).SequenceEqual(mac) || packet.ServerIdentifier == null
            || packet.YiAddr.Equals(IPAddress.Any))
        {
            Console.WriteLine($"Ignored unrelated DHCP response from {response.RemoteEndPoint}");
            continue;
        }
        Console.WriteLine($"OFFER xid=0x{packet.Xid:X8} yiaddr={packet.YiAddr} server={packet.ServerIdentifier} from={response.RemoteEndPoint}");
        return 0;
    }
    catch (TimeoutException) { break; }
}
Console.Error.WriteLine("No matching DHCP offer within 5 seconds.");
return 1;

static byte[] BuildDiscover(uint xid, byte[] mac)
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
    var index = 240;
    data[index++] = 53;
    data[index++] = 1;
    data[index++] = (byte)DhcpMessageType.Discover;
    data[index++] = 61;
    data[index++] = 7;
    data[index++] = 1;
    Array.Copy(mac, 0, data, index, mac.Length);
    index += mac.Length;
    data[index++] = 12;
    var hostname = System.Text.Encoding.ASCII.GetBytes("netboot-verifier");
    data[index++] = checked((byte)hostname.Length);
    Array.Copy(hostname, 0, data, index, hostname.Length);
    index += hostname.Length;
    data[index++] = 55;
    data[index++] = 4;
    data[index++] = 1;
    data[index++] = 3;
    data[index++] = 6;
    data[index++] = 51;
    data[index++] = 255;
    Array.Resize(ref data, index);
    return data;
}
