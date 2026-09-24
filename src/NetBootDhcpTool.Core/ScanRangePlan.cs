using System.Net;
using System.Net.Sockets;

namespace NetBootDhcpTool.Core;

public enum ScanRangeError
{
    None,
    InvalidLocalIp,
    InvalidSubnetMask,
    InvalidLocalHost,
    InvalidTargetIp,
    TargetOutsideSubnet,
    TargetNotUsableHost,
    NoSubnetTargets,
    TooManyTargets
}

/// <summary>Validated, count-bounded scan work. Targets are generated only as workers request them.</summary>
public sealed class ScanRangePlan
{
    public const int MaxProbeTargets = 4096;

    private ScanRangePlan(IPAddress localIp, IPAddress subnetMask, IPAddress? targetIp, int targetCount, int prefixLength)
    {
        LocalIp = localIp;
        SubnetMask = subnetMask;
        TargetIp = targetIp;
        TargetCount = targetCount;
        PrefixLength = prefixLength;
    }

    public IPAddress LocalIp { get; }
    public IPAddress SubnetMask { get; }
    public IPAddress? TargetIp { get; }
    public int TargetCount { get; }
    public int PrefixLength { get; }
    public bool IsSingleTarget => TargetIp is not null;

    public static bool TryCreate(string? localIpText, string? subnetMaskText, string? targetIpText,
        out ScanRangePlan? plan, out ScanRangeError error)
    {
        plan = null;
        error = ScanRangeError.None;
        if (!IPAddress.TryParse(localIpText?.Trim(), out var localIp)
            || localIp.AddressFamily != AddressFamily.InterNetwork)
        {
            error = ScanRangeError.InvalidLocalIp;
            return false;
        }
        if (!IPAddress.TryParse(subnetMaskText?.Trim(), out var mask)
            || !IpNetwork.TryGetPrefixLength(mask, out var prefixLength))
        {
            error = ScanRangeError.InvalidSubnetMask;
            return false;
        }

        var localValue = IpNetwork.ToUInt32(localIp);
        var maskValue = IpNetwork.ToUInt32(mask);
        var network = localValue & maskValue;
        var broadcast = network | ~maskValue;
        if (prefixLength <= 30 && (localValue == network || localValue == broadcast))
        {
            error = ScanRangeError.InvalidLocalHost;
            return false;
        }

        var targetText = targetIpText?.Trim() ?? "";
        if (targetText.Length > 0)
        {
            if (!IPAddress.TryParse(targetText, out var targetIp)
                || targetIp.AddressFamily != AddressFamily.InterNetwork)
            {
                error = ScanRangeError.InvalidTargetIp;
                return false;
            }
            if (!IpNetwork.SameSubnet(localIp, targetIp, mask))
            {
                error = ScanRangeError.TargetOutsideSubnet;
                return false;
            }
            var targetValue = IpNetwork.ToUInt32(targetIp);
            if (prefixLength <= 30 && (targetValue == network || targetValue == broadcast))
            {
                error = ScanRangeError.TargetNotUsableHost;
                return false;
            }
            plan = new ScanRangePlan(localIp, mask, targetIp, 1, prefixLength);
            return true;
        }

        var subnetSize = 1UL << (32 - prefixLength);
        var count = prefixLength <= 30 ? subnetSize - 2 : subnetSize;
        if (localValue >= network && (ulong)localValue < (ulong)network + subnetSize) count--;
        if (count == 0)
        {
            error = ScanRangeError.NoSubnetTargets;
            return false;
        }
        if (count > MaxProbeTargets)
        {
            error = ScanRangeError.TooManyTargets;
            return false;
        }
        plan = new ScanRangePlan(localIp, mask, null, (int)count, prefixLength);
        return true;
    }

    public IEnumerable<IPAddress> EnumerateTargets()
    {
        if (TargetIp is not null)
        {
            yield return TargetIp;
            yield break;
        }
        foreach (var host in IpNetwork.Hosts(LocalIp, SubnetMask))
        {
            if (!host.Equals(LocalIp)) yield return host;
        }
    }
}
