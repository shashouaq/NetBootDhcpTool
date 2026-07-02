using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

var server = args.Length > 0 ? IPAddress.Parse(args[0]) : IPAddress.Broadcast;
var xid = (uint)Random.Shared.Next();
var mac = new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 };
var discover = BuildDiscover(xid, mac);
using var udp = new UdpClient(AddressFamily.InterNetwork);
udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
udp.Client.ReceiveTimeout = 5000;
udp.Client.Bind(new IPEndPoint(IPAddress.Any, 68));
udp.EnableBroadcast = true;
await udp.SendAsync(discover, new IPEndPoint(server, 67));
Console.WriteLine($"DISCOVER sent xid=0x{xid:X8} to {server}:67");
UdpReceiveResult response;
try
{
    response = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
}
catch (TimeoutException)
{
    Console.Error.WriteLine("No DHCP reply within 5 seconds.");
    return 1;
}
var type = GetMessageType(response.Buffer);
var yiaddr = new IPAddress(response.Buffer.AsSpan(16, 4));
var serverId = GetServerId(response.Buffer);
Console.WriteLine($"REPLY type={type} yiaddr={yiaddr} server={serverId} from={response.RemoteEndPoint}");
return type == 2 ? 0 : 2;

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
    var i = 240;
    data[i++] = 53;
    data[i++] = 1;
    data[i++] = 1;
    data[i++] = 12;
    data[i++] = 15;
    foreach (var b in System.Text.Encoding.ASCII.GetBytes("netboot-verifier")) data[i++] = b;
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

static byte GetMessageType(byte[] data)
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

static string GetServerId(byte[] data)
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
