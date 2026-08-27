using System.Net;

namespace NetBootDhcpTool.Core;

public sealed record ValidatedStaticRoute(string DestinationPrefix, string NextHop, int RouteMetric, bool IsDefaultRoute);

public static class StaticRouteValidator
{
    public static ValidatedStaticRoute Normalize(StaticRouteRule rule)
    {
        if (rule == null) throw new ArgumentNullException(nameof(rule));
        if (!IpNetwork.TryParseCidr(rule.DestinationPrefix, out _, out var prefix, out var destination))
        {
            throw new FormatException("Destination must be an IPv4 CIDR, for example 192.168.10.0/24 / 目标网段必须是 IPv4 CIDR，例如 192.168.10.0/24");
        }
        if (rule.RouteMetric is < 1 or > 65535)
        {
            throw new FormatException("Route metric must be between 1 and 65535 / 路由跃点必须在 1 到 65535 之间");
        }

        var nextHop = "0.0.0.0";
        if (!string.IsNullOrWhiteSpace(rule.NextHop))
        {
            if (!IPAddress.TryParse(rule.NextHop.Trim(), out var gateway) || gateway.GetAddressBytes().Length != 4)
            {
                throw new FormatException("Next hop must be blank or an IPv4 address / 网关必须留空或填写 IPv4 地址");
            }
            nextHop = gateway.ToString();
        }

        return new ValidatedStaticRoute(destination, nextHop, rule.RouteMetric, prefix == 0);
    }
}
