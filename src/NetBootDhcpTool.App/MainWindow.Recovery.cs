using System.Windows;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.App;

public partial class MainWindow
{
    private async void OpenRecovery_Click(object sender, RoutedEventArgs e)
    {
        var window = new RecoveryCenterWindow(BuildRecoveryEntries(), _lang, this);
        ApplyOwnedWindowTheme(window);
        if (window.ShowDialog() != true || window.SelectedEntry is not { } selected) return;
        await RestoreRecoveryEntryAsync(selected);
    }

    private List<RecoveryEntryViewModel> BuildRecoveryEntries()
    {
        var entries = new List<RecoveryEntryViewModel>();
        foreach (var backup in _adapterBackups.OrderByDescending(x => x.CapturedAt))
        {
            var available = ResolveAdapterForBackup(backup) != null;
            entries.Add(new RecoveryEntryViewModel
            {
                Kind = RecoveryEntryKind.AdapterConfiguration,
                Payload = backup,
                TypeDisplay = Ui("recovery.kind.adapter"),
                AdapterName = backup.AdapterName,
                IdentityDisplay = RecoveryIdentity(backup.AdapterId, backup.InterfaceIndex, backup.AdapterMac),
                CapturedAtDisplay = backup.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                Summary = Ui("recovery.entry.adapter.summary", backup.DhcpEnabled ? Ui("dhcp.enabled") : Ui("dhcp.disabled"), backup.IpAddress, backup.PrefixLength, string.IsNullOrWhiteSpace(backup.Gateway) ? "-" : backup.Gateway),
                StatusDisplay = Ui(available ? "recovery.status.available" : "recovery.status.unavailable"),
                IsAvailable = available
            });
        }

        foreach (var backup in _originalAdapterMacs.Values.Where(x => x.RestoreOnExit).OrderByDescending(x => x.CapturedAt))
        {
            var available = ResolveAdapterForMacBackup(backup) != null;
            entries.Add(new RecoveryEntryViewModel
            {
                Kind = RecoveryEntryKind.MacAddress,
                Payload = backup,
                TypeDisplay = Ui("recovery.kind.mac"),
                AdapterName = backup.AdapterName,
                IdentityDisplay = RecoveryIdentity(backup.AdapterId, backup.InterfaceIndex, backup.OriginalMacAddress),
                CapturedAtDisplay = backup.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                Summary = Ui("recovery.entry.mac.summary", backup.OriginalMacAddress),
                StatusDisplay = Ui(available ? "recovery.status.available" : "recovery.status.unavailable"),
                IsAvailable = available
            });
        }

        foreach (var route in _appliedStaticRoutes)
        {
            var available = ResolveAdapterForRoute(route) != null;
            entries.Add(new RecoveryEntryViewModel
            {
                Kind = RecoveryEntryKind.StaticRoute,
                Payload = route,
                TypeDisplay = Ui("recovery.kind.route"),
                AdapterName = route.AdapterName,
                IdentityDisplay = RecoveryIdentity(route.AdapterId, route.InterfaceIndex.ToString(), route.AdapterMac),
                CapturedAtDisplay = Ui("recovery.entry.session"),
                Summary = Ui("recovery.entry.route.summary", route.DestinationPrefix, route.NextHop, route.RouteMetric),
                StatusDisplay = Ui(available ? "recovery.status.available" : "recovery.status.unavailable"),
                IsAvailable = available
            });
        }

        return entries;
    }

    private static string RecoveryIdentity(string adapterId, string interfaceIndex, string mac) =>
        string.Join(" | ", new[]
        {
            string.IsNullOrWhiteSpace(adapterId) ? "" : $"ID {adapterId}",
            string.IsNullOrWhiteSpace(interfaceIndex) ? "" : $"ifIndex {interfaceIndex}",
            string.IsNullOrWhiteSpace(mac) ? "" : $"MAC {mac}"
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private async Task RestoreRecoveryEntryAsync(RecoveryEntryViewModel entry)
    {
        var confirmed = entry.Payload switch
        {
            AdapterConfigBackup backup => AppDialog.Show(this, Ui("recovery.restore.adapter"), Ui("recovery.restore.adapter.confirm", backup.AdapterName, backup.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss")), confirm: true, danger: true),
            AdapterMacBackup backup => AppDialog.Show(this, Ui("recovery.restore.mac"), Ui("recovery.restore.mac.confirm", backup.AdapterName, backup.OriginalMacAddress), confirm: true, danger: true),
            AppliedStaticRoute route => AppDialog.Show(this, Ui("recovery.restore.routes"), Ui("recovery.restore.route.confirm", $"{route.DestinationPrefix} -> {route.AdapterName} ({route.NextHop})"), confirm: true, danger: true),
            _ => false
        };
        if (!confirmed) return;

        try
        {
            SetBusy(true, Ui("recovery.operation.running"));
            switch (entry.Payload)
            {
                case AdapterConfigBackup backup:
                {
                    var adapter = ResolveAdapterForBackup(backup) ?? throw new InvalidOperationException(Ui("recovery.status.unavailable"));
                    await RestoreAdapterBackupAsync(adapter, backup, OperationToken);
                    ShowActionFeedbackKey("recovery.restore.adapter");
                    break;
                }
                case AdapterMacBackup backup:
                    await RestoreMacBackupAsync(backup, OperationToken);
                    await RefreshAdaptersAsync();
                    ShowActionFeedbackKey("recovery.restore.mac");
                    break;
                case AppliedStaticRoute route:
                {
                    var adapter = ResolveAdapterForRoute(route) ?? throw new InvalidOperationException(Ui("recovery.status.unavailable"));
                    await RemoveAppliedRouteAsync(route, adapter, OperationToken);
                    ShowActionFeedbackKey("recovery.restore.routes");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Recovery Center restore canceled by user");
        }
        catch (Exception ex)
        {
            _logger.Error("Recovery Center restore failed", ex);
            AppDialog.Show(this, Ui("recovery.title"), ExplainFailure(ex, _lang.T("help.recovery.center")), danger: true);
        }
        finally
        {
            SetBusy(false);
            UpdateRecoveryBanner();
        }
    }

    private async Task RestoreAdapterBackupAsync(NetworkAdapterInfo adapter, AdapterConfigBackup backup, CancellationToken ct)
    {
        var snapshot = new AdapterIpv4Snapshot
        {
            DhcpEnabled = backup.DhcpEnabled,
            Addresses = backup.Addresses,
            Routes = backup.Routes,
            IpAddress = backup.IpAddress,
            PrefixLength = backup.PrefixLength,
            Gateway = backup.Gateway,
            Dns = backup.Dns,
            AutomaticMetric = backup.AutomaticMetric,
            InterfaceMetric = backup.InterfaceMetric
        };
        await _adapterService.RestoreIPv4ConfigAsync(adapter, snapshot, ct);
        await VerifyRestoredAdapterConfigAsync(adapter, snapshot, ct);
        adapter.IPv4Address = snapshot.IpAddress;
        UpdateAdapterIpText(adapter);
        AddOperationHistory("Recovery", snapshot.IpAddress, adapter.MacAddress, "Completed / 已完成", "Adapter backup restored / 网卡备份已恢复", scope: adapter.Name);
        await RefreshAdaptersAsync();
    }

    private NetworkAdapterInfo? ResolveAdapterForBackup(AdapterConfigBackup backup) =>
        AdapterIdentityMatcher.Find(Adapters, backup);

    private NetworkAdapterInfo? ResolveAdapterForMacBackup(AdapterMacBackup backup) =>
        AdapterIdentityMatcher.Find(Adapters, backup);

    private async Task RestoreMacBackupAsync(AdapterMacBackup backup, CancellationToken ct)
    {
        var adapter = ResolveAdapterForMacBackup(backup)
            ?? throw new InvalidOperationException(Ui("recovery.status.unavailable"));
        await _adapterService.RestoreMacAddressAsync(adapter, backup.OriginalMacAddress, ct);
        await VerifyMacAddressAsync(backup, backup.OriginalMacAddress, ct);

        _originalAdapterMacs.Remove(MacBackupKey(backup));
        SaveMacBackups();
        AddOperationHistory("MAC Recovery / MAC 恢复", "", backup.OriginalMacAddress, "Completed / 已完成",
            "Original MAC address restored / 已恢复原始 MAC 地址", scope: backup.AdapterName);
        _logger.Info($"Adapter MAC restored from Recovery Center: idx={backup.InterfaceIndex} name={backup.AdapterName} mac={backup.OriginalMacAddress}");
    }

    private async Task RemoveAppliedRouteAsync(AppliedStaticRoute route, NetworkAdapterInfo adapter, CancellationToken ct)
    {
        if (!_appliedStaticRoutes.Contains(route))
            throw new InvalidOperationException(Ui("recovery.status.unavailable"));

        await _routeService.RemoveAsync(route, adapter, ct);
        _appliedStaticRoutes.Remove(route);
        SaveStaticRouteSession();
        UpdateRouteStatuses();
        await LoadCurrentStaticRoutesAsync(Adapters.ToList(), ct);
        AddOperationHistory("Static Route Recovery / 静态路由恢复", "", adapter.MacAddress, "Completed / 已完成",
            $"Removed {route.DestinationPrefix} via {route.NextHop} / 已删除路由 {route.DestinationPrefix}，下一跳 {route.NextHop}", scope: adapter.Name);
        _logger.Info($"Static route removed from Recovery Center: {route.DestinationPrefix} adapter={adapter.Name}");
    }

}
