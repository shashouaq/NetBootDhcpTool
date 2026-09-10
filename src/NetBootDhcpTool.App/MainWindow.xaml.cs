using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.IO.Compression;
using System.Text;
using System.Media;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
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
    private List<AdapterMacBackup> _savedMacBackups = [];
    private List<OperationHistoryItem> _operationHistory = [];
    private List<NetworkProfile> _allProfiles = [];
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _leaseHintCts;
    private readonly DispatcherTimer _leasePingTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _adapterStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(6) };
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
    private bool _adapterStatusRefreshInProgress;
    private bool _adapterRefreshInProgress;
    private bool _adapterActionInProgress;
    private bool _updateDownloadInProgress;
    private bool _updateSlowWarningShown;
    private bool _darkTheme;
    private bool _safetyOnboardingCompleted;
    private bool _settingsInputsLoading;
    private bool _settingsDirty;
    private string? _businessConfigurationFingerprint;
    private DhcpFirewallRuleLease? _dhcpFirewallRules;
    private UpdateCheckResult? _lastUpdateResult;
    private bool _updateCheckStarted;
    private string? _activeOperationText;
    private CancellationTokenSource? _operationCts;
    private long _operationStartedTimestamp;
    private bool _suppressProfileSelection;
    private Stopwatch? _scanStopwatch;
    private DateTime? _scanLastHitAt;
    private string _scanPhase = "";
    private int _cleanupFailureCount;
    private bool _compactLayout;

    public ObservableCollection<NetworkAdapterInfo> Adapters => _viewModel.Adapters;
    public ObservableCollection<ScanResult> ScanResults => _viewModel.ScanResults;
    public ObservableCollection<FavoriteConfig> Favorites => _viewModel.Favorites;
    public ObservableCollection<DhcpLease> Leases => _viewModel.Leases;
    public ObservableCollection<AdapterIpHistoryItem> AdapterIpHistory => _viewModel.AdapterIpHistory;
    public ObservableCollection<StaticRouteRule> StaticRoutes => _viewModel.StaticRoutes;
    public ObservableCollection<StaticRouteRule> CurrentStaticRoutes => _viewModel.CurrentStaticRoutes;
    public ObservableCollection<OperationHistoryItem> OperationHistory => _viewModel.OperationHistory;
    public ObservableCollection<NetworkProfile> Profiles => _viewModel.Profiles;

    public MainWindow()
    {
        InitializeComponent();
        ResizeMode = ResizeMode.CanResizeWithGrip;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SizeChanged += MainWindow_SizeChanged;
        KeyDown += MainWindow_KeyDown;
        AddHandler(Button.ClickEvent, new RoutedEventHandler(LogButtonClick), true);
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        CollectionViewSource.GetDefaultView(ScanResults).Filter = ScanResultFilter;
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
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            if (TxtActionFeedback != null) TxtActionFeedback.Text = "";
        };
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        ApplyLanguage();
        LoadDefaults();
        LoadFavorites();
        LoadNetworkHistory();
        _adapterBackups = JsonStore.LoadOrDefault(_paths.AdapterBackupsFile, new List<AdapterConfigBackup>(), _logger);
        LoadMacBackups();
        _operationHistory = JsonStore.LoadOrDefault(_paths.OperationHistoryFile, new List<OperationHistoryItem>(), _logger);
        foreach (var item in _operationHistory.OrderByDescending(x => x.Time).Take(500)) OperationHistory.Add(item);
        _allProfiles = ProfileStore.Load(_paths.ProfilesFile, _logger);
        foreach (var profile in _allProfiles.OrderByDescending(x => x.UpdatedAt)) Profiles.Add(profile);
        SetDhcpRunningState(false);
        UpdateAdapterActionButtons();
        UpdateManualScanButtons();
        UpdateFavoriteButtons();
        UpdateProfileButtons();
        UpdateHistorySummary();
        UpdateLastOperationPresentation();
        UpdateRecoveryBanner();
        ChkLogAutoScroll.IsChecked = _settings.LogAutoScroll;
        SetLogPanelExpanded(_settings.LogPanelExpanded);
        UpdateEmptyStates();
        UpdateSessionStatus();
        _logger.Info("MainWindow ready");
        Dispatcher.BeginInvoke(() =>
        {
            SetBusy(false);
            if (!_safetyOnboardingCompleted) ShowSafetyGuide(firstRun: true);
            _ = InitializeNetworkStateAsync();
            _ = CheckForUpdatesAsync();
        }, DispatcherPriority.ApplicationIdle);
    }

    private async Task InitializeNetworkStateAsync()
    {
        await RefreshAdaptersAsync();
        await RecoverStaleRoutesAsync();
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAfterCleanup)
        {
            base.OnClosing(e);
            return;
        }
        if (_closingCleanupStarted)
        {
            e.Cancel = true;
            return;
        }

        if (_settingsDirty && !AppDialog.Show(this,
            IsChineseUi() ? "未保存设置" : "Unsaved Settings",
            IsChineseUi()
                ? "设置页存在尚未保存的更改。关闭后这些更改会丢失，仍要退出吗？"
                : "The Settings page has unsaved changes. They will be lost when the application closes. Exit anyway?",
            confirm: true))
        {
            e.Cancel = true;
            return;
        }

        SaveWindowLayout();
        e.Cancel = true;
        _closingCleanupStarted = true;
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        _adapterStatusTimer.Stop();
        _feedbackTimer.Stop();
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
        BtnSafetyGuide.Content = _lang.T("safety.guide");
        BtnRecovery.Content = _lang.T("recovery.title");
        BtnOpenRecovery.Content = _lang.T("recovery.title");
        BtnOpenHistory.Content = _lang.T("history");
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
        TabProfiles.Header = _lang.T("profiles");
        TabHistory.Header = _lang.T("history");
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
        BtnPreviewDhcp.Content = _lang.T("preview.dhcp");
        BtnShowDhcpGateway.Content = _lang.T("show.gateway");
        BtnShowDhcpDns.Content = _lang.T("show.dns");
        DhcpConfirm.Content = _lang.T("isolated.confirm");
        RestoreOnStop.Content = _lang.T("restore.stop");
        LblManualIp.Text = _lang.T("server.ip");
        LblManualMask.Text = _lang.T("subnet.mask");
        LblManualTargetIp.Text = _lang.T("target.ip");
        BtnApplyScan.Content = _lang.T("apply.scan");
        BtnPreviewScan.Content = _lang.T("preview.scan");
        BtnStopScan.Content = _lang.T("stop.scan");
        BtnRestoreScan.Content = _lang.T("restore.scan");
        BtnAddRoute.Content = _lang.T("add.route");
        BtnRemoveRoute.Content = _lang.T("remove.route");
        BtnPreviewRoutes.Content = _lang.T("preview.routes");
        BtnApplyRoutes.Content = _lang.T("apply.routes");
        BtnClearAppliedRoutes.Content = _lang.T("clear.applied.routes");
        RouteHint.Text = _lang.T("route.hint");
        RouteRulesTab.Header = _lang.T("route.rules");
        CurrentRoutesTab.Header = _lang.T("route.current");
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
        BtnCopyScan.Content = _lang.T("copy.scan");
        BtnClearScanFilter.Content = _lang.T("filter.clear");
        BtnClearFavoriteSearch.Content = _lang.T("filter.clear");
        BtnClearHistoryFilter.Content = _lang.T("filter.clear");
        LblHistorySearch.Text = _lang.T("history.search");
        BtnCopyHistory.Content = _lang.T("copy.history");
        BtnRollbackHistory.Content = _lang.T("rollback");
        BtnClearHistory.Content = _lang.T("history.clear");
        BtnSaveProfile.Content = _lang.T("profile.save.current");
        BtnLoadProfile.Content = _lang.T("profile.load");
        BtnCompareProfile.Content = _lang.T("profile.compare");
        BtnDuplicateProfile.Content = _lang.T("profile.duplicate");
        BtnImportProfile.Content = _lang.T("profile.import");
        BtnExportProfile.Content = _lang.T("profile.export");
        BtnDeleteProfile.Content = _lang.T("profile.delete");
        BtnCancelOperation.Content = _lang.T("cancel");
        AllowWifi.Content = _lang.T("allow.wifi");
        AllowGateway.Content = _lang.T("allow.gateway");
        DetectExistingDhcp.Content = _lang.T("detect.existing.dhcp");
        AllowRestartAnyAdapter.Content = _lang.T("allow.restart.any");
        AllowMacChangeAnyAdapter.Content = _lang.T("allow.mac.any");
        RedactSupportPackage.Content = _lang.T("support.redact");
        BtnSaveSettings.Content = _lang.T("save");
        UpdateSettingsStatus();
        DhcpHint.Text = _lang.T("dhcp.client.hint");
        BtnFillDhcpRange.Content = _lang.T("fill.dhcp.range");
        BtnFillTargetIp.Content = _lang.T("fill.target.ip");
        BtnToggleLog.Content = _lang.T("show.log");
        ChkLogAutoScroll.Content = _lang.T("log.auto.scroll");
        TxtSessionStatus.ToolTip = _lang.T("help.session.status");
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
        ScanFilterBox.ToolTip = _lang.T("help.scan.filter");
        HistoryFilterBox.ToolTip = _lang.T("help.history.search");
        BtnSafetyGuide.ToolTip = _lang.T("help.safety.guide");
        DhcpConfirm.ToolTip = _lang.T("help.confirm.isolated");
        RestoreOnStop.ToolTip = _lang.T("help.restore.stop");
        LeaseGrid.ToolTip = _lang.T("help.lease.grid");
        ScanGrid.ToolTip = _lang.T("help.scan.grid");
        RouteGrid.ToolTip = _lang.T("help.route.grid");
        CurrentRouteGrid.ToolTip = _lang.T("help.route.current.grid");
        FavoriteGrid.ToolTip = _lang.T("help.favorite.grid");
        TabDhcp.ToolTip = _lang.T("help.tab.dhcp");
        TabScan.ToolTip = _lang.T("help.tab.scan");
        TabRoutes.ToolTip = _lang.T("help.tab.routes");
        TabFavorites.ToolTip = _lang.T("help.tab.favorites");
        TabProfiles.ToolTip = _lang.T("help.profile.load");
        TabHistory.ToolTip = _lang.T("help.history.copy");
        TabSettings.ToolTip = _lang.T("help.tab.settings");
        ApplyGridHeaders();
        UpdateManualScanButtons();
        UpdateFavoriteButtons();
        UpdateVersionPresentation();
        UpdateDhcpInputValidation();
        UpdateSessionStatus();
        UpdateProfileSummary();
        UpdateHistorySummary();
        UpdateLastOperationPresentation();
        UpdateRecoveryBanner();
        UpdateFilterButtons();
        UpdateSelectionHints();
        UpdateEmptyStates();
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
        _settingsInputsLoading = true;
        try
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
            RestoreOnStop.IsChecked = _settings.RestoreIpOnDhcpStop;
            AllowWifi.IsChecked = _settings.AllowDhcpOnWifi;
            AllowGateway.IsChecked = _settings.AllowDhcpOnAdapterWithGateway;
            DetectExistingDhcp.IsChecked = _settings.DetectExistingDhcpBeforeStart;
            AllowRestartAnyAdapter.IsChecked = _settings.AllowRestartOnAnyAdapter;
            AllowMacChangeAnyAdapter.IsChecked = _settings.AllowMacChangeOnAnyAdapter;
            RedactSupportPackage.IsChecked = _settings.RedactSupportPackage;
            _safetyOnboardingCompleted = _settings.SafetyOnboardingCompleted;
            _darkTheme = _settings.DarkTheme;
            ApplyTheme();
            SelectLanguageBox(_settings.Language);
            RestoreWindowLayout();
        }
        finally
        {
            _settingsInputsLoading = false;
            _settingsDirty = false;
            UpdateSettingsStatus();
        }
    }

    private void RestoreWindowLayout()
    {
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(MinWidth, workArea.Width);
        var maxHeight = Math.Max(MinHeight, workArea.Height);
        Width = Math.Clamp(_settings.WindowWidth, MinWidth, maxWidth);
        Height = Math.Clamp(_settings.WindowHeight, MinHeight, maxHeight);

        if (_settings.WindowLeft is double left && _settings.WindowTop is double top
            && !double.IsNaN(left) && !double.IsInfinity(left)
            && !double.IsNaN(top) && !double.IsInfinity(top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var maxLeft = Math.Max(workArea.Left, workArea.Right - Math.Min(Width, workArea.Width));
            var maxTop = Math.Max(workArea.Top, workArea.Bottom - Math.Min(Height, workArea.Height));
            Left = Math.Clamp(left, workArea.Left, maxLeft);
            Top = Math.Clamp(top, workArea.Top, maxTop);
        }

        var rememberedTab = Tabs.Items.OfType<TabItem>().FirstOrDefault(x =>
            x.Name.Equals(_settings.LastTab, StringComparison.OrdinalIgnoreCase));
        if (rememberedTab != null) Tabs.SelectedItem = rememberedTab;
        if (string.Equals(_settings.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase)) WindowState = WindowState.Maximized;
    }

    private void SaveWindowLayout()
    {
        var bounds = WindowState == WindowState.Normal && Width > 0 && Height > 0
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.Width > 0 && bounds.Height > 0 && !double.IsNaN(bounds.Width) && !double.IsNaN(bounds.Height))
        {
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
        }
        _settings.WindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
        _settings.LogPanelExpanded = LogBox.Visibility == Visibility.Visible;
        _settings.LogAutoScroll = ChkLogAutoScroll.IsChecked != false;
        if (Tabs.SelectedItem is TabItem selected && !string.IsNullOrWhiteSpace(selected.Name)) _settings.LastTab = selected.Name;
        JsonStore.Save(_paths.SettingsFile, _settings);
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
            AppDialog.Show(this, _lang.T("refresh"), ExplainFailure(ex, _lang.T("help.refresh")), danger: true);
        }
        finally
        {
            _adapterRefreshInProgress = false;
            SetBusy(false);
            UpdateAdapterActionButtons();
            UpdateSessionStatus();
            if (!_closingCleanupStarted) _adapterStatusTimer.Start();
        }
    }

    private async Task LoadCurrentStaticRoutesAsync(IReadOnlyList<NetworkAdapterInfo> adapters, CancellationToken ct = default)
    {
        try
        {
            var adapterByIndex = adapters
                .Where(x => !string.IsNullOrWhiteSpace(x.InterfaceIndex))
                .ToDictionary(x => x.InterfaceIndex, StringComparer.OrdinalIgnoreCase);
            CurrentStaticRoutes.Clear();
            var routes = await _routeService.GetCurrentStaticRoutesAsync(ct);
            foreach (var route in routes)
            {
                adapterByIndex.TryGetValue(route.InterfaceIndex, out var adapter);
                CurrentStaticRoutes.Add(new StaticRouteRule
                {
                    DestinationPrefix = route.DestinationPrefix,
                    AddressFamily = route.AddressFamily,
                    AdapterId = adapter?.Id ?? "",
                    AdapterName = adapter?.Name ?? $"Interface {route.InterfaceIndex} / 未枚举接口",
                    AdapterMac = adapter?.MacAddress ?? "",
                    NextHop = IsBlankNextHop(route.NextHop, route.AddressFamily) ? "" : route.NextHop,
                    RouteMetric = route.RouteMetric,
                    InterfaceMetric = route.InterfaceMetric,
                    Status = BuildCurrentRouteStatus(route)
                });
            }
            CurrentRouteGrid?.Items.Refresh();
            UpdateEmptyStates();
            _logger.Info($"Current static routes loaded: count={routes.Count}");
        }
        catch (Exception ex)
        {
            _logger.Warn("Read current static routes failed: " + ex.Message);
            UpdateEmptyStates();
        }
    }

    private string BuildCurrentRouteStatus(CurrentStaticRoute route)
    {
        var owned = _appliedStaticRoutes.Any(x =>
            !string.IsNullOrWhiteSpace(route.InstanceId)
            && x.InstanceId.Equals(route.InstanceId, StringComparison.OrdinalIgnoreCase));
        var source = owned ? "Tool-owned / 本工具创建" : "Existing / 系统已有";
        return $"Protocol={route.Protocol}; Store={route.PolicyStore}; Source={source}";
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

    private void LoadMacBackups()
    {
        _savedMacBackups = JsonStore.LoadOrDefault(_paths.MacBackupsFile, new List<AdapterMacBackup>(), _logger);
        _originalAdapterMacs.Clear();
        foreach (var backup in _savedMacBackups.Where(x => !string.IsNullOrWhiteSpace(x.OriginalMacAddress)))
        {
            _originalAdapterMacs[MacBackupKey(backup)] = backup;
        }
    }

    private void SaveMacBackups()
    {
        _savedMacBackups = _originalAdapterMacs.Values
            .Where(x => x.RestoreOnExit && !string.IsNullOrWhiteSpace(x.OriginalMacAddress))
            .OrderBy(x => x.AdapterName)
            .ToList();
        JsonStore.Save(_paths.MacBackupsFile, _savedMacBackups);
        UpdateRecoveryBanner();
    }

    private static string MacBackupKey(AdapterMacBackup backup) =>
        !string.IsNullOrWhiteSpace(backup.AdapterId) ? backup.AdapterId : backup.InterfaceIndex;

    private string Ui(string key, params object[] args)
    {
        var template = _lang.T(key);
        try { return args.Length == 0 ? template : string.Format(template, args); }
        catch (FormatException) { return template; }
    }

    private void AddOperationHistory(string type, string ip, string mac, string status, string detail, string? scope = null, long durationMs = 0, bool rollbackAvailable = false)
    {
        var measuredDuration = durationMs > 0 || _operationStartedTimestamp == 0
            ? durationMs
            : (long)Stopwatch.GetElapsedTime(_operationStartedTimestamp).TotalMilliseconds;
        var item = new OperationHistoryItem
        {
            Type = type,
            Scope = string.IsNullOrWhiteSpace(scope) ? (SelectedAdapter?.Name ?? "") : scope,
            IpAddress = ip,
            MacAddress = mac,
            Status = status,
            Detail = detail,
            DurationMs = measuredDuration,
            RollbackAvailable = rollbackAvailable
        };
        _operationHistory.Insert(0, item);
        while (_operationHistory.Count > 500) _operationHistory.RemoveAt(_operationHistory.Count - 1);
        JsonStore.Save(_paths.OperationHistoryFile, _operationHistory);
        RefreshOperationHistoryView();
        UpdateLastOperationPresentation();
        UpdateRecoveryBanner();
    }

    private void UpdateLastOperationPresentation()
    {
        if (TxtLastOperation == null) return;
        var latest = _operationHistory.OrderByDescending(x => x.Time).FirstOrDefault();
        BtnOpenHistory.IsEnabled = latest != null;
        TxtLastOperation.Text = latest == null
            ? Ui("last.operation.none")
            : Ui("last.operation", latest.Type, latest.Status, latest.Time.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private void UpdateRecoveryBanner()
    {
        if (RecoveryBanner == null) return;
        var routeCount = _appliedStaticRoutes.Count;
        var macCount = _originalAdapterMacs.Values.Count(x => x.RestoreOnExit);
        RecoveryBanner.Visibility = routeCount > 0 || macCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtRecoveryBanner.Text = routeCount > 0 || macCount > 0
            ? Ui("recovery.banner", routeCount + macCount, routeCount, macCount)
            : "";
        AutomationProperties.SetHelpText(RecoveryBanner, Ui("recovery.summary"));
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = TabHistory;
        HistoryFilterBox.Focus();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = ActualWidth > 0 && ActualWidth < 1160;
        if (compact == _compactLayout || AdapterSummaryGrid == null) return;
        _compactLayout = compact;
        AdapterSummaryGrid.ColumnDefinitions[2].Width = new GridLength(compact ? 145 : 180);
        AdapterSummaryGrid.ColumnDefinitions[8].Width = new GridLength(compact ? 120 : 145);
        TxtAdapterDetails.TextWrapping = compact ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _logger.Info($"Responsive layout changed: compact={compact} width={ActualWidth:0}");
    }

    private void OpenRecovery_Click(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Owner = this,
            Title = Ui("recovery.title"),
            Width = 620,
            Height = 420,
            MinWidth = 520,
            MinHeight = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Resources["WindowBackgroundBrush"] as Brush
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = Ui("recovery.close"), MinWidth = 90 };
        var restoreMac = new Button { Content = Ui("recovery.restore.mac"), MinWidth = 150, IsEnabled = _originalAdapterMacs.Values.Any(x => x.RestoreOnExit) };
        var restoreAdapter = new Button { Content = Ui("recovery.restore.adapter"), MinWidth = 150, IsEnabled = SelectedAdapter != null && _adapterBackups.Any(x => x.InterfaceIndex == SelectedAdapter.InterfaceIndex) };
        var restoreRoutes = new Button { Content = Ui("recovery.restore.routes"), MinWidth = 140, IsEnabled = _appliedStaticRoutes.Count > 0 };
        HelpButtonService.Attach(restoreRoutes, "help.recovery.center");
        HelpButtonService.Attach(restoreAdapter, "help.recovery.center");
        HelpButtonService.Attach(restoreMac, "help.recovery.center");
        HelpButtonService.Attach(close, "help.dialog.cancel");
        buttons.Children.Add(restoreRoutes);
        buttons.Children.Add(restoreAdapter);
        buttons.Children.Add(restoreMac);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var content = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        content.Children.Add(new TextBlock { Text = Ui("recovery.summary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        content.Children.Add(new TextBlock { Text = Ui("recovery.routes", _appliedStaticRoutes.Count), Margin = new Thickness(0, 4, 0, 4) });
        content.Children.Add(new TextBlock { Text = Ui("recovery.adapter", _adapterBackups.Count), Margin = new Thickness(0, 4, 0, 4) });
        content.Children.Add(new TextBlock { Text = Ui("recovery.mac", _originalAdapterMacs.Values.Count(x => x.RestoreOnExit)), Margin = new Thickness(0, 4, 0, 4) });
        var selected = SelectedAdapter;
        content.Children.Add(new TextBlock
        {
            Text = selected == null ? Ui("recovery.no.adapter") : (IsChineseUi() ? $"当前选中网卡：{selected.Name}" : $"Selected adapter: {selected.Name}"),
            Foreground = Resources["MutedTextBrush"] as Brush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0)
        });
        root.Children.Add(content);
        window.Content = root;
        close.Click += (_, _) => window.Close();
        restoreRoutes.Click += async (_, _) =>
        {
            if (!AppDialog.Show(this, Ui("recovery.restore.routes"), IsChineseUi() ? "将删除本次运行创建的静态路由。确认继续？" : "Static routes created in this run will be removed. Continue?", confirm: true, danger: true)) return;
            window.Close();
            try
            {
                SetBusy(true, IsChineseUi() ? "正在清理恢复中心路由..." : "Clearing Recovery Center routes...");
                if (!await ClearAppliedRoutesAsync(OperationToken)) AppDialog.Show(this, Ui("recovery.title"), IsChineseUi() ? "部分路由未能清理，请查看日志。" : "Some routes could not be cleared; see the log.", danger: true);
                else ShowActionFeedbackKey("recovery.restore.routes");
            }
            catch (Exception ex) { _logger.Error("Recovery Center route cleanup failed", ex); AppDialog.Show(this, Ui("recovery.title"), ExplainFailure(ex, _lang.T("help.recovery.center")), danger: true); }
            finally { SetBusy(false); UpdateRecoveryBanner(); }
        };
        restoreAdapter.Click += async (_, _) =>
        {
            if (selected == null) { AppDialog.Show(this, Ui("recovery.title"), Ui("recovery.no.adapter")); return; }
            var backup = _adapterBackups.LastOrDefault(x => x.InterfaceIndex == selected.InterfaceIndex);
            if (backup == null) { AppDialog.Show(this, Ui("recovery.title"), Ui("recovery.no.adapter")); return; }
            if (!AppDialog.Show(this, Ui("recovery.restore.adapter"), IsChineseUi() ? $"将恢复 {backup.AdapterName} 在 {backup.CapturedAt:yyyy-MM-dd HH:mm:ss} 保存的配置。确认继续？" : $"Restore the configuration saved for {backup.AdapterName} at {backup.CapturedAt:yyyy-MM-dd HH:mm:ss}?", confirm: true, danger: true)) return;
            window.Close();
            try
            {
                SetBusy(true, IsChineseUi() ? "正在恢复选中网卡..." : "Restoring selected adapter...");
                await RestoreAdapterBackupAsync(selected, backup, OperationToken);
                ShowActionFeedbackKey("recovery.restore.adapter");
            }
            catch (Exception ex) { _logger.Error("Recovery Center adapter restore failed", ex); AppDialog.Show(this, Ui("recovery.title"), ExplainFailure(ex, _lang.T("help.recovery.center")), danger: true); }
            finally { SetBusy(false); UpdateRecoveryBanner(); }
        };
        restoreMac.Click += async (_, _) =>
        {
            if (!AppDialog.Show(this, Ui("recovery.restore.mac"), IsChineseUi() ? "将恢复所有已记录且标记为退出时还原的 MAC。确认继续？" : "Restore all recorded MAC addresses marked for exit restoration. Continue?", confirm: true, danger: true)) return;
            window.Close();
            try
            {
                SetBusy(true, IsChineseUi() ? "正在恢复 MAC..." : "Restoring MAC addresses...");
                if (!await RestoreOriginalMacAddressesAsync(OperationToken)) AppDialog.Show(this, Ui("recovery.title"), IsChineseUi() ? "部分 MAC 未能恢复，请查看日志。" : "Some MAC addresses could not be restored; see the log.", danger: true);
                else ShowActionFeedbackKey("recovery.restore.mac");
                await RefreshAdaptersAsync();
            }
            catch (Exception ex) { _logger.Error("Recovery Center MAC restore failed", ex); AppDialog.Show(this, Ui("recovery.title"), ExplainFailure(ex, _lang.T("help.recovery.center")), danger: true); }
            finally { SetBusy(false); UpdateRecoveryBanner(); }
        };
        ApplyOwnedWindowTheme(window);
        window.ShowDialog();
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

    private void RefreshOperationHistoryView()
    {
        if (HistoryGrid == null) return;
        var q = HistoryFilterBox.Text?.Trim() ?? "";
        OperationHistory.Clear();
        foreach (var item in _operationHistory.Where(x => string.IsNullOrWhiteSpace(q)
            || x.Type.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.Scope.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.Status.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.IpAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.MacAddress.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            OperationHistory.Add(item);
        }
        UpdateHistorySummary();
    }

    private void UpdateHistorySummary()
    {
        if (TxtHistorySummary == null) return;
        TxtHistorySummary.Text = IsChineseUi()
            ? $"显示 {OperationHistory.Count}/{_operationHistory.Count} 条"
            : $"Showing {OperationHistory.Count}/{_operationHistory.Count}";
        UpdateEmptyStates();
    }

    private void UpdateEmptyStates()
    {
        if (TxtLeaseEmpty == null) return;

        SetEmptyState(
            TxtLeaseEmpty,
            LeaseGrid.Items.Count,
            Leases.Count,
            "暂无 DHCP 租约\n启动 DHCP 后等待客户端接入。",
            "No DHCP leases yet\nStart DHCP and wait for clients.");
        SetEmptyState(
            TxtScanEmpty,
            ScanGrid.Items.Count,
            ScanResults.Count,
            ScanResults.Count == 0 ? "尚未执行扫描\n填写范围后点击“预览扫描”。" : "当前筛选没有匹配结果\n清除筛选条件后重试。",
            ScanResults.Count == 0 ? "No scan results yet\nEnter a scope and choose Preview Scan." : "No results match the current filters\nClear the filters and try again.");
        SetEmptyState(
            TxtRouteEmpty,
            RouteGrid.Items.Count,
            StaticRoutes.Count,
            "暂无计划路由\n点击“新增路由”开始配置。",
            "No planned routes\nChoose Add Route to create one.");
        SetEmptyState(
            TxtCurrentRouteEmpty,
            CurrentRouteGrid.Items.Count,
            CurrentStaticRoutes.Count,
            "未发现当前系统静态路由。",
            "No current system static routes found.");
        SetEmptyState(
            TxtFavoriteEmpty,
            FavoriteGrid.Items.Count,
            _allFavorites.Count,
            _allFavorites.Count == 0 ? "暂无收藏配置\n可新增、导入或使用模板。" : "当前筛选没有匹配收藏配置\n清除搜索条件后重试。",
            _allFavorites.Count == 0 ? "No favorite configurations yet\nAdd, import, or use a template." : "No favorites match the current search\nClear the search and try again.");
        SetEmptyState(
            TxtProfileEmpty,
            ProfileGrid.Items.Count,
            _allProfiles.Count,
            "暂无网络配置方案\n可保存当前配置或导入方案。",
            "No network profiles yet\nSave the current setup or import profiles.");
        SetEmptyState(
            TxtHistoryEmpty,
            HistoryGrid.Items.Count,
            _operationHistory.Count,
            _operationHistory.Count == 0 ? "暂无操作历史。" : "当前筛选没有匹配历史记录。",
            _operationHistory.Count == 0 ? "No operation history yet." : "No history matches the current search.");
    }

    private void SetEmptyState(TextBlock target, int visibleCount, int totalCount, string chinese, string english)
    {
        target.Text = IsChineseUi() ? chinese : english;
        target.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(target, totalCount == 0
            ? (IsChineseUi() ? "列表为空" : "The list is empty")
            : (IsChineseUi() ? "当前筛选没有匹配项" : "No items match the current filters"));
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
        if (TxtFavoriteSummary != null)
        {
            TxtFavoriteSummary.Text = IsChineseUi()
                ? (string.IsNullOrWhiteSpace(q) ? $"共 {_allFavorites.Count} 条" : $"显示 {Favorites.Count}/{_allFavorites.Count} 条")
                : (string.IsNullOrWhiteSpace(q) ? $"{_allFavorites.Count} item(s)" : $"{Favorites.Count}/{_allFavorites.Count} item(s)");
        }
        UpdateEmptyStates();
    }

    private bool ScanResultFilter(object item)
    {
        if (item is not ScanResult result) return false;
        var q = ScanFilterBox?.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(q)
            && !result.IpAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            && !result.MacAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            && !result.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase)
            && !result.StatusText.Contains(q, StringComparison.OrdinalIgnoreCase)
            && !result.Remark.Contains(q, StringComparison.OrdinalIgnoreCase)) return false;

        var status = (ScanStatusFilterBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        return status switch
        {
            "online" => result.PingOk,
            "assigned" => result.StatusText.Contains("Assigned", StringComparison.OrdinalIgnoreCase),
            "offline" => !result.PingOk && !result.StatusText.Contains("Assigned", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private void RefreshScanResultView()
    {
        CollectionViewSource.GetDefaultView(ScanResults).Refresh();
        UpdateScanSummary();
        UpdateFilterButtons();
    }

    private void UpdateScanSummary()
    {
        if (TxtScanSummary == null || ScanGrid == null) return;
        TxtScanSummary.Text = IsChineseUi()
            ? $"显示 {ScanGrid.Items.Count}/{ScanResults.Count} 条"
            : $"Showing {ScanGrid.Items.Count}/{ScanResults.Count}";
        UpdateEmptyStates();
    }

    private void ScanFilterBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshScanResultView();

    private void ScanStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshScanResultView();

    private void ClearScanFilter_Click(object sender, RoutedEventArgs e)
    {
        ScanFilterBox.Clear();
        ScanStatusFilterBox.SelectedIndex = 0;
        UpdateFilterButtons();
    }

    private void ClearFavoriteSearch_Click(object sender, RoutedEventArgs e)
    {
        FavoriteSearch.Clear();
        UpdateFilterButtons();
    }

    private void ClearHistoryFilter_Click(object sender, RoutedEventArgs e)
    {
        HistoryFilterBox.Clear();
        UpdateFilterButtons();
    }

    private void UpdateFilterButtons()
    {
        if (!IsInitialized) return;
        BtnClearScanFilter.IsEnabled = !string.IsNullOrWhiteSpace(ScanFilterBox.Text)
            || (ScanStatusFilterBox.SelectedItem as ComboBoxItem)?.Tag is string scanStatus && scanStatus != "all";
        BtnClearFavoriteSearch.IsEnabled = !string.IsNullOrWhiteSpace(FavoriteSearch.Text);
        BtnClearHistoryFilter.IsEnabled = !string.IsNullOrWhiteSpace(HistoryFilterBox.Text);
        RefreshButtonStateColors();
    }

    private IEnumerable<ScanResult> GetSelectedOrVisibleScanResults()
    {
        var selected = ScanGrid.SelectedItems.OfType<ScanResult>().ToList();
        return selected.Count > 0 ? selected : ScanGrid.Items.OfType<ScanResult>();
    }

    private void CopyScan_Click(object sender, RoutedEventArgs e)
    {
        var rows = GetSelectedOrVisibleScanResults().ToList();
        var header = IsChineseUi() ? "IP\t连通\t延迟(ms)\t状态\t网页\tMAC\t主机名\t备注" : "IP\tPing\tLatency(ms)\tStatus\tWeb\tMAC\tHostname\tRemark";
        var text = header + Environment.NewLine + string.Join(Environment.NewLine, rows.Select(x => string.Join("\t", x.IpAddress, x.PingOk, x.LatencyMs, x.StatusText, x.WebText, x.MacAddress, x.Hostname, x.Remark)));
        if (TryCopyText(rows.Count == 0 ? "" : text, $"已复制 {rows.Count} 条扫描结果。", $"Copied {rows.Count} scan result(s)."))
            _logger.Info($"Scan results copied: count={rows.Count}");
    }

    private void ScanGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        CopyScan_Click(sender, e);
        e.Handled = true;
    }

    private void PreviewDhcp_Click(object sender, RoutedEventArgs e)
    {
        ShowDhcpPreview(confirm: false);
    }

    private bool ShowDhcpPreview(bool confirm)
    {
        var adapter = SelectedAdapter;
        if (adapter == null)
        {
            AppDialog.Show(this, _lang.T("preview.dhcp"), IsChineseUi() ? "请先选择网卡。" : "Select an adapter first.");
            return false;
        }
        if (!TryValidateDhcpInputs(out var validation))
        {
            AppDialog.Show(this, _lang.T("preview.dhcp"), validation, danger: true);
            return false;
        }

        var current = string.IsNullOrWhiteSpace(adapter.IPv4Address) ? "DHCP / 未配置" : $"{adapter.IPv4Address} / {adapter.SubnetMask}";
        var gateway = string.IsNullOrWhiteSpace(DhcpGateway.Text) ? "-" : DhcpGateway.Text.Trim();
        var dns = string.IsNullOrWhiteSpace(DhcpDns.Text) ? "-" : DhcpDns.Text.Trim();
        var message = IsChineseUi()
            ? $"操作：启动 DHCP（仅本机，不会自动修改其他网卡）\n网卡：{adapter.DisplayName}\n连接：{adapter.ConnectionDisplay}，{adapter.KindDisplay}，{adapter.LinkSpeedDisplay}\n\n当前 IPv4：{current}\n变更为：{DhcpServerIp.Text.Trim()} / {DhcpMask.Text.Trim()}\n地址池：{DhcpStart.Text.Trim()} - {DhcpEnd.Text.Trim()}\n网关：{gateway}\nDNS：{dns}\n租约：{DhcpLeaseSeconds.Text.Trim()} 秒\n\n安全边界：必须是隔离调试网络；应用前会保存网卡备份，停止或退出时按策略恢复。"
            : $"Operation: Start DHCP (this adapter only; other adapters are not changed automatically)\nAdapter: {adapter.DisplayName}\nLink: {adapter.ConnectionDisplay}, {adapter.KindDisplay}, {adapter.LinkSpeedDisplay}\n\nCurrent IPv4: {current}\nChange to: {DhcpServerIp.Text.Trim()} / {DhcpMask.Text.Trim()}\nPool: {DhcpStart.Text.Trim()} - {DhcpEnd.Text.Trim()}\nGateway: {gateway}\nDNS: {dns}\nLease: {DhcpLeaseSeconds.Text.Trim()} seconds\n\nSafety boundary: use an isolated test network only. An adapter backup is saved before applying and restoration follows the stop/exit policy.";
        return AppDialog.Show(this, _lang.T("preview.dhcp"), message, confirm, danger: confirm);
    }

    private void PreviewScan_Click(object sender, RoutedEventArgs e)
    {
        ShowScanPreview(confirm: false);
    }

    private bool ShowScanPreview(bool confirm)
    {
        var adapter = SelectedAdapter;
        if (adapter == null)
        {
            AppDialog.Show(this, _lang.T("preview.scan"), IsChineseUi() ? "请先选择网卡。" : "Select an adapter first.");
            return false;
        }
        if (!TryGetNetwork(ManualIp.Text, ManualMask.Text, out var localIp, out var mask))
        {
            AppDialog.Show(this, _lang.T("preview.scan"), IsChineseUi() ? "本机 IP 或子网掩码无效。" : "The local IP or subnet mask is invalid.", danger: true);
            return false;
        }
        var targetText = ManualTargetIp.Text.Trim();
        var scope = $"{localIp}/{IpNetwork.PrefixLength(mask)}";
        if (string.IsNullOrWhiteSpace(targetText)) scope += IsChineseUi() ? "（扫描整个网段）" : " (whole subnet)";
        else scope += IsChineseUi() ? $"（仅探测 {targetText}）" : $" (probe only {targetText})";
        var current = string.IsNullOrWhiteSpace(adapter.IPv4Address) ? "DHCP / 未配置" : $"{adapter.IPv4Address} / {adapter.SubnetMask}";
        var message = IsChineseUi()
            ? $"操作：应用临时 IP 后扫描\n网卡：{adapter.DisplayName}\n连接：{adapter.ConnectionDisplay}，{adapter.KindDisplay}\n\n当前 IPv4：{current}\n变更为：{ManualIp.Text.Trim()} / {ManualMask.Text.Trim()}\n扫描范围：{scope}\n\n应用前会保存网卡备份；扫描结束后不会自动恢复，仍可使用“恢复网卡”或回滚。"
            : $"Operation: Apply a temporary IP and scan\nAdapter: {adapter.DisplayName}\nLink: {adapter.ConnectionDisplay}, {adapter.KindDisplay}\n\nCurrent IPv4: {current}\nChange to: {ManualIp.Text.Trim()} / {ManualMask.Text.Trim()}\nScan scope: {scope}\n\nAn adapter backup is saved before applying. The adapter is not restored automatically after scanning; use Restore Adapter or Rollback when needed.";
        return AppDialog.Show(this, _lang.T("preview.scan"), message, confirm, danger: confirm);
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
        if (!EnsureSafetyOnboarding()) return;
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
            var wlanBefore = await _adapterService.LogReadonlyWlanStateAsync("before DHCP start", OperationToken);
            if (wlanBefore.PolicyBlockedRecent)
            {
                var docPath = Path.Combine(_paths.DocsDirectory, "troubleshooting", "windows-wifi-wired-conflict.md");
                AppDialog.Show(this,
                    IsChineseUi() ? "WLAN 策略提示" : "WLAN Policy Warning",
                    IsChineseUi()
                        ? $"检测到最近 30 分钟内出现过“策略禁止在该接口上自动连接”的 WLAN 事件。请先参考：{docPath}"
                        : $"A WLAN event indicating policy-blocked auto connection was found in the last 30 minutes. Review: {docPath}");
            }
            if (!ShowDhcpPreview(confirm: true)) return;
            await RememberAdapterConfigAsync(adapter, OperationToken);
            await _adapterService.ApplyStaticIPv4Async(adapter, DhcpServerIp.Text, DhcpMask.Text, DhcpGateway.Text, DhcpDns.Text, OperationToken);
            adapterConfigured = true;
            await VerifyStaticIPv4Async(adapter, DhcpServerIp.Text, DhcpMask.Text, OperationToken);
            _activeDhcpAdapterIndex = adapter.InterfaceIndex;
            MarkAdapterIp(adapter, DhcpServerIp.Text);
            await _adapterService.LogReadonlyWlanStateAsync("after DHCP start", OperationToken);
            _dhcpFirewallRules = await _adapterService.EnsureDhcpFirewallRulesAsync(OperationToken);
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
            AddOperationHistory("DHCP", DhcpServerIp.Text.Trim(), adapter.MacAddress, "Running / 运行中",
                $"Pool {DhcpStart.Text.Trim()} - {DhcpEnd.Text.Trim()}", scope: adapter.Name, rollbackAvailable: true);
            ShowActionFeedback("DHCP 已启动。", "DHCP started.");
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Start DHCP canceled by user or timeout");
            if (!serverStarted)
            {
                await RemoveDhcpFirewallRulesAsync();
                if (adapterConfigured && changedAdapter != null)
                {
                    try { await RestoreOriginalAdapterConfigAsync(changedAdapter); } catch (Exception restoreEx) { _logger.Error("DHCP cancellation rollback failed", restoreEx); }
                }
            }
            AppDialog.Show(this, _lang.T("start.dhcp"), IsChineseUi() ? "操作已取消，已完成的网卡变更已尝试回滚。" : "The operation was canceled; completed adapter changes were rolled back where possible.");
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
            AppDialog.Show(this, _lang.T("start.dhcp"), ExplainFailure(ex, _lang.T("help.start.dhcp")), danger: true);
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
            await RemoveDhcpFirewallRulesAsync(OperationToken);
            _leasePingTimer.Stop();
            _leaseHintCts?.Cancel();
            if (RestoreOnStop.IsChecked == true)
            {
                var restoreAdapter = GetDhcpRestoreAdapter();
                if (restoreAdapter != null)
                {
                    await RestoreOriginalAdapterConfigAsync(restoreAdapter, OperationToken);
                }
            }
            await _adapterService.LogReadonlyWlanStateAsync("after DHCP stop", OperationToken);
            _activeDhcpAdapterIndex = null;
            SetDhcpRunningState(false);
            UpdateManualScanButtons();
            AddOperationHistory("DHCP", "", GetDhcpRestoreAdapter()?.MacAddress ?? "", "Stopped / 已停止",
                "Service stopped and cleanup requested / 服务已停止并执行清理", scope: "DHCP session", rollbackAvailable: false);
            ShowActionFeedback("DHCP 已停止。", "DHCP stopped.");
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Stop DHCP canceled by user or timeout");
            AppDialog.Show(this, _lang.T("stop.dhcp"), IsChineseUi() ? "停止操作已取消；请确认 DHCP 和临时网卡配置状态。" : "Stop operation was canceled; verify the DHCP and temporary adapter state.", danger: true);
        }
        catch (Exception ex)
        {
            _logger.Error("Stop DHCP failed", ex);
            AppDialog.Show(this, _lang.T("stop.dhcp"), ExplainFailure(ex, _lang.T("help.stop.dhcp")), danger: true);
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
        if (!EnsureSafetyOnboarding()) return;
        try
        {
            var adapter = SelectedAdapter ?? throw new InvalidOperationException("No adapter selected");
            if (!ShowScanPreview(confirm: true)) return;
            _scanRunning = true;
            UpdateManualScanButtons();
            UpdateFavoriteButtons();
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            ScanResults.Clear();
            UpdateEmptyStates();
            ScanProgress.Value = 0;
            _scanStopwatch = Stopwatch.StartNew();
            _scanLastHitAt = null;
            _scanPhase = IsChineseUi() ? "正在配置网卡" : "Configuring adapter";
            UpdateScanTiming(0, 0);
            SetBusy(true, "Configuring adapter... / 正在配置网卡...");
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(OperationToken);
            var scanToken = _scanCts.Token;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await RememberAdapterConfigAsync(adapter, scanToken);
            await _adapterService.ApplyStaticIPv4Async(adapter, ManualIp.Text, ManualMask.Text, "", "", scanToken);
            try
            {
                await VerifyStaticIPv4Async(adapter, ManualIp.Text, ManualMask.Text, scanToken);
            }
            catch
            {
                try { await RestoreOriginalAdapterConfigAsync(adapter, CancellationToken.None); }
                catch (Exception restoreEx) { _logger.Error("Manual scan verification rollback failed", restoreEx); }
                throw;
            }
            MarkAdapterIp(adapter, ManualIp.Text);
            SetBusy(false);
            _scanPhase = IsChineseUi() ? "正在扫描" : "Scanning";
            var progress = new Progress<(int done, int total, ScanResult? result)>(p =>
            {
                ScanProgress.Maximum = p.total;
                ScanProgress.Value = p.done;
                if (p.result != null)
                {
                    ScanResults.Add(p.result);
                    _scanLastHitAt = DateTime.Now;
                }
                if (p.result != null) AddOperationHistory("Scan", p.result.IpAddress, p.result.MacAddress, p.result.StatusText, p.result.Remark);
                ScanStatus.Text = $"{p.done}/{p.total} Found={ScanResults.Count}";
                UpdateScanTiming(p.done, p.total);
                UpdateScanSummary();
            });
            if (IPAddress.TryParse(ManualTargetIp.Text.Trim(), out var targetIp))
            {
                await ProbeTargetUntilOnlineAsync(targetIp, progress, scanToken);
            }
            else
            {
                await _scanner.ScanAsync(IPAddress.Parse(ManualIp.Text), IPAddress.Parse(ManualMask.Text), _settings.PingConcurrency, _settings.PingTimeoutMs, _settings.HttpTimeoutMs, progress, scanToken);
            }
            _scanPhase = IsChineseUi() ? "扫描完成" : "Scan complete";
            UpdateScanTiming((int)ScanProgress.Value, (int)ScanProgress.Maximum);
            AddOperationHistory("Scan", ManualTargetIp.Text.Trim(), adapter.MacAddress, "Completed / 已完成",
                $"{ScanResults.Count} result(s) / {ScanResults.Count} 条结果", scope: adapter.Name,
                durationMs: _scanStopwatch?.ElapsedMilliseconds ?? 0,
                rollbackAvailable: _originalAdapterConfigs.ContainsKey(adapter.InterfaceIndex));
            ShowActionFeedback("扫描已完成。", "Scan completed.");
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Scan canceled by user or timeout");
            ScanStatus.Text = IsChineseUi() ? "已取消，已保留已收集结果" : "Canceled; collected results were kept";
            _scanPhase = IsChineseUi() ? "已取消" : "Canceled";
            UpdateScanTiming((int)ScanProgress.Value, (int)ScanProgress.Maximum);
        }
        catch (Exception ex)
        {
            _logger.Error("Apply and scan failed", ex);
            AppDialog.Show(this, _lang.T("apply.scan"), ExplainFailure(ex, _lang.T("help.apply.scan")), danger: true);
        }
        finally
        {
            _scanStopwatch?.Stop();
            SetBusy(false);
            _scanRunning = false;
            _scanCts?.Dispose();
            _scanCts = null;
            UpdateManualScanButtons();
            UpdateFavoriteButtons();
        }
    }

    private void UpdateScanTiming(int done, int total)
    {
        if (TxtScanTiming == null || _scanStopwatch == null) return;
        var elapsed = _scanStopwatch.Elapsed;
        var eta = done > 0 && total > done
            ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (total - done) / done)
            : TimeSpan.Zero;
        var elapsedText = FormatDuration(elapsed);
        var etaText = done > 0 && total > done ? FormatDuration(eta) : "-";
        var lastHitText = _scanLastHitAt.HasValue ? _scanLastHitAt.Value.ToString("HH:mm:ss") : "-";
        TxtScanTiming.Text = IsChineseUi()
            ? $"{_scanPhase} | 已耗时 {elapsedText} | 预计剩余 {etaText} | 最近发现 {lastHitText}"
            : $"{_scanPhase} | Elapsed {elapsedText} | ETA {etaText} | Last hit {lastHitText}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours}h {value.Minutes}m";
        if (value.TotalMinutes >= 1) return $"{(int)value.TotalMinutes}m {value.Seconds}s";
        return $"{Math.Max(0, (int)value.TotalSeconds)}s";
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
        UpdateScanSummary();
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
            UpdateScanSummary();
            ScanStatus.Text = result.PingOk ? $"1/1 Found=1 Ping={latency}ms" : $"0/1 Waiting {attempt}";
            UpdateScanTiming(result.PingOk ? 1 : 0, 1);
            _logger.Info($"Target probe: {ip} attempt={attempt} ping={latency}");

            if (result.PingOk)
            {
                result.Hostname = ResolveHost(ip);
                var probes = await _probe.ProbeAsync(ip, _settings.HttpTimeoutMs, ct);
                result.HttpOk = probes.http;
                result.HttpsOk = probes.https;
                ScanGrid.Items.Refresh();
                UpdateScanSummary();
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
        // Static routes are read-only with respect to adapter configuration, so Wi-Fi
        // and virtual adapters are valid targets here. Prefer an online adapter when
        // the route row has not inherited the main adapter selection yet.
        var adapter = SelectedAdapter
            ?? Adapters.FirstOrDefault(x => x.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
            ?? Adapters.FirstOrDefault();
        var rule = new StaticRouteRule
        {
            AdapterId = adapter?.Id ?? "",
            AdapterName = adapter?.Name ?? "",
            AdapterMac = adapter?.MacAddress ?? "",
            RouteMetric = 1
        };
        StaticRoutes.Add(rule);
        RouteGrid.SelectedItem = rule;
        RouteGrid.ScrollIntoView(rule);
        UpdateEmptyStates();
        UpdateSessionStatus();
        UpdateProfileButtons();
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
        if (!EnsureSafetyOnboarding()) return;
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
            await _adapterService.RestartAdapterAsync(adapter, _settings.AllowRestartOnAnyAdapter, OperationToken);
            _logger.Info($"Adapter restart completed: idx={adapter.InterfaceIndex} name={adapter.Name} status={adapter.Status}");
            await RefreshAdaptersAsync();
            var refreshed = Adapters.FirstOrDefault(x => x.InterfaceIndex.Equals(adapter.InterfaceIndex, StringComparison.OrdinalIgnoreCase));
            if (refreshed == null || !refreshed.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(IsChineseUi() ? "重启后校验失败：网卡未恢复为已连接状态。" : "Post-change verification failed: the adapter did not return to the connected state.");
            AddOperationHistory("Adapter", "", refreshed.MacAddress, "Restarted / 已重启", refreshed.Name, scope: refreshed.Name);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"Adapter restart canceled: idx={adapter.InterfaceIndex} name={adapter.Name}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Adapter restart failed: idx={adapter.InterfaceIndex} name={adapter.Name}", ex);
            AppDialog.Show(this, _lang.T("restart.adapter"), ExplainFailure(ex, _lang.T("help.restart.adapter")), danger: true);
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
        if (!EnsureSafetyOnboarding()) return;

        var dialog = new MacAddressWindow(adapter) { Owner = this };
        dialog.ApplyOwnerTheme(this);
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
        SaveMacBackups();

        _adapterActionInProgress = true;
        var macChanged = false;
        UpdateAdapterActionButtons();
        try
        {
            SetBusy(true, IsChineseUi() ? "正在修改 MAC，请稍后..." : "Changing MAC, please wait...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await _adapterService.ChangeMacAddressAsync(adapter, requestedMac, _settings.AllowMacChangeOnAnyAdapter, OperationToken);
            macChanged = true;
            adapter.MacAddress = requestedMac;
            TxtMac.Text = requestedMac;
            AdapterBox.Items.Refresh();
            _logger.Info($"Adapter MAC changed: idx={adapter.InterfaceIndex} name={adapter.Name} old={existingBackup.OriginalMacAddress} new={requestedMac} restoreOnExit={existingBackup.RestoreOnExit}");
            await RefreshAdaptersAsync();
            var observed = Adapters.FirstOrDefault(x => x.InterfaceIndex.Equals(adapter.InterfaceIndex, StringComparison.OrdinalIgnoreCase));
            var expectedMac = NetworkAdapterService.NormalizeMacAddress(requestedMac);
            var actualMac = observed == null ? "" : NetworkAdapterService.NormalizeMacAddress(observed.MacAddress);
            if (observed == null || !actualMac.Equals(expectedMac, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(IsChineseUi() ? "修改后校验失败：当前 MAC 与目标值不一致。" : "Post-change verification failed: the current MAC does not match the requested value.");
            AddOperationHistory("MAC", "", requestedMac, "Changed / 已修改", $"{existingBackup.OriginalMacAddress} -> {requestedMac}", scope: adapter.Name, rollbackAvailable: existingBackup.RestoreOnExit);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"Adapter MAC change canceled: idx={adapter.InterfaceIndex} name={adapter.Name}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Adapter MAC change failed: idx={adapter.InterfaceIndex} name={adapter.Name} requested={requestedMac}", ex);
            if (!hadBackup && !macChanged) { _originalAdapterMacs.Remove(key); SaveMacBackups(); }
            AppDialog.Show(this, _lang.T("change.mac"), ExplainFailure(ex, _lang.T("help.change.mac")), danger: true);
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
        // SelectionChanged fires while DataGrid is still inside its edit transaction.
        // Refreshing here throws "Refresh is not allowed during AddNew/EditItem" and
        // makes the route row appear unusable, especially when choosing a virtual NIC.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => RouteGrid.Items.Refresh()));
    }

    private void PreviewRoutes_Click(object sender, RoutedEventArgs e) => _ = PreviewRoutesAsync(apply: false);

    private async void ApplyRoutes_Click(object sender, RoutedEventArgs e) => await PreviewRoutesAsync(apply: true);

    private async Task<IReadOnlyList<StaticRoutePlanItem>?> PreviewRoutesAsync(bool apply)
    {
        try
        {
            if (apply && !EnsureSafetyOnboarding()) return null;
            var targets = BuildStaticRouteTargets();
            SetBusy(true, apply ? "Preparing route changes... / 正在准备路由变更..." : "Preparing route preview... / 正在准备路由预览...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var plan = await _routeService.PreviewAsync(targets, _appliedStaticRoutes.ToList(), OperationToken);
            var summary = BuildRoutePreviewText(plan, apply);
            if (!AppDialog.Show(this,
                apply
                    ? (IsChineseUi() ? "确认应用静态路由" : "Confirm Static Routes")
                    : (IsChineseUi() ? "静态路由预览" : "Static Route Preview"),
                summary,
                confirm: apply,
                danger: apply))
            {
                return plan;
            }
            if (!apply) return plan;

            if (_appliedStaticRoutes.Count > 0 && !await ClearAppliedRoutesAsync(OperationToken)) return plan;
            var results = await _routeService.ApplyAsync(
                targets,
                onCreated: applied =>
                {
                    _appliedStaticRoutes.Add(applied);
                    SaveStaticRouteSession();
                    UpdateRecoveryBanner();
                },
                ct: OperationToken);
            foreach (var result in results) result.Rule.Status = result.StatusText;
            SaveStaticRouteSession();
            RouteGrid.Items.Refresh();
            await LoadCurrentStaticRoutesAsync(Adapters.ToList(), OperationToken);
            await VerifyAppliedRoutesAsync(results, OperationToken);
            AddOperationHistory("Static Routes", "", "", "Completed / 已完成",
                $"{results.Count(x => x.Created)}/{results.Count} route(s) applied / 已应用",
                scope: string.Join(", ", targets.Select(x => x.Adapter.Name).Distinct()),
                rollbackAvailable: _appliedStaticRoutes.Count > 0);
            ShowActionFeedback("静态路由已应用。", "Static routes applied.");
            _logger.Info($"Static route batch applied: rules={results.Count} created={results.Count(x => x.Created)}");
            return plan;
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Static route operation canceled by user or timeout");
            AppDialog.Show(this, _lang.T("apply.routes"), IsChineseUi() ? "路由操作已取消，已完成的路由会按本次运行记录保留，可从历史或清理入口处理。" : "The route operation was canceled; completed routes remain recorded for this run and can be handled from History or Clear Applied.", danger: true);
            return null;
        }
        catch (Exception ex)
        {
            if (apply && _appliedStaticRoutes.Count > 0)
            {
                try { await ClearAppliedRoutesAsync(); }
                catch (Exception cleanupEx) { _logger.Error("Route failure cleanup failed", cleanupEx); }
            }
            _logger.Error("Static route operation failed", ex);
            AppDialog.Show(this, _lang.T("apply.routes"), ExplainFailure(ex, _lang.T("help.apply.routes")), danger: true);
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string BuildRoutePreviewText(IReadOnlyList<StaticRoutePlanItem> plan, bool apply)
    {
        var lines = plan.Select(x =>
        {
            var gateway = x.Target.Route.NextHop switch { "0.0.0.0" or "::" => "Direct / 直连", _ => "Gateway / 网关 " + x.Target.Route.NextHop };
            var warning = string.IsNullOrWhiteSpace(x.OverlapWarning) ? "" : $"  [{x.OverlapWarning}]";
            var action = x.ShouldCreate ? "ADD / 新增" : x.StatusText;
            return $"{action}  {x.Target.Route.AddressFamily} {x.Target.Route.DestinationPrefix}  ->  {x.Target.Adapter.Name}  {gateway}  metric={x.Target.Route.RouteMetric} + interface={x.InterfaceMetric} = {x.EffectiveMetric}{warning}";
        });
        var prefix = IsChineseUi()
            ? $"计划 {plan.Count} 条路由；新增 {plan.Count(x => x.ShouldCreate)} 条。已有系统路由不会被修改。{Environment.NewLine}{Environment.NewLine}"
            : $"Plan: {plan.Count} route(s); add {plan.Count(x => x.ShouldCreate)}. Existing system routes are not modified.{Environment.NewLine}{Environment.NewLine}";
        var footer = IsChineseUi()
            ? $"{Environment.NewLine}综合跃点按当前接口跃点计算；相同前缀取更低综合跃点，不同前缀按最长前缀优先。{(apply ? "这些路由会影响本机流量，确认后写入系统。" : "当前仅预览，不会写入系统。")}"
            : $"{Environment.NewLine}Effective metrics use current interface metrics; same-prefix routes prefer the lower value and overlapping prefixes use longest-prefix match. {(apply ? "These routes affect local traffic and will be written after confirmation." : "Preview only; nothing is written.")}";
        return prefix + string.Join(Environment.NewLine, lines) + footer;
    }

    private async void ClearAppliedRoutes_Click(object sender, RoutedEventArgs e)
    {
        if (_appliedStaticRoutes.Count == 0)
        {
            AppDialog.Show(this,
                IsChineseUi() ? "清除已应用路由" : "Clear Applied Routes",
                IsChineseUi() ? "当前没有本次运行创建的路由。" : "There are no routes created by this run.");
            return;
        }
        if (!EnsureSafetyOnboarding()) return;
        if (!AppDialog.Show(this,
            IsChineseUi() ? "清除已应用路由" : "Clear Applied Routes",
            IsChineseUi()
                ? $"将删除本次运行创建的 {_appliedStaticRoutes.Count} 条路由；系统原有路由不会改变。确认继续？"
                : $"This will remove {_appliedStaticRoutes.Count} route(s) created by this run. Existing system routes will not change. Continue?",
            confirm: true,
            danger: true)) return;
        try
        {
            SetBusy(true, "Clearing routes... / 正在清除路由...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var cleared = await ClearAppliedRoutesAsync(OperationToken);
            if (!cleared)
            {
                AppDialog.Show(this,
                    IsChineseUi() ? "清除静态路由" : "Clear Static Routes",
                    IsChineseUi() ? "部分路由未能清除，详细原因已写入日志。" : "Some routes could not be cleared. See the log for details.",
                    danger: true);
            }
            else
            {
                ShowActionFeedback("已清除本次运行创建的静态路由。", "Static routes created by this run were cleared.");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Clear static routes canceled by user or timeout");
        }
        catch (Exception ex)
        {
            _logger.Error("Clear static routes failed", ex);
            AppDialog.Show(this, _lang.T("clear.applied.routes"), ExplainFailure(ex, _lang.T("help.clear.applied.routes")), danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RemoveRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not StaticRouteRule rule) { ShowActionFeedbackKey("selection.route"); return; }
        try
        {
            var applied = _appliedStaticRoutes.Where(x => x.RuleId.Equals(rule.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (applied.Count > 0)
            {
                if (!EnsureSafetyOnboarding()) return;
                if (!AppDialog.Show(this,
                    IsChineseUi() ? "删除已应用路由" : "Remove Applied Route",
                    IsChineseUi()
                        ? $"目标：{rule.DestinationPrefix}\n网卡：{rule.AdapterName}\n\n将删除本工具本次创建的系统路由。确认继续？"
                        : $"Destination: {rule.DestinationPrefix}\nAdapter: {rule.AdapterName}\n\nThe system route created by this run will be removed. Continue?",
                    confirm: true,
                    danger: true)) return;
                SetBusy(true, "Removing route... / 正在删除路由...");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                foreach (var route in applied)
                {
                    var adapter = ResolveAdapterForRoute(route);
                    if (adapter == null) throw new InvalidOperationException("The original adapter is unavailable / 原网卡当前不可用");
                    await _routeService.RemoveAsync(route, adapter, OperationToken);
                    _appliedStaticRoutes.Remove(route);
                }
                SaveStaticRouteSession();
            }
            StaticRoutes.Remove(rule);
            RouteGrid.SelectedItem = null;
            UpdateEmptyStates();
            UpdateSessionStatus();
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Remove static route canceled by user or timeout");
        }
        catch (Exception ex)
        {
            _logger.Error("Remove static route failed", ex);
            AppDialog.Show(this, _lang.T("remove.route"), ExplainFailure(ex, _lang.T("help.remove.route")), danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private List<StaticRouteTarget> BuildStaticRouteTargets()
    {
        if (StaticRoutes.Count == 0) throw new InvalidOperationException("Add at least one route / 请至少新增一条路由");
        var targets = new List<StaticRouteTarget>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in StaticRoutes)
        {
            var adapter = ResolveAdapterForRule(rule) ?? throw new InvalidOperationException($"Adapter not found for route {rule.DestinationPrefix} / 路由未找到对应网卡：{rule.DestinationPrefix}");
            var normalized = StaticRouteValidator.Normalize(rule);
            var key = $"{normalized.AddressFamily}|{normalized.DestinationPrefix}|{adapter.InterfaceIndex}|{normalized.NextHop}";
            if (!keys.Add(key)) throw new InvalidOperationException($"Duplicate route target: {normalized.DestinationPrefix} on {adapter.Name} / 重复路由目标：{normalized.DestinationPrefix} 指向 {adapter.Name}");
            rule.DestinationPrefix = normalized.DestinationPrefix;
            rule.AddressFamily = normalized.AddressFamily.ToString();
            rule.NextHop = normalized.NextHop == "0.0.0.0" ? "" : normalized.NextHop;
            if (normalized.NextHop == "::") rule.NextHop = "";
            rule.RouteMetric = 1;
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

    private static bool IsBlankNextHop(string nextHop, string addressFamily)
    {
        if (string.IsNullOrWhiteSpace(nextHop)) return true;
        if (!IPAddress.TryParse(nextHop, out var parsed)) return false;
        return addressFamily.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
            ? parsed.Equals(IPAddress.IPv6Any)
            : parsed.Equals(IPAddress.Any);
    }

    private async void RestoreScan_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAdapter == null) return;
        if (!EnsureSafetyOnboarding()) return;
        try
        {
            SetBusy(true, "Restoring adapter... / 正在恢复网卡...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await RestoreOriginalAdapterConfigAsync(SelectedAdapter, OperationToken);
            await RefreshAdaptersAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Restore manual adapter canceled by user or timeout");
        }
        catch (Exception ex)
        {
            _logger.Error("Restore manual adapter failed", ex);
            AppDialog.Show(this, "Restore / 恢复网卡", ExplainFailure(ex, _lang.T("help.restore.scan")), danger: true);
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
        dialog.ApplyOwnerTheme(this);
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
        dialog.ApplyOwnerTheme(this);
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
        ApplyOwnedWindowTheme(choice);
        choice.ShowDialog();
    }

    private void LoadFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav) { ShowActionFeedbackKey("selection.favorite"); return; }
        LoadFavorite(fav);
    }

    private async void ApplyFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav) { ShowActionFeedbackKey("selection.favorite"); return; }
        LoadFavorite(fav);
        await ApplyAndScanAsync();
    }

    private void DeleteFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav) { ShowActionFeedbackKey("selection.favorite"); return; }
        if (!AppDialog.Show(this,
            IsChineseUi() ? "删除收藏夹" : "Delete Favorite",
            IsChineseUi()
                ? $"收藏夹：{fav.Name}\n\n删除后不会影响其它收藏夹。确认删除？"
                : $"Favorite: {fav.Name}\n\nOther saved favorites will not be affected. Delete it?",
            confirm: true,
            danger: true)) return;
        _allFavorites.RemoveAll(x => x.Id == fav.Id);
        SaveFavorites();
        FilterFavorites();
        _logger.Info("Favorite deleted: " + fav.Name);
        UpdateSessionStatus();
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
            var loaded = FavoriteStore.Load(dialog.FileName, _logger).ToList();
            var invalid = loaded.Where(x => !IsValidFavorite(x)).ToList();
            var imported = loaded.Where(IsValidFavorite).ToList();
            var duplicateImported = 0;
            var distinctImported = new List<FavoriteConfig>();
            foreach (var item in imported)
            {
                var duplicateIndex = distinctImported.FindIndex(x => SameFavorite(x, item));
                if (duplicateIndex >= 0)
                {
                    distinctImported[duplicateIndex] = item;
                    duplicateImported++;
                }
                else
                {
                    distinctImported.Add(item);
                }
            }

            if (distinctImported.Count == 0)
            {
                AppDialog.Show(this, IsChineseUi() ? "导入收藏夹" : "Import Favorites",
                    IsChineseUi()
                        ? $"文件读取 {loaded.Count} 条，但没有可用记录。\n跳过：{invalid.Count} 条（名称、本机 IP 或掩码无效）。"
                        : $"Read {loaded.Count} record(s), but none are usable.\nSkipped: {invalid.Count} (invalid name, local IP, or mask).",
                    danger: true);
                return;
            }

            var addCount = distinctImported.Count(x => _allFavorites.All(existing => !SameFavorite(existing, x)));
            var updateCount = distinctImported.Count - addCount;
            var credentialCount = distinctImported.Count(x => x.HasUsablePassword);
            var preview = IsChineseUi()
                ? $"文件：{Path.GetFileName(dialog.FileName)}\n读取：{loaded.Count} 条\n可导入：{distinctImported.Count} 条\n新增：{addCount} 条\n覆盖：{updateCount} 条\n跳过无效：{invalid.Count} 条\n文件内重复：{duplicateImported} 条\n包含可用凭据：{credentialCount} 条\n\n确认后才会更新本地收藏夹。密码不会显示。"
                : $"File: {Path.GetFileName(dialog.FileName)}\nRead: {loaded.Count} record(s)\nUsable: {distinctImported.Count}\nAdd: {addCount}\nReplace: {updateCount}\nInvalid skipped: {invalid.Count}\nDuplicates in file: {duplicateImported}\nUsable credentials included: {credentialCount}\n\nLocal Favorites are updated only after confirmation. Passwords are not shown.";
            if (!AppDialog.Show(this, IsChineseUi() ? "确认导入收藏夹" : "Review Imported Favorites", preview, confirm: true,
                danger: updateCount > 0 || credentialCount > 0 || invalid.Count > 0 || duplicateImported > 0)) return;

            var added = 0;
            var updated = 0;
            foreach (var item in distinctImported)
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
            AddOperationHistory("Favorite Import", "", "", "Completed / 已完成", $"added={added} updated={updated} skipped={invalid.Count + duplicateImported}", scope: "Local favorites / 本地收藏夹");
            _logger.Info($"Favorites imported: added={added} updated={updated} invalid={invalid.Count} duplicates={duplicateImported}");
            AppDialog.Show(this, IsChineseUi() ? "导入收藏夹" : "Import Favorites",
                IsChineseUi() ? $"导入完成。\n新增：{added} 条\n覆盖：{updated} 条\n跳过：{invalid.Count + duplicateImported} 条" : $"Import complete.\nAdded: {added}\nReplaced: {updated}\nSkipped: {invalid.Count + duplicateImported}");
        }
        catch (Exception ex)
        {
            _logger.Error("Import favorites failed", ex);
            AppDialog.Show(this, "Import / 导入", ExplainFailure(ex, _lang.T("help.import.favorite")), danger: true);
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
            ShowActionFeedback("收藏夹已导出。", "Favorites exported.");
            AppDialog.Show(this, "Export / 导出", "导出完成（未包含密码）。\n" + dialog.FileName);
        }
        catch (Exception ex)
        {
            _logger.Error("Export favorites failed", ex);
            AppDialog.Show(this, "Export / 导出", ExplainFailure(ex, _lang.T("help.export.favorite")), danger: true);
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
        if (TryOpenUrl($"{(fav.PreferHttps ? "https" : "http")}://{fav.TargetIp}"))
        {
            _logger.Info($"Favorite web opened: {fav.Name} https={fav.PreferHttps}");
            ShowActionFeedback("已打开收藏网页。", "Favorite web page opened.");
        }
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
            WindowStartupLocation = WindowStartupLocation.CenterOwner
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
        ApplyOwnedWindowTheme(window);
        window.ShowDialog();
    }

    private void ManualIp_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateManualScanButtons();
    }
    private void ManualInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateManualScanButtons();
    }

    private void DhcpNetworkInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDhcpInputValidation();
    }

    private void SyncManualTargetIp()
    {
        if (!TryGetNetwork(ManualIp.Text, ManualMask.Text, out var localIp, out var mask)) return;
        ManualTargetIp.Text = GetNearbyHost(localIp, mask, 1).ToString();
    }

    private void SyncDhcpRange(string referenceIpText)
    {
        if (!TryGetNetwork(referenceIpText, DhcpMask.Text, out var referenceIp, out var mask)) return;
        var serverIp = IpNetwork.FromUInt32((IpNetwork.ToUInt32(referenceIp) & IpNetwork.ToUInt32(mask)) + 1);
        DhcpServerIp.Text = serverIp.ToString();
        DhcpStart.Text = GetNearbyHost(serverIp, mask, 99).ToString();
        DhcpEnd.Text = GetNearbyHost(serverIp, mask, 199).ToString();
    }

    private void FillDhcpRange_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetIpv4(DhcpServerIp.Text, out _))
        {
            AppDialog.Show(this, _lang.T("fill.dhcp.range"), IsChineseUi() ? "请先输入有效的本机 IPv4 地址。" : "Enter a valid local IPv4 address first.");
            return;
        }

        SyncDhcpRange(DhcpServerIp.Text);
        UpdateDhcpInputValidation();
    }

    private void FillTargetIp_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetValidIpv4Mask(ManualMask.Text, out _) || !TryGetIpv4(ManualIp.Text, out _))
        {
            AppDialog.Show(this, _lang.T("fill.target.ip"), IsChineseUi() ? "请先输入有效的本机 IPv4 地址和子网掩码。" : "Enter a valid local IPv4 address and subnet mask first.");
            return;
        }

        SyncManualTargetIp();
        UpdateManualScanButtons();
    }

    private void DhcpConfirmation_Changed(object sender, RoutedEventArgs e) => UpdateDhcpInputValidation();

    private void UpdateDhcpInputValidation()
    {
        if (DhcpValidation == null) return;

        var valid = TryValidateDhcpInputs(out var message);
        var confirmed = DhcpConfirm.IsChecked == true;
        DhcpValidation.Text = valid && confirmed && _safetyOnboardingCompleted
            ? (IsChineseUi() ? "参数有效，可以启动。" : "Parameters are valid and ready to start.")
            : valid
                ? !confirmed
                    ? (IsChineseUi() ? "参数有效；请确认当前为隔离调试网络。" : "Parameters are valid; confirm this is an isolated test network.")
                    : (IsChineseUi() ? "请先完成安全引导，网络变更按钮才会解锁。" : "Complete the Safety Guide before network-change actions are unlocked.")
                : message;
        DhcpValidation.Foreground = valid && confirmed && _safetyOnboardingCompleted ? Brushes.ForestGreen : Brushes.Brown;

        if (!_dhcpServer.IsRunning && !_closingCleanupStarted)
        {
            BtnPreviewDhcp.IsEnabled = valid && SelectedAdapter != null;
            BtnStartDhcp.IsEnabled = valid && confirmed && SelectedAdapter != null && _safetyOnboardingCompleted;
            BtnPreviewDhcp.Background = BtnPreviewDhcp.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
            BtnStartDhcp.Background = BtnStartDhcp.IsEnabled ? Brushes.LightGreen : Brushes.LightGray;
            RefreshButtonStateColors();
        }
    }

    private bool TryValidateDhcpInputs(out string message)
    {
        message = "";
        if (!TryGetIpv4(DhcpServerIp.Text, out var serverIp))
        {
            message = IsChineseUi() ? "本机 IP 必须是有效的 IPv4 地址。" : "Local IP must be a valid IPv4 address.";
            return false;
        }
        if (!TryGetValidIpv4Mask(DhcpMask.Text, out var mask))
        {
            message = IsChineseUi() ? "子网掩码必须是连续的 IPv4 掩码。" : "Subnet mask must be a contiguous IPv4 mask.";
            return false;
        }
        if (!TryGetIpv4(DhcpStart.Text, out var poolStart) || !TryGetIpv4(DhcpEnd.Text, out var poolEnd))
        {
            message = IsChineseUi() ? "地址池起止地址必须是有效的 IPv4 地址。" : "Pool start and end must be valid IPv4 addresses.";
            return false;
        }
        if (!IpNetwork.IsUsableHost(serverIp, serverIp, mask)
            || !IpNetwork.IsUsableHost(poolStart, serverIp, mask)
            || !IpNetwork.IsUsableHost(poolEnd, serverIp, mask))
        {
            message = IsChineseUi() ? "本机 IP 和地址池必须位于同一网段的可用主机范围内。" : "Local IP and pool addresses must be usable hosts in the same subnet.";
            return false;
        }
        if (IpNetwork.ToUInt32(poolStart) > IpNetwork.ToUInt32(poolEnd))
        {
            message = IsChineseUi() ? "地址池起始地址不能大于结束地址。" : "Pool start cannot be greater than pool end.";
            return false;
        }
        if (IpNetwork.ToUInt32(serverIp) >= IpNetwork.ToUInt32(poolStart)
            && IpNetwork.ToUInt32(serverIp) <= IpNetwork.ToUInt32(poolEnd))
        {
            message = IsChineseUi() ? "DHCP 服务端 IP 不能落在地址池内。" : "The DHCP server IP cannot be inside the address pool.";
            return false;
        }
        if (LblDhcpGateway.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(DhcpGateway.Text) && !TryGetIpv4(DhcpGateway.Text, out _))
        {
            message = IsChineseUi() ? "网关必须是有效的 IPv4 地址。" : "Gateway must be a valid IPv4 address.";
            return false;
        }
        if (LblDhcpDns.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(DhcpDns.Text) && !TryGetIpv4(DhcpDns.Text, out _))
        {
            message = IsChineseUi() ? "DNS 必须是有效的 IPv4 地址。" : "DNS must be a valid IPv4 address.";
            return false;
        }
        if (!int.TryParse(DhcpLeaseSeconds.Text.Trim(), out var leaseSeconds) || leaseSeconds < 1 || leaseSeconds > 604800)
        {
            message = IsChineseUi() ? "租约秒数必须在 1 到 604800 之间。" : "Lease seconds must be between 1 and 604800.";
            return false;
        }

        message = IsChineseUi() ? "参数有效。" : "Parameters are valid.";
        return true;
    }

    private static bool TryGetIpv4(string text, out IPAddress ip)
    {
        if (!IPAddress.TryParse(text.Trim(), out var parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            ip = IPAddress.None;
            return false;
        }
        ip = parsed;
        return true;
    }

    private static bool TryGetValidIpv4Mask(string text, out IPAddress mask)
    {
        if (!TryGetIpv4(text, out var parsed))
        {
            mask = IPAddress.None;
            return false;
        }
        mask = parsed;
        var prefix = IpNetwork.PrefixLength(mask);
        var expected = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return IpNetwork.ToUInt32(mask) == expected;
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
        var validIp = TryGetIpv4(ManualIp.Text, out var manualIp);
        var validMask = TryGetValidIpv4Mask(ManualMask.Text, out var manualMask);
        var targetText = ManualTargetIp.Text.Trim();
        var validTarget = string.IsNullOrWhiteSpace(targetText)
            || (TryGetIpv4(targetText, out var targetIp) && validIp && validMask && IpNetwork.SameSubnet(manualIp, targetIp, manualMask));
        var canApply = hasAdapter && validIp && validMask && validTarget && !_scanRunning && _safetyOnboardingCompleted;
        if (ScanValidation != null)
        {
            ScanValidation.Text = !hasAdapter
                ? (IsChineseUi() ? "请先选择网卡。" : "Select an adapter first.")
                : !validIp
                    ? (IsChineseUi() ? "本机 IP 必须是有效的 IPv4 地址。" : "Local IP must be a valid IPv4 address.")
                    : !validMask
                        ? (IsChineseUi() ? "子网掩码必须是连续的 IPv4 掩码。" : "Subnet mask must be a contiguous IPv4 mask.")
                        : !validTarget
                            ? (IsChineseUi() ? "目标 IP 必须是同一网段内的有效 IPv4 地址，留空则扫描整个网段。" : "Target IP must be a valid IPv4 address in the same subnet, or leave it empty for a subnet scan.")
                            : !_safetyOnboardingCompleted
                                ? (IsChineseUi() ? "请先完成安全引导，网络变更按钮才会解锁。" : "Complete the Safety Guide before network-change actions are unlocked.")
                            : (IsChineseUi() ? $"将扫描 {manualIp}/{IpNetwork.PrefixLength(manualMask)}。" : $"Scan scope: {manualIp}/{IpNetwork.PrefixLength(manualMask)}.");
            ScanValidation.Foreground = canApply ? Brushes.ForestGreen : Brushes.Brown;
        }
        BtnPreviewScan.IsEnabled = hasAdapter && validIp && validMask && validTarget && !_scanRunning;
        BtnApplyScan.IsEnabled = canApply;
        BtnStopScan.IsEnabled = _scanRunning;
        BtnRestoreScan.IsEnabled = hasAdapter && SelectedAdapter != null && _originalAdapterConfigs.ContainsKey(SelectedAdapter.InterfaceIndex) && !_scanRunning && _safetyOnboardingCompleted;
        BtnAddFavorite.IsEnabled = validIp && validMask && !_scanRunning;
        BtnPreviewScan.Background = BtnPreviewScan.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnApplyScan.Background = canApply ? Brushes.LightGreen : Brushes.LightGray;
        BtnStopScan.Background = _scanRunning ? Brushes.OrangeRed : Brushes.LightGray;
        BtnStopScan.Foreground = _scanRunning ? Brushes.White : Brushes.Black;
        BtnRestoreScan.Background = BtnRestoreScan.IsEnabled ? Brushes.MistyRose : Brushes.LightGray;
        BtnAddFavorite.Background = BtnAddFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        RefreshButtonStateColors();
        UpdateSessionStatus();
        UpdateProfileButtons();
    }

    private void UpdateAdapterActionButtons()
    {
        if (!IsInitialized) return;
        var enabled = SelectedAdapter != null && _safetyOnboardingCompleted && !_adapterActionInProgress && !_closingCleanupStarted && !_dhcpServer.IsRunning;
        BtnRestartAdapter.IsEnabled = enabled;
        BtnChangeMac.IsEnabled = enabled;
        BtnRestartAdapter.Background = enabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnChangeMac.Background = enabled ? Brushes.LightBlue : Brushes.LightGray;
        RefreshButtonStateColors();
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
        UpdateSelectionHints();
        UpdateFilterButtons();
        RefreshButtonStateColors();
    }

    private NetworkProfile CaptureCurrentProfile(string name, string description)
    {
        var leaseSeconds = int.TryParse(DhcpLeaseSeconds.Text.Trim(), out var lease) ? lease : 3600;
        return new NetworkProfile
        {
            Name = name,
            Description = description,
            Dhcp = new DefaultDhcpSettings
            {
                ServerIp = DhcpServerIp.Text.Trim(),
                SubnetMask = DhcpMask.Text.Trim(),
                PoolStart = DhcpStart.Text.Trim(),
                PoolEnd = DhcpEnd.Text.Trim(),
                Gateway = DhcpGateway.Text.Trim(),
                Dns = DhcpDns.Text.Trim(),
                LeaseSeconds = leaseSeconds
            },
            ManualIp = ManualIp.Text.Trim(),
            ManualMask = ManualMask.Text.Trim(),
            ManualTargetIp = ManualTargetIp.Text.Trim(),
            Routes = StaticRoutes.Select(CloneRoute).ToList()
        };
    }

    private static StaticRouteRule CloneRoute(StaticRouteRule source) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        DestinationPrefix = source.DestinationPrefix,
        AddressFamily = source.AddressFamily,
        AdapterId = source.AdapterId,
        AdapterName = source.AdapterName,
        AdapterMac = source.AdapterMac,
        NextHop = source.NextHop,
        RouteMetric = source.RouteMetric,
        InterfaceMetric = source.InterfaceMetric
    };

    private void RefreshProfilesView(NetworkProfile? select = null)
    {
        _suppressProfileSelection = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in _allProfiles.OrderByDescending(x => x.UpdatedAt)) Profiles.Add(profile);
            if (select != null) ProfileGrid.SelectedItem = Profiles.FirstOrDefault(x => x.Id.Equals(select.Id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressProfileSelection = false;
        }
        UpdateProfileButtons();
        UpdateProfileSummary();
        UpdateEmptyStates();
    }

    private void UpdateProfileSummary()
    {
        if (TxtProfileSummary == null) return;
        TxtProfileSummary.Text = IsChineseUi() ? $"本地方案 {_allProfiles.Count} 个" : $"{_allProfiles.Count} local profile(s)";
    }

    private void UpdateProfileButtons()
    {
        if (!IsInitialized) return;
        var selected = ProfileGrid.SelectedItem is NetworkProfile;
        var blocked = _scanRunning || _dhcpServer.IsRunning || _closingCleanupStarted;
        BtnSaveProfile.IsEnabled = !blocked;
        BtnLoadProfile.IsEnabled = selected && !blocked;
        BtnCompareProfile.IsEnabled = selected && !blocked;
        BtnDuplicateProfile.IsEnabled = selected && !blocked;
        BtnImportProfile.IsEnabled = !blocked;
        BtnExportProfile.IsEnabled = _allProfiles.Count > 0 && !blocked;
        BtnDeleteProfile.IsEnabled = selected && !blocked;
        BtnSaveProfile.Background = BtnSaveProfile.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnLoadProfile.Background = BtnLoadProfile.IsEnabled ? Brushes.LightGreen : Brushes.LightGray;
        BtnCompareProfile.Background = BtnCompareProfile.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnDuplicateProfile.Background = BtnDuplicateProfile.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnImportProfile.Background = BtnImportProfile.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnExportProfile.Background = BtnExportProfile.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnDeleteProfile.Background = BtnDeleteProfile.IsEnabled ? Brushes.MistyRose : Brushes.LightGray;
        UpdateSelectionHints();
        RefreshButtonStateColors();
    }

    private Brush ThemedActionBrush(bool enabled, string role = "normal")
    {
        var key = !enabled
            ? "DisabledActionBrush"
            : role switch
            {
                "positive" => "PositiveActionBrush",
                "danger" => "DangerActionBrush",
                "warning" => "WarningActionBrush",
                _ => "ActionBrush"
            };
        return Resources[key] as Brush ?? Brushes.LightBlue;
    }

    private void RefreshButtonStateColors()
    {
        if (!IsInitialized) return;
        BtnStartDhcp.Background = ThemedActionBrush(BtnStartDhcp.IsEnabled, "positive");
        BtnStopDhcp.Background = ThemedActionBrush(BtnStopDhcp.IsEnabled, "danger");
        BtnStopDhcp.Foreground = BtnStopDhcp.IsEnabled ? Brushes.White : (Brush)Resources["TextBrush"];
        BtnPreviewScan.Background = ThemedActionBrush(BtnPreviewScan.IsEnabled);
        BtnApplyScan.Background = ThemedActionBrush(BtnApplyScan.IsEnabled, "positive");
        BtnStopScan.Background = ThemedActionBrush(BtnStopScan.IsEnabled, "danger");
        BtnStopScan.Foreground = BtnStopScan.IsEnabled ? Brushes.White : (Brush)Resources["TextBrush"];
        BtnRestoreScan.Background = ThemedActionBrush(BtnRestoreScan.IsEnabled, "danger");
        BtnAddFavorite.Background = ThemedActionBrush(BtnAddFavorite.IsEnabled);
        BtnRestartAdapter.Background = ThemedActionBrush(BtnRestartAdapter.IsEnabled, "warning");
        BtnChangeMac.Background = ThemedActionBrush(BtnChangeMac.IsEnabled, "warning");
        BtnNewFavorite.Background = ThemedActionBrush(BtnNewFavorite.IsEnabled);
        BtnImportFavorite.Background = ThemedActionBrush(BtnImportFavorite.IsEnabled);
        BtnExportFavorite.Background = ThemedActionBrush(BtnExportFavorite.IsEnabled);
        BtnFavoriteColumns.Background = ThemedActionBrush(BtnFavoriteColumns.IsEnabled);
        BtnLoadFavorite.Background = ThemedActionBrush(BtnLoadFavorite.IsEnabled, "positive");
        BtnApplyFavorite.Background = ThemedActionBrush(BtnApplyFavorite.IsEnabled, "positive");
        BtnDeleteFavorite.Background = ThemedActionBrush(BtnDeleteFavorite.IsEnabled, "danger");
        BtnOpenFavorite.Background = ThemedActionBrush(BtnOpenFavorite.IsEnabled);
        BtnSaveProfile.Background = ThemedActionBrush(BtnSaveProfile.IsEnabled);
        BtnLoadProfile.Background = ThemedActionBrush(BtnLoadProfile.IsEnabled, "positive");
        BtnCompareProfile.Background = ThemedActionBrush(BtnCompareProfile.IsEnabled);
        BtnDuplicateProfile.Background = ThemedActionBrush(BtnDuplicateProfile.IsEnabled);
        BtnImportProfile.Background = ThemedActionBrush(BtnImportProfile.IsEnabled);
        BtnExportProfile.Background = ThemedActionBrush(BtnExportProfile.IsEnabled);
        BtnDeleteProfile.Background = ThemedActionBrush(BtnDeleteProfile.IsEnabled, "danger");
        BtnRecovery.Background = ThemedActionBrush(BtnRecovery.IsEnabled);
        BtnOpenHistory.Background = ThemedActionBrush(BtnOpenHistory.IsEnabled);
        BtnOpenRecovery.Background = ThemedActionBrush(BtnOpenRecovery.IsEnabled);
        BtnClearScanFilter.Background = ThemedActionBrush(BtnClearScanFilter.IsEnabled);
        BtnClearFavoriteSearch.Background = ThemedActionBrush(BtnClearFavoriteSearch.IsEnabled);
        BtnClearHistoryFilter.Background = ThemedActionBrush(BtnClearHistoryFilter.IsEnabled);
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_scanRunning || _dhcpServer.IsRunning) return;
        var dialog = new ProfileEditorWindow(IsChineseUi()) { Owner = this };
        dialog.ApplyOwnerTheme(this);
        if (dialog.ShowDialog() != true) return;
        var existing = _allProfiles.FirstOrDefault(x => x.Name.Equals(dialog.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !AppDialog.Show(this, IsChineseUi() ? "覆盖配置方案" : "Replace Profile",
            IsChineseUi() ? $"“{dialog.ProfileName}”已存在，是否覆盖？" : $"“{dialog.ProfileName}” already exists. Replace it?",
            confirm: true, danger: true)) return;

        var profile = CaptureCurrentProfile(dialog.ProfileName, dialog.ProfileDescription);
        if (existing != null)
        {
            profile.Id = existing.Id;
            profile.CreatedAt = existing.CreatedAt;
            _allProfiles.Remove(existing);
        }
        _allProfiles.Add(profile);
        ProfileStore.Save(_paths.ProfilesFile, _allProfiles, _logger);
        RefreshProfilesView(profile);
        AddOperationHistory("Profile", "", "", "Saved / 已保存", profile.Name, scope: "Local profile / 本地方案");
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is NetworkProfile profile) LoadProfileIntoForms(profile);
        else ShowActionFeedbackKey("selection.profile");
    }

    private void LoadProfileIntoForms(NetworkProfile profile)
    {
        var d = profile.Dhcp ?? new DefaultDhcpSettings();
        DhcpServerIp.Text = d.ServerIp;
        DhcpMask.Text = d.SubnetMask;
        DhcpStart.Text = d.PoolStart;
        DhcpEnd.Text = d.PoolEnd;
        DhcpGateway.Text = d.Gateway;
        DhcpDns.Text = d.Dns;
        DhcpLeaseSeconds.Text = d.LeaseSeconds.ToString();
        if (!string.IsNullOrWhiteSpace(d.Gateway)) LblDhcpGateway.Visibility = Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(d.Dns)) LblDhcpDns.Visibility = Visibility.Visible;
        ManualIp.Text = profile.ManualIp;
        ManualMask.Text = profile.ManualMask;
        ManualTargetIp.Text = profile.ManualTargetIp;
        StaticRoutes.Clear();
        foreach (var source in profile.Routes ?? [])
        {
            var rule = CloneRoute(source);
            var adapter = Adapters.FirstOrDefault(x => x.Id.Equals(rule.AdapterId, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(rule.AdapterName) && x.Name.Equals(rule.AdapterName, StringComparison.OrdinalIgnoreCase)));
            if (adapter != null)
            {
                rule.AdapterId = adapter.Id;
                rule.AdapterName = adapter.Name;
                rule.AdapterMac = adapter.MacAddress;
            }
            StaticRoutes.Add(rule);
        }
        UpdateDhcpInputValidation();
        UpdateManualScanButtons();
        RouteGrid.Items.Refresh();
        UpdateEmptyStates();
        Tabs.SelectedItem = TabDhcp;
        AddOperationHistory("Profile", "", "", "Loaded / 已加载", profile.Name, scope: "Forms / 表单");
    }

    private void CompareProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is not NetworkProfile profile) { ShowActionFeedbackKey("selection.profile"); return; }
        var current = CaptureCurrentProfile("current", "");
        var differences = new List<string>();
        void Diff(string label, string saved, string actual)
        {
            if (!string.Equals(saved, actual, StringComparison.OrdinalIgnoreCase))
                differences.Add(IsChineseUi() ? $"{label}：方案 [{saved}]，当前 [{actual}]" : $"{label}: profile [{saved}], current [{actual}]");
        }

        Diff(IsChineseUi() ? "DHCP 本机 IP" : "DHCP local IP", profile.Dhcp.ServerIp, current.Dhcp.ServerIp);
        Diff(IsChineseUi() ? "DHCP 掩码" : "DHCP mask", profile.Dhcp.SubnetMask, current.Dhcp.SubnetMask);
        Diff(IsChineseUi() ? "DHCP 地址池" : "DHCP pool", $"{profile.Dhcp.PoolStart}-{profile.Dhcp.PoolEnd}", $"{current.Dhcp.PoolStart}-{current.Dhcp.PoolEnd}");
        Diff(IsChineseUi() ? "手动扫描本机 IP" : "Manual local IP", profile.ManualIp, current.ManualIp);
        Diff(IsChineseUi() ? "手动扫描掩码" : "Manual mask", profile.ManualMask, current.ManualMask);
        Diff(IsChineseUi() ? "手动扫描目标" : "Manual target", profile.ManualTargetIp, current.ManualTargetIp);
        var savedRoutes = new HashSet<string>((profile.Routes ?? []).Select(ProfileRouteKey), StringComparer.OrdinalIgnoreCase);
        var currentRoutes = new HashSet<string>(current.Routes.Select(ProfileRouteKey), StringComparer.OrdinalIgnoreCase);
        foreach (var route in savedRoutes.Except(currentRoutes)) differences.Add((IsChineseUi() ? "方案多出的路由：" : "Profile-only route: ") + route);
        foreach (var route in currentRoutes.Except(savedRoutes)) differences.Add((IsChineseUi() ? "当前多出的路由：" : "Current-only route: ") + route);
        var message = differences.Count == 0
            ? (IsChineseUi() ? "当前表单与方案完全一致，未发现差异。" : "The current forms match the profile; no differences found.")
            : string.Join(Environment.NewLine, differences);
        AppDialog.Show(this, IsChineseUi() ? $"方案对比：{profile.Name}" : $"Profile comparison: {profile.Name}", message);
    }

    private static string ProfileRouteKey(StaticRouteRule route) =>
        string.Join("|", route.AddressFamily, route.DestinationPrefix, route.AdapterId, route.AdapterName, route.NextHop, route.RouteMetric);

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is not NetworkProfile source) { ShowActionFeedbackKey("selection.profile"); return; }
        var defaultName = source.Name + (IsChineseUi() ? "（副本）" : " (Copy)");
        var dialog = new ProfileEditorWindow(IsChineseUi(), defaultName, source.Description) { Owner = this };
        dialog.ApplyOwnerTheme(this);
        if (dialog.ShowDialog() != true) return;
        var profile = ProfileStore.Clone(source);
        profile.Id = Guid.NewGuid().ToString("N");
        profile.Name = dialog.ProfileName;
        profile.Description = dialog.ProfileDescription;
        profile.CreatedAt = DateTime.Now;
        profile.UpdatedAt = DateTime.Now;
        _allProfiles.Add(profile);
        ProfileStore.Save(_paths.ProfilesFile, _allProfiles, _logger);
        RefreshProfilesView(profile);
        AddOperationHistory("Profile", "", "", "Duplicated / 已复制", profile.Name, scope: "Local profile / 本地方案");
    }

    private void ImportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = IsChineseUi() ? "导入配置方案" : "Import Network Profiles" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = ProfileStore.Load(dialog.FileName, _logger);
            if (imported.Count == 0)
            {
                AppDialog.Show(this, IsChineseUi() ? "导入方案" : "Import Profiles", IsChineseUi() ? "文件中没有可用配置方案。" : "The file contains no usable profiles.", danger: true);
                return;
            }
            var validProfiles = new List<NetworkProfile>();
            var skipped = new List<string>();
            foreach (var profile in imported)
            {
                if (TryValidateImportedProfile(profile, out var issue)) validProfiles.Add(profile);
                else skipped.Add($"{profile.Name}: {issue}");
            }
            var duplicateImported = validProfiles.Count - validProfiles
                .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Sum(x => 1);
            validProfiles = validProfiles
                .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last())
                .ToList();
            var replaceCount = validProfiles.Count(profile => _allProfiles.Any(x => x.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)
                || x.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)));
            var newCount = validProfiles.Count - replaceCount;
            var preview = IsChineseUi()
                ? $"文件：{Path.GetFileName(dialog.FileName)}\n读取：{imported.Count} 个方案\n将新增：{newCount} 个\n将覆盖：{replaceCount} 个\n将跳过：{skipped.Count + duplicateImported} 个\n\n"
                  + (skipped.Count == 0 && duplicateImported == 0 ? "没有发现格式问题。" : "发现问题的方案不会写入：")
                : $"File: {Path.GetFileName(dialog.FileName)}\nRead: {imported.Count} profile(s)\nNew: {newCount}\nReplace: {replaceCount}\nSkipped: {skipped.Count + duplicateImported}\n\n"
                  + (skipped.Count == 0 && duplicateImported == 0 ? "No format problems found." : "Profiles with issues will not be written:");
            if (skipped.Count > 0)
            {
                preview += Environment.NewLine + string.Join(Environment.NewLine, skipped.Take(8));
                if (skipped.Count > 8) preview += Environment.NewLine + (IsChineseUi() ? $"……另有 {skipped.Count - 8} 个。" : $"...and {skipped.Count - 8} more.");
            }
            if (duplicateImported > 0) preview += Environment.NewLine + (IsChineseUi() ? "同名方案只保留文件中的最后一份。" : "For duplicate names, only the last entry in the file will be kept.");
            if (validProfiles.Count == 0)
            {
                AppDialog.Show(this, IsChineseUi() ? "导入方案" : "Import Profiles", preview, danger: true);
                return;
            }
            if (!AppDialog.Show(this, IsChineseUi() ? "确认导入方案" : "Review Imported Profiles", preview, confirm: true, danger: skipped.Count > 0 || duplicateImported > 0)) return;

            foreach (var profile in validProfiles)
            {
                var existing = _allProfiles.FirstOrDefault(x => x.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)
                    || x.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    profile.Id = existing.Id;
                    profile.CreatedAt = existing.CreatedAt;
                    _allProfiles.Remove(existing);
                }
                _allProfiles.Add(profile);
            }
            ProfileStore.Save(_paths.ProfilesFile, _allProfiles, _logger);
            RefreshProfilesView();
            AddOperationHistory("Profile", "", "", "Imported / 已导入", $"{validProfiles.Count} profile(s); skipped {skipped.Count + duplicateImported}", scope: Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            _logger.Error("Import profiles failed", ex);
            AppDialog.Show(this, IsChineseUi() ? "导入方案失败" : "Import Profiles Failed", ExplainFailure(ex, _lang.T("help.profile.import")), danger: true);
        }
    }

    private bool TryValidateImportedProfile(NetworkProfile profile, out string issue)
    {
        issue = "";
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            issue = IsChineseUi() ? "缺少方案名称" : "missing profile name";
            return false;
        }
        if (profile.Name.Trim().Length > 120)
        {
            issue = IsChineseUi() ? "方案名称过长" : "profile name is too long";
            return false;
        }

        var dhcp = profile.Dhcp ?? new DefaultDhcpSettings();
        var validationIssue = "";
        bool OptionalIp(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || TryGetIpv4(value, out _)) return true;
            validationIssue = IsChineseUi() ? $"{label} 不是有效 IPv4" : $"{label} is not a valid IPv4";
            return false;
        }
        bool OptionalMask(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || TryGetValidIpv4Mask(value, out _)) return true;
            validationIssue = IsChineseUi() ? $"{label} 不是连续掩码" : $"{label} is not a contiguous mask";
            return false;
        }

        if (!OptionalIp(dhcp.ServerIp, "DHCP IP") || !OptionalMask(dhcp.SubnetMask, "DHCP mask")
            || !OptionalIp(dhcp.PoolStart, "DHCP pool start") || !OptionalIp(dhcp.PoolEnd, "DHCP pool end")
            || !OptionalIp(dhcp.Gateway, "DHCP gateway") || !OptionalIp(dhcp.Dns, "DHCP DNS")
            || !OptionalIp(profile.ManualIp, "Manual IP") || !OptionalMask(profile.ManualMask, "Manual mask")
            || !OptionalIp(profile.ManualTargetIp, "Manual target"))
        {
            issue = validationIssue;
            return false;
        }

        if (TryGetIpv4(profile.ManualIp, out var manualIp) && TryGetValidIpv4Mask(profile.ManualMask, out var manualMask)
            && TryGetIpv4(profile.ManualTargetIp, out var targetIp) && !IpNetwork.SameSubnet(manualIp, targetIp, manualMask))
        {
            issue = IsChineseUi() ? "手动目标不在本机同一网段" : "manual target is outside the local subnet";
            return false;
        }
        foreach (var route in profile.Routes ?? [])
        {
            try { StaticRouteValidator.Normalize(route); }
            catch (Exception ex)
            {
                issue = IsChineseUi() ? $"路由无效：{ex.Message}" : $"invalid route: {ex.Message}";
                return false;
            }
        }
        return true;
    }

    private void ExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = $"NetBoot-profiles-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ProfileStore.Save(dialog.FileName, _allProfiles, _logger);
            ShowActionFeedback("配置方案已导出。", "Profiles exported.");
            AddOperationHistory("Profile", "", "", "Exported / 已导出", $"{_allProfiles.Count} profile(s)", scope: Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            _logger.Error("Export profiles failed", ex);
            AppDialog.Show(this, IsChineseUi() ? "导出方案失败" : "Export Profiles Failed", ExplainFailure(ex, _lang.T("help.profile.export")), danger: true);
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is not NetworkProfile profile) { ShowActionFeedbackKey("selection.profile"); return; }
        if (!AppDialog.Show(this, IsChineseUi() ? "删除配置方案" : "Delete Profile",
            IsChineseUi() ? $"确认删除“{profile.Name}”？不会修改网络配置。" : $"Delete “{profile.Name}”? Network configuration will not be changed.",
            confirm: true, danger: true)) return;
        _allProfiles.RemoveAll(x => x.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
        ProfileStore.Save(_paths.ProfilesFile, _allProfiles, _logger);
        RefreshProfilesView();
        AddOperationHistory("Profile", "", "", "Deleted / 已删除", profile.Name, scope: "Local profile / 本地方案");
    }

    private void ProfileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressProfileSelection) UpdateProfileButtons();
    }

    private void ProfileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadProfile_Click(sender, e);

    private void HistoryFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshOperationHistoryView();

    private void RouteGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionHints();

    private void UpdateSelectionHints()
    {
        if (TxtFavoriteSelectionHint != null)
            TxtFavoriteSelectionHint.Text = FavoriteGrid?.SelectedItem is FavoriteConfig ? "" : Ui("selection.favorite");
        if (TxtProfileSelectionHint != null)
            TxtProfileSelectionHint.Text = ProfileGrid?.SelectedItem is NetworkProfile ? "" : Ui("selection.profile");
        if (TxtRouteSelectionHint != null)
            TxtRouteSelectionHint.Text = RouteGrid?.SelectedItem is StaticRouteRule ? "" : Ui("selection.route");
    }

    private IEnumerable<OperationHistoryItem> GetSelectedOrVisibleHistory()
    {
        var selected = HistoryGrid.SelectedItems.OfType<OperationHistoryItem>().ToList();
        return selected.Count > 0 ? selected : OperationHistory;
    }

    private void CopyHistory_Click(object sender, RoutedEventArgs e)
    {
        var rows = GetSelectedOrVisibleHistory().ToList();
        var header = IsChineseUi() ? "时间\t操作\t范围\t状态\t详情\t耗时\t回滚" : "Time\tOperation\tScope\tStatus\tDetail\tDuration\tRollback";
        var text = header + Environment.NewLine + string.Join(Environment.NewLine, rows.Select(x => string.Join("\t", x.Time, x.Type, x.Scope, x.Status, x.Detail, x.DurationDisplay, x.RollbackDisplay)));
        if (TryCopyText(rows.Count == 0 ? "" : text, $"已复制 {rows.Count} 条操作历史。", $"Copied {rows.Count} operation-history row(s)."))
            _logger.Info($"Operation history copied: count={rows.Count}");
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_operationHistory.Count == 0) return;
        if (!AppDialog.Show(this, IsChineseUi() ? "清空操作历史" : "Clear Operation History",
            IsChineseUi() ? "只清空本地操作历史，不会删除运行日志，也不会撤销网络配置。是否继续？" : "Only local operation history will be cleared. Runtime logs and network configuration will not be changed. Continue?",
            confirm: true, danger: true)) return;
        _operationHistory.Clear();
        JsonStore.Save(_paths.OperationHistoryFile, _operationHistory);
        RefreshOperationHistoryView();
        UpdateLastOperationPresentation();
        ShowActionFeedback("操作历史已清空。", "Operation history cleared.");
    }

    private void HistoryGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        CopyHistory_Click(sender, e);
        e.Handled = true;
    }

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedAdapter == null) return;
        UpdateAdapterIpText(SelectedAdapter);
        TxtMac.Text = SelectedAdapter.MacAddress;
        TxtGateway.Text = SelectedAdapter.Gateway;
        TxtStatus.Text = SelectedAdapter.Status;
        TxtStatus.Foreground = SelectedAdapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) ? Brushes.ForestGreen : Brushes.Firebrick;
        UpdateAdapterDetails(SelectedAdapter);
        TxtManualAdapterIp.Text = BuildAdapterIpDisplay(SelectedAdapter);
        UpdateManualScanButtons();
        UpdateFavoriteButtons();
        UpdateAdapterActionButtons();
        UpdateSessionStatus();
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
        if (!EnsureSafetyOnboarding()) return;
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
            await _adapterService.RestoreIPv4ConfigAsync(adapter, snapshot, OperationToken);
            await VerifyRestoredAdapterConfigAsync(adapter, snapshot, OperationToken);
            AddOperationHistory("Rollback", backup.IpAddress, adapter.MacAddress, "Completed / 已完成", $"{(backup.DhcpEnabled ? "DHCP" : "Static")} {backup.IpAddress}/{backup.PrefixLength}", scope: adapter.Name);
            await RefreshAdaptersAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Adapter rollback canceled by user or timeout");
        }
        catch (Exception ex)
        {
            _logger.Error("Adapter rollback failed", ex);
            AppDialog.Show(this, "Rollback / 回滚", ExplainFailure(ex, _lang.T("help.rollback")), danger: true);
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
            ShowActionFeedback("结果已导出。", "Results exported.");
        }
        catch (Exception ex)
        {
            _logger.Error("Export results failed", ex);
            AppDialog.Show(this, "Export / 导出", ExplainFailure(ex, _lang.T("help.export.results")), danger: true);
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
        var admin = false;
        try { admin = _adapterService.IsAdministrator(); } catch { }
        if (adapter == null)
        {
            AppDialog.Show(this, _lang.T("diagnostics"), IsChineseUi() ? "❌ 未选择网卡。\n\n建议下一步：先刷新并选择一个网卡。" : "❌ No adapter selected.\n\nNext step: refresh and select an adapter.", danger: true);
            return;
        }

        var checks = new List<string>
        {
            $"{(admin ? "✅" : "❌")} {(IsChineseUi() ? "管理员权限" : "Administrator privilege")}: {(admin ? (IsChineseUi() ? "可用" : "Available") : (IsChineseUi() ? "不可用" : "Unavailable"))}",
            $"{(adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) ? "✅" : "⚠️")} {(IsChineseUi() ? "链路状态" : "Link state")}: {adapter.ConnectionDisplay}",
            $"{(adapter.IsWifi ? "⚠️" : "✅")} {(IsChineseUi() ? "有线边界" : "Wired boundary")}: {adapter.KindDisplay}",
            $"{(adapter.IsVirtual ? "⚠️" : "✅")} {(IsChineseUi() ? "虚拟网卡" : "Virtual adapter")}: {(adapter.IsVirtual ? (IsChineseUi() ? "是" : "Yes") : (IsChineseUi() ? "否" : "No"))}",
            $"{(TryGetIpv4(adapter.IPv4Address, out _) ? "✅" : "⚠️")} IPv4: {adapter.IPv4Address}",
            $"{(TryGetValidIpv4Mask(adapter.SubnetMask, out _) ? "✅" : "⚠️")} {(IsChineseUi() ? "子网掩码" : "Subnet mask")}: {adapter.SubnetMask}",
            $"{(adapter.HasGateway ? "⚠️" : "✅")} {(IsChineseUi() ? "默认网关" : "Default gateway")}: {(string.IsNullOrWhiteSpace(adapter.Gateway) ? "-" : adapter.Gateway)}",
            $"{(_originalAdapterConfigs.ContainsKey(adapter.InterfaceIndex) ? "✅" : "ℹ️")} {(IsChineseUi() ? "回滚备份" : "Rollback backup")}: {(_originalAdapterConfigs.ContainsKey(adapter.InterfaceIndex) ? (IsChineseUi() ? "本次运行可用" : "Available this run") : (IsChineseUi() ? "未捕获" : "Not captured"))}"
        };
        var next = !admin
            ? (IsChineseUi() ? "使用“以管理员身份运行”重新启动工具。" : "Restart the tool with Run as administrator.")
            : !adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)
                ? (IsChineseUi() ? "确认网线、交换机端口和网卡启用状态。" : "Check the cable, switch port, and adapter enabled state.")
                : adapter.IsWifi || adapter.IsVirtual
                    ? (IsChineseUi() ? "DHCP/IP 配置类操作默认不建议用于此网卡；路由只读/路由草稿仍可继续。" : "DHCP/IP configuration is not recommended on this adapter by default; read-only routes and drafts can still continue.")
                    : (IsChineseUi() ? "基础检查通过；执行变更前仍请先使用预览。" : "Basic checks passed; use Preview before applying a change.");
        AppDialog.Show(this, _lang.T("diagnostics"),
            string.Join(Environment.NewLine, checks) + Environment.NewLine + Environment.NewLine
            + (IsChineseUi() ? "下一步：" : "Next step: ") + next);
    }

    private void SafetyGuide_Click(object sender, RoutedEventArgs e) => ShowSafetyGuide(firstRun: false);

    private bool EnsureSafetyOnboarding()
    {
        return _safetyOnboardingCompleted || ShowSafetyGuide(firstRun: false);
    }

    private bool ShowSafetyGuide(bool firstRun)
    {
        var title = firstRun
            ? (IsChineseUi() ? "首次运行安全引导" : "First-run Safety Guide")
            : _lang.T("safety.guide");
        var message = IsChineseUi()
            ? "在执行任何网络变更前，请确认以下边界：\n\n"
              + "□ 仅对明确选中的网卡操作，并核对名称、MAC、InterfaceIndex。\n"
              + "□ DHCP/IP/路由/MAC/网卡重启可能中断通信；不要用于生产网络。\n"
              + "□ DHCP 必须在隔离调试网络运行；已有 DHCP 会先检测。\n"
              + "□ 应用前会保存可回滚备份；退出或停止时按恢复策略处理。\n"
              + "□ 预览只读，只有确认应用后才会写入系统。\n\n"
              + (firstRun ? "点击“确认并解锁”后才会启用网络变更；选择“只读模式”可继续查看、导入方案和诊断，但不会解锁变更操作。" : "确认后会解锁网络变更按钮；你也可以随时重新打开本引导复核边界。")
            : "Before any network change, confirm these boundaries:\n\n"
              + "□ Operate only on the explicitly selected adapter; verify name, MAC, and InterfaceIndex.\n"
              + "□ DHCP/IP/routes/MAC/adapter restart can interrupt traffic; never use them on production networks.\n"
              + "□ DHCP is for isolated test networks; an existing DHCP server is checked first.\n"
              + "□ A recoverable backup is saved before changes; stop/exit follows the restoration policy.\n"
              + "□ Preview is read-only; the system changes only after an explicit apply confirmation.\n\n"
              + (firstRun ? "Choose “Acknowledge & Unlock” to enable network changes, or “Read-only mode” to continue viewing, importing profiles, and diagnosing without unlocking changes." : "Acknowledgement unlocks network-change buttons; reopen this guide anytime to review the boundary.");

        if (!AppDialog.Show(this, title, message, confirm: true, danger: false,
            okText: firstRun ? (IsChineseUi() ? "确认并解锁" : "Acknowledge & Unlock") : null,
            cancelText: firstRun ? (IsChineseUi() ? "只读模式" : "Read-only mode") : null))
        {
            if (firstRun)
            {
                _safetyOnboardingCompleted = false;
                _settings.SafetyOnboardingCompleted = false;
                UpdateManualScanButtons();
                UpdateAdapterActionButtons();
                SetDhcpRunningState(_dhcpServer.IsRunning);
                UpdateSessionStatus();
                ShowActionFeedback("已进入只读模式，网络变更仍保持锁定。", "Read-only mode enabled; network changes remain locked.");
                _logger.Info("Safety onboarding declined; read-only mode selected");
            }
            return false;
        }
        _safetyOnboardingCompleted = true;
        _settings.SafetyOnboardingCompleted = true;
        JsonStore.Save(_paths.SettingsFile, _settings);
        _logger.Info("Safety onboarding acknowledged");
        UpdateManualScanButtons();
        UpdateAdapterActionButtons();
        SetDhcpRunningState(_dhcpServer.IsRunning);
        UpdateSessionStatus();
        return true;
    }

    private async void PackageLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "ZIP (*.zip)|*.zip", FileName = $"NetBoot-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (dialog.ShowDialog(this) != true) return;
        var redact = RedactSupportPackage.IsChecked == true;
        try
        {
            SetBusy(true, "Creating support package... / 正在生成支持包...");
            var manifest = IsChineseUi()
                ? $"本支持包不包含收藏夹凭据。包含运行日志、设置、网卡备份、配置方案和操作历史。网络地址脱敏：{(redact ? "是" : "否")}。\n"
                : $"This support package excludes Favorite credentials. It contains runtime logs, settings, adapter backups, profiles, and operation history. Network address redaction: {(redact ? "enabled" : "disabled")}.\n";
            await Task.Run(() =>
            {
                OperationToken.ThrowIfCancellationRequested();
                using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log")) AddSupportFile(archive, file, Path.Combine("logs", Path.GetFileName(file)), redact);
                if (File.Exists(_paths.SettingsFile)) AddSupportFile(archive, _paths.SettingsFile, "appsettings.json", redact);
                if (File.Exists(_paths.AdapterBackupsFile)) AddSupportFile(archive, _paths.AdapterBackupsFile, "adapter-backups.json", redact);
                if (File.Exists(_paths.MacBackupsFile)) AddSupportFile(archive, _paths.MacBackupsFile, "mac-backups.json", redact);
                if (File.Exists(_paths.ProfilesFile)) AddSupportFile(archive, _paths.ProfilesFile, "network-profiles.json", redact);
                if (File.Exists(_paths.OperationHistoryFile)) AddSupportFile(archive, _paths.OperationHistoryFile, "operation-history.json", redact);
                using var writer = new StreamWriter(archive.CreateEntry("manifest.txt").Open(), Encoding.UTF8);
                writer.Write(manifest);
            }, OperationToken);
            _logger.Info("Support package created: " + dialog.FileName);
            ShowActionFeedback("支持包已生成。", "Support package created.");
            AddOperationHistory("Support", "", "", "Completed / 已完成", Path.GetFileName(dialog.FileName), scope: "Local diagnostics / 本地诊断");
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Support package canceled");
        }
        catch (Exception ex)
        {
            _logger.Error("Support package failed", ex);
            AppDialog.Show(this, "Support / 支持包", ExplainFailure(ex, _lang.T("help.package.logs")), danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void AddSupportFile(ZipArchive archive, string sourcePath, string entryName, bool redact)
    {
        if (!redact)
        {
            archive.CreateEntryFromFile(sourcePath, entryName);
            return;
        }

        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(RedactSupportText(File.ReadAllText(sourcePath)));
    }

    private static string RedactSupportText(string text)
    {
        return SupportDataRedactor.RedactNetworkValues(text);
    }

    private void ApplyOwnedWindowTheme(Window window)
    {
        var windowBackground = Resources["WindowBackgroundBrush"] as Brush ?? Brushes.White;
        var panelBackground = Resources["PanelBackgroundBrush"] as Brush ?? Brushes.White;
        var inputBackground = Resources["InputBackgroundBrush"] as Brush ?? Brushes.White;
        var text = Resources["TextBrush"] as Brush ?? Brushes.Black;
        var border = Resources["BorderBrush"] as Brush ?? Brushes.LightGray;

        window.Background = windowBackground;
        window.Foreground = text;
        if (window.Content is DependencyObject content)
        {
            ApplyOwnedVisualTheme(content, panelBackground, inputBackground, text, border);
        }
    }

    private static void ApplyOwnedVisualTheme(
        DependencyObject root,
        Brush panelBackground,
        Brush inputBackground,
        Brush text,
        Brush border)
    {
        switch (root)
        {
            case Border value:
                value.Background = panelBackground;
                value.BorderBrush = border;
                break;
            case TextBox value:
                value.Background = inputBackground;
                value.Foreground = text;
                value.BorderBrush = border;
                break;
            case Button value:
                value.Background = inputBackground;
                value.Foreground = text;
                value.BorderBrush = border;
                break;
            case CheckBox value:
                value.Foreground = text;
                break;
            case ListBox value:
                value.Background = inputBackground;
                value.Foreground = text;
                value.BorderBrush = border;
                break;
            case DataGrid value:
                value.Background = panelBackground;
                value.Foreground = text;
                value.BorderBrush = border;
                break;
            case TextBlock value:
                value.Foreground = text;
                break;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            ApplyOwnedVisualTheme(child, panelBackground, inputBackground, text, border);
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _darkTheme = !_darkTheme;
        _settings.DarkTheme = _darkTheme;
        JsonStore.Save(_paths.SettingsFile, _settings);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var colors = _darkTheme
            ? new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#1F2328",
                ["PanelBackgroundBrush"] = "#2B3138",
                ["InputBackgroundBrush"] = "#22272E",
                ["TextBrush"] = "#F0F3F6",
                ["MutedTextBrush"] = "#B5C0CC",
                ["BorderBrush"] = "#52606D",
                ["AccentBrush"] = "#5BA9E6",
                ["DangerBrush"] = "#FF8B85",
                ["WarningBrush"] = "#F2B566",
                ["ActionBrush"] = "#304C63",
                ["PositiveActionBrush"] = "#245C43",
                ["DangerActionBrush"] = "#6A2E31",
                ["WarningActionBrush"] = "#6A4D2A",
                ["DisabledActionBrush"] = "#3B4148"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#F8FAFC",
                ["PanelBackgroundBrush"] = "#FFFFFF",
                ["InputBackgroundBrush"] = "#FFFFFF",
                ["TextBrush"] = "#162033",
                ["MutedTextBrush"] = "#57606A",
                ["BorderBrush"] = "#C8D3E0",
                ["AccentBrush"] = "#1769AA",
                ["DangerBrush"] = "#B3261E",
                ["WarningBrush"] = "#B06000",
                ["ActionBrush"] = "#DDEEFF",
                ["PositiveActionBrush"] = "#DFF4E5",
                ["DangerActionBrush"] = "#FDE2E2",
                ["WarningActionBrush"] = "#FFF0D6",
                ["DisabledActionBrush"] = "#E5E7EB"
            };
        foreach (var pair in colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(pair.Value);
            Resources[pair.Key] = new SolidColorBrush(color);
        }
        RootGrid.Background = (Brush)Resources["WindowBackgroundBrush"];
        Foreground = (Brush)Resources["TextBrush"];
        BusyOverlay.Background = new SolidColorBrush(_darkTheme ? Color.FromArgb(190, 31, 35, 40) : Color.FromArgb(170, 255, 255, 255));
        BtnTheme.Content = _darkTheme ? _lang.T("theme.light") : _lang.T("theme");
        UpdateAdapterDetails(SelectedAdapter);
        RefreshButtonStateColors();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = _paths.LogsDirectory, UseShellExecute = true });
            ShowActionFeedback("日志目录已打开。", "Log folder opened.");
        }
        catch (Exception ex)
        {
            _logger.Warn("Open logs folder failed: " + ex.Message);
            ShowActionFeedback("日志目录打开失败。", "Could not open the log folder.", error: true);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
        UpdateLogSummary();
        ShowActionFeedback("日志已清空。", "Log view cleared.");
    }
    private void CopyLog_Click(object sender, RoutedEventArgs e) => TryCopyText(
        new TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd).Text,
        "已复制当前日志。",
        "Visible log copied.");

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

    private void SettingsInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingsInputsLoading) return;
        _settingsDirty = true;
        UpdateSettingsStatus();
    }

    private void UpdateSettingsStatus()
    {
        if (TxtSettingsStatus == null || BtnSaveSettings == null) return;
        TxtSettingsStatus.Text = _settingsDirty
            ? (IsChineseUi() ? "有未保存更改（Ctrl+S 保存）" : "Unsaved changes (Ctrl+S to save)")
            : (IsChineseUi() ? "设置已保存" : "Settings saved");
        TxtSettingsStatus.Foreground = _settingsDirty
            ? (Resources["WarningBrush"] as Brush ?? Brushes.DarkOrange)
            : (Resources["MutedTextBrush"] as Brush ?? Brushes.Gray);
        BtnSaveSettings.IsEnabled = _settingsDirty;
        BtnSaveSettings.Background = ThemedActionBrush(BtnSaveSettings.IsEnabled);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.AllowDhcpOnWifi = AllowWifi.IsChecked == true;
        _settings.AllowDhcpOnAdapterWithGateway = AllowGateway.IsChecked == true;
        _settings.DetectExistingDhcpBeforeStart = DetectExistingDhcp.IsChecked == true;
        _settings.AllowRestartOnAnyAdapter = AllowRestartAnyAdapter.IsChecked == true;
        _settings.AllowMacChangeOnAnyAdapter = AllowMacChangeAnyAdapter.IsChecked == true;
        _settings.RestoreIpOnDhcpStop = RestoreOnStop.IsChecked == true;
        _settings.RedactSupportPackage = RedactSupportPackage.IsChecked == true;
        JsonStore.Save(_paths.SettingsFile, _settings);
        _settingsDirty = false;
        _logger.Info($"Settings saved: dhcpWifi={_settings.AllowDhcpOnWifi} dhcpGateway={_settings.AllowDhcpOnAdapterWithGateway} detectExistingDhcp={_settings.DetectExistingDhcpBeforeStart} restartAnyAdapter={_settings.AllowRestartOnAnyAdapter} macAnyAdapter={_settings.AllowMacChangeOnAnyAdapter} restoreIp={_settings.RestoreIpOnDhcpStop} redactSupport={_settings.RedactSupportPackage}");
        UpdateSettingsStatus();
        SetDhcpRunningState(_dhcpServer.IsRunning);
        UpdateAdapterActionButtons();
        UpdateManualScanButtons();
        UpdateSessionStatus();
        ShowActionFeedback("设置已保存。", "Settings saved.");
    }

    private void ScanGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ScanGrid.SelectedItem is ScanResult result)
        {
            if (TryOpenUrl("http://" + result.IpAddress))
            {
                ShowActionFeedback("已打开目标网页。", "Target web page opened.");
            }
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
            UpdateEmptyStates();
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
        BtnPreviewDhcp.IsEnabled = !running;
        BtnRollback.IsEnabled = !running && _safetyOnboardingCompleted;
        BtnRollbackHistory.IsEnabled = !running && _safetyOnboardingCompleted;
        BtnStartDhcp.IsEnabled = !running && _safetyOnboardingCompleted;
        BtnStopDhcp.IsEnabled = running;
        BtnStartDhcp.Background = running ? Brushes.LightGray : Brushes.LightGreen;
        BtnStopDhcp.Background = running ? Brushes.OrangeRed : Brushes.LightGray;
        BtnStopDhcp.Foreground = running ? Brushes.White : Brushes.Black;
        RouteGrid.IsEnabled = !running;
        CurrentRouteGrid.IsEnabled = !running;
        BtnAddRoute.IsEnabled = !running;
        BtnRemoveRoute.IsEnabled = !running && _safetyOnboardingCompleted;
        BtnPreviewRoutes.IsEnabled = !running;
        BtnApplyRoutes.IsEnabled = !running && _safetyOnboardingCompleted;
        BtnClearAppliedRoutes.IsEnabled = !running && _safetyOnboardingCompleted;
        UpdateAdapterActionButtons();
        UpdateDhcpInputValidation();
        UpdateProfileButtons();
        UpdateSessionStatus();
    }

    private async Task CleanupWorkEnvironmentAsync()
    {
        _cleanupFailureCount = 0;
        PersistStateBeforeExit();
        var restoreAdapter = _dhcpWasStartedInThisSession;
        _leasePingTimer.Stop();
        _leaseHintCts?.Cancel();
        _scanCts?.Cancel();
        _dhcpServer.Stop();

        await RunCleanupStepAsync(
            "Cleaning temporary firewall rules... / 正在清理临时防火墙规则...",
            async ct =>
            {
                await RemoveDhcpFirewallRulesAsync(ct);
                if (_dhcpFirewallRules != null)
                {
                    throw new InvalidOperationException("Temporary DHCP firewall rules remain.");
                }
            });

        await RunCleanupStepAsync(
            "Removing temporary routes... / 正在清理临时路由...",
            async ct =>
            {
                if (!await ClearAppliedRoutesAsync(ct))
                {
                    throw new InvalidOperationException("Some temporary static routes remain.");
                }
            });

        StaticRoutes.Clear();
        CurrentStaticRoutes.Clear();

        if (restoreAdapter)
        {
            await RunCleanupStepAsync(
                "Restoring adapter settings... / 正在恢复网卡设置...",
                async ct =>
                {
                    var adapter = GetDhcpRestoreAdapter();
                    if (adapter == null) throw new InvalidOperationException("The DHCP adapter is unavailable.");
                    await RestoreOriginalAdapterConfigAsync(adapter, ct);
                });
        }

        await RunCleanupStepAsync(
            "Restoring adapter MAC addresses... / 正在恢复网卡 MAC 地址...",
            async ct =>
            {
                if (!await RestoreOriginalMacAddressesAsync(ct))
                {
                    throw new InvalidOperationException("Some adapter MAC addresses could not be restored.");
                }
            });

        _activeDhcpAdapterIndex = null;
        _logger.Info($"Window closing cleanup completed: failures={_cleanupFailureCount}");
    }

    private async Task RunCleanupStepAsync(string text, Func<CancellationToken, Task> action)
    {
        BusyText.Text = text;
        UpdateSessionStatus();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await action(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _cleanupFailureCount++;
            _logger.Error("Window closing cleanup step timed out: " + text);
        }
        catch (Exception ex)
        {
            _cleanupFailureCount++;
            _logger.Error("Window closing cleanup step failed: " + text, ex);
        }
    }

    private async Task<bool> RestoreOriginalMacAddressesAsync(CancellationToken ct = default)
    {
        var pending = _originalAdapterMacs.Values.Where(x => x.RestoreOnExit).ToList();
        if (pending.Count == 0)
        {
            _logger.Info("No adapter MAC restoration required on normal exit");
            return true;
        }

        var succeeded = true;
        var available = Adapters.ToList();
        foreach (var backup in pending)
        {
            ct.ThrowIfCancellationRequested();
            var adapter = available.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(backup.AdapterId) && x.Id.Equals(backup.AdapterId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(backup.InterfaceIndex) && x.InterfaceIndex.Equals(backup.InterfaceIndex, StringComparison.OrdinalIgnoreCase)));
            if (adapter == null)
            {
                _logger.Warn($"MAC restoration skipped: adapter unavailable idx={backup.InterfaceIndex} name={backup.AdapterName}");
                succeeded = false;
                continue;
            }
            try
            {
                await _adapterService.RestoreMacAddressAsync(adapter, backup.OriginalMacAddress, ct);
                await VerifyMacAddressAsync(backup, backup.OriginalMacAddress, ct);
                _logger.Info($"Adapter MAC restored on normal exit: idx={backup.InterfaceIndex} name={backup.AdapterName} mac={backup.OriginalMacAddress}");
                _originalAdapterMacs.Remove(MacBackupKey(backup));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                succeeded = false;
                _logger.Error($"Adapter MAC restoration failed: idx={backup.InterfaceIndex} name={backup.AdapterName}", ex);
            }
        }
        SaveMacBackups();
        return succeeded;
    }

    private async Task RemoveDhcpFirewallRulesAsync(CancellationToken ct = default)
    {
        var lease = _dhcpFirewallRules;
        if (lease == null || lease.CreatedRuleNames.Count == 0) return;
        try
        {
            await _adapterService.RemoveDhcpFirewallRulesAsync(lease, ct);
            _dhcpFirewallRules = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.Warn("Remove DHCP firewall rules canceled by user or timeout");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Remove DHCP firewall rules failed", ex);
        }
    }

    private async Task<bool> ClearAppliedRoutesAsync(CancellationToken ct = default)
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
            ct.ThrowIfCancellationRequested();
            var adapter = ResolveAdapterForRoute(route);
            if (adapter == null)
            {
                remaining.Add(route);
                _logger.Warn($"Static route cleanup skipped: adapter unavailable idx={route.InterfaceIndex} destination={route.DestinationPrefix}");
                continue;
            }
            try
            {
                await _routeService.RemoveAsync(route, adapter, ct);
                _logger.Info($"Static route removed: {route.DestinationPrefix} adapter={adapter.Name}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
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
        UpdateRecoveryBanner();
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
        UpdateRecoveryBanner();
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
            SaveMacBackups();
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
            if (TryOpenUrl((useHttps ? "https://" : "http://") + ip))
            {
                ShowActionFeedback("已打开目标网页。", "Target web page opened.");
            }
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
        if (sender is Hyperlink link && link.CommandParameter is string ip && TryOpenUrl("https://" + ip))
        {
            ShowActionFeedback("已打开目标网页。", "Target web page opened.");
        }
    }

    private bool TryOpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !IPAddress.TryParse(uri.Host, out _))
        {
            ShowActionFeedback("网页地址无效。", "The web address is invalid.", error: true);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("Open web page failed: " + ex.Message);
            ShowActionFeedback("网页打开失败。", "Could not open the web page.", error: true);
            return false;
        }
    }

    private void ShowActionFeedback(string chinese, string english, bool error = false)
    {
        if (TxtActionFeedback == null) return;
        _feedbackTimer.Stop();
        TxtActionFeedback.Text = IsChineseUi() ? chinese : english;
        TxtActionFeedback.Foreground = error
            ? (Resources["DangerBrush"] as Brush ?? Brushes.Firebrick)
            : (Resources["AccentBrush"] as Brush ?? Brushes.ForestGreen);
        _feedbackTimer.Start();
    }

    private void ShowActionFeedbackKey(string key, bool error = false)
    {
        var text = _lang.T(key);
        ShowActionFeedback(text, text, error);
    }

    private bool TryCopyText(string text, string chineseSuccess, string englishSuccess)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowActionFeedback("没有可复制的内容。", "There is nothing to copy.");
            return false;
        }
        try
        {
            Clipboard.SetText(text);
            ShowActionFeedback(chineseSuccess, englishSuccess);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("Clipboard copy failed: " + ex.Message);
            ShowActionFeedback("复制失败，请重试或检查剪贴板占用。", "Copy failed; retry or check whether another app is using the clipboard.", error: true);
            return false;
        }
    }

    private void SetBusy(bool busy, string? text = null)
    {
        if (busy)
        {
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            _operationStartedTimestamp = Stopwatch.GetTimestamp();
            _activeOperationText = text;
            BtnCancelOperation.IsEnabled = true;
            BtnCancelOperation.Visibility = Visibility.Visible;
        }
        else
        {
            _activeOperationText = null;
            _operationCts?.Dispose();
            _operationCts = null;
            _operationStartedTimestamp = 0;
            BtnCancelOperation.Visibility = Visibility.Collapsed;
        }
        BusyText.Text = text ?? "Please wait... / 请稍后...";
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSessionStatus();
    }

    private CancellationToken OperationToken => _operationCts?.Token ?? CancellationToken.None;

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts == null) return;
        _logger.Warn("User requested cancellation of the active operation");
        _operationCts.Cancel();
        _scanCts?.Cancel();
        BusyText.Text = IsChineseUi() ? "正在取消，请稍候..." : "Canceling, please wait...";
        BtnCancelOperation.IsEnabled = false;
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _operationCts?.Cancel();
            _scanCts?.Cancel();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None && !_adapterRefreshInProgress && !_closingCleanupStarted)
        {
            _ = RefreshAdaptersAsync();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Enter && !_closingCleanupStarted)
        {
            var previewTriggered = false;
            if (Tabs.SelectedItem == TabDhcp && BtnPreviewDhcp.IsEnabled)
            {
                PreviewDhcp_Click(sender, e);
                previewTriggered = true;
            }
            else if (Tabs.SelectedItem == TabScan && BtnPreviewScan.IsEnabled)
            {
                PreviewScan_Click(sender, e);
                previewTriggered = true;
            }
            else if (Tabs.SelectedItem == TabRoutes && BtnPreviewRoutes.IsEnabled)
            {
                PreviewRoutes_Click(sender, e);
                previewTriggered = true;
            }
            if (previewTriggered)
            {
                e.Handled = true;
                return;
            }
        }
        switch (e.Key)
        {
            case Key.R when !_adapterRefreshInProgress && !_closingCleanupStarted:
                _ = RefreshAdaptersAsync();
                e.Handled = true;
                break;
            case Key.L:
                SetLogPanelExpanded(LogBox.Visibility != Visibility.Visible);
                e.Handled = true;
                break;
            case Key.C when HistoryGrid.IsKeyboardFocusWithin:
                CopyHistory_Click(sender, e);
                e.Handled = true;
                break;
            case Key.S when Tabs.SelectedItem == TabSettings && _settingsDirty && !_closingCleanupStarted:
                SaveSettings_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    private string ExplainFailure(Exception ex, string nextStep)
    {
        var message = ex.Message ?? "";
        var category = message.Contains("Administrator", StringComparison.OrdinalIgnoreCase)
            || message.Contains("管理员", StringComparison.OrdinalIgnoreCase)
            ? (IsChineseUi() ? "权限检查" : "Permission check")
            : message.Contains("Interface", StringComparison.OrdinalIgnoreCase)
                || message.Contains("网卡", StringComparison.OrdinalIgnoreCase)
                ? (IsChineseUi() ? "网卡状态" : "Adapter state")
                : message.Contains("route", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("路由", StringComparison.OrdinalIgnoreCase)
                    ? (IsChineseUi() ? "系统路由" : "System route")
                    : message.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)
                        ? (IsChineseUi() ? "系统命令" : "System command")
                        : (IsChineseUi() ? "操作失败" : "Operation failed");
        return IsChineseUi()
            ? $"分类：{category}\n原因：{message}\n\n建议下一步：{nextStep}\n\n可打开“支持包”导出本地诊断材料。"
            : $"Category: {category}\nReason: {message}\n\nNext step: {nextStep}\n\nUse Support Package to export local diagnostic material if needed.";
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        SetLogPanelExpanded(LogBox.Visibility != Visibility.Visible);
    }

    private void SetLogPanelExpanded(bool expanded)
    {
        LogBox.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        LogPanel.Height = expanded ? 150 : 36;
        BtnToggleLog.Content = expanded ? _lang.T("hide.log") : _lang.T("show.log");
        UpdateLogSummary();
    }

    private void UpdateLogSummary()
    {
        if (TxtLogSummary == null) return;
        var count = LogBox.Document.Blocks.Count;
        TxtLogSummary.Text = IsChineseUi() ? $"当前显示 {count} 条" : $"{count} visible line(s)";
    }

    private void UpdateSessionStatus()
    {
        if (TxtSessionStatus == null) return;

        var isAdmin = false;
        try { isAdmin = _adapterService.IsAdministrator(); } catch { }
        var adapter = SelectedAdapter;
        var adapterText = adapter == null
            ? (IsChineseUi() ? "未选择网卡" : "No adapter selected")
            : adapter.DisplayName;
        var state = _activeOperationText
            ?? (_dhcpServer.IsRunning
                ? (IsChineseUi() ? "DHCP 运行中" : "DHCP running")
                : _scanRunning
                    ? (IsChineseUi() ? "手动扫描中" : "Manual scan running")
                    : (IsChineseUi() ? "就绪" : "Ready"));
        var changes = _appliedStaticRoutes.Count > 0 || _dhcpServer.IsRunning || _originalAdapterMacs.Values.Any(x => x.RestoreOnExit)
            ? (IsChineseUi() ? "存在本次运行变更，退出时按策略清理/恢复" : "Session changes exist; cleanup/restoration follows the policy on exit")
            : _originalAdapterConfigs.Count > 0
                ? (IsChineseUi() ? "已保存网卡备份，可按需回滚" : "Adapter backup saved; rollback is available")
                : (IsChineseUi() ? "暂无待清理变更" : "No pending session changes");
        var privilege = isAdmin
            ? (IsChineseUi() ? "管理员权限可用" : "Administrator available")
            : (IsChineseUi() ? "权限受限：配置类操作可能不可用" : "Limited permissions: configuration actions may be unavailable");
        var safety = _safetyOnboardingCompleted
            ? ""
            : (IsChineseUi() ? "安全引导未确认：网络变更已锁定" : "Safety Guide not acknowledged: network changes are locked");

        TxtSessionStatus.Text = IsChineseUi()
            ? $"{state}  |  {privilege}  |  当前网卡：{adapterText}  |  {changes}{(string.IsNullOrWhiteSpace(safety) ? "" : "  |  " + safety)}"
            : $"{state}  |  {privilege}  |  Adapter: {adapterText}  |  {changes}{(string.IsNullOrWhiteSpace(safety) ? "" : "  |  " + safety)}";
        TxtSessionStatus.Foreground = isAdmin && string.IsNullOrWhiteSpace(_activeOperationText) ? Brushes.DarkSlateGray : Brushes.Brown;
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
            Foreground = Resources["TextBrush"] as Brush ?? Brushes.Black
        });
        LogBox.Document.Blocks.Add(paragraph);
        while (LogBox.Document.Blocks.Count > 500) LogBox.Document.Blocks.Remove(LogBox.Document.Blocks.FirstBlock);
        if (ChkLogAutoScroll?.IsChecked != false) LogBox.ScrollToEnd();
        UpdateLogSummary();
    }

    private async Task RememberAdapterConfigAsync(NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adapter.InterfaceIndex)) return;
        if (!_lastAdapterIps.ContainsKey(adapter.InterfaceIndex))
        {
            _lastAdapterIps[adapter.InterfaceIndex] = adapter.IPv4Address;
        }
        if (!_originalAdapterConfigs.ContainsKey(adapter.InterfaceIndex))
        {
            _originalAdapterConfigs[adapter.InterfaceIndex] = await _adapterService.CaptureIPv4ConfigAsync(adapter, ct);
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
        UpdateAdapterDetails(adapter);
    }

    private void UpdateAdapterDetails(NetworkAdapterInfo? adapter)
    {
        if (TxtAdapterDetails == null) return;
        if (adapter == null)
        {
            TxtAdapterDetails.Text = IsChineseUi() ? "尚未选择网卡。" : "No adapter selected.";
            return;
        }
        var gateway = string.IsNullOrWhiteSpace(adapter.Gateway) ? "-" : adapter.Gateway;
        TxtAdapterDetails.Text = IsChineseUi()
            ? $"{adapter.KindDisplay}  |  {adapter.ConnectionDisplay}  |  速率：{adapter.LinkSpeedDisplay}  |  网关：{gateway}  |  InterfaceIndex：{adapter.InterfaceIndex}"
            : $"{adapter.KindDisplay}  |  {adapter.ConnectionDisplay}  |  Speed: {adapter.LinkSpeedDisplay}  |  Gateway: {gateway}  |  InterfaceIndex: {adapter.InterfaceIndex}";
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
            adapter.LinkSpeedMbps = latest.LinkSpeedMbps;
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

    private async Task RestoreOriginalAdapterConfigAsync(NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adapter.InterfaceIndex)) return;
        if (_originalAdapterConfigs.TryGetValue(adapter.InterfaceIndex, out var snapshot))
        {
            await _adapterService.RestoreIPv4ConfigAsync(adapter, snapshot, ct);
            await VerifyRestoredAdapterConfigAsync(adapter, snapshot, ct);
            adapter.IPv4Address = snapshot.IpAddress;
            UpdateAdapterIpText(adapter);
            TxtManualAdapterIp.Text = BuildAdapterIpDisplay(adapter);
            _logger.Info($"Adapter restored to original config: idx={adapter.InterfaceIndex} {snapshot.DisplayText}");
            return;
        }
        await _adapterService.RestoreDhcpAsync(adapter, ct);
        var restored = await _adapterService.CaptureIPv4ConfigAsync(adapter, ct);
        if (!restored.DhcpEnabled) throw new InvalidOperationException(Ui("verify.restore.failed"));
    }

    private async Task VerifyStaticIPv4Async(NetworkAdapterInfo adapter, string expectedIp, string expectedMask, CancellationToken ct)
    {
        var expectedAddress = IPAddress.Parse(expectedIp.Trim()).ToString();
        var expectedPrefix = IpNetwork.PrefixLength(IPAddress.Parse(expectedMask.Trim()));
        var actual = await _adapterService.CaptureIPv4ConfigAsync(adapter, ct);
        var matched = !actual.DhcpEnabled && actual.Addresses.Any(x =>
            x.IpAddress.Equals(expectedAddress, StringComparison.OrdinalIgnoreCase) && x.PrefixLength == expectedPrefix);
        _logger.Info($"Post-change adapter verification: idx={adapter.InterfaceIndex} expected={expectedAddress}/{expectedPrefix} actual={actual.DisplayText} matched={matched}");
        if (!matched) throw new InvalidOperationException(Ui("verify.static.failed"));
    }

    private async Task VerifyRestoredAdapterConfigAsync(NetworkAdapterInfo adapter, AdapterIpv4Snapshot expected, CancellationToken ct)
    {
        expected.NormalizeLegacyFields();
        var actual = await _adapterService.CaptureIPv4ConfigAsync(adapter, ct);
        var expectedAddresses = expected.DhcpEnabled
            ? Array.Empty<string>()
            : expected.Addresses.Select(x => $"{x.IpAddress}/{x.PrefixLength}").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var actualAddresses = actual.DhcpEnabled
            ? Array.Empty<string>()
            : actual.Addresses.Select(x => $"{x.IpAddress}/{x.PrefixLength}").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var matched = expected.DhcpEnabled == actual.DhcpEnabled
            && expectedAddresses.SequenceEqual(actualAddresses, StringComparer.OrdinalIgnoreCase);
        _logger.Info($"Post-restore adapter verification: idx={adapter.InterfaceIndex} expected={expected.DisplayText} actual={actual.DisplayText} matched={matched}");
        if (!matched) throw new InvalidOperationException(Ui("verify.restore.failed"));
    }

    private async Task VerifyAppliedRoutesAsync(IReadOnlyList<StaticRouteApplyResult> results, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var result in results.Where(x => x.Created && x.Applied != null))
        {
            var applied = result.Applied!;
            var adapter = ResolveAdapterForRoute(applied);
            if (adapter == null || !await _routeService.ExistsAsync(applied, adapter, ct)) missing.Add(applied.DestinationPrefix);
        }
        _logger.Info($"Post-change static-route verification: checked={results.Count(x => x.Created)} missing={missing.Count}");
        if (missing.Count > 0) throw new InvalidOperationException(IsChineseUi()
            ? $"{Ui("verify.static.failed")} 缺少路由：{string.Join(", ", missing)}"
            : $"{Ui("verify.static.failed")} Missing routes: {string.Join(", ", missing)}");
    }

    private async Task VerifyMacAddressAsync(AdapterMacBackup backup, string expectedMac, CancellationToken ct)
    {
        var expected = NetworkAdapterService.NormalizeMacAddress(expectedMac);
        var observed = (await Task.Run(() => _adapterService.GetAdapters(logAdapters: false), ct))
            .FirstOrDefault(x => (!string.IsNullOrWhiteSpace(backup.AdapterId) && x.Id.Equals(backup.AdapterId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(backup.InterfaceIndex) && x.InterfaceIndex.Equals(backup.InterfaceIndex, StringComparison.OrdinalIgnoreCase)));
        var actual = observed == null || string.IsNullOrWhiteSpace(observed.MacAddress) ? "" : NetworkAdapterService.NormalizeMacAddress(observed.MacAddress);
        var matched = observed != null && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        _logger.Info($"Post-change MAC verification: idx={backup.InterfaceIndex} expected={expected} actual={actual} matched={matched}");
        if (!matched) throw new InvalidOperationException(IsChineseUi() ? "恢复后校验失败：MAC 与目标值不一致。" : "Post-change verification failed: the MAC does not match the target value.");
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
            WindowStartupLocation = WindowStartupLocation.CenterOwner
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
            TryCopyText(text, "已复制自定义字段。", "Custom fields copied.");
        };
        closeButton.Click += (_, _) => window.Close();
        ApplyOwnedWindowTheme(window);
        window.ShowDialog();
    }

    private void ShowFavoriteDetailsDialog(FavoriteConfig fav)
    {
        var passwordRevealed = false;
        var passwordRow = new FavoriteField
        {
            Name = IsChineseUi() ? "密码" : "Password",
            Value = fav.PasswordUnavailable ? Ui("credential.unavailable") : fav.HasUsablePassword ? Ui("credential.hidden") : "-"
        };
        var rows = new List<FavoriteField>
        {
            new() { Name = IsChineseUi() ? "名称" : "Name", Value = fav.Name },
            new() { Name = IsChineseUi() ? "设备" : "Device", Value = fav.DeviceNumber },
            new() { Name = "SN", Value = fav.SerialNumber },
            new() { Name = IsChineseUi() ? "备注" : "Remark", Value = fav.RemarkName },
            new() { Name = IsChineseUi() ? "账号" : "User", Value = fav.Username },
            passwordRow,
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
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var revealButton = new Button { Content = fav.HasUsablePassword ? Ui("credential.reveal") : Ui("credential.unavailable"), MinWidth = 110, Margin = new Thickness(4), IsEnabled = fav.HasUsablePassword };
        var copyButton = new Button { Content = Ui("credential.copy"), MinWidth = 90, Margin = new Thickness(4) };
        var closeButton = new Button { Content = IsChineseUi() ? "关闭" : "Close", MinWidth = 90, Margin = new Thickness(4) };
        HelpButtonService.Attach(revealButton, "help.favorite.details.reveal");
        HelpButtonService.Attach(copyButton, "help.favorite.details.copy");
        HelpButtonService.Attach(closeButton, "help.favorite.details.close");
        buttons.Children.Add(revealButton);
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
        revealButton.Click += (_, _) =>
        {
            passwordRevealed = !passwordRevealed;
            passwordRow.Value = passwordRevealed ? fav.Password : Ui("credential.hidden");
            revealButton.Content = passwordRevealed ? Ui("credential.hide") : Ui("credential.reveal");
            grid.Items.Refresh();
        };
        copyButton.Click += (_, _) =>
        {
            if (passwordRevealed && fav.HasUsablePassword
                && !AppDialog.Show(window, IsChineseUi() ? "复制密码" : "Copy Password", Ui("credential.copy.confirm"), confirm: true, danger: true,
                    okText: IsChineseUi() ? "继续复制" : "Copy", cancelText: IsChineseUi() ? "取消" : "Cancel")) return;
            var copyRows = passwordRevealed ? rows : rows.Where(x => !ReferenceEquals(x, passwordRow)).ToList();
            TryCopyText(
                string.Join(Environment.NewLine, copyRows.Select(x => $"{x.Name}: {x.Value}")),
                "已复制收藏详情。",
                "Favorite details copied.");
        };
        closeButton.Click += (_, _) => window.Close();
        ApplyOwnedWindowTheme(window);
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
        if (!string.IsNullOrWhiteSpace(selected.Name)) _settings.LastTab = selected.Name;
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
            ["目标地址", "地址族", "使用网卡", "网关", "优先级", "状态"],
            ["Destination", "Family", "Adapter", "Gateway", "Priority", "Status"]);
        SetHeaders(CurrentRouteGrid.Columns, zh,
            ["目标地址", "地址族", "使用网卡", "网关", "跃点", "协议"],
            ["Destination", "Family", "Adapter", "Gateway", "Metric", "Protocol"]);
        SetHeaders(FavoriteGrid.Columns, zh,
            ["名称", "设备", "序列号", "备注", "账号", "密码", "IP", "掩码", "自定义", "更新", "最近使用", "说明"],
            ["Name", "Device", "SN", "Remark", "User", "Password", "IP", "Mask", "Custom", "Updated", "Last Used", "Description"]);
        SetHeaders(ProfileGrid.Columns, zh,
            ["名称", "说明", "路由", "更新"],
            ["Name", "Description", "Routes", "Updated"]);
        SetHeaders(HistoryGrid.Columns, zh,
            ["时间", "操作", "范围", "状态", "详情", "耗时", "回滚"],
            ["Time", "Operation", "Scope", "Status", "Detail", "Duration", "Rollback"]);
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
        return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.LocalIp, b.LocalIp, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.TargetIp, b.TargetIp, StringComparison.OrdinalIgnoreCase);
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
