using System.Net;
using System.Net.Sockets;

namespace NetBootDhcpTool.Core;

public enum ProfileSectionStatus
{
    Empty,
    Draft,
    Valid,
    Invalid
}

public enum ProfileIssueCode
{
    DhcpServerIpMissing,
    DhcpMaskMissing,
    DhcpPoolMissing,
    DhcpServerIpInvalid,
    DhcpMaskInvalid,
    DhcpPoolInvalid,
    DhcpPoolOutsideSubnet,
    DhcpPoolNotUsable,
    DhcpPoolOrderInvalid,
    DhcpServerInsidePool,
    DhcpGatewayInvalid,
    DhcpDnsInvalid,
    DhcpLeaseOutOfRange,
    ManualScanIncomplete,
    ManualScanInvalid,
    RouteAdapterMissing,
    RouteInvalid,
    RouteAddressFamilyMismatch,
    DuplicateRoute
}

public sealed record ProfileSectionAssessment(ProfileSectionStatus Status, IReadOnlyList<ProfileIssueCode> Issues);

public sealed record NetworkProfileAssessment(
    ProfileSectionAssessment Dhcp,
    ProfileSectionAssessment ManualScan,
    ProfileSectionAssessment Routes)
{
    public ProfileSectionStatus OverallStatus =>
        Dhcp.Status == ProfileSectionStatus.Invalid || ManualScan.Status == ProfileSectionStatus.Invalid || Routes.Status == ProfileSectionStatus.Invalid
            ? ProfileSectionStatus.Invalid
            : Dhcp.Status == ProfileSectionStatus.Draft || ManualScan.Status == ProfileSectionStatus.Draft || Routes.Status == ProfileSectionStatus.Draft
                ? ProfileSectionStatus.Draft
                : Dhcp.Status == ProfileSectionStatus.Valid || ManualScan.Status == ProfileSectionStatus.Valid || Routes.Status == ProfileSectionStatus.Valid
                    ? ProfileSectionStatus.Valid
                    : ProfileSectionStatus.Draft;
}

/// <summary>Applies the same IPv4, DHCP scope, T11 scan, and route rules to saved profiles and executable forms.</summary>
public static class ProfileValidation
{
    public static NetworkProfileAssessment Assess(NetworkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new NetworkProfileAssessment(
            AssessDhcp(profile.Dhcp ?? new DefaultDhcpSettings()),
            AssessManualScan(profile.ManualIp, profile.ManualMask, profile.ManualTargetIp),
            AssessRoutes(profile.Routes ?? []));
    }

    public static ProfileSectionAssessment AssessDhcp(DefaultDhcpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<ProfileIssueCode>();
        var serverText = settings.ServerIp?.Trim() ?? "";
        var maskText = settings.SubnetMask?.Trim() ?? "";
        var startText = settings.PoolStart?.Trim() ?? "";
        var endText = settings.PoolEnd?.Trim() ?? "";

        var hasServer = TryIpv4(serverText, out var serverIp);
        var hasMask = TryIpv4Mask(maskText, out var mask);
        var hasStart = TryIpv4(startText, out var poolStart);
        var hasEnd = TryIpv4(endText, out var poolEnd);

        if (serverText.Length == 0) issues.Add(ProfileIssueCode.DhcpServerIpMissing);
        else if (!hasServer) issues.Add(ProfileIssueCode.DhcpServerIpInvalid);
        if (maskText.Length == 0) issues.Add(ProfileIssueCode.DhcpMaskMissing);
        else if (!hasMask) issues.Add(ProfileIssueCode.DhcpMaskInvalid);
        if (startText.Length == 0 || endText.Length == 0) issues.Add(ProfileIssueCode.DhcpPoolMissing);
        else if (!hasStart || !hasEnd) issues.Add(ProfileIssueCode.DhcpPoolInvalid);

        if (!string.IsNullOrWhiteSpace(settings.Gateway) && !TryIpv4(settings.Gateway, out _))
            issues.Add(ProfileIssueCode.DhcpGatewayInvalid);
        if (!string.IsNullOrWhiteSpace(settings.Dns) && !TryIpv4(settings.Dns, out _))
            issues.Add(ProfileIssueCode.DhcpDnsInvalid);
        if (settings.LeaseSeconds is < 1 or > 604800)
            issues.Add(ProfileIssueCode.DhcpLeaseOutOfRange);

        if (issues.Any(IsDhcpValueError))
            return new ProfileSectionAssessment(ProfileSectionStatus.Invalid, issues);
        if (issues.Count > 0)
            return new ProfileSectionAssessment(ProfileSectionStatus.Draft, issues);

        if (!IpNetwork.SameSubnet(serverIp, poolStart, mask) || !IpNetwork.SameSubnet(serverIp, poolEnd, mask))
            issues.Add(ProfileIssueCode.DhcpPoolOutsideSubnet);
        else if (!IpNetwork.IsUsableHost(serverIp, serverIp, mask)
            || !IpNetwork.IsUsableHost(poolStart, serverIp, mask)
            || !IpNetwork.IsUsableHost(poolEnd, serverIp, mask))
            issues.Add(ProfileIssueCode.DhcpPoolNotUsable);
        else if (IpNetwork.ToUInt32(poolStart) > IpNetwork.ToUInt32(poolEnd))
            issues.Add(ProfileIssueCode.DhcpPoolOrderInvalid);
        else if (IpNetwork.ToUInt32(serverIp) >= IpNetwork.ToUInt32(poolStart)
            && IpNetwork.ToUInt32(serverIp) <= IpNetwork.ToUInt32(poolEnd))
            issues.Add(ProfileIssueCode.DhcpServerInsidePool);

        return new ProfileSectionAssessment(issues.Count == 0 ? ProfileSectionStatus.Valid : ProfileSectionStatus.Invalid, issues);
    }

    public static ProfileSectionAssessment AssessManualScan(string? localIp, string? mask, string? targetIp)
    {
        localIp = localIp?.Trim() ?? "";
        mask = mask?.Trim() ?? "";
        targetIp = targetIp?.Trim() ?? "";
        if (localIp.Length == 0 && mask.Length == 0 && targetIp.Length == 0)
            return new ProfileSectionAssessment(ProfileSectionStatus.Empty, []);
        if (localIp.Length == 0 || mask.Length == 0)
            return new ProfileSectionAssessment(ProfileSectionStatus.Draft, [ProfileIssueCode.ManualScanIncomplete]);
        return ScanRangePlan.TryCreate(localIp, mask, targetIp, out _, out _)
            ? new ProfileSectionAssessment(ProfileSectionStatus.Valid, [])
            : new ProfileSectionAssessment(ProfileSectionStatus.Invalid, [ProfileIssueCode.ManualScanInvalid]);
    }

    public static ProfileSectionAssessment AssessRoutes(IEnumerable<StaticRouteRule>? routes)
    {
        var items = (routes ?? []).ToList();
        if (items.Count == 0) return new ProfileSectionAssessment(ProfileSectionStatus.Empty, []);
        var issues = new List<ProfileIssueCode>();
        var accepted = new List<StaticRouteRule>();
        foreach (var route in items)
        {
            if (route is null)
            {
                issues.Add(ProfileIssueCode.RouteInvalid);
                continue;
            }
            if (string.IsNullOrWhiteSpace(route.AdapterId) && string.IsNullOrWhiteSpace(route.AdapterName) && string.IsNullOrWhiteSpace(route.AdapterMac))
                issues.Add(ProfileIssueCode.RouteAdapterMissing);
            try
            {
                var normalized = StaticRouteValidator.Normalize(route);
                var expectedFamily = normalized.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                if (!string.IsNullOrWhiteSpace(route.AddressFamily)
                    && !route.AddressFamily.Equals(expectedFamily, StringComparison.OrdinalIgnoreCase)
                    && !route.AddressFamily.Equals(normalized.AddressFamily.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(ProfileIssueCode.RouteAddressFamilyMismatch);
                    continue;
                }
                if (accepted.Any(existing => SameRouteTarget(existing, route)))
                    issues.Add(ProfileIssueCode.DuplicateRoute);
                else
                    accepted.Add(route);
            }
            catch
            {
                issues.Add(ProfileIssueCode.RouteInvalid);
            }
        }
        return new ProfileSectionAssessment(issues.Count == 0 ? ProfileSectionStatus.Valid : ProfileSectionStatus.Invalid, issues);
    }

    internal static bool RoutesEquivalent(StaticRouteRule left, StaticRouteRule right)
    {
        var leftRoute = StaticRouteValidator.Normalize(left);
        var rightRoute = StaticRouteValidator.Normalize(right);
        return leftRoute.AddressFamily == rightRoute.AddressFamily
            && leftRoute.DestinationPrefix.Equals(rightRoute.DestinationPrefix, StringComparison.OrdinalIgnoreCase)
            && leftRoute.NextHop.Equals(rightRoute.NextHop, StringComparison.OrdinalIgnoreCase)
            && left.RouteMetric == right.RouteMetric
            && SameAdapterIdentity(left, right);
    }

    private static bool SameRouteTarget(StaticRouteRule left, StaticRouteRule right)
    {
        var leftRoute = StaticRouteValidator.Normalize(left);
        var rightRoute = StaticRouteValidator.Normalize(right);
        return leftRoute.AddressFamily == rightRoute.AddressFamily
            && leftRoute.DestinationPrefix.Equals(rightRoute.DestinationPrefix, StringComparison.OrdinalIgnoreCase)
            && leftRoute.NextHop.Equals(rightRoute.NextHop, StringComparison.OrdinalIgnoreCase)
            && SameAdapterIdentity(left, right);
    }

    public static string AdapterIdentity(StaticRouteRule route)
    {
        if (!string.IsNullOrWhiteSpace(route.AdapterId)) return "id:" + route.AdapterId.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(route.AdapterMac)) return "mac:" + NormalizeMac(route.AdapterMac);
        if (!string.IsNullOrWhiteSpace(route.AdapterName)) return "name:" + route.AdapterName.Trim().ToUpperInvariant();
        return "adapter:unknown";
    }

    private static bool SameAdapterIdentity(StaticRouteRule left, StaticRouteRule right)
    {
        if (string.IsNullOrWhiteSpace(left.AdapterId) && string.IsNullOrWhiteSpace(left.AdapterMac) && string.IsNullOrWhiteSpace(left.AdapterName)
            && string.IsNullOrWhiteSpace(right.AdapterId) && string.IsNullOrWhiteSpace(right.AdapterMac) && string.IsNullOrWhiteSpace(right.AdapterName)) return true;
        if (!string.IsNullOrWhiteSpace(left.AdapterId) && !string.IsNullOrWhiteSpace(right.AdapterId))
            return left.AdapterId.Equals(right.AdapterId, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(left.AdapterMac) && !string.IsNullOrWhiteSpace(right.AdapterMac))
            return NormalizeMac(left.AdapterMac) == NormalizeMac(right.AdapterMac);
        return !string.IsNullOrWhiteSpace(left.AdapterName) && !string.IsNullOrWhiteSpace(right.AdapterName)
            && left.AdapterName.Trim().Equals(right.AdapterName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMac(string value) => new string(value.Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool IsDhcpValueError(ProfileIssueCode issue) => issue is
        ProfileIssueCode.DhcpServerIpInvalid or ProfileIssueCode.DhcpMaskInvalid or ProfileIssueCode.DhcpPoolInvalid
        or ProfileIssueCode.DhcpGatewayInvalid or ProfileIssueCode.DhcpDnsInvalid or ProfileIssueCode.DhcpLeaseOutOfRange;

    private static bool TryIpv4(string value, out IPAddress ip) =>
        IPAddress.TryParse(value, out ip!) && ip.AddressFamily == AddressFamily.InterNetwork;

    private static bool TryIpv4Mask(string value, out IPAddress mask) =>
        TryIpv4(value, out mask!) && IpNetwork.TryGetPrefixLength(mask, out _);
}
