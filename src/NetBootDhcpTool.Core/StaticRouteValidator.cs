using System.Net;
using System.Net.Sockets;

namespace NetBootDhcpTool.Core;

public sealed record ValidatedStaticRoute(
    string DestinationPrefix,
    string NextHop,
    int RouteMetric,
    AddressFamily AddressFamily,
    int PrefixLength,
    bool IsDefaultRoute);

public static class StaticRouteValidator
{
    public static ValidatedStaticRoute Normalize(StaticRouteRule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        if (!IpNetwork.TryParseCidr(rule.DestinationPrefix, out var destinationNetwork, out var prefix, out var destination))
        {
            throw new FormatException("Destination must be an IPv4/IPv6 address or CIDR, for example 192.168.10.0/24 / 目标必须是 IPv4/IPv6 地址或 CIDR，例如 192.168.10.0/24");
        }
        if (rule.RouteMetric is < 1 or > 65535)
        {
            throw new FormatException("Route metric must be between 1 and 65535 / 路由跃点必须在 1 到 65535 之间");
        }

        if (prefix == 0)
        {
            throw new FormatException("Default routes are not supported by this tool / 本工具不允许新增默认路由");
        }

        var nextHop = destinationNetwork.AddressFamily == AddressFamily.InterNetwork ? "0.0.0.0" : "::";
        if (!string.IsNullOrWhiteSpace(rule.NextHop))
        {
            if (!IPAddress.TryParse(rule.NextHop.Trim(), out var gateway)
                || gateway.AddressFamily != destinationNetwork.AddressFamily)
            {
                throw new FormatException("Next hop must be blank and match the destination address family / 网关必须留空且与目标地址族一致");
            }
            nextHop = gateway.ToString();
        }

        return new ValidatedStaticRoute(destination, nextHop, rule.RouteMetric, destinationNetwork.AddressFamily, prefix, false);
    }
}
