using System.IO;
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
            var ownershipVerified = route.OwnershipVerified && !string.IsNullOrWhiteSpace(route.InstanceId);
            var available = ownershipVerified && ResolveAdapterForRoute(route) != null;
            entries.Add(new RecoveryEntryViewModel
            {
                Kind = RecoveryEntryKind.StaticRoute,
                Payload = route,
                TypeDisplay = Ui("recovery.kind.route"),
                AdapterName = route.AdapterName,
                IdentityDisplay = RecoveryIdentity(route.AdapterId, route.InterfaceIndex.ToString(), route.AdapterMac),
                CapturedAtDisplay = Ui("recovery.entry.session"),
                Summary = ownershipVerified
                    ? Ui("recovery.entry.route.summary", route.DestinationPrefix, route.NextHop, route.RouteMetric)
                    : Ui("recovery.entry.route.pending", route.DestinationPrefix, route.NextHop, route.RouteMetric),
                StatusDisplay = Ui(!ownershipVerified ? "recovery.status.pending" : available ? "recovery.status.available" : "recovery.status.unavailable"),
                IsAvailable = available
            });
        }

        foreach (var lease in _dhcpFirewallRecoveries.Where(x => _dhcpFirewallRules?.LeaseId != x.LeaseId))
        {
            var pendingVerification = lease.Rules.Any(x => !x.OwnershipVerified || string.IsNullOrWhiteSpace(x.InstanceId));
            var firewallServiceActive = _dhcpServer.IsRunning;
            var details = string.Join(", ", lease.Rules.Select(x => $"{x.Direction} UDP {x.LocalPort}/{x.RemotePort}"));
            entries.Add(new RecoveryEntryViewModel
            {
                Kind = RecoveryEntryKind.DhcpFirewall,
                Payload = lease,
                TypeDisplay = Ui("recovery.kind.firewall"),
                AdapterName = lease.InterfaceAlias,
                IdentityDisplay = $"{lease.LeaseId} | {lease.Rules.Count} rule(s)",
                CapturedAtDisplay = lease.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Summary = pendingVerification
                    ? Ui("recovery.entry.firewall.pending", details)
                    : Ui("recovery.entry.firewall.summary", details),
                StatusDisplay = firewallServiceActive
                    ? Ui("recovery.status.firewall.active")
                    : pendingVerification ? Ui("recovery.status.firewall.pending") : Ui("recovery.status.available"),
                IsAvailable = _dhcpFirewallJournalWritable && !firewallServiceActive
            });
        }

        if (_addressManager is not null)
        {
            foreach (var address in _addressManager.Journal.Addresses)
                entries.Add(new RecoveryEntryViewModel { Kind = RecoveryEntryKind.AdapterAddress, Payload = address,
                    TypeDisplay = _lang.T("addresses.recover"), AdapterName = address.AdapterName, IdentityDisplay = address.AdapterId,
                    CapturedAtDisplay = address.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    Summary = $"{address.IpAddress}/{address.PrefixLength} ActiveStore", StatusDisplay = address.State + " " + address.Error,
                    IsAvailable = Adapters.Count(x => x.Id.Equals(address.AdapterId, StringComparison.OrdinalIgnoreCase)) == 1 });
            foreach (var lease in _addressManager.Journal.Coexistence)
                entries.Add(new RecoveryEntryViewModel { Kind = RecoveryEntryKind.AddressCoexistence, Payload = lease,
                    TypeDisplay = _lang.T("addresses.coexist"), AdapterName = lease.AdapterId, IdentityDisplay = lease.AdapterId,
                    CapturedAtDisplay = "-", Summary = _lang.T("addresses.coexist.body"), StatusDisplay = lease.State,
                    IsAvailable = Adapters.Count(x => x.Id.Equals(lease.AdapterId, StringComparison.OrdinalIgnoreCase)) == 1 });
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
        if (!EnsureSafetyOnboarding()) return;
        var confirmed = entry.Payload switch
        {
            AdapterConfigBackup backup => AppDialog.Show(this, Ui("recovery.restore.adapter"), Ui("recovery.restore.adapter.confirm", backup.AdapterName, backup.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss")), confirm: true, danger: true),
            AdapterMacBackup backup => AppDialog.Show(this, Ui("recovery.restore.mac"), Ui("recovery.restore.mac.confirm", backup.AdapterName, backup.OriginalMacAddress), confirm: true, danger: true),
            AppliedStaticRoute route => AppDialog.Show(this, Ui("recovery.restore.routes"), Ui("recovery.restore.route.confirm", $"{route.DestinationPrefix} -> {route.AdapterName} ({route.NextHop})"), confirm: true, danger: true),
            DhcpFirewallRuleLease lease => AppDialog.Show(this, Ui("recovery.firewall.title"), Ui("recovery.restore.firewall.confirm", lease.InterfaceAlias, lease.Rules.Count), confirm: true, danger: true),
            OwnedAdapterAddress address => AppDialog.Show(this, _lang.T("addresses.recover"), address.IpAddress + "\n" + _lang.T("addresses.remove.body"), confirm: true, danger: true),
            AdapterCoexistenceLease lease => AppDialog.Show(this, _lang.T("addresses.coexist"), lease.AdapterId + "\n" + _lang.T("addresses.coexist.body"), confirm: true, danger: true),
            _ => false
        };
        if (!confirmed) return;
        await ExecuteRecoveryEntryAsync(entry);
    }

    internal async Task<bool> ExecuteRecoveryEntryAsync(RecoveryEntryViewModel entry)
    {
        if (!_safetyOnboardingCompleted)
        {
            ShowActionFeedback(
                "请先完成安全引导，再恢复网络配置。",
                "Complete the Safety Guide before restoring network configuration.",
                error: true);
            return false;
        }
        var workflow = TryBeginNetworkWorkflow("Recovery Center restore");
        if (workflow is null) return false;

        try
        {
            SetBusy(true, Ui("recovery.operation.running"));
            switch (entry.Payload)
            {
                case OwnedAdapterAddress address:
                    if (_addressManager is null) throw new InvalidOperationException(_lang.T("addresses.storage.blocked"));
                    await StopAddressMonitoringAsync();
                    await _addressManager.RemoveAsync(address, OperationToken, true);
                    RefreshAddressRecovery();
                    break;
                case AdapterCoexistenceLease lease:
                    if (_addressManager is null) throw new InvalidOperationException(_lang.T("addresses.storage.blocked"));
                    await _addressManager.RestoreCoexistenceAsync(lease.AdapterId, OperationToken, true);
                    RefreshAddressRecovery();
                    break;
                case AdapterConfigBackup backup:
                {
                    var adapter = ResolveAdapterForBackup(backup) ?? throw new InvalidOperationException(Ui("recovery.status.unavailable"));
                    await RestoreAdapterBackupAsync(adapter, backup, OperationToken);
                    ShowActionFeedbackKey("recovery.restore.adapter");
                    break;
                }
                case AdapterMacBackup backup:
                    await RestoreMacBackupAsync(backup, OperationToken);
                    await RefreshAdaptersAsync(allowDuringNetworkWorkflow: true, cancellationToken: CancellationToken.None);
                    ShowActionFeedbackKey("recovery.restore.mac");
                    break;
                case AppliedStaticRoute route:
                {
                    var adapter = ResolveAdapterForRoute(route) ?? throw new InvalidOperationException(Ui("recovery.status.unavailable"));
                    await RemoveAppliedRouteAsync(route, adapter, OperationToken);
                    ShowActionFeedbackKey("recovery.restore.routes");
                    break;
                }
                case DhcpFirewallRuleLease firewallLease:
                {
                    if (!_dhcpFirewallJournalWritable) throw new InvalidOperationException(Ui("firewall.journal.unreadable"));
                    if (_dhcpFirewallRules?.LeaseId == firewallLease.LeaseId || _dhcpServer.IsRunning)
                        throw new InvalidOperationException(Ui("firewall.recovery.active"));
                    var stored = _dhcpFirewallRecoveries.FirstOrDefault(x => x.LeaseId.Equals(firewallLease.LeaseId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(Ui("recovery.status.unavailable"));
                    await _adapterService.RemoveDhcpFirewallRulesAsync(stored, CommitDhcpFirewallLease, OperationToken);
                    AddOperationHistory("DHCP firewall recovery", "", "", "Completed / 已完成",
                        "Owned DHCP firewall rules were verified removed / 已核实删除所属 DHCP 防火墙规则", scope: stored.InterfaceAlias, rollbackAvailable: false);
                    ShowActionFeedbackKey("recovery.restore.firewall");
                    break;
                }
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Recovery Center restore canceled by user");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error("Recovery Center restore failed", ex);
            if (entry.Payload is DhcpFirewallRuleLease firewallLease)
                AddOperationHistory("DHCP firewall recovery", "", "", "Pending / 待处理", ex.Message, scope: firewallLease.InterfaceAlias, rollbackAvailable: true);
            if (!_closingCleanupStarted)
                AppDialog.Show(this, Ui("recovery.title"), ExplainFailure(ex, _lang.T("help.recovery.center")), danger: true);
            return false;
        }
        finally
        {
            SetBusy(false);
            EndNetworkWorkflow(workflow);
            UpdateRecoveryBanner();
        }
    }

    private async Task RestoreAdapterBackupAsync(NetworkAdapterInfo adapter, AdapterConfigBackup backup, CancellationToken ct)
    {
        if (HasAddressRecovery(adapter.Id)) throw new InvalidOperationException("Clean appended addresses before full restore / 完整恢复前请先清理追加地址");
        await StopAddressMonitoringAsync();
        var preRestoreSnapshot = await _adapterService.CaptureIPv4ConfigAsync(adapter, ct);
        var snapshot = new AdapterIpv4Snapshot
        {
            DhcpEnabled = backup.DhcpEnabled,
            Addresses = backup.Addresses,
            Routes = backup.Routes,
            IpAddress = backup.IpAddress,
            PrefixLength = backup.PrefixLength,
            Gateway = backup.Gateway,
            Dns = backup.Dns,
            DnsMode = backup.DnsMode,
            AdapterEnabled = backup.AdapterEnabled,
            AutomaticMetric = backup.AutomaticMetric,
            InterfaceMetric = backup.InterfaceMetric
        };
        try
        {
            await _adapterService.RestoreIPv4ConfigAsync(adapter, snapshot, ct);
            await VerifyRestoredAdapterConfigAsync(adapter, snapshot, ct);
        }
        catch
        {
            await CompensateAdapterConfigAsync(adapter, preRestoreSnapshot, "Recovery Center adapter restore");
            throw;
        }
        adapter.IPv4Address = snapshot.IpAddress;
        UpdateAdapterIpText(adapter);
        AddOperationHistory("Recovery", snapshot.IpAddress, adapter.MacAddress, "Completed / 已完成", "Adapter backup restored / 网卡备份已恢复", scope: adapter.Name);
        await RefreshAdaptersAsync(allowDuringNetworkWorkflow: true, cancellationToken: CancellationToken.None);
    }

    private NetworkAdapterInfo? ResolveAdapterForBackup(AdapterConfigBackup backup) =>
        AdapterIdentityMatcher.Find(Adapters, backup);

    private NetworkAdapterInfo? ResolveAdapterForMacBackup(AdapterMacBackup backup) =>
        AdapterIdentityMatcher.Find(Adapters, backup);

    private async Task RestoreMacBackupAsync(AdapterMacBackup backup, CancellationToken ct)
    {
        if (!_macBackupsWritable)
            throw new InvalidOperationException("MAC recovery records could not be read; refusing to change the adapter without a writable recovery record.");
        var adapter = ResolveAdapterForMacBackup(backup)
            ?? throw new InvalidOperationException(Ui("recovery.status.unavailable"));
        var preRestoreMac = adapter.MacAddress;
        try
        {
            await _adapterService.RestoreMacAddressAsync(adapter, backup.OriginalMacAddress, ct);
            await VerifyMacAddressAsync(backup, backup.OriginalMacAddress, ct);
        }
        catch
        {
            try
            {
                await _adapterService.RestoreMacAddressAsync(adapter, preRestoreMac, CancellationToken.None);
                await VerifyMacAddressAsync(backup, preRestoreMac, CancellationToken.None);
            }
            catch (Exception compensationEx)
            {
                _logger.Error("Recovery Center MAC restore compensation failed; recovery entry remains available", compensationEx);
            }
            throw;
        }

        _originalAdapterMacs.Remove(MacBackupKey(backup));
        if (!SaveMacBackups())
        {
            _originalAdapterMacs[MacBackupKey(backup)] = backup;
            throw new IOException("The restored MAC recovery record could not be updated on disk.");
        }
        AddOperationHistory("MAC Recovery / MAC 恢复", "", backup.OriginalMacAddress, "Completed / 已完成",
            "Original MAC address restored / 已恢复原始 MAC 地址", scope: backup.AdapterName);
        _logger.Info($"Adapter MAC restored from Recovery Center: idx={backup.InterfaceIndex} name={backup.AdapterName} mac={backup.OriginalMacAddress}");
    }

    private async Task RemoveAppliedRouteAsync(AppliedStaticRoute route, NetworkAdapterInfo adapter, CancellationToken ct)
    {
        if (!_routeJournalWritable)
            throw new InvalidOperationException("The static route recovery journal could not be read; refusing to remove an untracked route.");
        if (!_appliedStaticRoutes.Contains(route))
            throw new InvalidOperationException(Ui("recovery.status.unavailable"));

        await _routeService.RemoveAsync(route, adapter, ct);
        RemoveAppliedRouteRecord(route);
        await LoadCurrentStaticRoutesAsync(Adapters.ToList(), ct);
        AddOperationHistory("Static Route Recovery / 静态路由恢复", "", adapter.MacAddress, "Completed / 已完成",
            $"Removed {route.DestinationPrefix} via {route.NextHop} / 已删除路由 {route.DestinationPrefix}，下一跳 {route.NextHop}", scope: adapter.Name);
        _logger.Info($"Static route removed from Recovery Center: {route.DestinationPrefix} adapter={adapter.Name}");
    }

}
