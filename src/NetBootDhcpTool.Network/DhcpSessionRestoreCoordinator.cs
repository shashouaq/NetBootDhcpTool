using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public enum DhcpSessionRestoreState
{
    Captured,
    RestorePending,
    Restored,
    RestoreFailed
}

public enum DhcpSessionRestoreFailure
{
    AdapterUnavailable,
    AdapterIdentityAmbiguous,
    ExpectedSnapshotUnavailable,
    ExternalConfigurationChanged
}

public sealed class DhcpSessionRestoreException : InvalidOperationException
{
    public DhcpSessionRestoreException(DhcpSessionRestoreFailure reason)
        : base(reason.ToString())
    {
        Reason = reason;
    }

    public DhcpSessionRestoreFailure Reason { get; }
}

public sealed class DhcpSessionRecovery
{
    public DhcpSessionRecovery(AdapterConfigBackup adapterIdentity, AdapterIpv4Snapshot originalSnapshot)
    {
        ArgumentNullException.ThrowIfNull(adapterIdentity);
        ArgumentNullException.ThrowIfNull(originalSnapshot);
        if (string.IsNullOrWhiteSpace(adapterIdentity.AdapterId))
            throw new ArgumentException("A stable adapter ID is required for DHCP session recovery.", nameof(adapterIdentity));

        AdapterIdentity = adapterIdentity;
        OriginalSnapshot = originalSnapshot;
    }

    public AdapterConfigBackup AdapterIdentity { get; }
    public AdapterIpv4Snapshot OriginalSnapshot { get; }
    public AdapterIpv4Snapshot? ExpectedSessionSnapshot { get; private set; }
    public DhcpSessionRestoreState State { get; private set; } = DhcpSessionRestoreState.Captured;
    public bool RequiresRestore => State is DhcpSessionRestoreState.RestorePending or DhcpSessionRestoreState.RestoreFailed;

    public void MarkModificationPending()
    {
        if (State != DhcpSessionRestoreState.Captured)
            throw new InvalidOperationException($"Cannot begin DHCP modification from state {State}.");
        State = DhcpSessionRestoreState.RestorePending;
    }

    public void SetExpectedSessionSnapshot(AdapterIpv4Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (State is not (DhcpSessionRestoreState.RestorePending or DhcpSessionRestoreState.RestoreFailed))
            throw new InvalidOperationException($"Cannot set the DHCP session snapshot from state {State}.");
        ExpectedSessionSnapshot = snapshot;
    }

    public void MarkRestored() => State = DhcpSessionRestoreState.Restored;
    public void MarkRestoreFailed() => State = DhcpSessionRestoreState.RestoreFailed;
}

/// <summary>
/// Restores only the uniquely identified adapter and only while its configuration still matches
/// the state captured for this DHCP session (or is already back at the original state).
/// </summary>
public static class DhcpSessionRestoreCoordinator
{
    public static bool MatchesAppliedHostConfiguration(AdapterIpv4Snapshot snapshot, string expectedIp, int expectedPrefixLength)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.NormalizeLegacyFields();
        var normalizedIp = System.Net.IPAddress.Parse(expectedIp).ToString();
        return !snapshot.DhcpEnabled
            && snapshot.Addresses.Count == 1
            && string.Equals(snapshot.Addresses[0].IpAddress, normalizedIp, StringComparison.OrdinalIgnoreCase)
            && snapshot.Addresses[0].PrefixLength == expectedPrefixLength
            && snapshot.Routes.Count == 0
            && snapshot.DnsMode == AdapterDnsMode.Automatic
            && snapshot.AdapterEnabled == true
            && !snapshot.AutomaticMetric
            && snapshot.InterfaceMetric == NetworkAdapterService.DhcpHostAdapterMetric;
    }

    public static async Task RestoreAsync(
        DhcpSessionRecovery recovery,
        IEnumerable<NetworkAdapterInfo> adapters,
        Func<NetworkAdapterInfo, CancellationToken, Task<AdapterIpv4Snapshot>> capture,
        Func<NetworkAdapterInfo, AdapterIpv4Snapshot, CancellationToken, Task> restoreAndVerify,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(restoreAndVerify);

        if (!recovery.RequiresRestore) return;

        try
        {
            var matches = AdapterIdentityMatcher.FindMatches(adapters, recovery.AdapterIdentity);
            if (matches.Count == 0) throw new DhcpSessionRestoreException(DhcpSessionRestoreFailure.AdapterUnavailable);
            if (matches.Count > 1) throw new DhcpSessionRestoreException(DhcpSessionRestoreFailure.AdapterIdentityAmbiguous);

            var adapter = matches[0];
            var current = await capture(adapter, cancellationToken);

            // A failed or canceled start can reach cleanup before the interface was changed.
            // Recognize the original state and complete without issuing another write.
            if (AdapterIpv4SnapshotComparer.Equivalent(current, recovery.OriginalSnapshot))
            {
                recovery.MarkRestored();
                return;
            }

            var expected = recovery.ExpectedSessionSnapshot
                ?? throw new DhcpSessionRestoreException(DhcpSessionRestoreFailure.ExpectedSnapshotUnavailable);
            if (!AdapterIpv4SnapshotComparer.Equivalent(current, expected))
                throw new DhcpSessionRestoreException(DhcpSessionRestoreFailure.ExternalConfigurationChanged);

            await restoreAndVerify(adapter, recovery.OriginalSnapshot, cancellationToken);
            recovery.MarkRestored();
        }
        catch
        {
            recovery.MarkRestoreFailed();
            throw;
        }
    }
}

public static class AdapterIpv4SnapshotComparer
{
    public static bool Equivalent(AdapterIpv4Snapshot left, AdapterIpv4Snapshot right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        left.NormalizeLegacyFields();
        right.NormalizeLegacyFields();

        return left.DhcpEnabled == right.DhcpEnabled
            && left.DnsMode != AdapterDnsMode.Unknown
            && left.DnsMode == right.DnsMode
            && (left.DnsMode != AdapterDnsMode.Static || SequenceEqual(left.Dns, right.Dns))
            && left.AdapterEnabled == right.AdapterEnabled
            && left.AutomaticMetric == right.AutomaticMetric
            && (left.AutomaticMetric || left.InterfaceMetric == right.InterfaceMetric)
            && SequenceEqual(left.Addresses.Select(AddressKey), right.Addresses.Select(AddressKey))
            && SequenceEqual(left.Routes.Select(RouteKey), right.Routes.Select(RouteKey));
    }

    private static string RouteKey(AdapterRouteSnapshot route) =>
        $"{route.DestinationPrefix}|{route.NextHop}|{route.RouteMetric}|{route.PolicyStore}|{route.Protocol}";

    private static string AddressKey(AdapterIpv4AddressSnapshot address) =>
        $"{address.IpAddress}/{address.PrefixLength}|SkipAsSource={address.SkipAsSource}";

    private static bool SequenceEqual(IEnumerable<string> left, IEnumerable<string> right) =>
        left.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
}
