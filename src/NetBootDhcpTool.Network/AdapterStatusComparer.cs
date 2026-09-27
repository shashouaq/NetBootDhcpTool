namespace NetBootDhcpTool.Network;

/// <summary>Identifies changes in adapter fields shown by the selected-adapter status panel.</summary>
public static class AdapterStatusComparer
{
    public static bool HasChanges(NetworkAdapterInfo current, NetworkAdapterInfo latest)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(latest);

        return !Same(current.Status, latest.Status)
            || !Same(current.IPv4Address, latest.IPv4Address)
            || !Same(current.SubnetMask, latest.SubnetMask)
            || !Same(current.Gateway, latest.Gateway)
            || !Same(current.Dns, latest.Dns)
            || !Same(current.MacAddress, latest.MacAddress)
            || current.LinkSpeedMbps != latest.LinkSpeedMbps;
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
