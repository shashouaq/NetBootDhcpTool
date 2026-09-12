using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

/// <summary>
/// Resolves saved network operations to the adapter they were captured from.
/// Legacy records without a stable ID require multiple matching attributes.
/// </summary>
public static class AdapterIdentityMatcher
{
    public static NetworkAdapterInfo? Find(IEnumerable<NetworkAdapterInfo> adapters, AdapterConfigBackup backup)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(backup);

        if (!string.IsNullOrWhiteSpace(backup.AdapterId))
            return adapters.FirstOrDefault(x => string.Equals(x.Id, backup.AdapterId, StringComparison.OrdinalIgnoreCase));

        return adapters.FirstOrDefault(x =>
            string.Equals(x.InterfaceIndex, backup.InterfaceIndex, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, backup.AdapterName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(backup.AdapterMac)
                || string.Equals(x.MacAddress, backup.AdapterMac, StringComparison.OrdinalIgnoreCase)));
    }

    public static NetworkAdapterInfo? Find(IEnumerable<NetworkAdapterInfo> adapters, AdapterMacBackup backup)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(backup);

        if (!string.IsNullOrWhiteSpace(backup.AdapterId))
            return adapters.FirstOrDefault(x => string.Equals(x.Id, backup.AdapterId, StringComparison.OrdinalIgnoreCase));

        return adapters.FirstOrDefault(x =>
            string.Equals(x.InterfaceIndex, backup.InterfaceIndex, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, backup.AdapterName, StringComparison.OrdinalIgnoreCase));
    }
}
