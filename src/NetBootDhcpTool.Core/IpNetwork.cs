using System.Net;

namespace NetBootDhcpTool.Core;

public static class IpNetwork
{
    public static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) throw new ArgumentException("IPv4 required", nameof(address));
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public static IPAddress FromUInt32(uint value)
    {
        return new IPAddress(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
    }

    public static int PrefixLength(IPAddress mask)
    {
        var value = ToUInt32(mask);
        var count = 0;
        for (var i = 31; i >= 0; i--)
        {
            if (((value >> i) & 1) == 1) count++;
            else break;
        }
        return count;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var m = ToUInt32(mask);
        return (ToUInt32(a) & m) == (ToUInt32(b) & m);
    }

    public static IReadOnlyList<IPAddress> Hosts(IPAddress ip, IPAddress mask)
    {
        var m = ToUInt32(mask);
        var network = ToUInt32(ip) & m;
        var broadcast = network | ~m;
        if (broadcast <= network + 1) return Array.Empty<IPAddress>();
        var result = new List<IPAddress>();
        for (var value = network + 1; value < broadcast; value++)
        {
            result.Add(FromUInt32(value));
        }
        return result;
    }

    public static IPAddress BroadcastAddress(IPAddress ip, IPAddress mask)
    {
        var m = ToUInt32(mask);
        var network = ToUInt32(ip) & m;
        return FromUInt32(network | ~m);
    }

    public static bool IsUsableHost(IPAddress ip, IPAddress localIp, IPAddress mask)
    {
        var m = ToUInt32(mask);
        var value = ToUInt32(ip);
        var network = ToUInt32(localIp) & m;
        var broadcast = network | ~m;
        return value > network && value < broadcast;
    }
}
