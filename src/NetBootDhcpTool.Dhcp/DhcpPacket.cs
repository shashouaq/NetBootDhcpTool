using System.Net;

namespace NetBootDhcpTool.Dhcp;

public sealed class DhcpPacket
{
    public byte Op { get; set; }
    public byte HType { get; set; }
    public byte HLen { get; set; }
    public byte Hops { get; set; }
    public uint Xid { get; set; }
    public ushort Secs { get; set; }
    public ushort Flags { get; set; }
    public IPAddress CiAddr { get; set; } = IPAddress.Any;
    public IPAddress YiAddr { get; set; } = IPAddress.Any;
    public IPAddress SiAddr { get; set; } = IPAddress.Any;
    public IPAddress GiAddr { get; set; } = IPAddress.Any;
    public byte[] ChAddr { get; set; } = new byte[16];
    public string MacAddress => string.Join("-", ChAddr.Take(HLen).Select(x => x.ToString("X2")));
    public string Hostname { get; set; } = "";
    public DhcpMessageType MessageType { get; set; }
    public IPAddress? RequestedIp { get; set; }
}
