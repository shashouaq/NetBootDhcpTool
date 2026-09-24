using System.Net;
using System.Net.Sockets;

namespace NetBootDhcpTool.Core;

public static class IpNetwork
{
    public static bool TryParseCidr(string? value, out IPAddress network, out int prefixLength, out string canonical)
    {
        network = IPAddress.None;
        prefixLength = 0;
        canonical = "";
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Trim().Split('/', 2, StringSplitOptions.TrimEntries);
        if ((parts.Length != 1 && parts.Length != 2) || !IPAddress.TryParse(parts[0], out var address))
        {
            return false;
        }

        var maxPrefixLength = address.AddressFamily == AddressFamily.InterNetwork ? 32
            : address.AddressFamily == AddressFamily.InterNetworkV6 ? 128
            : 0;
        if (maxPrefixLength == 0) return false;
        if (parts.Length == 1)
        {
            prefixLength = maxPrefixLength;
        }
        else if (!int.TryParse(parts[1], out prefixLength) || prefixLength is < 0 || prefixLength > maxPrefixLength)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        var remainingBits = prefixLength;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (remainingBits >= 8)
            {
                remainingBits -= 8;
                continue;
            }

            if (remainingBits == 0)
            {
                bytes[i] = 0;
            }
            else
            {
                bytes[i] = (byte)(bytes[i] & (byte)(0xFF << (8 - remainingBits)));
                remainingBits = 0;
            }

            for (var j = i + 1; j < bytes.Length; j++) bytes[j] = 0;
            break;
        }

        network = new IPAddress(bytes);
        canonical = $"{network}/{prefixLength}";
        return true;
    }

    public static string ParseCidr(string value)
    {
        if (!TryParseCidr(value, out _, out _, out var canonical)) throw new FormatException("Invalid IP network / 无效的 IP 网段");
        return canonical;
    }

    public static bool Contains(IPAddress network, int prefixLength, IPAddress address)
    {
        if (network.AddressFamily != address.AddressFamily) return false;
        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != addressBytes[i]) return false;
        }

        return remainingBits == 0
            || (networkBytes[fullBytes] & (byte)(0xFF << (8 - remainingBits)))
                == (addressBytes[fullBytes] & (byte)(0xFF << (8 - remainingBits)));
    }

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

    public static bool TryGetPrefixLength(IPAddress mask, out int prefixLength)
    {
        prefixLength = 0;
        if (mask.AddressFamily != AddressFamily.InterNetwork) return false;
        var value = ToUInt32(mask);
        var sawZero = false;
        for (var bit = 31; bit >= 0; bit--)
        {
            var set = ((value >> bit) & 1) != 0;
            if (set && sawZero) return false;
            if (set) prefixLength++;
            else sawZero = true;
        }
        return true;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var m = ToUInt32(mask);
        return (ToUInt32(a) & m) == (ToUInt32(b) & m);
    }

    public static IEnumerable<IPAddress> Hosts(IPAddress ip, IPAddress mask)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork || !TryGetPrefixLength(mask, out var prefixLength))
            throw new ArgumentException("IPv4 address and a contiguous IPv4 subnet mask are required.");
        var m = ToUInt32(mask);
        var network = ToUInt32(ip) & m;
        var broadcast = network | ~m;
        ulong start = prefixLength <= 30 ? (ulong)network + 1 : network;
        ulong endExclusive = prefixLength <= 30 ? broadcast : (ulong)broadcast + 1;
        for (var value = start; value < endExclusive; value++)
        {
            yield return FromUInt32((uint)value);
        }
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
