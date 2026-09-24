using System.Buffers.Binary;
using System.Net;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Dhcp;

public static class DhcpPacketParser
{
    private const int FixedHeaderLength = 236;
    private const int OptionsOffset = 240;
    private static ReadOnlySpan<byte> MagicCookie => [99, 130, 83, 99];

    public static DhcpPacket Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < OptionsOffset) throw new InvalidDataException("DHCP packet is shorter than the fixed header and magic cookie.");
        if (data[0] is not 1 and not 2) throw new InvalidDataException("DHCP op must be BOOTREQUEST or BOOTREPLY.");
        if (data[1] != 1 || data[2] != 6) throw new InvalidDataException("Only Ethernet DHCP packets with a six-byte client hardware address are supported.");
        if (!data.AsSpan(FixedHeaderLength, MagicCookie.Length).SequenceEqual(MagicCookie))
            throw new InvalidDataException("Invalid DHCP magic cookie.");

        var packet = new DhcpPacket
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
            ChAddr = data.AsSpan(28, 16).ToArray()
        };
        ValidateClientHardwareAddress(packet.ChAddr.AsSpan(0, packet.HLen));

        var seenSingletonOptions = new HashSet<byte>();
        var sawEnd = false;
        var index = OptionsOffset;
        while (index < data.Length)
        {
            var code = data[index++];
            if (code == 0) continue;
            if (code == 255)
            {
                sawEnd = true;
                break;
            }
            if (index >= data.Length) throw new InvalidDataException($"DHCP option {code} has no length byte.");
            var length = data[index++];
            if (index + length > data.Length) throw new InvalidDataException($"DHCP option {code} is truncated.");
            var value = data.AsSpan(index, length);
            if ((code is 53 or 50 or 54 or 61) && !seenSingletonOptions.Add(code))
                throw new InvalidDataException($"DHCP option {code} is duplicated.");

            switch (code)
            {
                case 53:
                    if (length != 1 || !Enum.IsDefined(typeof(DhcpMessageType), value[0]))
                        throw new InvalidDataException("DHCP message type option is missing, malformed, or unsupported.");
                    packet.MessageType = (DhcpMessageType)value[0];
                    break;
                case 50:
                    if (length != 4) throw new InvalidDataException("Requested IP option must contain exactly four bytes.");
                    packet.RequestedIp = new IPAddress(value);
                    break;
                case 54:
                    if (length != 4) throw new InvalidDataException("Server identifier option must contain exactly four bytes.");
                    packet.ServerIdentifier = new IPAddress(value);
                    break;
                case 61:
                    if (length == 0) throw new InvalidDataException("Client identifier option cannot be empty.");
                    packet.ClientIdentifier = value.ToArray();
                    break;
                case 12:
                    packet.Hostname = System.Text.Encoding.ASCII.GetString(value).Trim('\0', ' ', '\t', '\r', '\n');
                    break;
                case 52:
                    throw new InvalidDataException("DHCP option overload is not supported by this isolated server.");
            }
            index += length;
        }
        if (!sawEnd) throw new InvalidDataException("DHCP options do not contain an end marker.");
        if (!seenSingletonOptions.Contains(53)) throw new InvalidDataException("DHCP message type option is required.");
        if (packet.Op == 1 && packet.MessageType is DhcpMessageType.Offer or DhcpMessageType.Ack or DhcpMessageType.Nak)
            throw new InvalidDataException("Client request contains a server-only DHCP message type.");
        if (packet.Op == 2 && packet.MessageType is DhcpMessageType.Discover or DhcpMessageType.Request or DhcpMessageType.Decline or DhcpMessageType.Release)
            throw new InvalidDataException("Server reply contains a client-only DHCP message type.");
        return packet;
    }

    public static byte[] BuildReply(DhcpPacket request, DhcpServerSettings settings, IPAddress clientIp, DhcpMessageType type)
    {
        if (request.HType != 1 || request.HLen != 6 || request.ChAddr.Length < request.HLen)
            throw new InvalidDataException("Cannot reply to a DHCP packet with an unsupported client hardware address.");
        var data = new byte[576];
        data[0] = 2;
        data[1] = request.HType;
        data[2] = request.HLen;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), request.Xid);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10, 2), request.Flags);
        if (type != DhcpMessageType.Nak) WriteIp(data, 16, clientIp);
        WriteIp(data, 20, settings.ServerIp);
        Array.Copy(request.ChAddr, 0, data, 28, 16);
        MagicCookie.CopyTo(data.AsSpan(FixedHeaderLength, MagicCookie.Length));

        var index = OptionsOffset;
        AddOption(data, ref index, 53, [(byte)type]);
        AddOption(data, ref index, 54, settings.ServerIp.GetAddressBytes());
        if (request.ClientIdentifier is { Length: > 0 } clientIdentifier)
            AddOption(data, ref index, 61, clientIdentifier);
        if (type != DhcpMessageType.Nak)
        {
            AddOption(data, ref index, 1, settings.SubnetMask.GetAddressBytes());
            AddOption(data, ref index, 28, IpNetwork.BroadcastAddress(settings.ServerIp, settings.SubnetMask).GetAddressBytes());
            if (settings.Gateway != null) AddOption(data, ref index, 3, settings.Gateway.GetAddressBytes());
            if (settings.Dns != null) AddOption(data, ref index, 6, settings.Dns.GetAddressBytes());
            var lease = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lease, checked((uint)settings.LeaseSeconds));
            AddOption(data, ref index, 51, lease);
            var renewal = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(renewal, checked((uint)(settings.LeaseSeconds / 2)));
            AddOption(data, ref index, 58, renewal);
            var rebinding = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(rebinding, checked((uint)(settings.LeaseSeconds * 7L / 8)));
            AddOption(data, ref index, 59, rebinding);
        }
        data[index++] = 255;
        Array.Resize(ref data, index);
        return data;
    }

    private static void ValidateClientHardwareAddress(ReadOnlySpan<byte> address)
    {
        if (address.Length != 6 || address.IndexOfAnyExcept((byte)0) < 0 || address.IndexOfAnyExcept((byte)0xFF) < 0 || (address[0] & 1) != 0)
            throw new InvalidDataException("DHCP client hardware address is empty, broadcast, or multicast.");
    }

    private static void AddOption(byte[] data, ref int index, byte code, byte[] value)
    {
        if (value.Length > byte.MaxValue || index + value.Length + 2 >= data.Length)
            throw new InvalidDataException($"DHCP response option {code} exceeds the reply buffer.");
        data[index++] = code;
        data[index++] = checked((byte)value.Length);
        Array.Copy(value, 0, data, index, value.Length);
        index += value.Length;
    }

    private static void WriteIp(byte[] data, int offset, IPAddress ip)
    {
        var address = ip.GetAddressBytes();
        if (address.Length != 4) throw new InvalidDataException("Only IPv4 DHCP addresses are supported.");
        Array.Copy(address, 0, data, offset, address.Length);
    }
}
