using System.Buffers.Binary;
using System.Net;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Dhcp;

public static class DhcpPacketParser
{
    public static DhcpPacket Parse(byte[] data)
    {
        if (data.Length < 240) throw new InvalidOperationException("Invalid DHCP packet");
        var p = new DhcpPacket
        {
            Op = data[0],
            HType = data[1],
            HLen = data[2],
            Hops = data[3],
            Xid = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)),
            Secs = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8, 2)),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10, 2)),
            CiAddr = new IPAddress(data.AsSpan(12, 4)),
            YiAddr = new IPAddress(data.AsSpan(16, 4)),
            SiAddr = new IPAddress(data.AsSpan(20, 4)),
            GiAddr = new IPAddress(data.AsSpan(24, 4)),
            ChAddr = data.Skip(28).Take(16).ToArray()
        };

        var i = 240;
        while (i < data.Length)
        {
            var code = data[i++];
            if (code == 0) continue;
            if (code == 255) break;
            if (i >= data.Length) break;
            var len = data[i++];
            if (i + len > data.Length) break;
            var value = data.AsSpan(i, len);
            if (code == 53 && len > 0) p.MessageType = (DhcpMessageType)value[0];
            if (code == 50 && len == 4) p.RequestedIp = new IPAddress(value);
            if (code == 12) p.Hostname = System.Text.Encoding.ASCII.GetString(value).Trim();
            i += len;
        }
        return p;
    }

    public static byte[] BuildReply(DhcpPacket request, DhcpServerSettings settings, IPAddress clientIp, DhcpMessageType type)
    {
        var data = new byte[300];
        data[0] = 2;
        data[1] = request.HType;
        data[2] = request.HLen;
        data[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), request.Xid);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10, 2), request.Flags);
        WriteIp(data, 16, clientIp);
        WriteIp(data, 20, settings.ServerIp);
        Array.Copy(request.ChAddr, 0, data, 28, Math.Min(16, request.ChAddr.Length));
        data[236] = 99;
        data[237] = 130;
        data[238] = 83;
        data[239] = 99;
        var i = 240;
        AddOption(data, ref i, 53, [(byte)type]);
        AddOption(data, ref i, 54, settings.ServerIp.GetAddressBytes());
        AddOption(data, ref i, 1, settings.SubnetMask.GetAddressBytes());
        AddOption(data, ref i, 28, IpNetwork.BroadcastAddress(settings.ServerIp, settings.SubnetMask).GetAddressBytes());
        if (settings.Gateway != null) AddOption(data, ref i, 3, settings.Gateway.GetAddressBytes());
        if (settings.Dns != null) AddOption(data, ref i, 6, settings.Dns.GetAddressBytes());
        var lease = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lease, (uint)settings.LeaseSeconds);
        AddOption(data, ref i, 51, lease);
        data[i++] = 255;
        Array.Resize(ref data, i);
        return data;
    }

    private static void AddOption(byte[] data, ref int i, byte code, byte[] value)
    {
        data[i++] = code;
        data[i++] = (byte)value.Length;
        Array.Copy(value, 0, data, i, value.Length);
        i += value.Length;
    }

    private static void WriteIp(byte[] data, int offset, IPAddress ip)
    {
        Array.Copy(ip.GetAddressBytes(), 0, data, offset, 4);
    }
}
