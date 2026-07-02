using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.IO.Compression;
using System.Text;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Dhcp;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.App;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly LanguageService _lang;
    private readonly NetworkAdapterService _adapterService;
    private readonly HttpProbeService _probe;
    private readonly PingScanner _scanner;
    private readonly ExistingDhcpDetector _dhcpDetector;
    private readonly DhcpServer _dhcpServer;
    private readonly SemaphoreSlim _leaseUpdateGate = new(1, 1);
    private AppSettings _settings;
    private List<FavoriteConfig> _allFavorites = [];
    private List<AdapterConfigBackup> _adapterBackups = [];
    private List<OperationHistoryItem> _operationHistory = [];
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _leaseHintCts;
    private readonly DispatcherTimer _leasePingTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _adapterStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private bool _leaseProbeInProgress;
    private bool _closingCleanupStarted;
    private bool _closeAfterCleanup;
    private bool _scanRunning;
    private bool _dhcpWasStartedInThisSession;
    private readonly Dictionary<string, string> _lastAdapterIps = new();
    private readonly Dictionary<string, AdapterIpv4Snapshot> _originalAdapterConfigs = new();
    private TabItem? _lastBusinessTab;
    private string? _activeDhcpAdapterIndex;
    private bool _syncingNetworkInputs;
    private bool _adapterStatusRefreshInProgress;
    private bool _darkTheme;
    private string? _businessConfigurationFingerprint;

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];
    public ObservableCollection<ScanResult> ScanResults { get; } = [];
    public ObservableCollection<FavoriteConfig> Favorites { get; } = [];
    public ObservableCollection<DhcpLease> Leases { get; } = [];
    public ObservableCollection<AdapterIpHistoryItem> AdapterIpHistory { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _paths = new AppPaths(AppContext.BaseDirectory);
        Defaults.EnsureFiles(_paths);
        _logger = new FileLogger(_paths);
        _logger.LineWritten += line => Dispatcher.Invoke(() =>
        {
            AppendLogLine(line);
        });
        _settings = JsonStore.LoadOrDefault(_paths.SettingsFile, new AppSettings(), _logger);
        _lang = new LanguageService(_paths);
        _lang.Load(_settings.Language);
        _adapterService = new NetworkAdapterService(_logger);
        _probe = new HttpProbeService();
        _scanner = new PingScanner(_logger, _probe);
        _dhcpDetector = new ExistingDhcpDetector(_logger);
        _dhcpServer = new DhcpServer(_logger);
        _dhcpServer.LeaseChanged += lease => Dispatcher.Invoke(() => _ = UpdateLeaseAsync(lease));
        _leasePingTimer.Tick += (_, _) => _ = RefreshLeasePingAsync();
        _adapterStatusTimer.Tick += (_, _) => RefreshSelectedAdapterStatus();
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        ApplyLanguage();
        LoadDefaults();
        LoadFavorites();
        LoadNetworkHistory();
        _adapterBackups = JsonStore.LoadOrDefault(_paths.AdapterBackupsFile, new List<AdapterConfigBackup>(), _logger);
        _operationHistory = JsonStore.LoadOrDefault(_paths.OperationHistoryFile, new List<OperationHistoryItem>(), _logger);
        RefreshAdapters();
        SetDhcpRunningState(false);
        UpdateManualScanButtons();
        UpdateFavoriteButtons();
        _adapterStatusTimer.Start();
        _logger.Info("MainWindow ready");
        Dispatcher.BeginInvoke(() => SetBusy(false), DispatcherPriority.ApplicationIdle);
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAfterCleanup)
        {
            base.OnClosing(e);
            return;
        }
        if (_closingCleanupStarted) return;

        e.Cancel = true;
        _closingCleanupStarted = true;
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        _adapterStatusTimer.Stop();
        IsEnabled = false;
        SetBusy(true, "Cleaning up... / 正在清理工作环境...");
        await CleanupWorkEnvironmentAsync();
        _closeAfterCleanup = true;
        await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }

    private NetworkAdapterInfo? SelectedAdapter => AdapterBox.SelectedItem as NetworkAdapterInfo;

    private void ApplyLanguage()
    {
        Title = "NetBoot DHCP Tool v1.0.6 - Authors: Joel & Codex - 1406829360@qq.com";
        LblLanguage.Text = _lang.T("language");
        BtnRefresh.Content = _lang.T("refresh");
        BtnOpenLogs.Content = _lang.T("open.logs");
        BtnOpenLogs2.Content = _lang.T("open.logs");
        BtnAbout.Content = _lang.T("about");
        LblAdapter.Text = _lang.T("adapter");
        LblMac.Text = _lang.T("mac");
        LblGateway.Text = _lang.T("gateway");
        LblStatus.Text = _lang.T("status");
        TabDhcp.Header = _lang.T("auto.dhcp");
        TabScan.Header = _lang.T("manual.scan");
        TabFavorites.Header = _lang.T("favorites");
        TabSettings.Header = _lang.T("settings");
        LblServerIp.Text = _lang.T("server.ip");
        LblSubnetMask.Text = _lang.T("subnet.mask");
        LblPoolStart.Text = _lang.T("pool.start");
        LblPoolEnd.Text = _lang.T("pool.end");
        LblDhcpGateway.Text = _lang.T("gateway");
        LblDhcpDns.Text = _lang.T("dns");
        LblLeaseSeconds.Text = _lang.T("lease.seconds");
        BtnStartDhcp.Content = _lang.T("start.dhcp");
        BtnStopDhcp.Content = _lang.T("stop.dhcp");
        BtnShowDhcpGateway.Content = IsChineseUi() ? "+ 网关" : "+ Gateway";
        BtnShowDhcpDns.Content = "+ DNS";
        DhcpConfirm.Content = _lang.T("isolated.confirm");
        RestoreOnStop.Content = _lang.T("restore.stop");
        LblManualIp.Text = _lang.T("server.ip");
        LblManualMask.Text = _lang.T("subnet.mask");
        LblManualTargetIp.Text = _lang.T("target.ip");
        BtnApplyScan.Content = _lang.T("apply.scan");
        BtnStopScan.Content = _lang.T("stop.scan");
        BtnAddFavorite.Content = _lang.T("add.favorite");
        BtnNewFavorite.Content = _lang.T("new.favorite");
        BtnImportFavorite.Content = IsChineseUi() ? "导入" : "Import";
        BtnExportFavorite.Content = IsChineseUi() ? "导出" : "Export";
        BtnFavoriteColumns.Content = IsChineseUi() ? "列" : "Columns";
        LblSearch.Text = _lang.T("search");
        BtnLoadFavorite.Content = _lang.T("load");
        BtnApplyFavorite.Content = _lang.T("apply.and.scan");
        BtnDeleteFavorite.Content = _lang.T("delete");
        BtnClearLog.Content = _lang.T("clear.log");
        BtnCopyLog.Content = _lang.T("copy.log");
        AllowWifi.Content = _lang.T("allow.wifi");
        AllowGateway.Content = _lang.T("allow.gateway");
        DetectExistingDhcp.Content = _lang.T("detect.existing.dhcp");
        BtnSaveSettings.Content = _lang.T("save");
        DhcpHint.Text = _lang.T("dhcp.client.hint");
        ApplyGridHeaders();
        UpdateManualScanButtons();
        UpdateFavoriteButtons();
    }

    private void LoadDefaults()
    {
        var d = _settings.DefaultDhcp;
        DhcpServerIp.Text = d.ServerIp;
        DhcpMask.Text = d.SubnetMask;
        DhcpStart.Text = d.PoolStart;
        DhcpEnd.Text = d.PoolEnd;
        DhcpGateway.Text = "";
        DhcpDns.Text = "";
        DhcpLeaseSeconds.Text = d.LeaseSeconds.ToString();
        ManualIp.Text = d.ServerIp;
        ManualMask.Text = d.SubnetMask;
        RestoreOnStop.IsChecked = true;
        _settings.RestoreIpOnDhcpStop = true;
        AllowWifi.IsChecked = _settings.AllowDhcpOnWifi;
        AllowGateway.IsChecked = _settings.AllowDhcpOnAdapterWithGateway;
        DetectExistingDhcp.IsChecked = _settings.DetectExistingDhcpBeforeStart;
        SelectLanguageBox(_settings.Language);
    }

    private void RefreshAdapters()
    {
        try
        {
            Adapters.Clear();
            var adapters = _adapterService.GetAdapters()
                .OrderBy(x => x.IsVirtual)
                .ThenBy(x => x.IsWifi)
                .ThenBy(x => !x.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            foreach (var item in adapters)
            {
                Adapters.Add(item);
            }
            AdapterBox.ItemsSource = Adapters;
            if (Adapters.Count > 0)
            {
                var preferred = Adapters.FirstOrDefault(x => !x.IsVirtual && !x.IsWifi && x.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                    ?? Adapters.FirstOrDefault(x => !x.IsVirtual && !x.IsWifi)
                    ?? Adapters.FirstOrDefault(x => !x.IsVirtual)
                    ?? Adapters.FirstOrDefault();
                AdapterBox.SelectedItem = preferred;
            }
            if (Adapters.Count == 0) _logger.Warn("No adapters found for UI");
            _businessConfigurationFingerprint = CaptureBusinessConfigurationFingerprint();
            _logger.Info($"UI adapter list loaded: count={Adapters.Count}, selected={SelectedAdapter?.DisplayName ?? ""}");
        }
        catch (Exception ex)
        {
            _logger.Error("Refresh adapters failed", ex);
            AppDialog.Show(this, _lang.T("refresh"), ex.Message, danger: true);
        }
    }

    private void LoadFavorites()
    {
        _allFavorites = JsonStore.LoadOrDefault(_paths.FavoritesFile, new List<FavoriteConfig>(), _logger);
        FilterFavorites();
    }

    private void SaveFavorites()
    {
        JsonStore.Save(_paths.FavoritesFile, _allFavorites);
    }

    private void LoadNetworkHistory()
    {
        foreach (var item in JsonStore.LoadOrDefault(_paths.NetworkHistoryFile, new List<AdapterIpHistoryItem>(), _logger).OrderByDescending(x => x.ChangedAt).Take(5)) AdapterIpHistory.Add(item);
    }

    private void SaveNetworkHistory() => JsonStore.Save(_paths.NetworkHistoryFile, AdapterIpHistory.ToList());

    private void AddOperationHistory(string type, string ip, string mac, string status, string detail)
    {
        _operationHistory.Insert(0, new OperationHistoryItem { Type = type, IpAddress = ip, MacAddress = mac, Status = status, Detail = detail });
        while (_operationHistory.Count > 500) _operationHistory.RemoveAt(_operationHistory.Count - 1);
        JsonStore.Save(_paths.OperationHistoryFile, _operationHistory);
    }

    private void FilterFavorites()
    {
        var q = FavoriteSearch.Text?.Trim() ?? "";
        Favorites.Clear();
        foreach (var f in _allFavorites.Where(f => string.IsNullOrWhiteSpace(q)
            || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.DeviceNumber.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.SerialNumber.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.RemarkName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.MemoryText.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.LocalIp.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.TargetIp.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.CustomFieldsSummary.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Gateway.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            Favorites.Add(f);
        }
        UpdateFavoriteButtons();
    }

    private async void StartDhcp_Click(object sender, RoutedEventArgs e)
    {
        if (DhcpConfirm.IsChecked != true)
        {
            var message = "请勾选：" + _lang.T("isolated.confirm");
            _logger.Warn("Isolated network confirmation missing: " + message);
            AppDialog.Show(this, _lang.T("start.dhcp"), message);
            return;
        }
        try
        {
            SetBusy(true, "Starting DHCP... / 正在启动 DHCP...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var adapter = SelectedAdapter ?? throw new InvalidOperationException("No adapter selected");
            if (adapter.IsWifi && !_settings.AllowDhcpOnWifi) throw new InvalidOperationException(_lang.T("blocked.wifi"));
            if (adapter.HasGateway && !_settings.AllowDhcpOnAdapterWithGateway) throw new InvalidOperationException(_lang.T("blocked.gateway"));
            if (adapter.IsVirtual) throw new InvalidOperationException(_lang.T("blocked.virtual"));
            if (!adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(_lang.T("blocked.disconnected"));
            if (string.IsNullOrWhiteSpace(adapter.IPv4Address) && string.IsNullOrWhiteSpace(DhcpServerIp.Text)) throw new InvalidOperationException("Adapter has no IPv4 address");
            if (_settings.DetectExistingDhcpBeforeStart)
            {
                var detectIp = IPAddress.Parse(string.IsNullOrWhiteSpace(adapter.IPv4Address) ? DhcpServerIp.Text : adapter.IPv4Address);
                var existing = await _dhcpDetector.DetectAsync(detectIp, 1500);
                if (existing.found) throw new InvalidOperationException($"{_lang.T("existing.dhcp")}: {existing.server} offered {existing.offeredIp}");
            }
            var wlanBefore = await _adapterService.LogReadonlyWlanStateAsync("before DHCP start");
            if (wlanBefore.PolicyBlockedRecent)
            {
                var docPath = Path.Combine(_paths.DocsDirectory, "troubleshooting", "windows-wifi-wired-conflict.md");
                AppDialog.Show(this,
                    IsChineseUi() ? "WLAN 策略提示" : "WLAN Policy Warning",
                    IsChineseUi()
                        ? $"检测到最近 30 分钟内出现过“策略禁止在该接口上自动连接”的 WLAN 事件。请先参考：{docPath}"
                        : $"A WLAN event indicating policy-blocked auto connection was found in the last 30 minutes. Review: {docPath}");
            }
            if (!AppDialog.Show(this, _lang.T("start.dhcp"), _lang.T("warning.dhcp"), confirm: true, danger: true)) return;
            if (!ConfirmConfigurationChange(adapter, DhcpServerIp.Text, DhcpMask.Text, "DHCP")) return;
            await RememberAdapterConfigAsync(adapter);
            await _adapterService.ApplyStaticIPv4Async(adapter, DhcpServerIp.Text, DhcpMask.Text, DhcpGateway.Text, DhcpDns.Text);
            _activeDhcpAdapterIndex = adapter.InterfaceIndex;
            MarkAdapterIp(adapter, DhcpServerIp.Text);
            await _adapterService.LogReadonlyWlanStateAsync("after DHCP start");
            await _adapterService.EnsureDhcpFirewallRulesAsync();
            var settings = new DhcpServerSettings
            {
                ServerIp = IPAddress.Parse(DhcpServerIp.Text),
                SubnetMask = IPAddress.Parse(DhcpMask.Text),
                PoolStart = IPAddress.Parse(DhcpStart.Text),
                PoolEnd = IPAddress.Parse(DhcpEnd.Text),
                Gateway = IPAddress.TryParse(DhcpGateway.Text.Trim(), out var gateway) ? gateway : null,
                Dns = IPAddress.TryParse(DhcpDns.Text.Trim(), out var dns) ? dns : null,
                LeaseSeconds = int.TryParse(DhcpLeaseSeconds.Text, out var lease) ? lease : 3600
            };
            await _dhcpServer.StartAsync(settings);
            _dhcpWasStartedInThisSession = true;
            SetDhcpRunningState(true);
            _leasePingTimer.Start();
            _leaseHintCts?.Cancel();
            _leaseHintCts = new CancellationTokenSource();
            _ = ShowLeaseTimeoutHintAsync(_leaseHintCts.Token);
        }
        catch (Exception ex)
        {
            _logger.Error("Start DHCP failed", ex);
            AppDialog.Show(this, _lang.T("start.dhcp"), ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void StopDhcp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Stopping DHCP... / 正在停止 DHCP...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            _dhcpServer.Stop();
            _leasePingTimer.Stop();
            _leaseHintCts?.Cancel();
            if (RestoreOnStop.IsChecked == true)
            {
                var restoreAdapter = GetDhcpRestoreAdapter();
                if (restoreAdapter != null)
                {
                    await RestoreOriginalAdapterConfigAsync(restoreAdapter);
                }
            }
            await _adapterService.LogReadonlyWlanStateAsync("after DHCP stop");
            _activeDhcpAdapterIndex = null;
            SetDhcpRunningState(false);
            UpdateManualScanButtons();
        }
        catch (Exception ex)
        {
            _logger.Error("Stop DHCP failed", ex);
            AppDialog.Show(this, _lang.T("stop.dhcp"), ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ApplyScan_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAndScanAsync();
    }

    private async Task ApplyAndScanAsync()
    {
        if (_scanRunning) return;
        try
        {
            var adapter = SelectedAdapter ?? throw new InvalidOperationException("No adapter selected");
            if (!ConfirmConfigurationChange(adapter, ManualIp.Text, ManualMask.Text, "Manual Scan / 手动扫描")) return;
            _scanRunning = true;
            UpdateManualScanButtons();
            UpdateFavoriteButtons();
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            ScanResults.Clear();
            ScanProgress.Value = 0;
            SetBusy(true, "Configuring adapter... / 正在配置网卡...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await RememberAdapterConfigAsync(adapter);
            await _adapterService.ApplyStaticIPv4Async(adapter, ManualIp.Text, ManualMask.Text, "", "", _scanCts.Token);
            MarkAdapterIp(adapter, ManualIp.Text);
            SetBusy(false);
            var progress = new Progress<(int done, int total, ScanResult? result)>(p =>
            {
                ScanProgress.Maximum = p.total;
                ScanProgress.Value = p.done;
                if (p.result != null) ScanResults.Add(p.result);
                if (p.result != null) AddOperationHistory("Scan", p.result.IpAddress, p.result.MacAddress, p.result.StatusText, p.result.Remark);
                ScanStatus.Text = $"{p.done}/{p.total} Found={ScanResults.Count}";
            });
            if (IPAddress.TryParse(ManualTargetIp.Text.Trim(), out var targetIp))
            {
                await ProbeTargetUntilOnlineAsync(targetIp, progress, _scanCts.Token);
            }
            else
            {
                await _scanner.ScanAsync(IPAddress.Parse(ManualIp.Text), IPAddress.Parse(ManualMask.Text), _settings.PingConcurrency, _settings.PingTimeoutMs, _settings.HttpTimeoutMs, progress, _scanCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Scan canceled");
        }
        catch (Exception ex)
        {
            _logger.Error("Apply and scan failed", ex);
            AppDialog.Show(this, _lang.T("apply.scan"), ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
            _scanRunning = false;
            UpdateManualScanButtons();
            UpdateFavoriteButtons();
        }
    }

    private async Task ProbeTargetUntilOnlineAsync(IPAddress targetIp, IProgress<(int done, int total, ScanResult? result)> progress, CancellationToken ct)
    {
        var ip = targetIp.ToString();
        var result = new ScanResult
        {
            ConfiguredLocalIp = ManualIp.Text.Trim(),
            IpAddress = ip,
            PingOk = false,
            LatencyMs = -1,
            LastSeen = DateTime.Now,
            Remark = "Waiting / 等待连通"
        };
        result.SetWaitingStatus();
        ScanResults.Add(result);
        progress.Report((0, 1, null));
        _logger.Info($"Target probe start: {ip}");

        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            var latency = await PingLatencyAsync(ip);
            result.LatencyMs = latency;
            result.PingOk = latency >= 0;
            if (result.PingOk) result.ClearStatusOverride();
            else result.SetWaitingStatus();
            result.LastSeen = DateTime.Now;
            result.Remark = result.PingOk ? "Reachable / 已连通" : $"Retry {attempt} / 第 {attempt} 次探测";
            ScanGrid.Items.Refresh();
            ScanStatus.Text = result.PingOk ? $"1/1 Found=1 Ping={latency}ms" : $"0/1 Waiting {attempt}";
            _logger.Info($"Target probe: {ip} attempt={attempt} ping={latency}");

            if (result.PingOk)
            {
                result.Hostname = ResolveHost(ip);
                var probes = await _probe.ProbeAsync(ip, _settings.HttpTimeoutMs, ct);
                result.HttpOk = probes.http;
                result.HttpsOk = probes.https;
                ScanGrid.Items.Refresh();
                progress.Report((1, 1, null));
                _logger.Info($"Target probe online: {ip} {latency}ms http={result.HttpOk} https={result.HttpsOk}");
                return;
            }

            await Task.Delay(1000, ct);
        }
    }

    private void StopScan_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        UpdateManualScanButtons();
    }

    private async void RestoreScan_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAdapter == null) return;
        try
        {
            SetBusy(true, "Restoring adapter... / 正在恢复网卡...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await RestoreOriginalAdapterConfigAsync(SelectedAdapter);
            RefreshAdapters();
        }
        catch (Exception ex)
        {
            _logger.Error("Restore manual adapter failed", ex);
            AppDialog.Show(this, "Restore / 恢复网卡", ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        var fav = new FavoriteConfig
        {
            Name = string.IsNullOrWhiteSpace(ManualIp.Text) ? "Favorite" : ManualIp.Text,
            AdapterName = SelectedAdapter?.Name ?? "",
            AdapterMac = SelectedAdapter?.MacAddress ?? "",
            LocalIp = ManualIp.Text,
            SubnetMask = ManualMask.Text,
            Gateway = "",
            Dns = "",
            TargetIp = ManualTargetIp.Text,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        var dialog = new FavoriteWindow(fav) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _allFavorites.Add(fav);
            SaveFavorites();
            FilterFavorites();
            _logger.Info("Favorite added: " + fav.Name);
        }
    }

    private void NewFavorite_Click(object sender, RoutedEventArgs e)
    {
        var fav = new FavoriteConfig
        {
            Name = "New Favorite",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        var dialog = new FavoriteWindow(fav) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _allFavorites.Add(fav);
            SaveFavorites();
            FilterFavorites();
            _logger.Info("Favorite manually added / 手动新增收藏: " + fav.Name);
        }
    }

    private void TemplateFavorite_Click(object sender, RoutedEventArgs e)
    {
        var templates = new[]
        {
            new FavoriteConfig { Name = "Dell iDRAC", DeviceNumber = "BMC", LocalIp = ManualIp.Text, SubnetMask = ManualMask.Text, TargetIp = ManualTargetIp.Text, Username = "root", Description = "Dell iDRAC management" },
            new FavoriteConfig { Name = "HPE iLO", DeviceNumber = "BMC", LocalIp = ManualIp.Text, SubnetMask = ManualMask.Text, TargetIp = ManualTargetIp.Text, Username = "Administrator", Description = "HPE iLO management" },
            new FavoriteConfig { Name = "Lenovo XCC", DeviceNumber = "BMC", LocalIp = ManualIp.Text, SubnetMask = ManualMask.Text, TargetIp = ManualTargetIp.Text, Username = "USERID", Description = "Lenovo XClarity Controller" }
        };
        var choice = new Window { Owner = this, Title = "Templates / 模板", Width = 360, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new ListBox { ItemsSource = templates, DisplayMemberPath = "Name", Margin = new Thickness(12) };
        var add = new Button { Content = "Add / 添加", Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right };
        add.Click += (_, _) =>
        {
            if (list.SelectedItem is not FavoriteConfig template) return;
            template.UpdatedAt = DateTime.Now;
            _allFavorites.Add(template);
            SaveFavorites();
            FilterFavorites();
            choice.Close();
        };
        var panel = new DockPanel();
        DockPanel.SetDock(add, Dock.Bottom);
        panel.Children.Add(add);
        panel.Children.Add(list);
        choice.Content = panel;
        choice.ShowDialog();
    }

    private void LoadFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav) return;
        LoadFavorite(fav);
    }

    private async void ApplyFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav) return;
        LoadFavorite(fav);
        await ApplyAndScanAsync();
    }

    private void DeleteFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav) return;
        _allFavorites.RemoveAll(x => x.Id == fav.Id);
        SaveFavorites();
        FilterFavorites();
        _logger.Info("Favorite deleted: " + fav.Name);
    }

    private void ImportFavorites_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Favorites / 导入收藏夹",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = JsonStore.LoadOrDefault(dialog.FileName, new List<FavoriteConfig>(), _logger)
                .Where(IsValidFavorite)
                .ToList();
            var added = 0;
            var updated = 0;
            foreach (var item in imported)
            {
                var existing = _allFavorites.FirstOrDefault(x => SameFavorite(x, item));
                item.UpdatedAt = DateTime.Now;
                if (existing == null)
                {
                    if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                    _allFavorites.Add(item);
                    added++;
                }
                else
                {
                    MergeFavorite(existing, item);
                    updated++;
                }
            }
            SaveFavorites();
            FilterFavorites();
            _logger.Info($"Favorites imported: added={added} updated={updated}");
            AppDialog.Show(this, "Import / 导入", $"导入完成。\nAdded={added}, Updated={updated}");
        }
        catch (Exception ex)
        {
            _logger.Error("Import favorites failed", ex);
            AppDialog.Show(this, "Import / 导入", ex.Message, danger: true);
        }
    }

    private void ExportFavorites_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Favorites / 导出收藏夹",
            Filter = "JSON (*.json)|*.json",
            FileName = "NetBootDhcpTool-favorites.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            JsonStore.Save(dialog.FileName, _allFavorites);
            _logger.Info("Favorites exported: " + dialog.FileName);
            AppDialog.Show(this, "Export / 导出", "导出完成。\n" + dialog.FileName);
        }
        catch (Exception ex)
        {
            _logger.Error("Export favorites failed", ex);
            AppDialog.Show(this, "Export / 导出", ex.Message, danger: true);
        }
    }

    private void LoadFavorite(FavoriteConfig fav)
    {
        ManualIp.Text = fav.LocalIp;
        ManualMask.Text = fav.SubnetMask;
        ManualTargetIp.Text = fav.TargetIp;
        fav.LastUsedAt = DateTime.Now;
        SaveFavorites();
        _logger.Info("Favorite loaded: " + fav.Name);
        Tabs.SelectedItem = TabScan;
    }

    private void FavoriteSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterFavorites();
    private void FavoriteGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFavoriteButtons();
    private void FavoriteGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is FavoriteConfig fav) ShowFavoriteDetailsDialog(fav);
    }

    private void FavoriteCustomFields_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FavoriteConfig fav)
        {
            ShowFavoriteFieldsDialog(fav);
            e.Handled = true;
        }
    }

    private void FavoriteColumns_Click(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Owner = this,
            Title = IsChineseUi() ? "收藏夹列设置" : "Favorite Columns",
            Width = 380,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = IsChineseUi() ? "勾选显示列；在表格表头可拖动调整顺序。" : "Check columns to show. Drag headers in the grid to reorder.",
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var column in FavoriteGrid.Columns)
        {
            var box = new CheckBox
            {
                Content = column.Header?.ToString() ?? "",
                IsChecked = column.Visibility == Visibility.Visible,
                Margin = new Thickness(0, 4, 0, 4),
                Tag = column
            };
            box.Checked += (_, _) => column.Visibility = Visibility.Visible;
            box.Unchecked += (_, _) => column.Visibility = Visibility.Collapsed;
            panel.Children.Add(box);
        }
        var close = new Button { Content = IsChineseUi() ? "关闭" : "Close", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => window.Close();
        panel.Children.Add(close);
        window.Content = new ScrollViewer { Content = panel };
        window.ShowDialog();
    }

    private void ManualIp_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingNetworkInputs) SyncManualTargetIp();
        UpdateManualScanButtons();
    }
    private void ManualInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingNetworkInputs && sender == ManualMask) SyncManualTargetIp();
        UpdateManualScanButtons();
    }

    private void DhcpNetworkInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingNetworkInputs) SyncDhcpRange(sender == DhcpStart ? DhcpStart.Text : sender == DhcpEnd ? DhcpEnd.Text : DhcpServerIp.Text);
    }

    private void SyncManualTargetIp()
    {
        if (!TryGetNetwork(ManualIp.Text, ManualMask.Text, out var localIp, out var mask)) return;
        _syncingNetworkInputs = true;
        ManualTargetIp.Text = GetNearbyHost(localIp, mask, 1).ToString();
        _syncingNetworkInputs = false;
    }

    private void SyncDhcpRange(string referenceIpText)
    {
        if (!TryGetNetwork(referenceIpText, DhcpMask.Text, out var referenceIp, out var mask)) return;
        var serverIp = IpNetwork.FromUInt32((IpNetwork.ToUInt32(referenceIp) & IpNetwork.ToUInt32(mask)) + 1);
        _syncingNetworkInputs = true;
        DhcpServerIp.Text = serverIp.ToString();
        DhcpStart.Text = GetNearbyHost(serverIp, mask, 99).ToString();
        DhcpEnd.Text = GetNearbyHost(serverIp, mask, 199).ToString();
        _syncingNetworkInputs = false;
    }

    private static bool TryGetNetwork(string ipText, string maskText, out IPAddress ip, out IPAddress mask)
    {
        ip = IPAddress.None;
        mask = IPAddress.None;
        if (!IPAddress.TryParse(ipText.Trim(), out var parsedIp) || !IPAddress.TryParse(maskText.Trim(), out var parsedMask)) return false;
        ip = parsedIp;
        mask = parsedMask;
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && mask.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private static IPAddress GetNearbyHost(IPAddress ip, IPAddress mask, uint preferredOffset)
    {
        var network = IpNetwork.ToUInt32(ip) & IpNetwork.ToUInt32(mask);
        var broadcast = network | ~IpNetwork.ToUInt32(mask);
        var candidate = IpNetwork.ToUInt32(ip) + preferredOffset;
        if (candidate >= broadcast || candidate == IpNetwork.ToUInt32(ip)) candidate = network + 1;
        if (candidate == IpNetwork.ToUInt32(ip) && candidate + 1 < broadcast) candidate++;
        return IpNetwork.FromUInt32(candidate);
    }

    private void UpdateManualScanButtons()
    {
        if (!IsInitialized) return;
        var hasAdapter = SelectedAdapter != null;
        var validIp = IPAddress.TryParse(ManualIp.Text.Trim(), out _);
        var validMask = IPAddress.TryParse(ManualMask.Text.Trim(), out _);
        var targetText = ManualTargetIp.Text.Trim();
        var validTarget = string.IsNullOrWhiteSpace(targetText) || IPAddress.TryParse(targetText, out _);
        var canApply = hasAdapter && validIp && validMask && validTarget && !_scanRunning;
        BtnApplyScan.IsEnabled = canApply;
        BtnStopScan.IsEnabled = _scanRunning;
        BtnRestoreScan.IsEnabled = hasAdapter && SelectedAdapter != null && _originalAdapterConfigs.ContainsKey(SelectedAdapter.InterfaceIndex) && !_scanRunning;
        BtnAddFavorite.IsEnabled = validIp && validMask && !_scanRunning;
        BtnApplyScan.Background = canApply ? Brushes.LightGreen : Brushes.LightGray;
        BtnStopScan.Background = _scanRunning ? Brushes.OrangeRed : Brushes.LightGray;
        BtnStopScan.Foreground = _scanRunning ? Brushes.White : Brushes.Black;
        BtnRestoreScan.Background = BtnRestoreScan.IsEnabled ? Brushes.MistyRose : Brushes.LightGray;
        BtnAddFavorite.Background = BtnAddFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
    }

    private void UpdateFavoriteButtons()
    {
        if (!IsInitialized) return;
        var selected = FavoriteGrid.SelectedItem is FavoriteConfig;
        BtnNewFavorite.IsEnabled = !_scanRunning;
        BtnImportFavorite.IsEnabled = !_scanRunning;
        BtnExportFavorite.IsEnabled = !_scanRunning && _allFavorites.Count > 0;
        BtnFavoriteColumns.IsEnabled = !_scanRunning;
        BtnLoadFavorite.IsEnabled = selected && !_scanRunning;
        BtnApplyFavorite.IsEnabled = selected && !_scanRunning && SelectedAdapter != null;
        BtnDeleteFavorite.IsEnabled = selected && !_scanRunning;
        BtnNewFavorite.Background = BtnNewFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnImportFavorite.Background = BtnImportFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnExportFavorite.Background = BtnExportFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnFavoriteColumns.Background = BtnFavoriteColumns.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnLoadFavorite.Background = BtnLoadFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnApplyFavorite.Background = BtnApplyFavorite.IsEnabled ? Brushes.LightGreen : Brushes.LightGray;
        BtnDeleteFavorite.Background = BtnDeleteFavorite.IsEnabled ? Brushes.MistyRose : Brushes.LightGray;
    }

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedAdapter == null) return;
        UpdateAdapterIpText(SelectedAdapter);
        TxtMac.Text = SelectedAdapter.MacAddress;
        TxtGateway.Text = SelectedAdapter.Gateway;
        TxtStatus.Text = SelectedAdapter.Status;
        TxtStatus.Foreground = SelectedAdapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) ? Brushes.ForestGreen : Brushes.Firebrick;
        TxtManualAdapterIp.Text = BuildAdapterIpDisplay(SelectedAdapter);
        UpdateManualScanButtons();
        UpdateFavoriteButtons();
    }

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e) => RefreshAdapters();

    private bool ConfirmConfigurationChange(NetworkAdapterInfo adapter, string targetIp, string targetMask, string operation)
    {
        var current = string.IsNullOrWhiteSpace(adapter.IPv4Address) ? "DHCP / 未配置" : adapter.IPv4Address + " / " + adapter.SubnetMask;
        var target = targetIp.Trim() + " / " + targetMask.Trim();
        if (current.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
        return AppDialog.Show(this, "Confirm Network Change / 确认网络变更", $"{operation}\n{adapter.DisplayName}\n\nCurrent / 当前: {current}\nNew / 新配置: {target}\n\nA recoverable backup will be saved before applying. / 应用前将保存可回滚备份。", confirm: true, danger: true);
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var adapter = SelectedAdapter;
        if (adapter == null) return;
        var backup = _adapterBackups.LastOrDefault(x => x.InterfaceIndex == adapter.InterfaceIndex);
        if (backup == null)
        {
            AppDialog.Show(this, "Rollback / 回滚", "No saved backup for this adapter / 此网卡没有已保存备份。");
            return;
        }
        if (!AppDialog.Show(this, "Rollback / 回滚", $"{backup.AdapterName}\n{backup.CapturedAt:yyyy-MM-dd HH:mm:ss}\n{(backup.DhcpEnabled ? "DHCP" : backup.IpAddress)}\n\nRestore this backup? / 确认恢复此备份？", confirm: true, danger: true)) return;
        try
        {
            SetBusy(true, "Rolling back adapter... / 正在回滚网卡...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await _adapterService.RestoreIPv4ConfigAsync(adapter, new AdapterIpv4Snapshot
            {
                DhcpEnabled = backup.DhcpEnabled,
                IpAddress = backup.IpAddress,
                PrefixLength = backup.PrefixLength,
                Gateway = backup.Gateway,
                Dns = backup.Dns,
                AutomaticMetric = backup.AutomaticMetric,
                InterfaceMetric = backup.InterfaceMetric
            });
            RefreshAdapters();
        }
        catch (Exception ex)
        {
            _logger.Error("Adapter rollback failed", ex);
            AppDialog.Show(this, "Rollback / 回滚", ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ExportResults_Click(object sender, RoutedEventArgs e)
    {
        var rows = Tabs.SelectedItem == TabDhcp
            ? Leases.Select(x => new[] { x.Time.ToString(), x.MacAddress, x.IpAddress, x.Hostname, x.Status, x.PingLatencyMs.ToString(), x.Remark })
            : ScanResults.Select(x => new[] { x.LastSeen.ToString(), x.IpAddress, x.MacAddress, x.Hostname, x.StatusText, x.LatencyMs.ToString(), x.Remark });
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"NetBoot-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        var header = Tabs.SelectedItem == TabDhcp ? new[] { "Time", "MAC", "IP", "Hostname", "Status", "PingMs", "Remark" } : new[] { "LastSeen", "IP", "MAC", "Hostname", "Status", "PingMs", "Remark" };
        File.WriteAllLines(dialog.FileName, new[] { Csv(header) }.Concat(rows.Select(Csv)), new UTF8Encoding(true));
        _logger.Info("Results exported: " + dialog.FileName);
    }

    private static string Csv(IEnumerable<string> values) => string.Join(",", values.Select(x => "\"" + (x ?? "").Replace("\"", "\"\"") + "\""));

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var adapter = SelectedAdapter;
        if (adapter == null) return;
        AppDialog.Show(this, "Adapter Diagnostics / 网卡诊断", $"Name / 名称: {adapter.Name}\nStatus / 状态: {adapter.Status}\nIP: {adapter.IPv4Address}\nMask / 掩码: {adapter.SubnetMask}\nGateway / 网关: {adapter.Gateway}\nDNS: {adapter.Dns}\nMAC: {adapter.MacAddress}\nWi-Fi: {adapter.IsWifi}\nVirtual / 虚拟: {adapter.IsVirtual}");
    }

    private void PackageLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "ZIP (*.zip)|*.zip", FileName = $"NetBoot-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log")) archive.CreateEntryFromFile(file, Path.Combine("logs", Path.GetFileName(file)));
        if (File.Exists(_paths.SettingsFile)) archive.CreateEntryFromFile(_paths.SettingsFile, "appsettings.json");
        if (File.Exists(_paths.AdapterBackupsFile)) archive.CreateEntryFromFile(_paths.AdapterBackupsFile, "adapter-backups.json");
        _logger.Info("Support package created: " + dialog.FileName);
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _darkTheme = !_darkTheme;
        RootGrid.Background = _darkTheme ? new SolidColorBrush(Color.FromRgb(31, 35, 40)) : Brushes.White;
        Foreground = _darkTheme ? Brushes.Gainsboro : Brushes.Black;
        BtnTheme.Content = _darkTheme ? "Light / 浅色" : "Theme / 主题";
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.LogsDirectory);
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = _paths.LogsDirectory, UseShellExecute = true });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Document.Blocks.Clear();
    private void CopyLog_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(new TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd).Text);

    private void About_Click(object sender, RoutedEventArgs e)
    {
        AppDialog.Show(this, _lang.T("about"), "NetBoot DHCP Tool v1.0.6\nIPv4 DHCP / Scan / Favorites\nAuthors: Joel & Codex\nEmail: 1406829360@qq.com");
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        _settings.Language = tag;
        JsonStore.Save(_paths.SettingsFile, _settings);
        _lang.Load(tag);
        ApplyLanguage();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.AllowDhcpOnWifi = AllowWifi.IsChecked == true;
        _settings.AllowDhcpOnAdapterWithGateway = AllowGateway.IsChecked == true;
        _settings.DetectExistingDhcpBeforeStart = DetectExistingDhcp.IsChecked == true;
        _settings.RestoreIpOnDhcpStop = RestoreOnStop.IsChecked == true;
        JsonStore.Save(_paths.SettingsFile, _settings);
        _logger.Info("Settings saved");
    }

    private void ScanGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ScanGrid.SelectedItem is ScanResult result)
        {
            OpenUrl("http://" + result.IpAddress);
        }
    }

    private async Task UpdateLeaseAsync(DhcpLease incoming)
    {
        await _leaseUpdateGate.WaitAsync();
        try
        {
            var existing = Leases.FirstOrDefault(x => x.MacAddress.Equals(incoming.MacAddress, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = incoming;
                existing.Status = "Assigned";
                Leases.Add(existing);
                _logger.Info($"UI lease added: {existing.MacAddress} {existing.IpAddress} {existing.Status}");
            }
            else
            {
                existing.IpAddress = incoming.IpAddress;
                existing.Hostname = incoming.Hostname;
                existing.LeaseStart = incoming.LeaseStart;
                existing.LeaseEnd = incoming.LeaseEnd;
                if (!existing.Status.Equals("Online", StringComparison.OrdinalIgnoreCase))
                {
                    existing.Status = "Assigned";
                }
                _logger.Info($"UI lease updated: {existing.MacAddress} {existing.IpAddress} {existing.Status}");
            }

            await ProbeLeaseConnectivityAsync(existing);
            var probes = await _probe.ProbeAsync(existing.IpAddress, _settings.HttpTimeoutMs);
            existing.HttpOk = probes.http;
            existing.HttpsOk = probes.https;
            AddOperationHistory("DHCP Lease", existing.IpAddress, existing.MacAddress, existing.Status, existing.Hostname);
            _logger.Info($"UI lease probe: {existing.IpAddress} ping={existing.PingLatencyMs} http={existing.HttpOk} https={existing.HttpsOk}");
            LeaseGrid.Items.Refresh();
        }
        finally
        {
            _leaseUpdateGate.Release();
        }
    }

    private async Task RefreshLeasePingAsync()
    {
        if (_leaseProbeInProgress) return;
        if (Leases.Count == 0) return;
        _leaseProbeInProgress = true;
        try
        {
            foreach (var lease in Leases.ToList())
            {
                await ProbeLeaseConnectivityAsync(lease);
            }
        }
        finally
        {
            _leaseProbeInProgress = false;
        }
    }

    private async Task ProbeLeaseConnectivityAsync(DhcpLease lease)
    {
        var oldStatus = lease.Status;
        var latency = await PingLatencyAsync(lease.IpAddress);
        lease.PingLatencyMs = latency;
        lease.Status = latency >= 0 ? "Online" : "Offline";
        if (latency >= 0 && !oldStatus.Equals("Online", StringComparison.OrdinalIgnoreCase))
        {
            lease.Time = DateTime.Now;
        }
        if (!oldStatus.Equals(lease.Status, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info($"UI lease status changed: {lease.MacAddress} {lease.IpAddress} {oldStatus}->{lease.Status} ping={latency}");
        }
    }

    private static async Task<long> PingLatencyAsync(string ip)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 800);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string ResolveHost(string ip)
    {
        try { return Dns.GetHostEntry(ip).HostName; }
        catch { return ""; }
    }

    private async Task ShowLeaseTimeoutHintAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), ct);
            if (!ct.IsCancellationRequested && Leases.Count == 0)
            {
                var msg = _lang.T("dhcp.timeout.hint");
                _logger.Warn(msg);
                DhcpHint.Text = msg;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetDhcpRunningState(bool running)
    {
        AdapterBox.IsEnabled = !running;
        BtnRefresh.IsEnabled = !running;
        DhcpServerIp.IsEnabled = !running;
        DhcpMask.IsEnabled = !running;
        DhcpStart.IsEnabled = !running;
        DhcpEnd.IsEnabled = !running;
        DhcpGateway.IsEnabled = !running;
        DhcpDns.IsEnabled = !running;
        BtnShowDhcpGateway.IsEnabled = !running;
        BtnShowDhcpDns.IsEnabled = !running;
        DhcpLeaseSeconds.IsEnabled = !running;
        DhcpConfirm.IsEnabled = !running;
        RestoreOnStop.IsEnabled = !running;
        BtnStartDhcp.IsEnabled = !running;
        BtnStopDhcp.IsEnabled = running;
        BtnStartDhcp.Background = running ? Brushes.LightGray : Brushes.LightGreen;
        BtnStopDhcp.Background = running ? Brushes.OrangeRed : Brushes.LightGray;
        BtnStopDhcp.Foreground = running ? Brushes.White : Brushes.Black;
    }

    private async Task CleanupWorkEnvironmentAsync()
    {
        try
        {
            PersistStateBeforeExit();
            var restoreAdapter = _dhcpWasStartedInThisSession;
            _leasePingTimer.Stop();
            _leaseHintCts?.Cancel();
            _scanCts?.Cancel();
            _dhcpServer.Stop();
            if (restoreAdapter)
            {
                var adapter = GetDhcpRestoreAdapter();
                if (adapter != null)
                {
                    await RestoreOriginalAdapterConfigAsync(adapter);
                }
            }
            _activeDhcpAdapterIndex = null;
            _logger.Info("Window closing cleanup completed");
        }
        catch (Exception ex)
        {
            _logger.Error("Window closing cleanup failed", ex);
        }
    }

    private void PersistStateBeforeExit()
    {
        try
        {
            SaveFavorites();
        }
        catch (Exception ex)
        {
            _logger.Error("Persist state before exit failed", ex);
        }
    }

    private void OpenHttpLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink link && link.CommandParameter is string ip)
        {
            var useHttps = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            OpenUrl((useHttps ? "https://" : "http://") + ip);
        }
    }

    private void OpenHttpsLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink link && link.CommandParameter is string ip) OpenUrl("https://" + ip);
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private void SetBusy(bool busy, string? text = null)
    {
        BusyText.Text = text ?? "Please wait... / 请稍后...";
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendLogLine(string line)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
        var level = line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ? "ERROR"
            : line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ? "WARN"
            : "INFO";
        var accent = level switch
        {
            "ERROR" => Brushes.Firebrick,
            "WARN" => Brushes.DarkOrange,
            _ => Brushes.ForestGreen
        };
        paragraph.Inlines.Add(new Run("▌") { Foreground = accent, FontWeight = FontWeights.Bold });
        paragraph.Inlines.Add(new Run(" " + line)
        {
            Foreground = Brushes.Black
        });
        LogBox.Document.Blocks.Add(paragraph);
        LogBox.ScrollToEnd();
    }

    private async Task RememberAdapterConfigAsync(NetworkAdapterInfo adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.InterfaceIndex)) return;
        if (!_lastAdapterIps.ContainsKey(adapter.InterfaceIndex))
        {
            _lastAdapterIps[adapter.InterfaceIndex] = adapter.IPv4Address;
        }
        if (!_originalAdapterConfigs.ContainsKey(adapter.InterfaceIndex))
        {
            _originalAdapterConfigs[adapter.InterfaceIndex] = await _adapterService.CaptureIPv4ConfigAsync(adapter);
        }
        var snapshot = _originalAdapterConfigs[adapter.InterfaceIndex];
        _adapterBackups.RemoveAll(x => x.InterfaceIndex == adapter.InterfaceIndex);
        _adapterBackups.Add(new AdapterConfigBackup
        {
            InterfaceIndex = adapter.InterfaceIndex,
            AdapterName = adapter.Name,
            CapturedAt = DateTime.Now,
            DhcpEnabled = snapshot.DhcpEnabled,
            IpAddress = snapshot.IpAddress,
            PrefixLength = snapshot.PrefixLength,
            Gateway = snapshot.Gateway,
            Dns = snapshot.Dns,
            AutomaticMetric = snapshot.AutomaticMetric,
            InterfaceMetric = snapshot.InterfaceMetric
        });
        JsonStore.Save(_paths.AdapterBackupsFile, _adapterBackups);
    }

    private void MarkAdapterIp(NetworkAdapterInfo adapter, string ip)
    {
        var previous = adapter.IPv4Address;
        adapter.IPv4Address = ip.Trim();
        AddIpHistory(previous, adapter.IPv4Address);
        AdapterBox.Items.Refresh();
        UpdateAdapterIpText(adapter);
        TxtManualAdapterIp.Text = BuildAdapterIpDisplay(adapter);
    }

    private void UpdateAdapterIpText(NetworkAdapterInfo adapter)
    {
        TxtCurrentIp.Text = BuildAdapterIpDisplay(adapter);
    }

    private string BuildAdapterIpDisplay(NetworkAdapterInfo adapter)
    {
        _lastAdapterIps.TryGetValue(adapter.InterfaceIndex, out var previous);
        var current = string.IsNullOrWhiteSpace(adapter.IPv4Address) ? "-" : adapter.IPv4Address;
        var previousText = string.IsNullOrWhiteSpace(previous) ? "-" : previous;
        return IsChineseUi()
            ? $"上次IP: {previousText}\n当前IP: {current}"
            : $"Last IP: {previousText}\nCurrent IP: {current}";
    }

    private void AddIpHistory(string previous, string current)
    {
        previous = string.IsNullOrWhiteSpace(previous) ? "-" : previous;
        current = string.IsNullOrWhiteSpace(current) ? "-" : current;
        if (previous == current || AdapterIpHistory.FirstOrDefault() is { PreviousIp: var p, CurrentIp: var c } && p == previous && c == current) return;
        AdapterIpHistory.Insert(0, new AdapterIpHistoryItem { PreviousIp = previous, CurrentIp = current });
        while (AdapterIpHistory.Count > 5) AdapterIpHistory.RemoveAt(AdapterIpHistory.Count - 1);
        SaveNetworkHistory();
    }

    private string CaptureBusinessConfigurationFingerprint()
    {
        var adapter = SelectedAdapter;
        return adapter == null ? "" : string.Join("|", adapter.InterfaceIndex, adapter.Status, adapter.IPv4Address, adapter.Gateway, adapter.MacAddress);
    }

    private void NetworkChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshSelectedAdapterStatus, DispatcherPriority.Send);

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Dispatcher.BeginInvoke(RefreshSelectedAdapterStatus, DispatcherPriority.Send);

    private async void RefreshSelectedAdapterStatus()
    {
        if (_adapterStatusRefreshInProgress || SelectedAdapter == null) return;
        _adapterStatusRefreshInProgress = true;
        try
        {
            var selectedIndex = SelectedAdapter.InterfaceIndex;
            var latest = await Task.Run(() => _adapterService.GetAdapters().FirstOrDefault(x => x.InterfaceIndex == selectedIndex));
            if (latest == null) return;
            var adapter = SelectedAdapter;
            if (adapter == null || adapter.InterfaceIndex != selectedIndex) return;
            var changed = !adapter.Status.Equals(latest.Status, StringComparison.OrdinalIgnoreCase)
                || !adapter.IPv4Address.Equals(latest.IPv4Address, StringComparison.OrdinalIgnoreCase)
                || !adapter.Gateway.Equals(latest.Gateway, StringComparison.OrdinalIgnoreCase);
            if (!changed) return;
            var previousIp = adapter.IPv4Address;
            var previousStatus = adapter.Status;
            adapter.Status = latest.Status;
            adapter.IPv4Address = latest.IPv4Address;
            adapter.Gateway = latest.Gateway;
            adapter.MacAddress = latest.MacAddress;
            AddIpHistory(previousIp, latest.IPv4Address);
            AdapterBox.Items.Refresh();
            UpdateAdapterIpText(adapter);
            TxtManualAdapterIp.Text = BuildAdapterIpDisplay(adapter);
            TxtMac.Text = adapter.MacAddress;
            TxtGateway.Text = adapter.Gateway;
            TxtStatus.Text = adapter.Status;
            TxtStatus.Foreground = adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) ? Brushes.ForestGreen : Brushes.Firebrick;
            if (!previousStatus.Equals(adapter.Status, StringComparison.OrdinalIgnoreCase)) SystemSounds.Exclamation.Play();
            _logger.Info($"Adapter status changed: {adapter.DisplayName} status={adapter.Status} ip={adapter.IPv4Address} gateway={adapter.Gateway}");
        }
        catch (Exception ex)
        {
            _logger.Warn("Adapter status refresh failed: " + ex.Message);
        }
        finally
        {
            _adapterStatusRefreshInProgress = false;
        }
    }

    private async Task RestoreOriginalAdapterConfigAsync(NetworkAdapterInfo adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.InterfaceIndex)) return;
        if (_originalAdapterConfigs.TryGetValue(adapter.InterfaceIndex, out var snapshot))
        {
            await _adapterService.RestoreIPv4ConfigAsync(adapter, snapshot);
            adapter.IPv4Address = snapshot.IpAddress;
            UpdateAdapterIpText(adapter);
            TxtManualAdapterIp.Text = BuildAdapterIpDisplay(adapter);
            _logger.Info($"Adapter restored to original config: idx={adapter.InterfaceIndex} {snapshot.DisplayText}");
            return;
        }
        await _adapterService.RestoreDhcpAsync(adapter);
    }

    private NetworkAdapterInfo? GetDhcpRestoreAdapter()
    {
        if (string.IsNullOrWhiteSpace(_activeDhcpAdapterIndex)) return SelectedAdapter;
        return Adapters.FirstOrDefault(x => x.InterfaceIndex == _activeDhcpAdapterIndex) ?? SelectedAdapter;
    }

    private void ShowFavoriteFieldsDialog(FavoriteConfig fav)
    {
        var window = new Window
        {
            Owner = this,
            Title = $"Custom Fields / 自定义字段 - {fav.Name}",
            Width = 680,
            Height = 460,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copyButton = new Button { Content = "Copy / 复制", MinWidth = 90, Margin = new Thickness(4) };
        var closeButton = new Button { Content = "Close / 关闭", MinWidth = 90, Margin = new Thickness(4) };
        buttons.Children.Add(copyButton);
        buttons.Children.Add(closeButton);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var fields = fav.CustomFields.Where(x => !string.IsNullOrWhiteSpace(x.Name) || !string.IsNullOrWhiteSpace(x.Value)).ToList();
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserResizeColumns = true,
            CanUserResizeRows = false,
            RowHeight = 34,
            ItemsSource = fields
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Field / 字段", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(180) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value / 内容", Binding = new System.Windows.Data.Binding("Value"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        root.Children.Add(grid);
        window.Content = root;
        copyButton.Click += (_, _) =>
        {
            var text = string.Join(Environment.NewLine, fields.Select(x => $"{x.Name}: {x.Value}"));
            Clipboard.SetText(text);
        };
        closeButton.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private void ShowFavoriteDetailsDialog(FavoriteConfig fav)
    {
        var rows = new List<FavoriteField>
        {
            new() { Name = IsChineseUi() ? "名称" : "Name", Value = fav.Name },
            new() { Name = IsChineseUi() ? "设备" : "Device", Value = fav.DeviceNumber },
            new() { Name = "SN", Value = fav.SerialNumber },
            new() { Name = IsChineseUi() ? "备注" : "Remark", Value = fav.RemarkName },
            new() { Name = IsChineseUi() ? "账号" : "User", Value = fav.Username },
            new() { Name = IsChineseUi() ? "密码" : "Password", Value = fav.Password },
            new() { Name = IsChineseUi() ? "本机IP" : "Local IP", Value = fav.LocalIp },
            new() { Name = IsChineseUi() ? "掩码" : "Mask", Value = fav.SubnetMask },
            new() { Name = IsChineseUi() ? "对端IP" : "Target IP", Value = fav.TargetIp },
            new() { Name = IsChineseUi() ? "说明" : "Description", Value = fav.Description },
            new() { Name = IsChineseUi() ? "记忆文本" : "Memory", Value = fav.MemoryText }
        };
        rows.AddRange(fav.CustomFields.Where(x => !string.IsNullOrWhiteSpace(x.Name) || !string.IsNullOrWhiteSpace(x.Value)));

        var window = new Window
        {
            Owner = this,
            Title = (IsChineseUi() ? "收藏详情 - " : "Favorite Details - ") + fav.Name,
            Width = 760,
            Height = 560,
            MinWidth = 560,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copyButton = new Button { Content = IsChineseUi() ? "复制" : "Copy", MinWidth = 90, Margin = new Thickness(4) };
        var closeButton = new Button { Content = IsChineseUi() ? "关闭" : "Close", MinWidth = 90, Margin = new Thickness(4) };
        buttons.Children.Add(copyButton);
        buttons.Children.Add(closeButton);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = true,
            RowHeight = 34,
            ItemsSource = rows
        };
        grid.Columns.Add(new DataGridTextColumn { Header = IsChineseUi() ? "字段" : "Field", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(180) });
        grid.Columns.Add(new DataGridTextColumn { Header = IsChineseUi() ? "内容" : "Value", Binding = new System.Windows.Data.Binding("Value"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        root.Children.Add(grid);
        window.Content = root;
        copyButton.Click += (_, _) => Clipboard.SetText(string.Join(Environment.NewLine, rows.Select(x => $"{x.Name}: {x.Value}")));
        closeButton.Click += (_, _) => window.Close();
        window.ShowDialog();
    }

    private void ShowDhcpGateway_Click(object sender, RoutedEventArgs e)
    {
        BtnShowDhcpGateway.Visibility = Visibility.Collapsed;
        LblDhcpGateway.Visibility = Visibility.Visible;
        DhcpGateway.Visibility = Visibility.Visible;
    }

    private void ShowDhcpDns_Click(object sender, RoutedEventArgs e)
    {
        BtnShowDhcpDns.Visibility = Visibility.Collapsed;
        LblDhcpDns.Visibility = Visibility.Visible;
        DhcpDns.Visibility = Visibility.Visible;
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || Tabs.SelectedItem is not TabItem selected) return;
        if (selected != TabDhcp && selected != TabScan)
        {
            _lastBusinessTab = selected == TabFavorites || selected == TabSettings ? _lastBusinessTab : selected;
            return;
        }
        var currentFingerprint = CaptureBusinessConfigurationFingerprint();
        var configurationChanged = !string.IsNullOrWhiteSpace(_businessConfigurationFingerprint)
            && !string.Equals(_businessConfigurationFingerprint, currentFingerprint, StringComparison.Ordinal);
        if (_lastBusinessTab != null && _lastBusinessTab != selected && configurationChanged)
        {
            AppDialog.Show(this, IsChineseUi() ? "请先刷新" : "Refresh Required",
                IsChineseUi()
                    ? "自动 DHCP 和手动 IP/扫描属于不同业务场景。切换后请先点击“刷新”，重置并确认网卡信息后再操作。"
                    : "Auto DHCP and Manual IP/Scan are different workflows. After switching, click Refresh first to reset and confirm adapter information.");
        }
        _lastBusinessTab = selected;
        _businessConfigurationFingerprint = currentFingerprint;
    }

    private void ApplyGridHeaders()
    {
        var zh = IsChineseUi();
        SetHeaders(LeaseGrid.Columns, zh,
            ["最后上线", "MAC", "IP", "主机名", "开始", "结束", "状态", "延迟(ms)", "备注"],
            ["Last Online", "MAC", "IP", "Hostname", "Start", "End", "Status", "Ping ms", "Remark"]);
        SetHeaders(ScanGrid.Columns, zh,
            ["IP", "连通", "延迟(ms)", "状态", "网页", "本机IP", "MAC", "主机名", "最后发现", "备注"],
            ["IP", "Ping", "ms", "Status", "Web", "Local IP", "MAC", "Hostname", "Last Seen", "Remark"]);
        SetHeaders(FavoriteGrid.Columns, zh,
            ["名称", "设备", "序列号", "备注", "账号", "密码", "IP", "掩码", "自定义", "更新", "最近使用", "说明"],
            ["Name", "Device", "SN", "Remark", "User", "Password", "IP", "Mask", "Custom", "Updated", "Last Used", "Description"]);
    }

    private static void SetHeaders(IList<DataGridColumn> columns, bool zh, string[] zhHeaders, string[] enHeaders)
    {
        for (var i = 0; i < columns.Count && i < zhHeaders.Length && i < enHeaders.Length; i++)
        {
            if (columns[i] is DataGridColumn column) column.Header = zh ? zhHeaders[i] : enHeaders[i];
        }
    }

    private bool IsChineseUi() => (_settings.Language.Equals("auto", StringComparison.OrdinalIgnoreCase) && System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        || _settings.Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidFavorite(FavoriteConfig item)
    {
        return !string.IsNullOrWhiteSpace(item.Name)
            && IPAddress.TryParse(item.LocalIp, out _)
            && IPAddress.TryParse(item.SubnetMask, out _);
    }

    private static bool SameFavorite(FavoriteConfig a, FavoriteConfig b)
    {
        if (!string.IsNullOrWhiteSpace(a.Id) && a.Id.Equals(b.Id, StringComparison.OrdinalIgnoreCase)) return true;
        return a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)
            && a.LocalIp.Equals(b.LocalIp, StringComparison.OrdinalIgnoreCase)
            && a.TargetIp.Equals(b.TargetIp, StringComparison.OrdinalIgnoreCase);
    }

    private static void MergeFavorite(FavoriteConfig target, FavoriteConfig source)
    {
        target.Name = source.Name;
        target.DeviceNumber = source.DeviceNumber;
        target.SerialNumber = source.SerialNumber;
        target.RemarkName = source.RemarkName;
        target.Description = source.Description;
        target.Username = source.Username;
        target.Password = source.Password;
        target.MemoryText = source.MemoryText;
        target.LocalIp = source.LocalIp;
        target.SubnetMask = source.SubnetMask;
        target.TargetIp = source.TargetIp;
        target.CustomFields = source.CustomFields;
        target.UpdatedAt = DateTime.Now;
    }

    private void SelectLanguageBox(string language)
    {
        foreach (var item in LanguageBox.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string)?.Equals(language, StringComparison.OrdinalIgnoreCase) == true)
            {
                LanguageBox.SelectedItem = item;
                return;
            }
        }
        LanguageBox.SelectedIndex = 0;
    }
}
