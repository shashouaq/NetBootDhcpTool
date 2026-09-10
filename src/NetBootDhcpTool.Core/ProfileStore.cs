using System.Text.Json;

namespace NetBootDhcpTool.Core;

public static class ProfileStore
{
    public static List<NetworkProfile> Load(string path, ILogger? logger = null)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var profiles = JsonSerializer.Deserialize<List<NetworkProfile>>(File.ReadAllText(path), JsonStore.Options) ?? [];
            foreach (var profile in profiles)
            {
                profile.Dhcp ??= new DefaultDhcpSettings();
                profile.Routes ??= [];
            }
            return profiles;
        }
        catch (Exception ex)
        {
            logger?.Error($"Load profiles failed: {path}", ex);
            return [];
        }
    }

    public static void Save(string path, IEnumerable<NetworkProfile> profiles, ILogger? logger = null)
    {
        try
        {
            JsonStore.Save(path, profiles.Select(Clone).ToList());
        }
        catch (Exception ex)
        {
            logger?.Error($"Save profiles failed: {path}", ex);
            throw;
        }
    }

    public static NetworkProfile Clone(NetworkProfile source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Description = source.Description,
        Dhcp = new DefaultDhcpSettings
        {
            ServerIp = source.Dhcp.ServerIp,
            SubnetMask = source.Dhcp.SubnetMask,
            PoolStart = source.Dhcp.PoolStart,
            PoolEnd = source.Dhcp.PoolEnd,
            Gateway = source.Dhcp.Gateway,
            Dns = source.Dhcp.Dns,
            LeaseSeconds = source.Dhcp.LeaseSeconds
        },
        ManualIp = source.ManualIp,
        ManualMask = source.ManualMask,
        ManualTargetIp = source.ManualTargetIp,
        Routes = (source.Routes ?? []).Select(CloneRoute).ToList(),
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt
    };

    private static StaticRouteRule CloneRoute(StaticRouteRule source) => new()
    {
        Id = source.Id,
        DestinationPrefix = source.DestinationPrefix,
        AddressFamily = source.AddressFamily,
        AdapterId = source.AdapterId,
        AdapterName = source.AdapterName,
        AdapterMac = source.AdapterMac,
        NextHop = source.NextHop,
        RouteMetric = source.RouteMetric,
        InterfaceMetric = source.InterfaceMetric
    };
}
