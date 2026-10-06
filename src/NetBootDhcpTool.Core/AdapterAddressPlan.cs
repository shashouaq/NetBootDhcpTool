using System.Net;
using System.Net.Sockets;

namespace NetBootDhcpTool.Core;

public sealed class AdapterAddressDraft
{
    public string AdapterId { get; set; } = "";
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public string TargetIp { get; set; } = "";
    public AdapterAddressDraft Clone() => (AdapterAddressDraft)MemberwiseClone();
}

public sealed class OwnedAdapterAddress
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AdapterId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int PrefixLength { get; set; }
    public bool SkipAsSource { get; set; }
    public string PolicyStore { get; set; } = "ActiveStore";
    public string State { get; set; } = "Intent";
    public string Error { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AdapterCoexistenceLease
{
    public string AdapterId { get; set; } = "";
    public bool OriginalEnabled { get; set; }
    public string State { get; set; } = "Intent";
}

public sealed class AdapterAddressJournal
{
    public int SchemaVersion { get; set; } = 1;
    public List<OwnedAdapterAddress> Addresses { get; set; } = [];
    public List<AdapterCoexistenceLease> Coexistence { get; set; } = [];
}

public static class AdapterAddressPlan
{
    public const int MaximumAddresses = 32;
    public static List<AdapterAddressDraft> Validate(IEnumerable<AdapterAddressDraft> addresses)
    {
        var result = addresses.Select(x => x?.Clone() ?? throw new InvalidDataException("Empty address entry / 空地址条目")).ToList();
        if (result.Count is < 1 or > MaximumAddresses)
            throw new InvalidDataException("Enter 1–32 addresses / 请输入 1–32 个地址");
        if (result.Select(x => x.AdapterId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new InvalidDataException("One adapter per address plan / 一个地址方案仅配置同一网卡");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result)
        {
            if (string.IsNullOrWhiteSpace(item.AdapterId))
                throw new InvalidDataException("Select an adapter / 请选择网卡");
            if (!IPAddress.TryParse(item.IpAddress?.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidDataException("Invalid IPv4 address / IPv4 地址无效");
            if (!IPAddress.TryParse(item.SubnetMask?.Trim(), out var mask) || mask.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidDataException("Invalid subnet mask / 子网掩码无效");
            var prefix = IpNetwork.PrefixLength(mask);
            var expected = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            if (IpNetwork.ToUInt32(mask) != expected || prefix == 0
                || prefix <= 30 && !IpNetwork.IsUsableHost(ip, ip, mask) || ip.GetAddressBytes()[0] is 0 or 127 or >= 224)
                throw new InvalidDataException("Invalid host address or contiguous mask / 主机地址或连续掩码无效");
            item.IpAddress = ip.ToString();
            item.SubnetMask = mask.ToString();
            if (!keys.Add(item.AdapterId + "|" + item.IpAddress))
                throw new InvalidDataException("Duplicate adapter address / 同一网卡地址重复");
            if (!string.IsNullOrWhiteSpace(item.TargetIp)
                && !ScanRangePlan.TryCreate(item.IpAddress, item.SubnetMask, item.TargetIp, out _, out _))
                throw new InvalidDataException("Invalid target in this subnet / 本网段目标地址无效");
        }
        return result;
    }
}
