using System.Globalization;
using System.Net;

namespace NetBootDhcpTool.Core;

public sealed record ProfileDifference(string Field, string SavedValue, string CurrentValue);

/// <summary>Compares every effective form field while treating routes as an order-independent multiset.</summary>
public static class ProfileComparer
{
    public static IReadOnlyList<ProfileDifference> Compare(NetworkProfile saved, NetworkProfile current,
        Func<StaticRouteRule, string>? adapterIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(current);
        var differences = new List<ProfileDifference>();
        var savedDhcp = saved.Dhcp ?? new DefaultDhcpSettings();
        var currentDhcp = current.Dhcp ?? new DefaultDhcpSettings();
        Diff("Dhcp.ServerIp", Ip(savedDhcp.ServerIp), Ip(currentDhcp.ServerIp));
        Diff("Dhcp.SubnetMask", Mask(savedDhcp.SubnetMask), Mask(currentDhcp.SubnetMask));
        Diff("Dhcp.PoolStart", Ip(savedDhcp.PoolStart), Ip(currentDhcp.PoolStart));
        Diff("Dhcp.PoolEnd", Ip(savedDhcp.PoolEnd), Ip(currentDhcp.PoolEnd));
        Diff("Dhcp.Gateway", Ip(savedDhcp.Gateway), Ip(currentDhcp.Gateway));
        Diff("Dhcp.Dns", Ip(savedDhcp.Dns), Ip(currentDhcp.Dns));
        Diff("Dhcp.LeaseSeconds", savedDhcp.LeaseSeconds.ToString(CultureInfo.InvariantCulture), currentDhcp.LeaseSeconds.ToString(CultureInfo.InvariantCulture));
        Diff("ManualIp", Ip(saved.ManualIp), Ip(current.ManualIp));
        Diff("ManualMask", Mask(saved.ManualMask), Mask(current.ManualMask));
        Diff("ManualTargetIp", Ip(saved.ManualTargetIp), Ip(current.ManualTargetIp));

        Diff("DhcpPreserveAddresses", saved.DhcpPreserveAddresses.ToString(), current.DhcpPreserveAddresses.ToString());
        string AddressKey(AdapterAddressDraft x) => string.Join("|", x.AdapterId.Trim(), x.Name.Trim(), Ip(x.IpAddress), Mask(x.SubnetMask), Ip(x.TargetIp));
        Diff("Addresses", string.Join(";", (saved.Addresses ?? []).Select(AddressKey).Order(StringComparer.OrdinalIgnoreCase)),
            string.Join(";", (current.Addresses ?? []).Select(AddressKey).Order(StringComparer.OrdinalIgnoreCase)));
        var savedRoutes = (saved.Routes ?? []).Select(RouteKey).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Display).ToList(), StringComparer.OrdinalIgnoreCase);
        var currentRoutes = (current.Routes ?? []).Select(RouteKey).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Display).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in savedRoutes.Keys.Union(currentRoutes.Keys, StringComparer.OrdinalIgnoreCase))
        {
            savedRoutes.TryGetValue(key, out var savedInstances);
            currentRoutes.TryGetValue(key, out var currentInstances);
            savedInstances ??= [];
            currentInstances ??= [];
            var matched = Math.Min(savedInstances.Count, currentInstances.Count);
            for (var i = matched; i < savedInstances.Count; i++)
                differences.Add(new ProfileDifference("Route", savedInstances[i], ""));
            for (var i = matched; i < currentInstances.Count; i++)
                differences.Add(new ProfileDifference("Route", "", currentInstances[i]));
        }
        return differences;

        void Diff(string field, string savedValue, string currentValue)
        {
            if (!savedValue.Equals(currentValue, StringComparison.OrdinalIgnoreCase))
                differences.Add(new ProfileDifference(field, savedValue, currentValue));
        }

        (string Key, string Display) RouteKey(StaticRouteRule route)
        {
            if (route is null) return ("invalid|null-route", "invalid route: null route");
            try
            {
                var normalized = StaticRouteValidator.Normalize(route);
                var family = normalized.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                var adapter = adapterIdentity?.Invoke(route) ?? ProfileValidation.AdapterIdentity(route);
                var key = string.Join("|", family, normalized.DestinationPrefix, adapter.Trim(), normalized.NextHop, route.RouteMetric.ToString(CultureInfo.InvariantCulture));
                var display = $"{family} {normalized.DestinationPrefix} via {adapter.Trim()} next-hop {normalized.NextHop} metric {route.RouteMetric}";
                return (key, display);
            }
            catch
            {
                var raw = string.Join("|", route.DestinationPrefix?.Trim(), route.AdapterId?.Trim(), route.AdapterName?.Trim(), route.NextHop?.Trim(), route.RouteMetric);
                return ("invalid|" + raw, "invalid route: " + raw);
            }
        }
    }

    private static string Ip(string? value)
    {
        var text = value?.Trim() ?? "";
        return IPAddress.TryParse(text, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? ip.ToString()
            : text;
    }

    private static string Mask(string? value)
    {
        var text = value?.Trim() ?? "";
        if (IPAddress.TryParse(text, out var ip) && IpNetwork.TryGetPrefixLength(ip, out var prefix)) return "/" + prefix;
        return Ip(text);
    }
}
