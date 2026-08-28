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
    private static readonly Version CurrentVersion = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0, 8);
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly LanguageService _lang;
    private readonly NetworkAdapterService _adapterService;
    private readonly StaticRouteService _routeService;
    private readonly HttpProbeService _probe;
    private readonly PingScanner _scanner;
    private readonly ExistingDhcpDetector _dhcpDetector;
    private readonly DhcpServer _dhcpServer;
    private readonly MainWindowViewModel _viewModel;
    private readonly VersionUpdateService _updateService;
    private readonly CancellationTokenSource _updateCts = new();
    private readonly CancellationTokenSource _updateDownloadCts = new();
    private readonly SemaphoreSlim _leaseUpdateGate = new(1, 1);
    private AppSettings _settings;
    private List<FavoriteConfig> _allFavorites = [];
    private List<AdapterConfigBackup> _adapterBackups = [];
    private List<OperationHistoryItem> _operationHistory = [];
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _leaseHintCts;
    private readonly DispatcherTimer _leasePingTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _adapterStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _leaseProbeInProgress;
    private bool _closingCleanupStarted;
    private bool _closeAfterCleanup;
    private bool _scanRunning;
    private bool _dhcpWasStartedInThisSession;
    private readonly Dictionary<string, string> _lastAdapterIps = new();
    private readonly Dictionary<string, AdapterIpv4Snapshot> _originalAdapterConfigs = new();
    private readonly Dictionary<string, AdapterMacBackup> _originalAdapterMacs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppliedStaticRoute> _appliedStaticRoutes = [];
    private TabItem? _lastBusinessTab;
    private string? _activeDhcpAdapterIndex;
    private bool _syncingNetworkInputs;
    private bool _adapterStatusRefreshInProgress;
    private bool _adapterRefreshInProgress;
    private bool _adapterActionInProgress;
    private bool _updateDownloadInProgress;
    private bool _updateSlowWarningShown;
    private bool _darkTheme;
    private string? _businessConfigurationFingerprint;
    private DhcpFirewallRuleLease? _dhcpFirewallRules;
    private UpdateCheckResult? _lastUpdateResult;
    private bool _updateCheckStarted;

    public ObservableCollection<NetworkAdapterInfo> Adapters => _viewModel.Adapters;
    public ObservableCollection<ScanResult> ScanResults => _viewModel.ScanResults;
    public ObservableCollection<FavoriteConfig> Favorites => _viewModel.Favorites;
    public ObservableCollection<DhcpLease> Leases => _viewModel.Leases;
    public ObservableCollection<AdapterIpHistoryItem> AdapterIpHistory => _viewModel.AdapterIpHistory;
    public ObservableCollection<StaticRouteRule> StaticRoutes => _viewModel.StaticRoutes;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, new RoutedEventHandler(LogButtonClick), true);
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        _paths = new AppPaths(AppContext.BaseDirectory);
        Defaults.EnsureFiles(_paths);
        _logger = (Application.Current as App)?.Logger ?? new FileLogger(_paths);
        _logger.LineWritten += line => Dispatcher.BeginInvoke(() =>
        {
            AppendLogLine(line);
        });
        _settings = JsonStore.LoadOrDefault(_paths.SettingsFile, new AppSettings(), _logger);
        _lang = new LanguageService(_paths);
        _lang.Load(_settings.Language);
        _adapterService = new NetworkAdapterService(_logger);
        _routeService = new StaticRouteService(_logger);
        _probe = new HttpProbeService();
        _scanner = new PingScanner(_logger, _probe);
        _dhcpDetector = new ExistingDhcpDetector(_logger);
        _dhcpServer = new DhcpServer(_logger);
        _updateService = new VersionUpdateService();
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
        SetDhcpRunningState(false);
        UpdateAdapterActionButtons();
        UpdateManualScanButtons();
        UpdateFavoriteButtons();
        _logger.Info("MainWindow ready");
        Dispatcher.BeginInvoke(() =>
        {
            SetBusy(false);
            _ = RefreshAdaptersAsync();
            _ = RecoverStaleRoutesAsync();
            _ = CheckForUpdatesAsync();
        }, DispatcherPriority.ApplicationIdle);
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
        _updateCts.Cancel();
        _updateDownloadCts.Cancel();
        IsEnabled = false;
        SetBusy(true, "Cleaning up... / 正在清理工作环境...");
        await CleanupWorkEnvironmentAsync();
        _updateService.Dispose();
        _updateCts.Dispose();
        _updateDownloadCts.Dispose();
        _closeAfterCleanup = true;
        await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }

    private NetworkAdapterInfo? SelectedAdapter => AdapterBox.SelectedItem as NetworkAdapterInfo;

    private static string AdapterKey(NetworkAdapterInfo adapter) =>
        !string.IsNullOrWhiteSpace(adapter.Id) ? adapter.Id : adapter.InterfaceIndex;

    private void ApplyLanguage()
    {
        Title = $"NetBoot DHCP Tool v{CurrentVersionText} - Authors: Joel & Codex - 1406829360@qq.com";
        TxtVersion.Text = $"{_lang.T("version")}: v{CurrentVersionText}";
        LblLanguage.Text = _lang.T("language");
        LanguageBox.ToolTip = _lang.T("help.language");
        BtnRefresh.Content = _lang.T("refresh");
        BtnRollback.Content = _lang.T("rollback");
        BtnExport.Content = _lang.T("export.results");
        BtnDiagnostics.Content = _lang.T("diagnostics");
        BtnPackageLogs.Content = _lang.T("package.logs");
        BtnTheme.Content = _darkTheme ? _lang.T("theme.light") : _lang.T("theme");
        BtnRestartAdapter.Content = _lang.T("restart.adapter");
        BtnChangeMac.Content = _lang.T("change.mac");
        BtnOpenLogs.Content = _lang.T("open.logs");
        BtnOpenLogs2.Content = _lang.T("open.logs");
        BtnAbout.Content = _lang.T("about");
        LblAdapter.Text = _lang.T("adapter");
        LblMac.Text = _lang.T("mac");
        LblGateway.Text = _lang.T("gateway");
        LblStatus.Text = _lang.T("status");
        TabDhcp.Header = _lang.T("auto.dhcp");
        TabScan.Header = _lang.T("manual.scan");
        TabRoutes.Header = _lang.T("static.routes");
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
        BtnShowDhcpGateway.Content = _lang.T("show.gateway");
        BtnShowDhcpDns.Content = _lang.T("show.dns");
        DhcpConfirm.Content = _lang.T("isolated.confirm");
        RestoreOnStop.Content = _lang.T("restore.stop");
        LblManualIp.Text = _lang.T("server.ip");
        LblManualMask.Text = _lang.T("subnet.mask");
        LblManualTargetIp.Text = _lang.T("target.ip");
        BtnApplyScan.Content = _lang.T("apply.scan");
        BtnStopScan.Content = _lang.T("stop.scan");
        BtnRestoreScan.Content = _lang.T("restore.scan");
        BtnAddRoute.Content = _lang.T("add.route");
        BtnRemoveRoute.Content = _lang.T("remove.route");
        BtnApplyRoutes.Content = _lang.T("apply.routes");
        BtnClearAppliedRoutes.Content = _lang.T("clear.applied.routes");
        RouteHint.Text = _lang.T("route.hint");
        BtnAddFavorite.Content = _lang.T("add.favorite");
        BtnNewFavorite.Content = _lang.T("new.favorite");
        BtnTemplateFavorite.Content = _lang.T("template.favorite");
        BtnImportFavorite.Content = _lang.T("import.favorite");
        BtnExportFavorite.Content = _lang.T("export.favorite");
        BtnFavoriteColumns.Content = _lang.T("favorite.columns");
        BtnOpenFavorite.Content = _lang.T("open.favorite");
        LblSearch.Text = _lang.T("search");
        BtnLoadFavorite.Content = _lang.T("load");
        BtnApplyFavorite.Content = _lang.T("apply.and.scan");
        BtnDeleteFavorite.Content = _lang.T("delete");
        BtnClearLog.Content = _lang.T("clear.log");
        BtnCopyLog.Content = _lang.T("copy.log");
        AllowWifi.Content = _lang.T("allow.wifi");
        AllowGateway.Content = _lang.T("allow.gateway");
        DetectExistingDhcp.Content = _lang.T("detect.existing.dhcp");
        AllowRestartAnyAdapter.Content = _lang.T("allow.restart.any");
        AllowMacChangeAnyAdapter.Content = _lang.T("allow.mac.any");
        BtnSaveSettings.Content = _lang.T("save");
        DhcpHint.Text = _lang.T("dhcp.client.hint");
        AdapterBox.ToolTip = _lang.T("help.adapter.selector");
        TxtCurrentIp.ToolTip = _lang.T("help.current.ip");
        TxtMac.ToolTip = _lang.T("help.mac.value");
        TxtGateway.ToolTip = _lang.T("help.gateway.value");
        TxtStatus.ToolTip = _lang.T("help.status.value");
        DhcpServerIp.ToolTip = _lang.T("help.server.ip");
        DhcpMask.ToolTip = _lang.T("help.subnet.mask");
        DhcpStart.ToolTip = _lang.T("help.pool.start");
        DhcpEnd.ToolTip = _lang.T("help.pool.end");
        DhcpLeaseSeconds.ToolTip = _lang.T("help.lease.seconds");
        ManualIp.ToolTip = _lang.T("help.manual.ip");
        ManualMask.ToolTip = _lang.T("help.manual.mask");
        ManualTargetIp.ToolTip = _lang.T("help.manual.target.ip");
        FavoriteSearch.ToolTip = _lang.T("help.favorite.search");
        DhcpConfirm.ToolTip = _lang.T("help.confirm.isolated");
        RestoreOnStop.ToolTip = _lang.T("help.restore.stop");
        LeaseGrid.ToolTip = _lang.T("help.lease.grid");
        ScanGrid.ToolTip = _lang.T("help.scan.grid");
        RouteGrid.ToolTip = _lang.T("help.route.grid");
        FavoriteGrid.ToolTip = _lang.T("help.favorite.grid");
        TabDhcp.ToolTip = _lang.T("help.tab.dhcp");
        TabScan.ToolTip = _lang.T("help.tab.scan");
        TabRoutes.ToolTip = _lang.T("help.tab.routes");
        TabFavorites.ToolTip = _lang.T("help.tab.favorites");
        TabSettings.ToolTip = _lang.T("help.tab.settings");
        ApplyGridHeaders();
        UpdateManualScanButtons();
        UpdateFavoriteButtons();
        UpdateVersionPresentation();
    }

    private static string CurrentVersionText => CurrentVersion.ToString(3);

    private async Task CheckForUpdatesAsync()
    {
        if (_updateCheckStarted) return;
        _updateCheckStarted = true;
        UpdateVersionPresentation();
        try
        {
            _lastUpdateResult = await _updateService.CheckAsync(CurrentVersion, _updateCts.Token);
            _logger.Info(_lastUpdateResult.Succeeded
                ? $"Update check completed: current={CurrentVersionText} latest={_lastUpdateResult.LatestVersion} new={_lastUpdateResult.IsNewVersion}"
                : "Update check failed: " + _lastUpdateResult.Error);
        }
        catch (OperationCanceledException) when (_updateCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _lastUpdateResult = new UpdateCheckResult { CurrentVersion = CurrentVersion, Succeeded = false, Error = ex.Message };
            _logger.Error("Update check failed", ex);
        }
        UpdateVersionPresentation();
    }

    private void UpdateVersionPresentation()
    {
        if (!_updateCheckStarted)
        {
            TxtUpdateStatus.Text = "";
            TxtUpdateLink.Visibility = Visibility.Collapsed;
            UpdateLink.IsEnabled = false;
            return;
        }

        if (_lastUpdateResult == null)
        {
            TxtUpdateStatus.Text = _lang.T("checking.update");
            TxtUpdateLink.Visibility = Visibility.Collapsed;
            UpdateLink.IsEnabled = false;
            return;
        }

        if (_lastUpdateResult.Succeeded && _lastUpdateResult.IsNewVersion)
        {
            TxtUpdateStatus.Text = "";
            UpdateLink.Inlines.Clear();
            UpdateLink.Inlines.Add(new Run($"{_lang.T("new.version")} v{_lastUpdateResult.LatestVersion}"));
            UpdateLink.ToolTip = _lastUpdateResult.DownloadUrl;
            TxtUpdateLink.Visibility = Visibility.Visible;
            UpdateLink.IsEnabled = true;
            return;
        }

        TxtUpdateLink.Visibility = Visibility.Collapsed;
        UpdateLink.IsEnabled = false;
        TxtUpdateStatus.Text = _lastUpdateResult.Succeeded ? _lang.T("latest.version") : _lang.T("update.failed");
        TxtUpdateStatus.ToolTip = _lastUpdateResult.Succeeded ? null : _lastUpdateResult.Error;
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
        AllowRestartAnyAdapter.IsChecked = _settings.AllowRestartOnAnyAdapter;
        AllowMacChangeAnyAdapter.IsChecked = _settings.AllowMacChangeOnAnyAdapter;
        SelectLanguageBox(_settings.Language);
    }

    private async Task RefreshAdaptersAsync()
    {
        if (_adapterRefreshInProgress) return;
        _adapterRefreshInProgress = true;
        var previousIndex = SelectedAdapter?.InterfaceIndex;
        try
        {
            SetBusy(true, "Loading network adapters... / 正在加载网卡...");
            var adapters = (await Task.Run(() => _adapterService.GetAdapters()))
                .OrderBy(x => x.IsVirtual)
                .ThenBy(x => x.IsWifi)
                .ThenBy(x => !x.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            Adapters.Clear();
            foreach (var item in adapters)
            {
                Adapters.Add(item);
            }
            AdapterBox.ItemsSource = Adapters;
            if (Adapters.Count > 0)
            {
                var preferred = Adapters.FirstOrDefault(x => !string.IsNullOrWhiteSpace(previousIndex) && x.InterfaceIndex.Equals(previousIndex, StringComparison.OrdinalIgnoreCase))
                    ?? Adapters.FirstOrDefault(x => !x.IsVirtual && !x.IsWifi && x.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                    ?? Adapters.FirstOrDefault(x => !x.IsVirtual && !x.IsWifi)
                    ?? Adapters.FirstOrDefault(x => !x.IsVirtual)
                    ?? Adapters.FirstOrDefault();
                AdapterBox.SelectedItem = preferred;
            }
            if (Adapters.Count == 0) _logger.Warn("No adapters found for UI");
            RefreshRouteAdapterMetadata();
            await LoadCurrentStaticRoutesAsync(adapters);
            _businessConfigurationFingerprint = CaptureBusinessConfigurationFingerprint();
            _logger.Info($"UI adapter list loaded: count={Adapters.Count}, selected={SelectedAdapter?.DisplayName ?? ""}");
        }
        catch (Exception ex)
        {
            _logger.Error("Refresh adapters failed", ex);
            AppDialog.Show(this, _lang.T("refresh"), ex.Message, danger: true);
        }
        finally
        {
            _adapterRefreshInProgress = false;
            SetBusy(false);
            UpdateAdapterActionButtons();
            if (!_closingCleanupStarted) _adapterStatusTimer.Start();
        }
    }

    private async Task LoadCurrentStaticRoutesAsync(IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        try
        {
            var adapterByIndex = adapters
                .Where(x => !string.IsNullOrWhiteSpace(x.InterfaceIndex))
                .ToDictionary(x => x.InterfaceIndex, StringComparer.OrdinalIgnoreCase);
            foreach (var row in StaticRoutes.Where(x => x.IsExistingRoute).ToList()) StaticRoutes.Remove(row);
            var routes = await _routeService.GetCurrentStaticRoutesAsync();
            foreach (var route in routes)
            {
                adapterByIndex.TryGetValue(route.InterfaceIndex, out var adapter);
                StaticRoutes.Insert(0, new StaticRouteRule
                {
                    DestinationPrefix = route.DestinationPrefix,
                    AdapterId = adapter?.Id ?? "",
                    AdapterName = adapter?.Name ?? $"Interface {route.InterfaceIndex} / 未枚举接口",
                    AdapterMac = adapter?.MacAddress ?? "",
                    NextHop = route.NextHop == "0.0.0.0" ? "" : route.NextHop,
                    RouteMetric = route.RouteMetric,
                    Status = $"Existing route / 已有路由 ({route.Protocol})",
                    IsExistingRoute = true
                });
            }
            RouteGrid?.Items.Refresh();
            _logger.Info($"Current static routes loaded: count={routes.Count}");
        }
        catch (Exception ex)
        {
            _logger.Warn("Read current static routes failed: " + ex.Message);
        }
    }

    private void LoadFavorites()
    {
        _allFavorites = FavoriteStore.Load(_paths.FavoritesFile, _logger, migrateLegacy: true);
        FilterFavorites();
    }

    private void SaveFavorites()
    {
        FavoriteStore.Save(_paths.FavoritesFile, _allFavorites, _logger);
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
        var serverStarted = false;
        var adapterConfigured = false;
        NetworkAdapterInfo? changedAdapter = null;
        try
        {
            SetBusy(true, "Starting DHCP... / 正在启动 DHCP...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var adapter = SelectedAdapter ?? throw new InvalidOperationException("No adapter selected");
            changedAdapter = adapter;
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
            adapterConfigured = true;
            _activeDhcpAdapterIndex = adapter.InterfaceIndex;
            MarkAdapterIp(adapter, DhcpServerIp.Text);
            await _adapterService.LogReadonlyWlanStateAsync("after DHCP start");
            _dhcpFirewallRules = await _adapterService.EnsureDhcpFirewallRulesAsync();
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
            serverStarted = true;
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
            if (!serverStarted)
            {
                await RemoveDhcpFirewallRulesAsync();
                if (adapterConfigured && changedAdapter != null)
                {
                    try
                    {
                        await RestoreOriginalAdapterConfigAsync(changedAdapter);
                        _activeDhcpAdapterIndex = null;
                        _logger.Info("DHCP startup rollback restored adapter configuration");
                    }
                    catch (Exception restoreEx)
                    {
                        _logger.Error("DHCP startup rollback failed", restoreEx);
                    }
                }
            }
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
            await RemoveDhcpFirewallRulesAsync();
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

    private void AddRoute_Click(object sender, RoutedEventArgs e)
    {
        var adapter = SelectedAdapter ?? Adapters.FirstOrDefault(x => !x.IsWifi && !x.IsVirtual);
        var rule = new StaticRouteRule
        {
            AdapterId = adapter?.Id ?? "",
            AdapterName = adapter?.Name ?? "",
            AdapterMac = adapter?.MacAddress ?? "",
            RouteMetric = 10
        };
        StaticRoutes.Add(rule);
        RouteGrid.SelectedItem = rule;
        RouteGrid.ScrollIntoView(rule);
    }

    private async void RestartAdapter_Click(object sender, RoutedEventArgs e)
    {
        if (_adapterActionInProgress) return;
        var adapter = SelectedAdapter;
        if (adapter == null)
        {
            AppDialog.Show(this, _lang.T("restart.adapter"), IsChineseUi() ? "请先选择网卡。" : "Select an adapter first.");
            return;
        }
        if (!AppDialog.Show(this,
            _lang.T("restart.adapter"),
            IsChineseUi()
                ? $"{adapter.DisplayName}\n\n将执行禁用 → 启用，可能暂时中断此网卡通信。是否继续？"
                : $"{adapter.DisplayName}\n\nThe adapter will be disabled and enabled, temporarily interrupting its traffic. Continue?",
            confirm: true,
            danger: true)) return;

        _adapterActionInProgress = true;
        UpdateAdapterActionButtons();
        try
        {
            SetBusy(true, IsChineseUi() ? "正在重启网卡，请稍后..." : "Restarting adapter, please wait...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await _adapterService.RestartAdapterAsync(adapter, _settings.AllowRestartOnAnyAdapter);
            _logger.Info($"Adapter restart completed: idx={adapter.InterfaceIndex} name={adapter.Name} status={adapter.Status}");
            await RefreshAdaptersAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Adapter restart failed: idx={adapter.InterfaceIndex} name={adapter.Name}", ex);
            AppDialog.Show(this, _lang.T("restart.adapter"), ex.Message, danger: true);
        }
        finally
        {
            _adapterActionInProgress = false;
            UpdateAdapterActionButtons();
            SetBusy(false);
        }
    }

    private async void ChangeMac_Click(object sender, RoutedEventArgs e)
    {
        if (_adapterActionInProgress) return;
        var adapter = SelectedAdapter;
        if (adapter == null)
        {
            AppDialog.Show(this, _lang.T("change.mac"), IsChineseUi() ? "请先选择网卡。" : "Select an adapter first.");
            return;
        }

        var dialog = new MacAddressWindow(adapter) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var requestedMac = dialog.MacAddress;
        var key = AdapterKey(adapter);
        var hadBackup = _originalAdapterMacs.TryGetValue(key, out var existingBackup);
        if (!hadBackup)
        {
            if (string.IsNullOrWhiteSpace(adapter.MacAddress))
            {
                AppDialog.Show(this, _lang.T("change.mac"), IsChineseUi() ? "无法读取当前 MAC，已取消。" : "The current MAC could not be read; operation canceled.", danger: true);
                return;
            }
            existingBackup = new AdapterMacBackup
            {
                InterfaceIndex = adapter.InterfaceIndex,
                AdapterId = adapter.Id,
                AdapterName = adapter.Name,
                OriginalMacAddress = adapter.MacAddress,
                CapturedAt = DateTime.Now
            };
            _originalAdapterMacs[key] = existingBackup;
        }
        existingBackup!.RestoreOnExit = dialog.RestoreOnExit;

        _adapterActionInProgress = true;
        UpdateAdapterActionButtons();
        try
        {
            SetBusy(true, IsChineseUi() ? "正在修改 MAC，请稍后..." : "Changing MAC, please wait...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await _adapterService.ChangeMacAddressAsync(adapter, requestedMac, _settings.AllowMacChangeOnAnyAdapter);
            adapter.MacAddress = requestedMac;
            TxtMac.Text = requestedMac;
            AdapterBox.Items.Refresh();
            _logger.Info($"Adapter MAC changed: idx={adapter.InterfaceIndex} name={adapter.Name} old={existingBackup.OriginalMacAddress} new={requestedMac} restoreOnExit={existingBackup.RestoreOnExit}");
            await RefreshAdaptersAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"Adapter MAC change failed: idx={adapter.InterfaceIndex} name={adapter.Name} requested={requestedMac}", ex);
            if (!hadBackup) _originalAdapterMacs.Remove(key);
            AppDialog.Show(this, _lang.T("change.mac"), ex.Message, danger: true);
        }
        finally
        {
            _adapterActionInProgress = false;
            UpdateAdapterActionButtons();
            SetBusy(false);
        }
    }

    private void RouteAdapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.DataContext is not StaticRouteRule rule || box.SelectedItem is not NetworkAdapterInfo adapter) return;
        rule.AdapterId = adapter.Id;
        rule.AdapterName = adapter.Name;
        rule.AdapterMac = adapter.MacAddress;
        rule.Status = "Not applied / 未应用";
        RouteGrid.Items.Refresh();
    }

    private async void ApplyRoutes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var targets = BuildStaticRouteTargets();
            var summary = string.Join(Environment.NewLine, targets.Select(x =>
                $"{x.Route.DestinationPrefix}  ->  {x.Adapter.Name}  {x.Route.NextHop switch { "0.0.0.0" => "Direct / 直连", _ => "Gateway / 网关 " + x.Route.NextHop }}  metric={x.Route.RouteMetric}"));
            if (!AppDialog.Show(this, IsChineseUi() ? "确认应用静态路由" : "Confirm Static Routes", summary + Environment.NewLine + Environment.NewLine + (IsChineseUi() ? "这些路由将影响本机流量。是否继续？" : "These routes will affect traffic from this computer. Continue?"), confirm: true, danger: true)) return;
            if (targets.Any(x => x.Route.IsDefaultRoute)
                && !AppDialog.Show(this,
                    IsChineseUi() ? "确认默认路由" : "Confirm Default Route",
                    IsChineseUi()
                        ? "检测到 0.0.0.0/0，可能改变整机所有 IPv4 流量的出口。仍要应用吗？"
                        : "0.0.0.0/0 may change the default path for all IPv4 traffic. Apply it anyway?",
                    confirm: true,
                    danger: true)) return;

            SetBusy(true, "Applying routes... / 正在应用路由...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_appliedStaticRoutes.Count > 0 && !await ClearAppliedRoutesAsync()) return;

            var results = await _routeService.ApplyAsync(targets);
            foreach (var result in results)
            {
                result.Rule.Status = result.StatusText;
                if (result.Created && result.Applied != null) _appliedStaticRoutes.Add(result.Applied);
            }
            SaveStaticRouteSession();
            RouteGrid.Items.Refresh();
            _logger.Info($"Static route batch applied: rules={results.Count} created={results.Count(x => x.Created)}");
        }
        catch (Exception ex)
        {
            _logger.Error("Apply static routes failed", ex);
            AppDialog.Show(this, _lang.T("apply.routes"), ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ClearAppliedRoutes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Clearing routes... / 正在清除路由...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!await ClearAppliedRoutesAsync())
            {
                AppDialog.Show(this,
                    IsChineseUi() ? "清除静态路由" : "Clear Static Routes",
                    IsChineseUi() ? "部分路由未能清除，详细原因已写入日志。" : "Some routes could not be cleared. See the log for details.",
                    danger: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Clear static routes failed", ex);
            AppDialog.Show(this, _lang.T("clear.applied.routes"), ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RemoveRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not StaticRouteRule rule) return;
        if (rule.IsExistingRoute)
        {
            AppDialog.Show(this, _lang.T("remove.route"), IsChineseUi()
                ? "这是本机已有路由，仅用于展示，未执行删除。"
                : "This route already exists on the computer and is display-only; it was not deleted.");
            return;
        }
        try
        {
            var applied = _appliedStaticRoutes.Where(x => x.RuleId.Equals(rule.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (applied.Count > 0)
            {
                SetBusy(true, "Removing route... / 正在删除路由...");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                foreach (var route in applied)
                {
                    var adapter = ResolveAdapterForRoute(route);
                    if (adapter == null) throw new InvalidOperationException("The original adapter is unavailable / 原网卡当前不可用");
                    await _routeService.RemoveAsync(route, adapter);
                    _appliedStaticRoutes.Remove(route);
                }
                SaveStaticRouteSession();
            }
            StaticRoutes.Remove(rule);
            RouteGrid.SelectedItem = null;
        }
        catch (Exception ex)
        {
            _logger.Error("Remove static route failed", ex);
            AppDialog.Show(this, _lang.T("remove.route"), ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private List<StaticRouteTarget> BuildStaticRouteTargets()
    {
        var editableRoutes = StaticRoutes.Where(x => !x.IsExistingRoute).ToList();
        if (editableRoutes.Count == 0) throw new InvalidOperationException("Add at least one route / 请至少新增一条路由");
        var targets = new List<StaticRouteTarget>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in editableRoutes)
        {
            var adapter = ResolveAdapterForRule(rule) ?? throw new InvalidOperationException($"Adapter not found for route {rule.DestinationPrefix} / 路由未找到对应网卡：{rule.DestinationPrefix}");
            var normalized = StaticRouteValidator.Normalize(rule);
            var key = $"{normalized.DestinationPrefix}|{adapter.InterfaceIndex}|{normalized.NextHop}";
            if (!keys.Add(key)) throw new InvalidOperationException($"Duplicate route target: {normalized.DestinationPrefix} on {adapter.Name} / 重复路由目标：{normalized.DestinationPrefix} 指向 {adapter.Name}");
            rule.DestinationPrefix = normalized.DestinationPrefix;
            rule.NextHop = normalized.NextHop == "0.0.0.0" ? "" : normalized.NextHop;
            rule.AdapterId = adapter.Id;
            rule.AdapterName = adapter.Name;
            rule.AdapterMac = adapter.MacAddress;
            targets.Add(new StaticRouteTarget(rule, adapter, normalized));
        }
        return targets;
    }

    private NetworkAdapterInfo? ResolveAdapterForRule(StaticRouteRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.AdapterId))
        {
            var byId = Adapters.FirstOrDefault(x => x.Id.Equals(rule.AdapterId, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(rule.AdapterMac))
        {
            var byMac = Adapters.FirstOrDefault(x => x.MacAddress.Equals(rule.AdapterMac, StringComparison.OrdinalIgnoreCase));
            if (byMac != null) return byMac;
        }
        return Adapters.FirstOrDefault(x => x.Name.Equals(rule.AdapterName, StringComparison.OrdinalIgnoreCase));
    }

    private NetworkAdapterInfo? ResolveAdapterForRoute(AppliedStaticRoute route)
    {
        var adapter = !string.IsNullOrWhiteSpace(route.AdapterId)
            ? Adapters.FirstOrDefault(x => x.Id.Equals(route.AdapterId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (adapter != null && (string.IsNullOrWhiteSpace(route.AdapterMac) || adapter.MacAddress.Equals(route.AdapterMac, StringComparison.OrdinalIgnoreCase))) return adapter;
        adapter = Adapters.FirstOrDefault(x => x.InterfaceIndex == route.InterfaceIndex.ToString() && (string.IsNullOrWhiteSpace(route.AdapterMac) || x.MacAddress.Equals(route.AdapterMac, StringComparison.OrdinalIgnoreCase)));
        return adapter;
    }

    private void RefreshRouteAdapterMetadata()
    {
        foreach (var rule in StaticRoutes)
        {
            var adapter = ResolveAdapterForRule(rule);
            if (adapter == null)
            {
                rule.Status = "Adapter unavailable / 网卡不可用";
                continue;
            }
            rule.AdapterId = adapter.Id;
            rule.AdapterName = adapter.Name;
            rule.AdapterMac = adapter.MacAddress;
        }
        RouteGrid?.Items.Refresh();
    }

    private async void RestoreScan_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAdapter == null) return;
        try
        {
            SetBusy(true, "Restoring adapter... / 正在恢复网卡...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await RestoreOriginalAdapterConfigAsync(SelectedAdapter);
            await RefreshAdaptersAsync();
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
        HelpButtonService.Attach(add, "help.template.favorite.add");
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
            var imported = FavoriteStore.Load(dialog.FileName, _logger)
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
            FavoriteStore.SaveCredentialFreeExport(dialog.FileName, _allFavorites);
            _logger.Info("Favorites exported: " + dialog.FileName);
            AppDialog.Show(this, "Export / 导出", "导出完成（未包含密码）。\n" + dialog.FileName);
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

    private void OpenFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav || !IPAddress.TryParse(fav.TargetIp, out _)) return;
        OpenUrl($"{(fav.PreferHttps ? "https" : "http")}://{fav.TargetIp}");
        _logger.Info($"Favorite web opened: {fav.Name} https={fav.PreferHttps}");
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
        HelpButtonService.Attach(close, "help.favorite.columns.close");
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

    private void UpdateAdapterActionButtons()
    {
        if (!IsInitialized) return;
        var enabled = SelectedAdapter != null && !_adapterActionInProgress && !_closingCleanupStarted && !_dhcpServer.IsRunning;
        BtnRestartAdapter.IsEnabled = enabled;
        BtnChangeMac.IsEnabled = enabled;
        BtnRestartAdapter.Background = enabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnChangeMac.Background = enabled ? Brushes.LightBlue : Brushes.LightGray;
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
        BtnOpenFavorite.IsEnabled = selected && !_scanRunning && FavoriteGrid.SelectedItem is FavoriteConfig openFavorite && IPAddress.TryParse(openFavorite.TargetIp, out _);
        BtnNewFavorite.Background = BtnNewFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnImportFavorite.Background = BtnImportFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnExportFavorite.Background = BtnExportFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnFavoriteColumns.Background = BtnFavoriteColumns.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnLoadFavorite.Background = BtnLoadFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnApplyFavorite.Background = BtnApplyFavorite.IsEnabled ? Brushes.LightGreen : Brushes.LightGray;
        BtnDeleteFavorite.Background = BtnDeleteFavorite.IsEnabled ? Brushes.MistyRose : Brushes.LightGray;
        BtnOpenFavorite.Background = BtnOpenFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
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
        UpdateAdapterActionButtons();
    }

    private async void RefreshAdapters_Click(object sender, RoutedEventArgs e) => await RefreshAdaptersAsync();

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
                Addresses = backup.Addresses,
                Routes = backup.Routes,
                IpAddress = backup.IpAddress,
                PrefixLength = backup.PrefixLength,
                Gateway = backup.Gateway,
                Dns = backup.Dns,
                AutomaticMetric = backup.AutomaticMetric,
                InterfaceMetric = backup.InterfaceMetric
            });
            await RefreshAdaptersAsync();
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

    private async void ExportResults_Click(object sender, RoutedEventArgs e)
    {
        var rows = Tabs.SelectedItem == TabDhcp
            ? Leases.Select(x => new[] { x.Time.ToString(), x.MacAddress, x.IpAddress, x.Hostname, x.Status, x.PingLatencyMs.ToString(), x.Remark })
            : ScanResults.Select(x => new[] { x.LastSeen.ToString(), x.IpAddress, x.MacAddress, x.Hostname, x.StatusText, x.LatencyMs.ToString(), x.Remark });
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"NetBoot-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SetBusy(true, "Exporting results... / 正在导出结果...");
            var header = Tabs.SelectedItem == TabDhcp
                ? (IsChineseUi() ? new[] { "时间", "MAC", "IP", "主机名", "状态", "Ping毫秒", "备注" } : new[] { "Time", "MAC", "IP", "Hostname", "Status", "PingMs", "Remark" })
                : (IsChineseUi() ? new[] { "最后发现", "IP", "MAC", "主机名", "状态", "Ping毫秒", "备注" } : new[] { "LastSeen", "IP", "MAC", "Hostname", "Status", "PingMs", "Remark" });
            await Task.Run(() => File.WriteAllLines(dialog.FileName, new[] { Csv(header) }.Concat(rows.Select(Csv)), new UTF8Encoding(true)));
            _logger.Info("Results exported: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            _logger.Error("Export results failed", ex);
            AppDialog.Show(this, "Export / 导出", ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string Csv(IEnumerable<string> values) => string.Join(",", values.Select(x => "\"" + (x ?? "").Replace("\"", "\"\"") + "\""));

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var adapter = SelectedAdapter;
        if (adapter == null) return;
        AppDialog.Show(this, "Adapter Diagnostics / 网卡诊断", $"Name / 名称: {adapter.Name}\nStatus / 状态: {adapter.Status}\nIP: {adapter.IPv4Address}\nMask / 掩码: {adapter.SubnetMask}\nGateway / 网关: {adapter.Gateway}\nDNS: {adapter.Dns}\nMAC: {adapter.MacAddress}\nWi-Fi: {adapter.IsWifi}\nVirtual / 虚拟: {adapter.IsVirtual}");
    }

    private async void PackageLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "ZIP (*.zip)|*.zip", FileName = $"NetBoot-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SetBusy(true, "Creating support package... / 正在生成支持包...");
            await Task.Run(() =>
            {
                using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log")) archive.CreateEntryFromFile(file, Path.Combine("logs", Path.GetFileName(file)));
                if (File.Exists(_paths.SettingsFile)) archive.CreateEntryFromFile(_paths.SettingsFile, "appsettings.json");
                if (File.Exists(_paths.AdapterBackupsFile)) archive.CreateEntryFromFile(_paths.AdapterBackupsFile, "adapter-backups.json");
            });
            _logger.Info("Support package created: " + dialog.FileName);
        }
        catch (Exception ex)
        {
            _logger.Error("Support package failed", ex);
            AppDialog.Show(this, "Support / 支持包", ex.Message, danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _darkTheme = !_darkTheme;
        RootGrid.Background = _darkTheme ? new SolidColorBrush(Color.FromRgb(31, 35, 40)) : Brushes.White;
        Foreground = _darkTheme ? Brushes.Gainsboro : Brushes.Black;
        BtnTheme.Content = _darkTheme ? _lang.T("theme.light") : _lang.T("theme");
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
        AppDialog.Show(this, _lang.T("about"), $"NetBoot DHCP Tool v{CurrentVersionText}\n{_lang.T("about.details")}");
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
        _settings.AllowRestartOnAnyAdapter = AllowRestartAnyAdapter.IsChecked == true;
        _settings.AllowMacChangeOnAnyAdapter = AllowMacChangeAnyAdapter.IsChecked == true;
        _settings.RestoreIpOnDhcpStop = RestoreOnStop.IsChecked == true;
        JsonStore.Save(_paths.SettingsFile, _settings);
        _logger.Info($"Settings saved: dhcpWifi={_settings.AllowDhcpOnWifi} dhcpGateway={_settings.AllowDhcpOnAdapterWithGateway} detectExistingDhcp={_settings.DetectExistingDhcpBeforeStart} restartAnyAdapter={_settings.AllowRestartOnAnyAdapter} macAnyAdapter={_settings.AllowMacChangeOnAnyAdapter} restoreIp={_settings.RestoreIpOnDhcpStop}");
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
        RouteGrid.IsEnabled = !running;
        BtnAddRoute.IsEnabled = !running;
        BtnRemoveRoute.IsEnabled = !running;
        BtnApplyRoutes.IsEnabled = !running;
        BtnClearAppliedRoutes.IsEnabled = !running;
        UpdateAdapterActionButtons();
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
            await RemoveDhcpFirewallRulesAsync();
            await ClearAppliedRoutesAsync();
            StaticRoutes.Clear();
            if (restoreAdapter)
            {
                var adapter = GetDhcpRestoreAdapter();
                if (adapter != null)
                {
                    await RestoreOriginalAdapterConfigAsync(adapter);
                }
            }
            await RestoreOriginalMacAddressesAsync();
            _activeDhcpAdapterIndex = null;
            _logger.Info("Window closing cleanup completed");
        }
        catch (Exception ex)
        {
            _logger.Error("Window closing cleanup failed", ex);
        }
    }

    private async Task RestoreOriginalMacAddressesAsync()
    {
        var pending = _originalAdapterMacs.Values.Where(x => x.RestoreOnExit).ToList();
        if (pending.Count == 0)
        {
            _logger.Info("No adapter MAC restoration required on normal exit");
            return;
        }

        var available = Adapters.ToList();
        foreach (var backup in pending)
        {
            var adapter = available.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(backup.AdapterId) && x.Id.Equals(backup.AdapterId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(backup.InterfaceIndex) && x.InterfaceIndex.Equals(backup.InterfaceIndex, StringComparison.OrdinalIgnoreCase)));
            if (adapter == null)
            {
                _logger.Warn($"MAC restoration skipped: adapter unavailable idx={backup.InterfaceIndex} name={backup.AdapterName}");
                continue;
            }
            try
            {
                await _adapterService.RestoreMacAddressAsync(adapter, backup.OriginalMacAddress);
                _logger.Info($"Adapter MAC restored on normal exit: idx={backup.InterfaceIndex} name={backup.AdapterName} mac={backup.OriginalMacAddress}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Adapter MAC restoration failed: idx={backup.InterfaceIndex} name={backup.AdapterName}", ex);
            }
        }
    }

    private async Task RemoveDhcpFirewallRulesAsync()
    {
        var lease = _dhcpFirewallRules;
        _dhcpFirewallRules = null;
        if (lease == null || lease.CreatedRuleNames.Count == 0) return;
        try
        {
            await _adapterService.RemoveDhcpFirewallRulesAsync(lease);
        }
        catch (Exception ex)
        {
            _logger.Error("Remove DHCP firewall rules failed", ex);
        }
    }

    private async Task<bool> ClearAppliedRoutesAsync()
    {
        if (_appliedStaticRoutes.Count == 0)
        {
            SaveStaticRouteSession();
            UpdateRouteStatuses();
            return true;
        }

        var remaining = new List<AppliedStaticRoute>();
        foreach (var route in _appliedStaticRoutes.ToList())
        {
            var adapter = ResolveAdapterForRoute(route);
            if (adapter == null)
            {
                remaining.Add(route);
                _logger.Warn($"Static route cleanup skipped: adapter unavailable idx={route.InterfaceIndex} destination={route.DestinationPrefix}");
                continue;
            }
            try
            {
                await _routeService.RemoveAsync(route, adapter);
                _logger.Info($"Static route removed: {route.DestinationPrefix} adapter={adapter.Name}");
            }
            catch (Exception ex)
            {
                remaining.Add(route);
                _logger.Error($"Static route cleanup failed: {route.DestinationPrefix} adapter={adapter.Name}", ex);
            }
        }

        _appliedStaticRoutes.Clear();
        _appliedStaticRoutes.AddRange(remaining);
        SaveStaticRouteSession();
        UpdateRouteStatuses();
        return remaining.Count == 0;
    }

    private void UpdateRouteStatuses()
    {
        foreach (var rule in StaticRoutes)
        {
            rule.Status = _appliedStaticRoutes.Any(x => x.RuleId.Equals(rule.Id, StringComparison.OrdinalIgnoreCase))
                ? "Applied / 已应用"
                : "Not applied / 未应用";
        }
        RouteGrid?.Items.Refresh();
    }

    private void SaveStaticRouteSession()
    {
        try
        {
            if (_appliedStaticRoutes.Count == 0)
            {
                if (File.Exists(_paths.StaticRouteSessionFile)) File.Delete(_paths.StaticRouteSessionFile);
                return;
            }
            JsonStore.Save(_paths.StaticRouteSessionFile, _appliedStaticRoutes);
        }
        catch (Exception ex)
        {
            _logger.Error("Save static route session failed", ex);
        }
    }

    private async Task RecoverStaleRoutesAsync()
    {
        if (!File.Exists(_paths.StaticRouteSessionFile)) return;
        var staleRoutes = JsonStore.LoadOrDefault(_paths.StaticRouteSessionFile, new List<AppliedStaticRoute>(), _logger);
        if (staleRoutes.Count == 0)
        {
            SaveStaticRouteSession();
            return;
        }

        _appliedStaticRoutes.Clear();
        _appliedStaticRoutes.AddRange(staleRoutes);
        var summary = string.Join(Environment.NewLine, staleRoutes.Select(x => $"{x.DestinationPrefix} -> {x.AdapterName} ({x.NextHop})"));
        _logger.Warn($"Stale static route session detected: count={staleRoutes.Count}");
        if (!AppDialog.Show(this,
            IsChineseUi() ? "发现上次未清理的静态路由" : "Stale Static Routes Found",
            summary + Environment.NewLine + Environment.NewLine + (IsChineseUi() ? "是否立即清理？" : "Clean them up now?"),
            confirm: true,
            danger: true)) return;

        try
        {
            SetBusy(true, "Cleaning stale routes... / 正在清理上次残留路由...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (!await ClearAppliedRoutesAsync())
            {
                AppDialog.Show(this,
                    IsChineseUi() ? "清理残留路由" : "Clean Stale Routes",
                    IsChineseUi() ? "部分残留路由未能清理，详细原因已写入日志。" : "Some stale routes could not be cleaned. See the log for details.",
                    danger: true);
            }
        }
        finally
        {
            SetBusy(false);
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

    private void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        if (_lastUpdateResult is not { Succeeded: true, IsNewVersion: true } result || string.IsNullOrWhiteSpace(result.DownloadUrl)) return;
        var changes = result.Changes.Count > 0
            ? string.Join(Environment.NewLine, result.Changes.Select(x => "• " + x))
            : result.ReleaseNotes;
        if (string.IsNullOrWhiteSpace(changes)) changes = IsChineseUi() ? "发布方未提供详细更新内容。" : "The publisher did not provide detailed release notes.";
        var message = IsChineseUi()
            ? $"版本 v{result.LatestVersion}\n\n更新内容：\n{changes}\n\n确认后将在后台下载更新包，不会自动安装。下载文件会保存到“下载”文件夹。"
            : $"Version v{result.LatestVersion}\n\nChanges:\n{changes}\n\nAfter confirmation the package will download in the background and will not be installed automatically. It will be saved to your Downloads folder.";
        if (!AppDialog.Show(this, IsChineseUi() ? "发现新版本" : "New Version", message, confirm: true)) return;
        if (_updateDownloadInProgress)
        {
            AppDialog.Show(this, IsChineseUi() ? "更新下载" : "Update Download", IsChineseUi() ? "更新已在后台下载中。" : "The update is already downloading.");
            return;
        }
        _ = DownloadUpdateAsync(result);
    }

    private async Task DownloadUpdateAsync(UpdateCheckResult result)
    {
        _updateDownloadInProgress = true;
        _updateSlowWarningShown = false;
        var fileName = string.IsNullOrWhiteSpace(result.ArchiveName) ? $"NetBootDhcpTool-v{result.LatestVersion}.7z" : Path.GetFileName(result.ArchiveName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"NetBootDhcpTool-v{result.LatestVersion}.7z";
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var destination = Path.Combine(downloads, fileName);
        if (File.Exists(destination)) destination = Path.Combine(downloads, Path.GetFileNameWithoutExtension(fileName) + $"-{DateTime.Now:yyyyMMdd-HHmmss}" + Path.GetExtension(fileName));
        _logger.Info($"Update background download started: version={result.LatestVersion} destination={destination}");
        TxtUpdateStatus.Text = IsChineseUi() ? "正在后台下载更新..." : "Downloading update in background...";
        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            var speed = p.BytesPerSecond / 1024d;
            var size = p.TotalBytes.HasValue ? $"{p.BytesReceived / 1024d:0.0}/{p.TotalBytes.Value / 1024d:0.0} KB" : $"{p.BytesReceived / 1024d:0.0} KB";
            TxtUpdateStatus.Text = IsChineseUi()
                ? $"后台下载 {size}，速度 {speed:0.0} KB/s"
                : $"Background download {size}, {speed:0.0} KB/s";
            if (!_updateSlowWarningShown && p.LowSpeedDuration >= TimeSpan.FromSeconds(10))
            {
                _updateSlowWarningShown = true;
                _logger.Warn($"Update download speed below 3 KB/s for {p.LowSpeedDuration.TotalSeconds:0} seconds");
                AppDialog.Show(this,
                    IsChineseUi() ? "下载速度过慢" : "Download Too Slow",
                    IsChineseUi() ? "下载速度连续 10 秒低于 3 KB/s，可以通过邮件向作者获取更新包：1406829360@qq.com" : "Download speed stayed below 3 KB/s for 10 seconds. You can email the author for the update package: 1406829360@qq.com",
                    danger: true);
            }
        });
        try
        {
            var downloaded = await Task.Run(() => _updateService.DownloadAsync(result, destination, progress, _updateDownloadCts.Token));
            _logger.Info($"Update background download completed: version={result.LatestVersion} path={downloaded.FilePath} sha256={downloaded.Sha256}");
            TxtUpdateStatus.Text = IsChineseUi() ? $"更新包已下载（未安装）：{downloaded.FilePath}" : $"Update downloaded (not installed): {downloaded.FilePath}";
        }
        catch (OperationCanceledException) when (_updateDownloadCts.IsCancellationRequested)
        {
            _logger.Info("Update background download canceled");
        }
        catch (Exception ex)
        {
            _logger.Error("Update background download failed", ex);
            TxtUpdateStatus.Text = IsChineseUi() ? "更新下载失败，请查看日志或联系作者" : "Update download failed; see logs or contact the author";
            AppDialog.Show(this, IsChineseUi() ? "更新下载失败" : "Update Download Failed", ex.Message + "\n\n1406829360@qq.com", danger: true);
        }
        finally
        {
            _updateDownloadInProgress = false;
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

    private void LogButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button button)
        {
            _logger.Info($"UI button clicked: name={button.Name} content={button.Content}");
        }
    }

    internal string GetHelpText(string helpKey)
    {
        var message = _lang.T(helpKey);
        return string.Equals(message, helpKey, StringComparison.Ordinal)
            ? (IsChineseUi() ? "暂无此功能的帮助说明。" : "No help text is available for this action.")
            : message;
    }

    internal void ShowHelp(string helpKey)
    {
        _logger.Info($"UI help opened: key={helpKey}");
        AppDialog.Show(this, _lang.T("help.title"), GetHelpText(helpKey));
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
        while (LogBox.Document.Blocks.Count > 500) LogBox.Document.Blocks.Remove(LogBox.Document.Blocks.FirstBlock);
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
            Addresses = snapshot.Addresses,
            Routes = snapshot.Routes,
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
            var latest = await Task.Run(() => _adapterService.GetAdapters(logAdapters: false).FirstOrDefault(x => x.InterfaceIndex == selectedIndex));
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
        HelpButtonService.Attach(copyButton, "help.favorite.fields.copy");
        HelpButtonService.Attach(closeButton, "help.favorite.fields.close");
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
            new() { Name = IsChineseUi() ? "密码" : "Password", Value = fav.PasswordDisplay },
            new() { Name = IsChineseUi() ? "网页协议" : "Web scheme", Value = fav.PreferHttps ? "HTTPS" : "HTTP" },
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
        HelpButtonService.Attach(copyButton, "help.favorite.details.copy");
        HelpButtonService.Attach(closeButton, "help.favorite.details.close");
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
        var started = Stopwatch.GetTimestamp();
        _logger.Info($"UI tab selection started: tab={selected.Name} routeRows={StaticRoutes.Count}");
        if (selected == TabRoutes)
        {
            Dispatcher.BeginInvoke(() => _logger.Info($"UI static route tab render completed: routeRows={StaticRoutes.Count} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}"), DispatcherPriority.ContextIdle);
        }
        if (selected != TabDhcp && selected != TabScan && selected != TabRoutes)
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
        SetHeaders(RouteGrid.Columns, zh,
            ["目标网段", "使用网卡", "网关", "跃点", "状态"],
            ["Destination", "Adapter", "Gateway", "Metric", "Status"]);
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
        target.PublicPassword = source.PublicPassword;
        target.Password = source.Password;
        target.ProtectedPassword = source.ProtectedPassword;
        target.IsPublicDefault = source.IsPublicDefault;
        target.PasswordUnavailable = source.PasswordUnavailable;
        target.PreferHttps = source.PreferHttps;
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
