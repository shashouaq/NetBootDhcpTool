using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.App;

public partial class MainWindow
{
    private readonly ObservableCollection<AdapterAddressDraft> _addressDrafts = [];
    private readonly ObservableCollection<ScanResult> _addressResults = [];
    private AdapterAddressManager? _addressManager;
    private readonly List<string> _activeDhcpAddressIds = [];
    private readonly HashSet<string> _startupAddressRecoveryIds = [];
    private readonly HashSet<string> _startupCoexistenceRecoveryIds = new(StringComparer.OrdinalIgnoreCase);
    private async Task CleanupDhcpAddressesAsync(CancellationToken ct)
    {
        if (_addressManager is null) return;
        foreach (var id in _activeDhcpAddressIds.ToArray())
        {
            var entry = _addressManager.Journal.Addresses.SingleOrDefault(x => x.Id == id);
            if (entry is not null) await _addressManager.RemoveAsync(entry, ct);
            _activeDhcpAddressIds.Remove(id);
        }
        RefreshAddressRecovery();
    }
    private CancellationTokenSource? _addressMonitorCts;
    private Task _addressMonitorTask = Task.CompletedTask;
    private Task _addressDisplayTask = Task.CompletedTask;
    private long _addressDisplayGeneration;
    private bool _addressJournalWritable = true;
    private string AddressJournalPath => Path.Combine(_paths.ConfigDirectory, "adapter-address-session.json");
    private sealed record AddressDisplay(string AdapterId, string IpAddress, int PrefixLength, string Origin, string State, string Owner);

    private void InitializeAddressManagement()
    {
        AddressDraftGrid.ItemsSource = _addressDrafts;
        AddressResultsGrid.ItemsSource = _addressResults;
        var load = JsonStore.Load<AdapterAddressJournal>(AddressJournalPath, _logger);
        RecordStorageLoad("Address recovery / 地址恢复", load.Status, load.SourcePath, load.Error, missingIsExpected: true);
        try
        {
            if (load.Status == DataLoadStatus.Failed) throw new InvalidDataException("Address journal unreadable / 地址日志无法读取");
            _addressManager = new AdapterAddressManager(new WindowsAdapterAddressBackend(_adapterService), load.Value ?? new(),
                journal => JsonStore.Save(AddressJournalPath, journal));
            _startupAddressRecoveryIds.UnionWith(_addressManager.Journal.Addresses.Select(x => x.Id));
            _startupCoexistenceRecoveryIds.UnionWith(_addressManager.Journal.Coexistence.Select(x => x.AdapterId));
        }
        catch (Exception ex) { _addressJournalWritable = false; _logger.Error("Address recovery journal invalid; writes blocked / 地址恢复日志无效，禁止写入", ex); AddressStatus.Text = ex.Message; }
        ApplyAddressLanguage();
        RefreshAddressRecovery();
        _adapterStatusTimer.Tick += (_, _) => RefreshAddressDisplay();
        Tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, Tabs)) RefreshAddressDisplay(); };
    }

    private void ApplyAddressLanguage()
    {
        if (TabAddresses is null || _lang is null) return;
        TabAddresses.Header = _lang.T("addresses.tab");
        TxtAddressGuide.Text = _lang.T("addresses.guide");
        TxtAddressRecovery.Text = _lang.T("addresses.recovery");
        foreach (var pair in new (Button Button, string Key)[] { (BtnAddressDraft,"draft"), (BtnAddressDrop,"drop"),
            (BtnAddressPreview,"preview"), (BtnAddressApply,"apply"), (BtnAddressRefresh,"refresh"), (BtnAddressRemove,"remove"),
            (BtnAddressProbe,"probe"), (BtnAddressStop,"stop"), (BtnAddressRecover,"recover"), (BtnAddressCoexist,"coexist") })
            pair.Button.Content = _lang.T("addresses." + pair.Key);
    }

    private void RefreshAddressRecovery()
    {
        AddressRecoveryGrid.ItemsSource = _addressManager?.Journal.Addresses.ToArray();
        TxtAddressRecovery.Text = _lang.T("addresses.recovery") + " " + (_addressManager?.Journal.Coexistence.Count ?? 0)
            + " coexistence recovery / 共存恢复项";
    }

    private void AddressDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_activeNetworkWorkflow is not null || _closingCleanupStarted) return;
        try
        {
            var draft = new AdapterAddressDraft { AdapterId = SelectedAdapter?.Id ?? "", Name = AddressName.Text.Trim(),
                IpAddress = AddressIp.Text.Trim(), SubnetMask = AddressMask.Text.Trim(), TargetIp = AddressTarget.Text.Trim() };
            var plan = AdapterAddressPlan.Validate(_addressDrafts.Append(draft));
            if (plan.Select(x => x.AdapterId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) throw new InvalidOperationException("One adapter per address plan / 一个地址方案仅配置同一网卡");
            _addressDrafts.Add(plan[^1]);
        }
        catch (Exception ex) { AddressStatus.Text = ex.Message; }
    }
    private void AddressDrop_Click(object sender, RoutedEventArgs e)
    {
        if (_activeNetworkWorkflow is null && !_closingCleanupStarted && AddressDraftGrid.SelectedItem is AdapterAddressDraft draft) _addressDrafts.Remove(draft);
    }
    private void AddressCurrent_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AddressCurrentGrid.SelectedItem is not AddressDisplay row) return;
        AddressIp.Text = row.IpAddress;
        AddressMask.Text = IpNetwork.FromUInt32(row.PrefixLength == 0 ? 0 : uint.MaxValue << (32 - row.PrefixLength)).ToString();
        AddressStatus.Text = _lang.T("addresses.use.existing");
    }

    private string BuildAddressPreview()
    {
        var plan = AdapterAddressPlan.Validate(_addressDrafts);
        if (plan.Select(x => x.AdapterId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) throw new InvalidOperationException("One adapter per address plan / 一个地址方案仅配置同一网卡");
        return _lang.T("addresses.preview.body") + "\n\n" + string.Join("\n", plan.Select(x =>
            $"{Adapters.FirstOrDefault(a => a.Id.Equals(x.AdapterId, StringComparison.OrdinalIgnoreCase))?.Name ?? x.AdapterId}: {x.Name} {x.IpAddress}/{IpNetwork.PrefixLength(IPAddress.Parse(x.SubnetMask))} → {x.TargetIp}"));
    }
    private void AddressPreview_Click(object sender, RoutedEventArgs e)
    {
        try { AppDialog.Show(this, _lang.T("addresses.preview"), BuildAddressPreview()); }
        catch (Exception ex) { AddressStatus.Text = ex.Message; }
    }

    private async Task AddressWorkAsync(string name, Func<CancellationToken, Task> action, bool requireSafety = true)
    {
        if (_addressManager is null && requireSafety) { AddressStatus.Text = _lang.T("addresses.storage.blocked"); return; }
        if (requireSafety && !EnsureSafetyOnboarding()) return;
        var workflow = TryBeginNetworkWorkflow(name, allowWhileDhcpRunning: !requireSafety);
        if (workflow is null) return;
        try
        {
            if (requireSafety || name.StartsWith("Scan address", StringComparison.Ordinal)) await StopAddressMonitoringAsync();
            SetBusy(true, name);
            await using var batch = _adapterService.BeginPowerShellBatch();
            await action(OperationToken);
            AddressStatus.Text = _lang.T("addresses.done");
            AddOperationHistory("Addresses", SelectedAdapter?.Name ?? "", "", "Completed / 已完成", name);
        }
        catch (Exception ex) { AddressStatus.Text = ex.Message; _logger.Error(name, ex); }
        finally { RefreshAddressRecovery(); UpdateRecoveryBanner(); SetBusy(false); EndNetworkWorkflow(workflow); }
    }
    private async void AddressApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var preview = BuildAddressPreview();
            if (!AppDialog.Show(this, _lang.T("addresses.apply"), preview, confirm: true)) return;
            await AddressWorkAsync("Append addresses / 追加地址", async ct =>
            {
                await EnsureNoReplacementRecoveryAsync(_addressDrafts.Select(x => x.AdapterId), ct);
                await _addressManager!.ApplyAsync(_addressDrafts, ct);
                await ReadSelectedAddressesAsync(ct);
            });
        }
        catch (Exception ex) { AddressStatus.Text = ex.Message; }
    }
    private async Task EnsureNoReplacementRecoveryAsync(IEnumerable<string> adapterIds, CancellationToken ct)
    {
        foreach (var id in adapterIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_addressManager?.Journal.Addresses.Any(x => _startupAddressRecoveryIds.Contains(x.Id) && x.AdapterId.Equals(id, StringComparison.OrdinalIgnoreCase)) == true
                || _addressManager?.Journal.Coexistence.Any(x => _startupCoexistenceRecoveryIds.Contains(x.AdapterId) && x.AdapterId.Equals(id, StringComparison.OrdinalIgnoreCase)) == true
                || _dhcpSessionRecoveries.Any(x => x.RequiresRestore && x.AdapterIdentity.AdapterId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Resolve the prior address/replacement recovery first / 请先处理之前的地址或替换配置恢复项");
            if (_originalAdapterConfigs.TryGetValue("id:" + id, out var original))
            {
                var adapter = new WindowsAdapterAddressBackend(_adapterService).Resolve(id);
                var current = await _adapterService.CaptureIPv4ConfigAsync(adapter, ct);
                var owned = _addressManager?.Journal.Addresses.Where(x => x.AdapterId.Equals(id, StringComparison.OrdinalIgnoreCase) && x.State == "Applied") ?? [];
                if (!AdapterAddressManager.PreservesConfiguration(original, current, owned))
                    throw new InvalidOperationException("Restore the replacement configuration before appending / 请先恢复替换配置，再追加地址");
            }
        }
    }
    private bool HasAddressRecovery(string adapterId) => _addressManager?.Journal.Addresses.Any(x => x.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase)) == true
        || _addressManager?.Journal.Coexistence.Any(x => x.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase)) == true;

        private void RefreshAddressDisplay()
    {
        if (Tabs.SelectedItem != TabAddresses || _closingCleanupStarted || _activeNetworkWorkflow is not null || !_addressDisplayTask.IsCompleted || SelectedAdapter is null) return;
        _addressDisplayTask = ReadAddressDisplayAsync();
    }
    private async Task ReadAddressDisplayAsync()
    {
        try { await ReadSelectedAddressesAsync(_backgroundReadCts.Token, fast: true); }
        catch (OperationCanceledException) when (_backgroundReadCts.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closingCleanupStarted) AddressStatus.Text = ex.Message; }
    }
    private Task ReadSelectedAddressesAsync(CancellationToken ct) => ReadSelectedAddressesAsync(ct, fast: false);
    private async Task ReadSelectedAddressesAsync(CancellationToken ct, bool fast)
    {
        var adapter = SelectedAdapter ?? throw new InvalidOperationException("Select adapter / 请选择网卡");
        var generation = ++_addressDisplayGeneration;
        var selectionGeneration = _adapterSelectionGeneration;
        var observations = fast ? await Task.Run(() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SingleOrDefault(x => x.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase))?.GetIPProperties().UnicastAddresses
            .Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(x => new AddressObservation { IpAddress = x.Address.ToString(), PrefixLength = x.PrefixLength,
                AddressState = x.DuplicateAddressDetectionState == System.Net.NetworkInformation.DuplicateAddressDetectionState.Preferred ? "Preferred" : x.DuplicateAddressDetectionState.ToString(),
                PrefixOrigin = x.PrefixOrigin.ToString() }).ToList() ?? [], ct) : await _adapterService.ReadAddressesAsync(adapter, ct);
        ct.ThrowIfCancellationRequested();
        if (_closingCleanupStarted || SelectedAdapter?.Id != adapter.Id || generation != _addressDisplayGeneration || selectionGeneration != _adapterSelectionGeneration) return;
        var selectedIp = (AddressCurrentGrid.SelectedItem as AddressDisplay)?.IpAddress;
        var rows = observations.Select(x => new AddressDisplay(adapter.Id, x.IpAddress, x.PrefixLength,
            x.IpAddress.StartsWith("169.254.", StringComparison.Ordinal) ? "APIPA / 自动私有" : x.PrefixOrigin == "Dhcp" ? "DHCP" : "Static / 固定", x.AddressState switch { "Preferred" => "Usable / 可用", "Tentative" => "Checking / 正在检测", "Duplicate" => "Conflict / 地址冲突", _ => x.AddressState },
            _addressManager?.Journal.Addresses.Any(y => y.AdapterId.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase) && y.IpAddress == x.IpAddress) == true
                ? "Tool recovery recorded / 工具恢复已记录" : "Existing / 已有配置")).ToArray();
        if ((AddressCurrentGrid.ItemsSource as AddressDisplay[])?.SequenceEqual(rows) != true)
        {
            AddressCurrentGrid.ItemsSource = rows;
            AddressCurrentGrid.SelectedItem = rows.FirstOrDefault(x => x.IpAddress == selectedIp);
        }
    }
    private async void AddressRefresh_Click(object sender, RoutedEventArgs e) => await AddressWorkAsync("Read addresses / 读取地址", ReadSelectedAddressesAsync, requireSafety: false);
    private async void AddressRemove_Click(object sender, RoutedEventArgs e)
    {
        if (AddressCurrentGrid.SelectedItem is not AddressDisplay row || _addressManager is null) return;
        var entry = _addressManager.Journal.Addresses.SingleOrDefault(x => x.AdapterId == row.AdapterId && x.IpAddress == row.IpAddress);
        if (entry is null) { AddressStatus.Text = _lang.T("addresses.not.owned"); return; }
        if (!AppDialog.Show(this, _lang.T("addresses.remove"), row.IpAddress + "\n" + _lang.T("addresses.remove.body"), confirm: true)) return;
        await AddressWorkAsync("Remove address / 移除地址", async ct => { await _addressManager.RemoveAsync(entry, ct); await ReadSelectedAddressesAsync(ct); });
    }
    private async void AddressRecover_Click(object sender, RoutedEventArgs e)
    {
        if (_addressManager is null || AddressRecoveryGrid.SelectedItem is not OwnedAdapterAddress entry) return;
        if (!AppDialog.Show(this, _lang.T("addresses.recover"), entry.AdapterName + " " + entry.IpAddress + "\n" + _lang.T("addresses.remove.body"), confirm: true, danger: true)) return;
        await AddressWorkAsync("Recover address / 恢复地址", ct => _addressManager.RemoveAsync(entry, ct, confirmUncertain: true));
    }
    private async void AddressCoexist_Click(object sender, RoutedEventArgs e)
    {
        if (_addressManager is null) return;
        var ids = _addressManager.Journal.Coexistence.Select(x => x.AdapterId).ToArray();
        if (ids.Length == 0 || !AppDialog.Show(this, _lang.T("addresses.coexist"), _lang.T("addresses.coexist.body") + "\n" + string.Join("\n", ids), confirm: true, danger: true)) return;
        await AddressWorkAsync("Restore coexistence / 恢复共存设置", async ct => { foreach (var id in ids) await _addressManager.RestoreCoexistenceAsync(id, ct, true); });
    }

    private async void AddressProbe_Click(object sender, RoutedEventArgs e)
    {
        var plans = new List<(AdapterAddressDraft Draft, ScanRangePlan Range)>();
        try
        {
            foreach (var draft in AdapterAddressPlan.Validate(_addressDrafts))
            {
                if (!ScanRangePlan.TryCreate(draft.IpAddress, draft.SubnetMask, draft.TargetIp, out var range, out var error)) throw new InvalidOperationException(error.ToString());
                plans.Add((draft, range!));
            }
            if (plans.Sum(x => (long)x.Range.TargetCount) > ScanRangePlan.MaxProbeTargets) throw new InvalidOperationException("Maximum 4096 total targets / 合计最多 4096 个目标");
        }
        catch (Exception ex) { AddressStatus.Text = ex.Message; return; }
        await AddressWorkAsync("Scan address plan / 按地址方案扫描", async ct =>
        {
            var probes = new Dictionary<string, SourceBoundProbe>();
            try
            {
                _addressResults.Clear();
                foreach (var (draft, range) in plans)
                {
                    var adapter = new WindowsAdapterAddressBackend(_adapterService).Resolve(draft.AdapterId);
                    var probe = new SourceBoundProbe(new(adapter.Id, int.Parse(adapter.InterfaceIndex), range.LocalIp));
                    probes.Add(draft.IpAddress, probe);
                    var scanner = new PingScanner(_logger, probe);
                    var activeWorkflow = _activeNetworkWorkflow;
                    var results = await scanner.ScanPlanAsync(range, _settings.PingConcurrency, _settings.PingTimeoutMs, _settings.HttpTimeoutMs,
                        new Progress<(int done, int total, ScanResult? result)>(x => { if (!_closingCleanupStarted && !ct.IsCancellationRequested && ReferenceEquals(activeWorkflow, _activeNetworkWorkflow)) AddressStatus.Text = $"{draft.Name} {draft.IpAddress}: {x.done}/{x.total}"; }), ct);
                    foreach (var result in results) { result.Remark = draft.Name; _addressResults.Add(result); }
                    if (range.IsSingleTarget && results.Count == 0)
                    {
                        var row = new ScanResult { ConfiguredLocalIp = draft.IpAddress, IpAddress = range.TargetIp!.ToString(), Remark = draft.Name };
                        row.Connectivity.Observe(-1, DateTime.Now); _addressResults.Add(row);
                    }
                }
                _addressMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_backgroundReadCts.Token);
                _addressMonitorTask = MonitorAddressesAsync(probes, _addressMonitorCts.Token);
                probes = []; // The monitoring task owns/disposes these probe instances.
            }
            finally { foreach (var probe in probes.Values) await probe.DisposeAsync(); }
        }, requireSafety: false);
    }
    private async Task MonitorAddressesAsync(Dictionary<string, SourceBoundProbe> probes, CancellationToken ct)
    {
        var cursor = 0;
        try
        {
            while (true)
            {
                await Task.Delay(2000, ct);
                var rows = _addressResults.ToArray();
                var count = Math.Min(32, rows.Length);
                using var limit = new SemaphoreSlim(8);
                await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
                {
                    await limit.WaitAsync(ct);
                    try
                    {
                        var row = rows[(cursor + i) % rows.Length];
                        ScanResult? result = null;
                        try { result = await probes[row.ConfiguredLocalIp].ProbeAsync(IPAddress.Parse(row.IpAddress), _settings.PingTimeoutMs, _settings.HttpTimeoutMs, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) { AddressStatus.Text = ex.Message; }
                        ct.ThrowIfCancellationRequested();
                        if (_closingCleanupStarted) return;
                        row.PingOk = result?.PingOk == true; row.HttpOk = result?.HttpOk == true; row.HttpsOk = result?.HttpsOk == true;
                        row.LatencyMs = result?.LatencyMs ?? -1;
                        row.Connectivity.ObserveReachability(row.LatencyMs, row.HttpOk || row.HttpsOk, DateTime.Now);
                        if (result is not null) row.LastSeen = result.LastSeen;
                    }
                    finally { limit.Release(); }
                }));
                if (rows.Length > 0) cursor = (cursor + count) % rows.Length;
                AddressResultsGrid.Items.Refresh();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { foreach (var probe in probes.Values) await probe.DisposeAsync(); }
    }
    private async Task StopAddressMonitoringAsync()
    {
        _addressMonitorCts?.Cancel();
        await _addressMonitorTask;
        _addressMonitorCts?.Dispose(); _addressMonitorCts = null;
        foreach (var result in _addressResults) result.Connectivity.Stop(DateTime.Now);
    }
    private async void AddressStop_Click(object sender, RoutedEventArgs e) => await StopAddressMonitoringAsync();
    private async Task CleanupOwnedAddressesAsync(CancellationToken ct)
    {
        if (_addressManager is null) return;
        var failures = new List<string>();
        foreach (var entry in _addressManager.Journal.Addresses.Where(x => !_startupAddressRecoveryIds.Contains(x.Id)).ToArray().Reverse())
            try { await _addressManager.RemoveAsync(entry, ct); } catch (Exception ex) { entry.Error = ex.Message; failures.Add(ex.Message); }
        foreach (var lease in _addressManager.Journal.Coexistence.Where(x => !_startupCoexistenceRecoveryIds.Contains(x.AdapterId)).ToArray())
            try { await _addressManager.RestoreCoexistenceAsync(lease.AdapterId, ct); } catch (Exception ex) { failures.Add(ex.Message); }
        JsonStore.Save(AddressJournalPath, _addressManager.Journal);
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
    }
}
