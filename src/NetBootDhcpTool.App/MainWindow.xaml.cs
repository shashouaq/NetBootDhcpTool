using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    private sealed record AdapterStatusRefreshRequest(string AdapterId, string InterfaceIndex, long SelectionGeneration, long WorkflowGeneration);

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
    private readonly UpdateController _updateController;
    private UpdateControllerState _updateState;
    private readonly Dictionary<LeaseProbeBindingKey, long> _leaseProbeGenerations = [];
    private readonly NetworkWorkflowCoordinator _networkWorkflows = new();
    private readonly List<string> _startupDataWarnings = [];
    private AppSettings _settings;
    private List<FavoriteConfig> _allFavorites = [];
    private List<AdapterConfigBackup> _adapterBackups = [];
    private List<AdapterMacBackup> _savedMacBackups = [];
    private DhcpLeaseJournal _dhcpLeaseJournal = new();
    private List<OperationHistoryItem> _operationHistory = [];
    private readonly CoalescingSnapshotWriter<OperationHistoryItem> _operationHistoryWriter;
    private int _pendingScanHistoryCount;
    private bool _scanHistoryFlushInProgress;
    private bool _operationHistoryWritable = true;
    private bool _operationHistorySaveFailed;
    private ScanProgressAccumulator? _scanProgressAccumulator;
    private long _scanProgressGeneration;
    private readonly Queue<string> _logDisplayQueue = new();
    private readonly object _logDisplaySync = new();
    private long _logDisplayOmitted;
    private int _logDisplayScrollCount;
    private List<NetworkProfile> _allProfiles = [];
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _leaseHintCts;
    private readonly DispatcherTimer _leasePingTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _adapterStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _scanProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _scanHistoryFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _adapterNetworkChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _logDisplayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _busyProgressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Stopwatch? _busyOperationStopwatch;
    private Stopwatch? _busyPhaseStopwatch;
    private string? _busyOperationId;
    private string? _busyPhaseName;
    private int _busyPhaseNumber;
    private int _busyCompletedPhaseCount;
    private int? _busyTotalPhaseCount;
    private int _leaseProbeRefreshCursor;
    private bool _closingCleanupStarted;
    private bool _waitingForOperationBeforeClose;
    private bool _unsavedSettingsExitConfirmed;
    private bool _closeAfterCleanup;
    private bool _scanRunning;
    private readonly List<DhcpSessionRecovery> _dhcpSessionRecoveries = [];
    private DhcpSessionRecovery? _activeDhcpSession;
    private readonly Dictionary<string, string> _lastAdapterIps = new();
    private readonly Dictionary<string, AdapterIpv4Snapshot> _originalAdapterConfigs = new();
    private readonly Dictionary<string, AdapterMacBackup> _originalAdapterMacs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppliedStaticRoute> _appliedStaticRoutes = [];
    private readonly List<DhcpFirewallRuleLease> _dhcpFirewallRecoveries = [];
    private readonly LeaseProbeCoordinator _leaseProbeCoordinator;
    private readonly CoalescedRefreshCoordinator<AdapterStatusRefreshRequest, NetworkAdapterInfo> _adapterStatusCoordinator;
    private TabItem? _lastBusinessTab;
    private bool _adapterStatusRefreshDirty;
    private bool _adapterRefreshInProgress;
    private bool _adapterActionInProgress;
    private bool _darkTheme;
    private bool _safetyOnboardingCompleted;
    private bool _settingsWritable = true;
    private bool _favoritesWritable = true;
    private bool _favoritePresetStateWritable = true;
    private bool _profilesWritable = true;
    private bool _macBackupsWritable = true;
    private bool _routeJournalWritable = true;
    private bool _dhcpFirewallJournalWritable = true;
    private bool _dhcpLeaseJournalWritable = true;
    private bool _settingsInputsLoading;
    private bool _settingsDirty;
    private string? _businessConfigurationFingerprint;
    private DhcpFirewallRuleLease? _dhcpFirewallRules;
    private string? _activeDhcpUiSessionId;
    private string? _activeOperationText;
    private CancellationTokenSource? _operationCts;
    private NetworkWorkflowLease? _activeNetworkWorkflow;
    private TaskCompletionSource? _standaloneOperationCompletion;
    private long _operationStartedTimestamp;
    private bool _suppressProfileSelection;
    private Stopwatch? _scanStopwatch;
    private DateTime? _scanLastHitAt;
    private string _scanPhase = "";
    private int _cleanupFailureCount;
    private bool _compactLayout;
    private bool _logDisplayClosed;
    private long _adapterSelectionGeneration;
    private long _adapterWorkflowGeneration;
    private long _adapterNetworkChangeVersion;
    private long _adapterNetworkChangeObservedVersion;
    private int _adapterNetworkChangeDispatchPending;

    public ObservableCollection<NetworkAdapterInfo> Adapters => _viewModel.Adapters;
    public ObservableCollection<ScanResult> ScanResults => _viewModel.ScanResults;
    public ObservableCollection<FavoriteConfig> Favorites => _viewModel.Favorites;
    public ObservableCollection<DhcpLease> Leases => _viewModel.Leases;
    public ObservableCollection<AdapterIpHistoryItem> AdapterIpHistory => _viewModel.AdapterIpHistory;
    public ObservableCollection<StaticRouteRule> StaticRoutes => _viewModel.StaticRoutes;
    public ObservableCollection<StaticRouteRule> CurrentStaticRoutes => _viewModel.CurrentStaticRoutes;
    public ObservableCollection<OperationHistoryItem> OperationHistory => _viewModel.OperationHistory;
    public ObservableCollection<NetworkProfile> Profiles => _viewModel.Profiles;

    public MainWindow(AppPaths paths, FileLogger? logger = null) : this(paths, logger, null, null)
    {
    }

    internal MainWindow(
        AppPaths paths,
        FileLogger? logger,
        Func<LeaseProbeRequest, CancellationToken, Task<LeaseProbeResult>>? leaseProbeAsync)
        : this(paths, logger, leaseProbeAsync, null)
    {
    }

    internal MainWindow(
        AppPaths paths,
        FileLogger? logger,
        Func<LeaseProbeRequest, CancellationToken, Task<LeaseProbeResult>>? leaseProbeAsync,
        UpdateController? updateController)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var initializationStopwatch = Stopwatch.StartNew();
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
        _paths = paths;
        _logger = logger ?? new FileLogger(_paths);
        _operationHistoryWriter = new CoalescingSnapshotWriter<OperationHistoryItem>(snapshot =>
            Task.Run(() => JsonStore.Save(_paths.OperationHistoryFile, snapshot)));
        foreach (var warning in _paths.MigrationWarnings) _logger.Warn($"Legacy data migration warning: {warning}");
        _logger.LineWritten += Logger_LineWritten;
        _logDisplayTimer.Tick += (_, _) => DrainLogDisplayQueue();
        _logDisplayTimer.Start();
        var settingsLoad = JsonStore.Load<AppSettings>(_paths.SettingsFile, _logger);
        _settings = settingsLoad.Value ?? new AppSettings();
        _settingsWritable = settingsLoad.Status is not DataLoadStatus.Failed and not DataLoadStatus.Missing;
        RecordStorageLoad("Settings / 设置", settingsLoad.Status, settingsLoad.SourcePath, settingsLoad.Error);
        _lang = new LanguageService(_paths);
        _lang.Load(_settings.Language);
        _adapterService = new NetworkAdapterService(_logger);
        _adapterStatusCoordinator = new CoalescedRefreshCoordinator<AdapterStatusRefreshRequest, NetworkAdapterInfo>(
            (request, ct) => Task.Run(() => _adapterService.GetAdapterByIdentity(request.AdapterId, request.InterfaceIndex), ct),
            (request, adapter) => Dispatcher.InvokeAsync(
                () => CommitSelectedAdapterStatus(request, adapter), DispatcherPriority.DataBind).Task,
            (request, ex) => _logger.Warn($"Adapter status refresh failed: adapterId={request.AdapterId} index={request.InterfaceIndex} reason={ex.Message}"));
        _routeService = new StaticRouteService(_logger);
        _probe = new HttpProbeService();
        _scanner = new PingScanner(_logger, _probe);
        _leaseProbeCoordinator = new LeaseProbeCoordinator(
            leaseProbeAsync ?? ProbeLeaseAsync,
            ApplyLeaseProbeResultAsync,
            (identity, ex) => _logger.Error($"DHCP lease probe failed: session={identity.SessionId} client={identity.ClientKey} ip={identity.IpAddress}", ex));
        _dhcpDetector = new ExistingDhcpDetector(_logger);
        _dhcpServer = new DhcpServer(_logger);
        var installContext = UpdateInstallContext.TryLoad(_paths.BaseDirectory, CurrentVersion);
        _updateController = updateController ?? new UpdateController(new VersionUpdateService(), CurrentVersion, installContext);
        _updateState = _updateController.State;
        _updateController.StateChanged += UpdateController_StateChanged;
        _dhcpServer.LeaseChanged += lease =>
        {
            lease.SessionId = Volatile.Read(ref _activeDhcpUiSessionId) ?? "";
            lease.IsCurrentSession = !string.IsNullOrWhiteSpace(lease.SessionId);
            Dispatcher.BeginInvoke(() => ProcessLeaseEvent(lease));
        };
        _dhcpServer.StoppedUnexpectedly += reason => Dispatcher.BeginInvoke(() => _ = HandleUnexpectedDhcpStopAsync(reason));
        _leasePingTimer.Tick += (_, _) => RefreshLeasePing();
        _adapterStatusTimer.Tick += (_, _) => RefreshSelectedAdapterStatus();
        _adapterNetworkChangeTimer.Tick += (_, _) => AdapterNetworkChangeDebounceTick();
        _scanProgressTimer.Tick += (_, _) => DrainScanProgressBatch();
        _scanHistoryFlushTimer.Tick += async (_, _) => await FlushScanHistoryAsync(force: true);
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            if (TxtActionFeedback != null) TxtActionFeedback.Text = "";
        };
        _busyProgressTimer.Tick += (_, _) => UpdateBusyElapsedText();
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        ApplyLanguage();
        LoadDefaults();
        LoadFavorites();
        LoadNetworkHistory();
        var adapterBackupsLoad = JsonStore.Load<List<AdapterConfigBackup>>(_paths.AdapterBackupsFile, _logger);
        _adapterBackups = adapterBackupsLoad.HasData ? adapterBackupsLoad.Value! : [];
        RecordStorageLoad("Adapter backups / 网卡备份", adapterBackupsLoad.Status, adapterBackupsLoad.SourcePath, adapterBackupsLoad.Error, missingIsExpected: true);
        LoadMacBackups();
        LoadDhcpFirewallRecoveries();
        LoadDhcpLeaseJournal();
        var operationHistoryLoad = JsonStore.Load<List<OperationHistoryItem>>(_paths.OperationHistoryFile, _logger);
        _operationHistory = operationHistoryLoad.HasData ? operationHistoryLoad.Value! : [];
        _operationHistoryWritable = operationHistoryLoad.Status != DataLoadStatus.Failed;
        RecordStorageLoad("Operation history / 操作历史", operationHistoryLoad.Status, operationHistoryLoad.SourcePath, operationHistoryLoad.Error, missingIsExpected: true);
        foreach (var item in _operationHistory.OrderByDescending(x => x.Time).Take(500)) OperationHistory.Add(item);
        var profilesLoad = ProfileStore.LoadWithStatus(_paths.ProfilesFile, _logger);
        _allProfiles = profilesLoad.HasData ? profilesLoad.Value! : [];
        _profilesWritable = profilesLoad.Status is not DataLoadStatus.Failed and not DataLoadStatus.Missing;
        RecordStorageLoad("Network profiles / 网络方案", profilesLoad.Status, profilesLoad.SourcePath, profilesLoad.Error);
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
        _logger.Info($"MainWindow initialization phase completed: phase=load-local-state-and-build-view elapsedMs={initializationStopwatch.ElapsedMilliseconds}");
        Dispatcher.BeginInvoke(() =>
        {
            SetBusy(false);
            if (!_safetyOnboardingCompleted) ShowSafetyGuide(firstRun: true);
            if (_paths.MigrationWarnings.Count > 0)
            {
                AppDialog.Show(this, Ui("migration.warning.title"), Ui("migration.warning.body", string.Join(Environment.NewLine, _paths.MigrationWarnings)), danger: true);
            }
            if (_startupDataWarnings.Count > 0)
                AppDialog.Show(this, "Data recovery / 数据恢复", string.Join(Environment.NewLine, _startupDataWarnings), danger: true);
            _ = InitializeNetworkStateAsync();
            _ = CheckForUpdatesAsync();
        }, DispatcherPriority.ApplicationIdle);
    }

    private async Task InitializeNetworkStateAsync()
    {
        var networkStateStopwatch = Stopwatch.StartNew();
        await RefreshAdaptersAsync();
        _logger.Info($"Startup phase completed: phase=adapter-and-route-discovery elapsedMs={networkStateStopwatch.ElapsedMilliseconds}");
        var recoveryStopwatch = Stopwatch.StartNew();
        await RecoverStaleRoutesAsync();
        _logger.Info($"Startup phase completed: phase=stale-route-recovery elapsedMs={recoveryStopwatch.ElapsedMilliseconds}");
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _logger.Info($"Window close requested: settingsDirty={_settingsDirty}, activeNetworkWorkflow={_activeNetworkWorkflow is not null}, standaloneOperation={_standaloneOperationCompletion is not null}, operationCancellation={_operationCts is not null}, closingCleanupStarted={_closingCleanupStarted}");
        if (_closeAfterCleanup)
        {
            _logger.Info("Window close accepted after cleanup completed");
            base.OnClosing(e);
            return;
        }
        if (_closingCleanupStarted)
        {
            _logger.Info("Window close deferred because cleanup is already running");
            e.Cancel = true;
            return;
        }

        if (_settingsDirty && !_unsavedSettingsExitConfirmed)
        {
            _logger.Info("Window close is waiting for confirmation because settings are unsaved");
            var confirmed = AppDialog.Show(this,
                IsChineseUi() ? "未保存设置" : "Unsaved Settings",
                IsChineseUi()
                    ? "设置页存在尚未保存的更改。关闭后这些更改会丢失，仍要退出吗？"
                    : "The Settings page has unsaved changes. They will be lost when the application closes. Exit anyway?",
                confirm: true);
            _logger.Info($"Unsaved-settings close confirmation returned: confirmed={confirmed}");
            if (!confirmed)
            {
                _logger.Info("Window close canceled by the user at unsaved-settings confirmation");
                e.Cancel = true;
                return;
            }
        }
        if (_settingsDirty) _unsavedSettingsExitConfirmed = true;

        if (_activeNetworkWorkflow is not null || _standaloneOperationCompletion is not null)
        {
            e.Cancel = true;
            if (_waitingForOperationBeforeClose) return;
            var closeWaitStopwatch = Stopwatch.StartNew();
            var activeOperationName = _activeOperationText ?? "unknown";
            _waitingForOperationBeforeClose = true;
            _closingCleanupStarted = true;
            IsEnabled = false;
            SetBusyTotalPhases(0);
            SetBusyPhase(IsChineseUi()
                ? "正在取消当前操作并等待恢复完成..."
                : "Canceling the active operation and waiting for recovery to finish...");
            _logger.Info($"Window close requested during active operation: operation={activeOperationName}");
            var activeWorkflow = _activeNetworkWorkflow;
            var completionTasks = new List<Task>();
            if (activeWorkflow is not null)
            {
                activeWorkflow.Cancel();
                completionTasks.Add(activeWorkflow.Completion);
            }
            if (_operationCts is not null)
            {
                _operationCts.Cancel();
                if (_standaloneOperationCompletion is not null)
                    completionTasks.Add(_standaloneOperationCompletion.Task);
            }
            await Task.WhenAll(completionTasks);
            _logger.Info($"Window close resumed after active operation ended: elapsedMs={closeWaitStopwatch.ElapsedMilliseconds}");
            _waitingForOperationBeforeClose = false;
            _closingCleanupStarted = false;
            IsEnabled = true;
            await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
            return;
        }

        SaveWindowLayout();
        e.Cancel = true;
        _closingCleanupStarted = true;
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        _adapterStatusTimer.Stop();
        _adapterNetworkChangeTimer.Stop();
        await _adapterStatusCoordinator.DisposeAsync();
        _logDisplayTimer.Stop();
        lock (_logDisplaySync) _logDisplayClosed = true;
        _logger.LineWritten -= Logger_LineWritten;
        DrainLogDisplayQueue(force: true);
        _feedbackTimer.Stop();
        _updateController.StateChanged -= UpdateController_StateChanged;
        IsEnabled = false;
        _logger.Info("Window close cleanup step started: update-controller-dispose");
        await _updateController.DisposeAsync();
        _logger.Info("Window close cleanup step completed: update-controller-dispose");
        SetBusy(true, IsChineseUi() ? "正在保存恢复信息并停止 DHCP..." : "Saving recovery state and stopping DHCP...");
        _logger.Info("Window close cleanup step started: work-environment-cleanup");
        await CleanupWorkEnvironmentAsync();
        _logger.Info("Window close cleanup step completed: work-environment-cleanup");
        SetBusy(false);
        _scanProgressTimer.Stop();
        _scanHistoryFlushTimer.Stop();
        if (_operationHistoryWritable)
        {
            try { await _operationHistoryWriter.SaveAsync(_operationHistory.ToArray()); }
            catch (Exception ex)
            {
                _operationHistorySaveFailed = true;
                _logger.Error("Final operation-history save failed; the in-memory snapshot is retained until process exit", ex);
            }
        }
        await _operationHistoryWriter.DisposeAsync();
        await _leaseProbeCoordinator.DisposeAsync();
        await _probe.DisposeAsync();
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
        BtnEditFavorite.Content = _lang.T("edit.favorite");
        BtnTemplateFavorite.Content = _lang.T("template.favorite");
        BtnRestoreFavoritePresets.Content = _lang.T("restore.favorite.presets");
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
        BtnCancelUpdateDownload.Content = _lang.T("cancel");
        BtnRestartUpgrade.Content = _lang.T("update.restart.upgrade");
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
        if (_updateState.CheckStarted) return;
        try
        {
            var result = await _updateController.CheckAsync();
            if (result == null) return;
            _logger.Info(result.Succeeded
                ? $"Update check completed: current={CurrentVersionText} latest={result.LatestVersion} new={result.IsNewVersion}"
                : "Update check failed: " + result.Error);
        }
        catch (OperationCanceledException) when (_closingCleanupStarted)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.Error("Update check failed", ex);
        }
    }

    private void UpdateVersionPresentation()
    {
        var result = _updateState.CheckResult;
        if (!_updateState.CheckStarted)
        {
            TxtUpdateStatus.Text = "";
            TxtUpdateLink.Visibility = Visibility.Collapsed;
            UpdateLink.IsEnabled = false;
            BtnRestartUpgrade.Visibility = Visibility.Collapsed;
            return;
        }

        if (result == null)
        {
            var speedSummary = FormatUpdateSpeedSummary(_updateState.SourceSpeeds);
            TxtUpdateStatus.Text = string.IsNullOrWhiteSpace(speedSummary)
                ? _lang.T("checking.update")
                : IsChineseUi() ? $"检查更新 · {speedSummary}" : $"Checking updates · {speedSummary}";
            TxtUpdateStatus.ToolTip = FormatUpdateSpeedDetails(_updateState.SourceSpeeds);
            TxtUpdateLink.Visibility = Visibility.Collapsed;
            UpdateLink.IsEnabled = false;
            BtnRestartUpgrade.Visibility = Visibility.Collapsed;
            return;
        }

        if (result.Succeeded && result.IsNewVersion)
        {
            var speedSummary = FormatUpdateSpeedSummary(result.DownloadSpeeds, result.DownloadUrl);
            TxtUpdateStatus.Text = speedSummary;
            var speedDetails = FormatUpdateSpeedDetails(result.DownloadSpeeds, result.DownloadUrl);
            TxtUpdateStatus.ToolTip = speedDetails;
            UpdateLink.Inlines.Clear();
            UpdateLink.Inlines.Add(new Run($"{_lang.T("new.version")} v{result.LatestVersion}"));
            UpdateLink.ToolTip = $"{speedDetails}{Environment.NewLine}{result.DownloadUrl}";
            TxtUpdateLink.Visibility = Visibility.Visible;
            UpdateLink.IsEnabled = true;
            return;
        }

        TxtUpdateLink.Visibility = Visibility.Collapsed;
        UpdateLink.IsEnabled = false;
        BtnRestartUpgrade.Visibility = Visibility.Collapsed;
        TxtUpdateStatus.Text = result.Succeeded ? _lang.T("latest.version") : _lang.T("update.failed");
        TxtUpdateStatus.ToolTip = result.Succeeded ? null : result.Error;
    }

    private string FormatUpdateSpeedSummary(IReadOnlyList<UpdateSourceSpeed> speeds, string? selectedUrl = null)
    {
        if (speeds.Count == 0) return "";
        var summary = string.Join(" · ", speeds.Select(source =>
        {
            var state = source.IsChecking
                ? IsChineseUi() ? "测速中" : "testing"
                : source.BytesPerSecond is > 0
                    ? FormatBytesPerSecond(source.BytesPerSecond.Value)
                    : IsChineseUi() ? "不可用" : "unavailable";
            return $"{source.SourceName} {state}";
        }));
        var selectedName = speeds.FirstOrDefault(source => source.Url.Equals(selectedUrl, StringComparison.OrdinalIgnoreCase))?.SourceName;
        if (!string.IsNullOrWhiteSpace(selectedName))
            summary += IsChineseUi() ? $" · 已选 {selectedName}" : $" · Selected {selectedName}";
        return summary;
    }

    private string FormatUpdateSpeedDetails(IReadOnlyList<UpdateSourceSpeed> speeds, string? selectedUrl = null)
    {
        if (speeds.Count == 0) return "";
        return string.Join(Environment.NewLine, speeds.Select(source =>
        {
            var state = source.IsChecking
                ? IsChineseUi() ? "测速中" : "testing"
                : source.BytesPerSecond is > 0
                    ? FormatBytesPerSecond(source.BytesPerSecond.Value)
                    : IsChineseUi() ? "测速失败" : "unavailable";
            var selected = source.Url.Equals(selectedUrl, StringComparison.OrdinalIgnoreCase)
                ? IsChineseUi() ? "（当前首选）" : " (selected)"
                : "";
            return $"{source.SourceName}: {state}{selected}";
        }));
    }

    private static string FormatBytesPerSecond(double bytesPerSecond) => bytesPerSecond >= 1024 * 1024
        ? $"{bytesPerSecond / (1024 * 1024):0.0} MB/s"
        : bytesPerSecond >= 1024
            ? $"{bytesPerSecond / 1024:0.0} KB/s"
            : $"{bytesPerSecond:0} B/s";

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
            AllowGateway.IsChecked = _settings.AllowDhcpOnAdapterWithGateway;
            DetectExistingDhcp.IsChecked = _settings.DetectExistingDhcpBeforeStart;
            AllowRestartAnyAdapter.IsChecked = _settings.AllowRestartOnAnyAdapter;
            AllowMacChangeAnyAdapter.IsChecked = _settings.AllowMacChangeOnAnyAdapter;
            RedactSupportPackage.IsChecked = _settings.RedactSupportPackage;
            _safetyOnboardingCompleted = _settingsWritable && _settings.SafetyOnboardingCompleted;
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
        TrySaveSettings(showFeedback: false);
    }

    private bool TrySaveSettings(bool showFeedback = true)
    {
        if (!_settingsWritable)
        {
            if (showFeedback) ShowStorageWriteBlocked("Settings / 设置");
            return false;
        }
        try
        {
            JsonStore.Save(_paths.SettingsFile, _settings);
            return true;
        }
        catch (Exception ex)
        {
            _settingsWritable = false;
            _logger.Error("Save settings failed; existing files were retained", ex);
            UpdateSettingsStatus();
            if (showFeedback)
                ShowActionFeedback("设置保存失败，原文件和备份已保留。", "Settings were not saved; the source and backup files were retained.", error: true);
            return false;
        }
    }

    private async Task RefreshAdaptersAsync(bool allowDuringNetworkWorkflow = false, CancellationToken cancellationToken = default)
    {
        if (_adapterRefreshInProgress) return;
        if (_activeNetworkWorkflow is not null && !allowDuringNetworkWorkflow) return;
        if (_activeNetworkWorkflow is null && _operationCts is not null) return;
        _adapterRefreshInProgress = true;
        var ownsBusyState = _activeNetworkWorkflow is null;
        var previousIndex = SelectedAdapter?.InterfaceIndex;
        Task<IReadOnlyList<CurrentStaticRoute>>? currentRoutesReadTask = null;
        try
        {
            if (ownsBusyState) SetBusy(true, "Reading network information... / 正在读取网络信息...", totalPhases: 2);
            var discoveryStopwatch = Stopwatch.StartNew();
            var adapterReadTask = Task.Run(() => _adapterService.GetAdapters());
            currentRoutesReadTask = _routeService.GetCurrentStaticRoutesAsync(
                ownsBusyState ? OperationToken : cancellationToken);
            _logger.Info("Parallel network discovery started: adapters and current IPv4/IPv6 routes");

            var adapters = (await adapterReadTask)
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
            var readToken = ownsBusyState ? OperationToken : cancellationToken;
            if (ownsBusyState) SetBusyPhase("Loading current IPv4/IPv6 routes... / 正在读取当前 IPv4/IPv6 路由...");
            await LoadCurrentStaticRoutesAsync(adapters, readToken, currentRoutesReadTask);
            _logger.Info($"Parallel network discovery completed: adapters={adapters.Count} routes={CurrentStaticRoutes.Count} elapsedMs={discoveryStopwatch.ElapsedMilliseconds}");
            _businessConfigurationFingerprint = CaptureBusinessConfigurationFingerprint();
            _logger.Info($"UI adapter list loaded: count={Adapters.Count}, selected={SelectedAdapter?.DisplayName ?? ""}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || OperationToken.IsCancellationRequested)
        {
            _logger.Info("Adapter refresh canceled while waiting for an active operation to finish");
        }
        catch (Exception ex)
        {
            _logger.Error("Refresh adapters failed", ex);
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("refresh"), ExplainFailure(ex, _lang.T("help.refresh")), danger: true);
        }
        finally
        {
            if (currentRoutesReadTask is not null)
            {
                try { await currentRoutesReadTask; }
                catch { /* The refresh path already reports route-read errors; observe a concurrent failure if adapter enumeration failed first. */ }
            }
            _adapterRefreshInProgress = false;
            if (ownsBusyState) SetBusy(false);
            UpdateAdapterActionButtons();
            UpdateSessionStatus();
            if (_adapterStatusRefreshDirty && !_closingCleanupStarted && _activeNetworkWorkflow is null)
                RefreshSelectedAdapterStatus();
            if (!_closingCleanupStarted) _adapterStatusTimer.Start();
        }
    }

    private async Task LoadCurrentStaticRoutesAsync(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        CancellationToken ct = default,
        Task<IReadOnlyList<CurrentStaticRoute>>? routesReadTask = null)
    {
        try
        {
            var adapterByIndex = adapters
                .Where(x => !string.IsNullOrWhiteSpace(x.InterfaceIndex))
                .ToDictionary(x => x.InterfaceIndex, StringComparer.OrdinalIgnoreCase);
            CurrentStaticRoutes.Clear();
            var routes = routesReadTask is null
                ? await _routeService.GetCurrentStaticRoutesAsync(ct)
                : await routesReadTask;
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
        var result = FavoriteStore.LoadWithStatus(_paths.FavoritesFile, _logger, migrateLegacy: true);
        var presetState = FavoritePresetStore.Load(_paths.FavoritePresetStateFile, _logger);
        _favoritePresetStateWritable = presetState.IsWritable;
        if (!presetState.IsWritable)
        {
            _startupDataWarnings.Add("Favorite preset deletion state is unreadable; preset restore and deletion are disabled.");
            _logger.Warn($"Favorite preset deletion state is unreadable: {_paths.FavoritePresetStateFile}");
        }
        _allFavorites = result.HasData ? result.Favorites : [];
        _favoritesWritable = result.Status is not DataLoadStatus.Failed and not DataLoadStatus.Missing
            && result.MigrationError is null;
        RecordStorageLoad("Favorites / 收藏", result.Status, result.SourcePath, result.Error ?? result.MigrationError);
        FilterFavorites();
    }

    private bool SaveFavorites()
    {
        if (!_favoritesWritable)
        {
            ShowStorageWriteBlocked("Favorites / 收藏");
            return false;
        }
        try
        {
            FavoriteStore.Save(_paths.FavoritesFile, _allFavorites, _logger);
            return true;
        }
        catch (Exception ex)
        {
            _favoritesWritable = false;
            _logger.Error("Save favorites failed; source and backup files were retained", ex);
            UpdateFavoriteButtons();
            ShowActionFeedback("收藏保存失败，原文件和备份已保留。", "Favorites were not saved; the source and backup files were retained.", error: true);
            return false;
        }
    }

    private List<FavoriteConfig> SnapshotFavorites() => _allFavorites.Select(FavoriteStore.Clone).ToList();

    private bool SaveFavoritesOrRestore(List<FavoriteConfig> previous)
    {
        if (SaveFavorites()) return true;
        _allFavorites = previous;
        FilterFavorites();
        return false;
    }

    private void LoadNetworkHistory()
    {
        var result = JsonStore.Load<List<AdapterIpHistoryItem>>(_paths.NetworkHistoryFile, _logger);
        RecordStorageLoad("Network history / 网卡历史", result.Status, result.SourcePath, result.Error, missingIsExpected: true);
        foreach (var item in (result.HasData ? result.Value! : []).OrderByDescending(x => x.ChangedAt).Take(5)) AdapterIpHistory.Add(item);
    }

    private void SaveNetworkHistory()
    {
        try { JsonStore.Save(_paths.NetworkHistoryFile, AdapterIpHistory.ToList()); }
        catch (Exception ex) { _logger.Error("Save network history failed; existing files were retained", ex); }
    }

    private void LoadMacBackups()
    {
        var result = JsonStore.Load<List<AdapterMacBackup>>(_paths.MacBackupsFile, _logger);
        _macBackupsWritable = result.Status is not DataLoadStatus.Failed;
        _savedMacBackups = result.HasData ? result.Value! : [];
        RecordStorageLoad("MAC recovery records / MAC 恢复记录", result.Status, result.SourcePath, result.Error, missingIsExpected: true);
        _originalAdapterMacs.Clear();
        foreach (var backup in _savedMacBackups.Where(x => !string.IsNullOrWhiteSpace(x.OriginalMacAddress)))
        {
            _originalAdapterMacs[MacBackupKey(backup)] = backup;
        }
    }

    private void LoadDhcpFirewallRecoveries()
    {
        var result = JsonStore.Load<List<DhcpFirewallRuleLease>>(_paths.DhcpFirewallSessionFile, _logger);
        _dhcpFirewallJournalWritable = result.Status != DataLoadStatus.Failed;
        if (result.HasData) _dhcpFirewallRecoveries.AddRange(result.Value!.Where(x => x.Rules.Count > 0));
        RecordStorageLoad("DHCP firewall recovery journal / DHCP 防火墙恢复日志", result.Status, result.SourcePath, result.Error, missingIsExpected: true);
        if (!_dhcpFirewallJournalWritable)
            _startupDataWarnings.Add(Ui("firewall.journal.unreadable"));
    }

    private void LoadDhcpLeaseJournal()
    {
        var result = JsonStore.Load<DhcpLeaseJournal>(_paths.DhcpLeaseJournalFile, _logger);
        _dhcpLeaseJournalWritable = result.Status != DataLoadStatus.Failed;
        _dhcpLeaseJournal = result.HasData ? result.Value! : new DhcpLeaseJournal();
        _dhcpLeaseJournal.Scopes ??= [];
        try
        {
            foreach (var scope in _dhcpLeaseJournal.Scopes)
            {
                if (scope == null || !Guid.TryParse(scope.AdapterId, out _) || !IPAddress.TryParse(scope.ServerIp, out var serverIp)
                    || !IPAddress.TryParse(scope.SubnetMask, out var subnetMask)
                    || serverIp.AddressFamily != AddressFamily.InterNetwork || subnetMask.AddressFamily != AddressFamily.InterNetwork)
                    throw new InvalidDataException("DHCP lease journal contains an invalid adapter or IPv4 scope.");
                if (scope.Bindings?.Any(x => x == null) == true || scope.DeclinedAddresses?.Any(x => x == null) == true)
                    throw new InvalidDataException("DHCP lease journal contains a null lease or conflict record.");
                scope.Bindings ??= [];
                scope.DeclinedAddresses ??= [];
            }
            var duplicateScope = _dhcpLeaseJournal.Scopes
                .GroupBy(x => DhcpJournalScopeKey(x.AdapterId, x.ServerIp, x.SubnetMask), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => x.Count() > 1);
            if (duplicateScope != null) throw new InvalidDataException("DHCP lease journal contains duplicate adapter/subnet scopes.");
        }
        catch (Exception ex)
        {
            _dhcpLeaseJournalWritable = false;
            _logger.Error("DHCP lease journal scope validation failed; original journal was preserved and DHCP start is locked", ex);
        }
        RecordStorageLoad("DHCP lease recovery journal / DHCP 租约恢复日志", result.Status, result.SourcePath, result.Error, missingIsExpected: true);
        if (!_dhcpLeaseJournalWritable) _startupDataWarnings.Add(Ui("dhcp.lease.journal.unreadable"));
    }

    private DhcpLeaseScopeState PrepareDhcpLeaseScope(DhcpServerSettings settings)
    {
        if (!_dhcpLeaseJournalWritable) throw new InvalidOperationException(Ui("dhcp.lease.journal.unreadable"));
        var scope = _dhcpLeaseJournal.Scopes.FirstOrDefault(x =>
            DhcpJournalScopeKey(x.AdapterId, x.ServerIp, x.SubnetMask)
                .Equals(DhcpJournalScopeKey(settings.AdapterId, settings.ServerIp.ToString(), settings.SubnetMask.ToString()), StringComparison.OrdinalIgnoreCase));
        (List<DhcpLeaseBinding> Bindings, List<DhcpDeclinedAddress> Declined, string ServerIp, string SubnetMask, string PoolStart, string PoolEnd)? prior = scope == null
            ? null
            : (scope.Bindings.ToList(), scope.DeclinedAddresses.ToList(), scope.ServerIp, scope.SubnetMask, scope.PoolStart, scope.PoolEnd);
        if (scope == null)
        {
            scope = new DhcpLeaseScopeState
            {
                AdapterId = settings.AdapterId,
                ServerIp = settings.ServerIp.ToString(),
                SubnetMask = settings.SubnetMask.ToString(),
                PoolStart = settings.PoolStart.ToString(),
                PoolEnd = settings.PoolEnd.ToString()
            };
            _dhcpLeaseJournal.Scopes.Add(scope);
        }
        scope.ServerIp = settings.ServerIp.ToString();
        scope.SubnetMask = settings.SubnetMask.ToString();
        scope.PoolStart = settings.PoolStart.ToString();
        scope.PoolEnd = settings.PoolEnd.ToString();
        var now = DateTimeOffset.UtcNow;
        scope.Bindings = scope.Bindings.Where(x => x.LeaseEnd > now).ToList();
        scope.DeclinedAddresses = scope.DeclinedAddresses.Where(x => x.ExpiresAt > now).ToList();
        try
        {
            JsonStore.Save(_paths.DhcpLeaseJournalFile, _dhcpLeaseJournal);
        }
        catch (Exception ex)
        {
            if (prior == null) _dhcpLeaseJournal.Scopes.Remove(scope);
            else
            {
                scope.Bindings = prior.Value.Item1;
                scope.DeclinedAddresses = prior.Value.Item2;
                scope.ServerIp = prior.Value.ServerIp;
                scope.SubnetMask = prior.Value.SubnetMask;
                scope.PoolStart = prior.Value.PoolStart;
                scope.PoolEnd = prior.Value.PoolEnd;
            }
            _dhcpLeaseJournalWritable = false;
            _logger.Error("Prepare DHCP lease journal failed; existing files were retained", ex);
            throw new IOException(Ui("dhcp.lease.journal.unreadable"), ex);
        }
        UpdateRecoveryBanner();
        return scope;
    }

    private static string DhcpJournalScopeKey(string adapterId, string serverIp, string subnetMask)
    {
        if (!Guid.TryParse(adapterId, out var normalizedAdapterId)
            || !IPAddress.TryParse(serverIp, out var address)
            || !IPAddress.TryParse(subnetMask, out var mask)
            || address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("DHCP lease scope identity is invalid.");
        var network = IpNetwork.FromUInt32(IpNetwork.ToUInt32(address) & IpNetwork.ToUInt32(mask));
        return $"{normalizedAdapterId:N}|{network}|{mask}";
    }

    private void PersistDhcpLeaseTable(DhcpLeaseScopeState scope, DhcpLeaseTableSnapshot snapshot)
    {
        if (!_dhcpLeaseJournalWritable) throw new InvalidOperationException(Ui("dhcp.lease.journal.unreadable"));
        var previousBindings = scope.Bindings;
        var previousDeclined = scope.DeclinedAddresses;
        scope.Bindings = snapshot.Bindings.ToList();
        scope.DeclinedAddresses = snapshot.DeclinedAddresses.ToList();
        try
        {
            JsonStore.Save(_paths.DhcpLeaseJournalFile, _dhcpLeaseJournal);
        }
        catch (Exception ex)
        {
            scope.Bindings = previousBindings;
            scope.DeclinedAddresses = previousDeclined;
            _dhcpLeaseJournalWritable = false;
            _logger.Error("Save DHCP lease journal failed; existing files were retained and no DHCP acknowledgement will be sent", ex);
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(UpdateRecoveryBanner);
            throw new IOException(Ui("dhcp.lease.journal.unreadable"), ex);
        }
    }

    private void CommitDhcpFirewallLease(DhcpFirewallRuleLease lease)
    {
        if (!_dhcpFirewallJournalWritable)
            throw new InvalidOperationException(Ui("firewall.journal.unreadable"));
        var index = _dhcpFirewallRecoveries.FindIndex(x => x.LeaseId.Equals(lease.LeaseId, StringComparison.OrdinalIgnoreCase));
        if (lease.Rules.Count == 0)
        {
            if (index >= 0) _dhcpFirewallRecoveries.RemoveAt(index);
        }
        else if (index < 0)
        {
            _dhcpFirewallRecoveries.Add(lease);
        }
        else
        {
            _dhcpFirewallRecoveries[index] = lease;
        }
        try
        {
            JsonStore.Save(_paths.DhcpFirewallSessionFile, _dhcpFirewallRecoveries);
        }
        catch (Exception ex)
        {
            _dhcpFirewallJournalWritable = false;
            _logger.Error("Save DHCP firewall recovery journal failed; existing files were retained", ex);
            UpdateRecoveryBanner();
            throw new IOException("The DHCP firewall recovery journal could not be updated. Any recorded intent was retained for recovery.", ex);
        }
        UpdateRecoveryBanner();
    }

    private void TrackActiveDhcpFirewallLease(DhcpFirewallRuleLease lease)
    {
        _dhcpFirewallRules = lease;
        CommitDhcpFirewallLease(lease);
    }

    private bool SaveMacBackups()
    {
        if (!_macBackupsWritable)
        {
            _logger.Warn("MAC backup save blocked because its source could not be read");
            return false;
        }
        _savedMacBackups = _originalAdapterMacs.Values
            .Where(x => x.RestoreOnExit && !string.IsNullOrWhiteSpace(x.OriginalMacAddress))
            .OrderBy(x => x.AdapterName)
            .ToList();
        try { JsonStore.Save(_paths.MacBackupsFile, _savedMacBackups); }
        catch (Exception ex)
        {
            _macBackupsWritable = false;
            _logger.Error("Save MAC recovery records failed; source and backup files were retained", ex);
            return false;
        }
        UpdateRecoveryBanner();
        return true;
    }

    private void RecordStorageLoad(string store, DataLoadStatus status, string? sourcePath, Exception? error, bool missingIsExpected = false)
    {
        if (status == DataLoadStatus.Loaded || status == DataLoadStatus.LoadedEmpty || (missingIsExpected && status == DataLoadStatus.Missing)) return;
        if (status == DataLoadStatus.RestoredFromBackup)
        {
            var source = Path.GetFileName(sourcePath ?? "backup");
            _startupDataWarnings.Add($"{store}: restored from valid backup {source}; the damaged or missing primary was preserved. / {store}：已从有效备份 {source} 恢复；损坏或缺失的主文件已保留。");
        }
        else if (status == DataLoadStatus.Missing)
        {
            _startupDataWarnings.Add($"{store}: no primary or backup file was found; writes are locked until initialization succeeds. / {store}：未找到主文件或备份；初始化成功前已锁定写入。");
        }
        else
        {
            _startupDataWarnings.Add($"{store}: primary and backup could not be read; files were preserved and related writes are locked. / {store}：主文件和备份均无法读取；文件已保留，相关写入已锁定。");
        }
        if (error is not null) _logger.Warn($"Data-load state for {store}: {status}; {error.Message}");
    }

    private void ShowStorageWriteBlocked(string store) => ShowActionFeedback(
        $"{store} 数据未成功读取，当前不能保存。请先恢复或修复原文件。",
        $"{store} data was not read successfully and cannot be saved. Restore or repair the source file first.",
        error: true);

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
        InsertOperationHistory(new OperationHistoryItem
        {
            Type = type,
            Scope = string.IsNullOrWhiteSpace(scope) ? (SelectedAdapter?.Name ?? "") : scope,
            IpAddress = ip,
            MacAddress = mac,
            Status = status,
            Detail = detail,
            DurationMs = measuredDuration,
            RollbackAvailable = rollbackAvailable
        });
        PersistOperationHistoryImmediately();
        RefreshOperationHistoryView();
        UpdateLastOperationPresentation();
        UpdateRecoveryBanner();
    }

    private void InsertOperationHistory(OperationHistoryItem item)
    {
        _operationHistory.Insert(0, item);
        while (_operationHistory.Count > 500) _operationHistory.RemoveAt(_operationHistory.Count - 1);
    }

    private void PersistOperationHistoryImmediately()
    {
        if (!_operationHistoryWritable)
        {
            _operationHistorySaveFailed = true;
            UpdateHistorySummary();
            return;
        }
        try
        {
            _operationHistoryWriter.SaveAsync(_operationHistory.ToArray()).GetAwaiter().GetResult();
            _operationHistorySaveFailed = false;
        }
        catch (Exception ex)
        {
            _operationHistorySaveFailed = true;
            _logger.Error("Save operation history failed; the in-memory snapshot is retained for retry", ex);
        }
        UpdateHistorySummary();
    }

    private void AddScanOperationHistory(ScanResult result)
    {
        InsertOperationHistory(new OperationHistoryItem
        {
            Type = "Scan",
            Scope = SelectedAdapter?.Name ?? "",
            IpAddress = result.IpAddress,
            MacAddress = result.MacAddress,
            Status = result.StatusText,
            Detail = result.Remark
        });
        _pendingScanHistoryCount++;
        if (!_scanHistoryFlushTimer.IsEnabled) _scanHistoryFlushTimer.Start();
        if (_pendingScanHistoryCount >= 128) _ = FlushScanHistoryAsync();
    }

    private async Task<bool> FlushScanHistoryAsync(bool force = false)
    {
        if (_pendingScanHistoryCount == 0 || (!force && _pendingScanHistoryCount < 128)) return !_operationHistorySaveFailed;
        if (_scanHistoryFlushInProgress) return false;
        if (!_operationHistoryWritable)
        {
            _operationHistorySaveFailed = true;
            UpdateHistorySummary();
            return false;
        }

        _scanHistoryFlushInProgress = true;
        var countBeingCommitted = _pendingScanHistoryCount;
        _pendingScanHistoryCount = 0;
        try
        {
            await _operationHistoryWriter.SaveAsync(_operationHistory.ToArray());
            _operationHistorySaveFailed = false;
            if (_pendingScanHistoryCount == 0) _scanHistoryFlushTimer.Stop();
            UpdateHistorySummary();
            return true;
        }
        catch (Exception ex)
        {
            _pendingScanHistoryCount += countBeingCommitted;
            _operationHistorySaveFailed = true;
            _logger.Error("Commit scan history batch failed; the in-memory snapshot is retained for retry", ex);
            UpdateHistorySummary();
            return false;
        }
        finally
        {
            _scanHistoryFlushInProgress = false;
            if (_pendingScanHistoryCount >= 128 && !_operationHistorySaveFailed)
                await FlushScanHistoryAsync(force: false);
            else if (force && _pendingScanHistoryCount > 0 && !_operationHistorySaveFailed)
                await FlushScanHistoryAsync(force: true);
        }
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
        var firewallCount = _dhcpFirewallRecoveries.Sum(x => x.Rules.Count);
        var routeJournalUnreadable = !_routeJournalWritable;
        var firewallJournalUnreadable = !_dhcpFirewallJournalWritable;
        var leaseJournalUnreadable = !_dhcpLeaseJournalWritable;
        RecoveryBanner.Visibility = routeCount > 0 || macCount > 0 || firewallCount > 0 || routeJournalUnreadable || firewallJournalUnreadable || leaseJournalUnreadable ? Visibility.Visible : Visibility.Collapsed;
        var journalWarnings = new List<string>();
        if (routeJournalUnreadable && firewallJournalUnreadable) journalWarnings.Add(Ui("recovery.banner.journals.unreadable"));
        else if (routeJournalUnreadable) journalWarnings.Add(Ui("recovery.banner.route.journal.unreadable"));
        else if (firewallJournalUnreadable) journalWarnings.Add(Ui("recovery.banner.firewall.journal.unreadable"));
        if (leaseJournalUnreadable) journalWarnings.Add(Ui("recovery.banner.lease.journal.unreadable"));
        TxtRecoveryBanner.Text = journalWarnings.Count > 0
            ? string.Join(" ", journalWarnings)
            : routeCount > 0 || macCount > 0 || firewallCount > 0
                ? Ui("recovery.banner", routeCount + macCount + firewallCount, routeCount, macCount, firewallCount)
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

    private void RefreshOperationHistoryView()
    {
        if (HistoryGrid == null) return;
        var selected = HistoryGrid.SelectedItems.OfType<OperationHistoryItem>().ToHashSet();
        var q = HistoryFilterBox.Text?.Trim() ?? "";
        OperationHistory.Clear();
        var visible = _operationHistory.Where(x => string.IsNullOrWhiteSpace(q)
            || x.Type.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.Scope.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.Status.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.IpAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
            || x.MacAddress.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var item in visible)
        {
            OperationHistory.Add(item);
        }
        foreach (var item in visible.Where(selected.Contains)) HistoryGrid.SelectedItems.Add(item);
        UpdateHistorySummary();
    }

    private void UpdateHistorySummary()
    {
        if (TxtHistorySummary == null) return;
        TxtHistorySummary.Text = IsChineseUi()
            ? $"显示 {OperationHistory.Count}/{_operationHistory.Count} 条"
            : $"Showing {OperationHistory.Count}/{_operationHistory.Count}";
        if (!_operationHistoryWritable || _operationHistorySaveFailed)
            TxtHistorySummary.Text += IsChineseUi() ? "（未保存，保留内存记录并等待重试）" : " (not saved; retained in memory for retry)";
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
        if (!TryGetManualScanPlan(out var scanPlan, out var validation))
        {
            AppDialog.Show(this, _lang.T("preview.scan"), validation, danger: true);
            return false;
        }
        var scope = scanPlan!.IsSingleTarget
            ? (IsChineseUi() ? $"仅探测 {scanPlan.TargetIp}" : $"probe only {scanPlan.TargetIp}")
            : (IsChineseUi() ? $"扫描整个网段 {scanPlan.LocalIp}/{scanPlan.PrefixLength}" : $"scan subnet {scanPlan.LocalIp}/{scanPlan.PrefixLength}");
        scope += IsChineseUi()
            ? $"，实际目标 {scanPlan.TargetCount} 个（上限 {ScanRangePlan.MaxProbeTargets}）"
            : $", {scanPlan.TargetCount} actual target(s) (limit {ScanRangePlan.MaxProbeTargets})";
        var current = string.IsNullOrWhiteSpace(adapter.IPv4Address) ? "DHCP / 未配置" : $"{adapter.IPv4Address} / {adapter.SubnetMask}";
        var message = IsChineseUi()
            ? $"操作：应用临时 IP 后扫描\n网卡：{adapter.DisplayName}\n连接：{adapter.ConnectionDisplay}，{adapter.KindDisplay}\n\n当前 IPv4：{current}\n变更为：{ManualIp.Text.Trim()} / {ManualMask.Text.Trim()}\n扫描范围：{scope}\n\n应用前会保存网卡备份；扫描结束后不会自动恢复，仍可使用“恢复网卡”或回滚。"
            : $"Operation: Apply a temporary IP and scan\nAdapter: {adapter.DisplayName}\nLink: {adapter.ConnectionDisplay}, {adapter.KindDisplay}\n\nCurrent IPv4: {current}\nChange to: {ManualIp.Text.Trim()} / {ManualMask.Text.Trim()}\nScan scope: {scope}\n\nAn adapter backup is saved before applying. The adapter is not restored automatically after scanning; use Restore Adapter or Rollback when needed.";
        return AppDialog.Show(this, _lang.T("preview.scan"), message, confirm, danger: confirm);
    }

    private async void StartDhcp_Click(object sender, RoutedEventArgs e)
    {
        if (!_dhcpFirewallJournalWritable)
        {
            AppDialog.Show(this, _lang.T("start.dhcp"), Ui("firewall.journal.unreadable"), danger: true);
            return;
        }
        if (!_dhcpLeaseJournalWritable)
        {
            AppDialog.Show(this, _lang.T("start.dhcp"), Ui("dhcp.lease.journal.unreadable"), danger: true);
            return;
        }
        if (_dhcpFirewallRecoveries.Count > 0)
        {
            AppDialog.Show(this, _lang.T("start.dhcp"), Ui("firewall.recovery.required"), danger: true);
            return;
        }
        var workflow = TryBeginNetworkWorkflow("DHCP start");
        if (workflow is null) return;
        var serverStarted = false;
        DhcpSessionRecovery? sessionRecovery = null;
        DhcpLeaseScopeState? leaseScope = null;
        try
        {
            if (DhcpConfirm.IsChecked != true)
            {
                var message = "请勾选：" + _lang.T("isolated.confirm");
                _logger.Warn("Isolated network confirmation missing: " + message);
                AppDialog.Show(this, _lang.T("start.dhcp"), message);
                return;
            }
            if (!EnsureSafetyOnboarding()) return;
            SetBusy(true,
                IsChineseUi() ? "正在检查目标网卡和 DHCP 参数..." : "Checking the target adapter and DHCP settings...",
                totalPhases: 8);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var adapter = SelectedAdapter ?? throw new InvalidOperationException("No adapter selected");
            if (string.IsNullOrWhiteSpace(adapter.Id))
                throw new InvalidOperationException(_lang.T("dhcp.restore.identity.missing"));
            await RefreshAdapterByIdentityAsync(adapter, OperationToken);
            if (adapter.IsWifi) throw new InvalidOperationException(_lang.T("blocked.wifi"));
            if (adapter.HasGateway && !_settings.AllowDhcpOnAdapterWithGateway) throw new InvalidOperationException(_lang.T("blocked.gateway"));
            if (adapter.IsVirtual) throw new InvalidOperationException(_lang.T("blocked.virtual"));
            if (!adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(_lang.T("blocked.disconnected"));
            if (string.IsNullOrWhiteSpace(adapter.IPv4Address) && string.IsNullOrWhiteSpace(DhcpServerIp.Text)) throw new InvalidOperationException("Adapter has no IPv4 address");
            var settings = new DhcpServerSettings
            {
                AdapterId = adapter.Id,
                InterfaceIndex = int.Parse(adapter.InterfaceIndex, System.Globalization.CultureInfo.InvariantCulture),
                ServerIp = IPAddress.Parse(DhcpServerIp.Text.Trim()),
                SubnetMask = IPAddress.Parse(DhcpMask.Text.Trim()),
                PoolStart = IPAddress.Parse(DhcpStart.Text.Trim()),
                PoolEnd = IPAddress.Parse(DhcpEnd.Text.Trim()),
                Gateway = IPAddress.TryParse(DhcpGateway.Text.Trim(), out var gateway) ? gateway : null,
                Dns = IPAddress.TryParse(DhcpDns.Text.Trim(), out var dns) ? dns : null,
                LeaseSeconds = int.Parse(DhcpLeaseSeconds.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture)
            };
            _ = new DhcpLeaseManager(settings); // Validate the entire scope before changing adapter or firewall state.
            SetBusyPhase(IsChineseUi() ? "检查现有 DHCP 服务和 WLAN 状态..." : "Checking existing DHCP and WLAN state...");
            if (_settings.DetectExistingDhcpBeforeStart)
            {
                var detectIp = IPAddress.Parse(string.IsNullOrWhiteSpace(adapter.IPv4Address) ? DhcpServerIp.Text : adapter.IPv4Address);
                var existing = await _dhcpDetector.DetectAsync(detectIp, settings.InterfaceIndex, 1500, OperationToken);
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
            SetBusyPhase(IsChineseUi() ? "等待启动确认..." : "Waiting for start confirmation...");
            if (!ShowDhcpPreview(confirm: true)) return;
            SetBusyPhase(IsChineseUi() ? "保存网卡恢复快照..." : "Saving the adapter recovery snapshot...");
            leaseScope = PrepareDhcpLeaseScope(settings);
            var captured = await RememberAdapterConfigAsync(
                adapter,
                OperationToken,
                forceFreshSnapshot: true,
                preserveBackupHistory: true,
                verifyAdapterAfterCapture: true);
            sessionRecovery = new DhcpSessionRecovery(captured.Backup, captured.Snapshot);
            await RefreshAdapterByIdentityAsync(adapter, OperationToken);
            if (!string.Equals(adapter.InterfaceIndex, captured.Backup.InterfaceIndex, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Ui("dhcp.start.adapter.changed"));
            _dhcpSessionRecoveries.Add(sessionRecovery);
            _activeDhcpSession = sessionRecovery;
            sessionRecovery.MarkModificationPending();
            SetBusyPhase(IsChineseUi() ? "配置并验证 DHCP 网卡 IPv4..." : "Applying and verifying the DHCP adapter IPv4 settings...");
            await _adapterService.RunWithIPv4RecoveryAsync(
                adapter,
                captured.Snapshot,
                async token =>
                {
                    await _adapterService.ApplyStaticIPv4Async(adapter, DhcpServerIp.Text, DhcpMask.Text, DhcpGateway.Text, DhcpDns.Text, token);
                    await VerifyStaticIPv4Async(adapter, DhcpServerIp.Text, DhcpMask.Text, token);
                },
                "DHCP host adapter configuration",
                OperationToken,
                onRestored: () =>
                {
                    sessionRecovery.MarkRestored();
                    _dhcpSessionRecoveries.Remove(sessionRecovery);
                });
            SetBusyPhase(IsChineseUi() ? "读取并核对配置结果..." : "Reading back and verifying the adapter configuration...");
            var expectedSessionSnapshot = await _adapterService.CaptureIPv4ConfigAsync(adapter, OperationToken);
            var expectedPrefix = IpNetwork.PrefixLength(IPAddress.Parse(DhcpMask.Text.Trim()));
            if (!DhcpSessionRestoreCoordinator.MatchesAppliedHostConfiguration(expectedSessionSnapshot, DhcpServerIp.Text.Trim(), expectedPrefix))
                throw new InvalidOperationException(Ui("verify.static.failed"));
            sessionRecovery.SetExpectedSessionSnapshot(expectedSessionSnapshot);
            MarkAdapterIp(adapter, DhcpServerIp.Text);
            SetBusyPhase(IsChineseUi() ? "检查 WLAN 并创建、核对防火墙规则..." : "Checking WLAN and creating/verifying firewall rules...");
            await _adapterService.LogReadonlyWlanStateAsync("after DHCP start", OperationToken);
            _dhcpFirewallRules = await _adapterService.EnsureDhcpFirewallRulesAsync(adapter.Name, TrackActiveDhcpFirewallLease, OperationToken);
            Volatile.Write(ref _activeDhcpUiSessionId, Guid.NewGuid().ToString("N"));
            SetBusyPhase(IsChineseUi() ? "启动 DHCP 服务..." : "Starting the DHCP service...");
            await _dhcpServer.StartAsync(settings, leaseScope.Bindings, leaseScope.DeclinedAddresses,
                snapshot => PersistDhcpLeaseTable(leaseScope, snapshot));
            serverStarted = true;
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
                _ = await RemoveDhcpFirewallRulesAsync();
                if (sessionRecovery?.RequiresRestore == true)
                {
                    try { await RestoreDhcpSessionAsync(sessionRecovery, CancellationToken.None); }
                    catch (Exception restoreEx) { _logger.Error("DHCP cancellation rollback failed; recovery remains pending", restoreEx); }
                }
            }
            if (!serverStarted)
            {
                _activeDhcpSession = null;
                var failedUiSessionId = _activeDhcpUiSessionId;
                Volatile.Write(ref _activeDhcpUiSessionId, null);
                await EndLeaseProbeSessionAsync(failedUiSessionId);
            }
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("start.dhcp"), IsChineseUi() ? "操作已取消，已完成的网卡变更已尝试回滚。" : "The operation was canceled; completed adapter changes were rolled back where possible.");
        }
        catch (Exception ex)
        {
            _logger.Error("Start DHCP failed", ex);
            if (!serverStarted)
            {
                _ = await RemoveDhcpFirewallRulesAsync();
                if (sessionRecovery?.RequiresRestore == true)
                {
                    try
                    {
                        await RestoreDhcpSessionAsync(sessionRecovery, CancellationToken.None);
                        _logger.Info("DHCP startup rollback restored adapter configuration");
                    }
                    catch (Exception restoreEx)
                    {
                        _logger.Error("DHCP startup rollback failed; recovery remains pending", restoreEx);
                    }
                }
            }
            if (!serverStarted)
            {
                _activeDhcpSession = null;
                var failedUiSessionId = _activeDhcpUiSessionId;
                Volatile.Write(ref _activeDhcpUiSessionId, null);
                await EndLeaseProbeSessionAsync(failedUiSessionId);
            }
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("start.dhcp"), ExplainFailure(ex, _lang.T("help.start.dhcp")), danger: true);
        }
        finally
        {
            SetBusy(false);
            EndNetworkWorkflow(workflow);
        }
    }

    private async void StopDhcp_Click(object sender, RoutedEventArgs e)
    {
        var workflow = TryBeginNetworkWorkflow("DHCP stop", allowWhileDhcpRunning: true);
        if (workflow is null) return;
        var sessionRecovery = _activeDhcpSession;
        try
        {
            var restoreAdapter = RestoreOnStop.IsChecked == true && sessionRecovery?.RequiresRestore == true;
            SetBusy(true,
                IsChineseUi() ? "停止 DHCP 服务..." : "Stopping the DHCP service...",
                totalPhases: restoreAdapter ? 4 : 3);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            _leasePingTimer.Stop();
            var stoppedUiSessionId = _activeDhcpUiSessionId;
            _dhcpServer.Stop();
            Volatile.Write(ref _activeDhcpUiSessionId, null);
            await EndLeaseProbeSessionAsync(stoppedUiSessionId);
            BtnCancelOperation.IsEnabled = false;
            SetBusyPhase(IsChineseUi() ? "清理本工具创建的防火墙规则..." : "Removing firewall rules owned by this app...");
            var firewallCleanupSucceeded = await RemoveDhcpFirewallRulesAsync(CancellationToken.None);
            _leaseHintCts?.Cancel();
            if (restoreAdapter)
            {
                if (sessionRecovery?.RequiresRestore == true)
                {
                    SetBusyPhase(IsChineseUi() ? "恢复网卡原始配置..." : "Restoring the adapter configuration...");
                    await RestoreDhcpSessionAsync(sessionRecovery, CancellationToken.None);
                }
            }
            SetBusyPhase(IsChineseUi() ? "检查停止后的 WLAN 状态..." : "Checking WLAN state after stopping DHCP...");
            await _adapterService.LogReadonlyWlanStateAsync("after DHCP stop", CancellationToken.None);
            _activeDhcpSession = null;
            SetDhcpRunningState(false);
            UpdateManualScanButtons();
            var stopStatus = firewallCleanupSucceeded ? "Stopped / 已停止" : "Stopped; firewall cleanup pending / 已停止；防火墙清理待处理";
            AddOperationHistory("DHCP", "", sessionRecovery?.AdapterIdentity.AdapterMac ?? "", stopStatus,
                firewallCleanupSucceeded
                    ? "Service stopped and owned firewall rules were verified removed / 服务已停止且已确认删除所属防火墙规则"
                    : "Service stopped; owned firewall rules remain in Recovery Center / 服务已停止；所属防火墙规则仍保留在恢复中心", scope: "DHCP session", rollbackAvailable: false);
            ShowActionFeedback(
                firewallCleanupSucceeded ? "DHCP 已停止。" : "DHCP 已停止，但临时防火墙规则尚未清理，请在恢复中心处理。",
                firewallCleanupSucceeded ? "DHCP stopped." : "DHCP stopped, but temporary firewall rules remain. Review them in Recovery Center.",
                error: !firewallCleanupSucceeded);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Stop DHCP canceled by user or timeout");
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("stop.dhcp"), IsChineseUi() ? "停止操作已取消；请确认 DHCP 和临时网卡配置状态。" : "Stop operation was canceled; verify the DHCP and temporary adapter state.", danger: true);
        }
        catch (Exception ex)
        {
            _logger.Error("Stop DHCP failed", ex);
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("stop.dhcp"), ExplainFailure(ex, _lang.T("help.stop.dhcp")), danger: true);
        }
        finally
        {
            SetBusy(false);
            EndNetworkWorkflow(workflow);
        }
    }

    private async Task HandleUnexpectedDhcpStopAsync(string reason)
    {
        while (!_closingCleanupStarted && !_dhcpServer.IsRunning)
        {
            var activeCompletion = _activeNetworkWorkflow?.Completion ?? _standaloneOperationCompletion?.Task;
            if (activeCompletion is null) break;
            await activeCompletion;
        }
        if (_closingCleanupStarted || _dhcpServer.IsRunning || _activeDhcpUiSessionId is null) return;

        var workflow = TryBeginNetworkWorkflow("Unexpected DHCP stop", allowWhileDhcpRunning: true);
        if (workflow is null)
        {
            _logger.Error("Unexpected DHCP stop cleanup could not acquire the network workflow; recovery records remain available.");
            return;
        }
        try
        {
            _logger.Error("DHCP stopped unexpectedly: " + reason);
            _leasePingTimer.Stop();
            _leaseHintCts?.Cancel();
            var stoppedUiSessionId = _activeDhcpUiSessionId;
            Volatile.Write(ref _activeDhcpUiSessionId, null);
            await EndLeaseProbeSessionAsync(stoppedUiSessionId);

            var firewallCleanupSucceeded = await RemoveDhcpFirewallRulesAsync(CancellationToken.None);
            var adapterRestored = false;
            var recovery = _activeDhcpSession;
            if (RestoreOnStop.IsChecked == true && recovery?.RequiresRestore == true)
            {
                try
                {
                    await RestoreDhcpSessionAsync(recovery, CancellationToken.None);
                    adapterRestored = true;
                }
                catch (Exception ex)
                {
                    _logger.Error("Unexpected DHCP stop adapter recovery remains pending", ex);
                }
            }
            if (adapterRestored) _activeDhcpSession = null;
            SetDhcpRunningState(false);
            UpdateManualScanButtons();
            var details = $"{reason}; firewallCleanup={firewallCleanupSucceeded}; adapterRestored={adapterRestored}";
            AddOperationHistory("DHCP", DhcpServerIp.Text.Trim(), recovery?.AdapterIdentity.AdapterMac ?? "",
                "Unexpected stop / 意外停止", details, scope: "DHCP session", rollbackAvailable: recovery?.RequiresRestore == true);
            ShowActionFeedback(
                firewallCleanupSucceeded
                    ? "DHCP 意外停止；请检查网卡恢复记录。"
                    : "DHCP 意外停止；临时防火墙规则仍待恢复中心清理。",
                firewallCleanupSucceeded
                    ? "DHCP stopped unexpectedly. Review adapter recovery if needed."
                    : "DHCP stopped unexpectedly; temporary firewall rules remain in Recovery Center.",
                error: true);
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected DHCP stop cleanup failed; recovery records remain available", ex);
            SetDhcpRunningState(false);
            UpdateManualScanButtons();
            ShowActionFeedback("DHCP 意外停止，自动清理未完成，请检查恢复中心。", "DHCP stopped unexpectedly and automatic cleanup did not finish. Review Recovery Center.", error: true);
        }
        finally
        {
            EndNetworkWorkflow(workflow);
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
        var workflow = TryBeginNetworkWorkflow("Manual configuration and scan");
        if (workflow is null) return;
        NetworkAdapterInfo? operationAdapter = null;
        ScanProgressAccumulator? progressAccumulator = null;
        long scanGeneration = 0;
        try
        {
            var adapter = operationAdapter = SelectedAdapter ?? throw new InvalidOperationException("No adapter selected");
            if (adapter.IsWifi) throw new InvalidOperationException(_lang.T("blocked.wifi"));
            if (!ShowScanPreview(confirm: true)) return;
            if (!TryGetManualScanPlan(out var scanPlan, out var validation))
                throw new InvalidOperationException(validation);
            _scanRunning = true;
            UpdateManualScanButtons();
            UpdateFavoriteButtons();
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
            var manualRecovery = await RememberAdapterConfigAsync(
                adapter,
                scanToken,
                forceFreshSnapshot: true,
                verifyAdapterAfterCapture: true);
            await _adapterService.RunWithIPv4RecoveryAsync(
                adapter,
                manualRecovery.Snapshot,
                async token =>
                {
                    await _adapterService.ApplyStaticIPv4Async(adapter, ManualIp.Text, ManualMask.Text, "", "", token);
                    await VerifyStaticIPv4Async(adapter, ManualIp.Text, ManualMask.Text, token);
                },
                "Manual scan adapter configuration",
                scanToken);
            MarkAdapterIp(adapter, ManualIp.Text);
            SetBusy(false);
            _scanPhase = IsChineseUi() ? "正在扫描" : "Scanning";
            scanGeneration = Interlocked.Increment(ref _scanProgressGeneration);
            progressAccumulator = new ScanProgressAccumulator(scanPlan!.TargetCount);
            _scanProgressAccumulator = progressAccumulator;
            _scanProgressTimer.Start();
            IProgress<(int done, int total, ScanResult? result)> progress = progressAccumulator;
            if (scanPlan!.IsSingleTarget)
            {
                await ProbeTargetUntilOnlineAsync(scanPlan.TargetIp!, progress, scanToken);
            }
            else
            {
                await _scanner.ScanPlanAsync(scanPlan, _settings.PingConcurrency, _settings.PingTimeoutMs, _settings.HttpTimeoutMs, progress, scanToken);
            }
            await FinishScanProgressAsync(progressAccumulator, scanGeneration);
            _scanPhase = IsChineseUi() ? "扫描完成" : "Scan complete";
            UpdateScanTiming((int)ScanProgress.Value, (int)ScanProgress.Maximum);
            AddOperationHistory("Scan", ManualTargetIp.Text.Trim(), adapter.MacAddress, "Completed / 已完成",
                $"{ScanResults.Count} result(s) / {ScanResults.Count} 条结果", scope: adapter.Name,
                durationMs: _scanStopwatch?.ElapsedMilliseconds ?? 0,
                rollbackAvailable: _originalAdapterConfigs.ContainsKey(AdapterSnapshotKey(adapter)));
            if (_operationHistorySaveFailed)
                ShowActionFeedback("扫描完成，但操作历史未能保存；记录仍保留在内存中等待重试。", "Scan completed, but operation history was not saved; records remain in memory for retry.", error: true);
            else ShowActionFeedback("扫描已完成。", "Scan completed.");
        }
        catch (OperationCanceledException)
        {
            if (progressAccumulator != null) await FinishScanProgressAsync(progressAccumulator, scanGeneration);
            CompleteCanceledScan(operationAdapter);
        }
        catch (Exception ex)
        {
            if (progressAccumulator != null) await FinishScanProgressAsync(progressAccumulator, scanGeneration);
            _logger.Error("Apply and scan failed", ex);
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("apply.scan"), ExplainFailure(ex, _lang.T("help.apply.scan")), danger: true);
        }
        finally
        {
            if (progressAccumulator != null) await FinishScanProgressAsync(progressAccumulator, scanGeneration);
            _scanStopwatch?.Stop();
            SetBusy(false);
            _scanRunning = false;
            _scanCts?.Dispose();
            _scanCts = null;
            EndNetworkWorkflow(workflow);
            UpdateManualScanButtons();
            UpdateFavoriteButtons();
        }
    }

    private void DrainScanProgressBatch()
    {
        var accumulator = _scanProgressAccumulator;
        if (accumulator == null || !accumulator.TryTakeBatch(128, out var batch)) return;
        ApplyScanProgressBatch(accumulator, _scanProgressGeneration, batch);
    }

    private void ApplyScanProgressBatch(ScanProgressAccumulator accumulator, long generation, ScanProgressBatch batch)
    {
        if (generation != _scanProgressGeneration || !ReferenceEquals(accumulator, _scanProgressAccumulator)) return;
        ScanProgress.Maximum = Math.Max(1, batch.Total);
        ScanProgress.Value = Math.Max(ScanProgress.Value, Math.Clamp(batch.Done, 0, (int)ScanProgress.Maximum));
        foreach (var result in batch.Results)
        {
            ScanResults.Add(result);
            AddScanOperationHistory(result);
            _scanLastHitAt = DateTime.Now;
        }
        ScanStatus.Text = $"{batch.Done}/{batch.Total} Found={ScanResults.Count}";
        UpdateScanTiming(batch.Done, batch.Total);
        UpdateScanSummary();
        if (batch.Results.Count > 0)
        {
            RefreshOperationHistoryView();
            UpdateLastOperationPresentation();
        }
    }

    private async Task FinishScanProgressAsync(ScanProgressAccumulator accumulator, long generation)
    {
        accumulator.Complete();
        _scanProgressTimer.Stop();
        if (generation == _scanProgressGeneration && ReferenceEquals(accumulator, _scanProgressAccumulator))
        {
            while (accumulator.TryTakeBatch(128, out var batch))
            {
                ApplyScanProgressBatch(accumulator, generation, batch);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            _scanProgressAccumulator = null;
            _ = await FlushScanHistoryAsync(force: true);
        }
    }

    private void CompleteCanceledScan(NetworkAdapterInfo? adapter)
    {
        _logger.Info("Scan canceled by user or timeout");
        ScanStatus.Text = IsChineseUi() ? "已取消，已保留已收集结果" : "Canceled; collected results were kept";
        _scanPhase = IsChineseUi() ? "已取消" : "Canceled";
        UpdateScanTiming((int)ScanProgress.Value, (int)ScanProgress.Maximum);
        var target = ManualTargetIp.Text.Trim();
        var resultCount = ScanResults.Count;
        AddOperationHistory(
            "Scan",
            target,
            adapter?.MacAddress ?? "",
            IsChineseUi() ? "已取消" : "Canceled",
            IsChineseUi() ? $"已保留 {resultCount} 条已收集结果" : $"Retained {resultCount} collected result(s)",
            scope: adapter?.Name,
            durationMs: _scanStopwatch?.ElapsedMilliseconds ?? 0,
            rollbackAvailable: adapter is not null && _originalAdapterConfigs.ContainsKey(AdapterSnapshotKey(adapter)));
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
            var latency = await PingLatencyAsync(ip, 800, ct);
            ct.ThrowIfCancellationRequested();
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
                var details = await _scanner.ProbeReachableDetailsAsync(targetIp, _settings.HttpTimeoutMs, ct);
                ct.ThrowIfCancellationRequested();
                result.Hostname = details.Hostname;
                result.HttpOk = details.HttpOk;
                result.HttpsOk = details.HttpsOk;
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

        var workflow = TryBeginNetworkWorkflow("Adapter restart");
        if (workflow is null) return;

        _adapterActionInProgress = true;
        UpdateAdapterActionButtons();
        AdapterConfigBackup? restartRecoveryBackup = null;
        try
        {
            SetBusy(true,
                IsChineseUi() ? "保存网卡恢复快照..." : "Saving the adapter recovery snapshot...",
                totalPhases: 3);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var captured = await RememberAdapterConfigAsync(
                adapter,
                OperationToken,
                forceFreshSnapshot: true,
                preserveBackupHistory: true,
                verifyAdapterAfterCapture: true);
            restartRecoveryBackup = captured.Backup;
            if (!captured.Snapshot.AdapterEnabled.HasValue)
                throw new InvalidOperationException(IsChineseUi()
                    ? "无法读取网卡原管理状态，已取消重启。"
                    : "The adapter's original administrative state could not be read; restart was canceled.");
            if (await _adapterService.IsAdapterEnabledAsync(adapter, OperationToken) != captured.Snapshot.AdapterEnabled.Value)
                throw new InvalidOperationException(IsChineseUi()
                    ? "保存恢复快照期间网卡管理状态发生变化，已取消重启。"
                    : "The adapter administrative state changed while its recovery snapshot was being saved; restart was canceled.");

            SetBusyPhase(IsChineseUi() ? "重启网卡并核对管理状态..." : "Restarting the adapter and verifying its administrative state...");
            var restartResult = await _adapterService.RestartAdapterAsync(
                adapter,
                _settings.AllowRestartOnAnyAdapter,
                OperationToken,
                expectedEnabledBefore: captured.Snapshot.AdapterEnabled);
            RemoveRestartRecoveryBackup(restartRecoveryBackup);
            _logger.Info($"Adapter restart completed: idx={adapter.InterfaceIndex} name={adapter.Name} wasDisabled={restartResult.WasDisabledBefore} status={restartResult.FinalStatus}");
            SetBusyPhase(IsChineseUi() ? "刷新网卡列表并核对原网卡身份..." : "Refreshing the adapter list and verifying its identity...");
            await RefreshAdaptersAsync(allowDuringNetworkWorkflow: true, cancellationToken: CancellationToken.None);
            var refreshed = Adapters.FirstOrDefault(x => x.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));
            if (refreshed == null || refreshed.Status.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(IsChineseUi() ? "重启后校验失败：网卡仍处于禁用状态或无法通过原身份找到。" : "Post-change verification failed: the adapter is still disabled or cannot be found by its original identity.");
            AddOperationHistory("Adapter", "", refreshed.MacAddress, "Restarted / 已重启", refreshed.Name, scope: refreshed.Name);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"Adapter restart canceled: idx={adapter.InterfaceIndex} name={adapter.Name}");
            if (restartRecoveryBackup != null) RemoveRestartRecoveryBackup(restartRecoveryBackup);
        }
        catch (Exception ex)
        {
            _logger.Error($"Adapter restart failed: idx={adapter.InterfaceIndex} name={adapter.Name}", ex);
            if (restartRecoveryBackup != null && ex is not AggregateException)
                RemoveRestartRecoveryBackup(restartRecoveryBackup);
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("restart.adapter"), ExplainFailure(ex, _lang.T("help.restart.adapter")), danger: true);
        }
        finally
        {
            _adapterActionInProgress = false;
            UpdateAdapterActionButtons();
            SetBusy(false);
            EndNetworkWorkflow(workflow);
        }
    }

    private void RemoveRestartRecoveryBackup(AdapterConfigBackup backup)
    {
        var nextBackups = _adapterBackups.Where(x => !ReferenceEquals(x, backup)).ToList();
        if (nextBackups.Count == _adapterBackups.Count) return;
        try
        {
            JsonStore.Save(_paths.AdapterBackupsFile, nextBackups);
            _adapterBackups = nextBackups;
        }
        catch (Exception ex)
        {
            _logger.Warn("Adapter restart recovery record remains available because its cleanup could not be saved: " + ex.Message);
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
        try
        {
            await RefreshAdapterByIdentityAsync(adapter, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error("MAC change canceled because the selected adapter could not be refreshed by stable identity", ex);
            AppDialog.Show(this, _lang.T("change.mac"), ExplainFailure(ex, _lang.T("help.change.mac")), danger: true);
            return;
        }
        var requestedMac = dialog.MacAddress;
        var preOperationMac = adapter.MacAddress;
        var key = AdapterKey(adapter);
        var hadBackup = _originalAdapterMacs.TryGetValue(key, out var existingBackup);
        var previousRestoreOnExit = existingBackup?.RestoreOnExit ?? false;
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
        }

        var workflow = TryBeginNetworkWorkflow("MAC change");
        if (workflow is null) return;

        _adapterActionInProgress = true;
        var macRecoveryWorkflowStarted = false;
        UpdateAdapterActionButtons();
        try
        {
            if (!hadBackup) _originalAdapterMacs[key] = existingBackup!;
            // Persist an in-flight recovery intent before the first MAC write. On success it is
            // reduced to the user's requested normal-exit preference below.
            existingBackup!.RestoreOnExit = true;
            if (!SaveMacBackups())
            {
                if (!hadBackup) _originalAdapterMacs.Remove(key);
                else existingBackup.RestoreOnExit = previousRestoreOnExit;
                ShowStorageWriteBlocked("MAC recovery records / MAC 恢复记录");
                return;
            }
            SetBusy(true,
                IsChineseUi() ? "修改 MAC 并核对驱动返回..." : "Changing the MAC address and checking the driver result...",
                totalPhases: 3);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            macRecoveryWorkflowStarted = true;
            await _adapterService.RunWithMacRecoveryAsync(
                adapter,
                preOperationMac,
                async token =>
                {
                    await _adapterService.ChangeMacAddressAsync(adapter, requestedMac, _settings.AllowMacChangeOnAnyAdapter, token);
                    adapter.MacAddress = requestedMac;
                    TxtMac.Text = requestedMac;
                    AdapterBox.Items.Refresh();
                    _logger.Info($"Adapter MAC change command returned: idx={adapter.InterfaceIndex} name={adapter.Name} old={existingBackup.OriginalMacAddress} requested={requestedMac}");
                    SetBusyPhase(IsChineseUi() ? "刷新网卡并核对新 MAC..." : "Refreshing the adapter and verifying the new MAC...");
                    await RefreshAdaptersAsync(allowDuringNetworkWorkflow: true, cancellationToken: CancellationToken.None);
                    var observed = Adapters.FirstOrDefault(x => x.InterfaceIndex.Equals(adapter.InterfaceIndex, StringComparison.OrdinalIgnoreCase));
                    var expectedMac = NetworkAdapterService.NormalizeMacAddress(requestedMac);
                    var actualMac = observed == null ? "" : NetworkAdapterService.NormalizeMacAddress(observed.MacAddress);
                    if (observed == null || !actualMac.Equals(expectedMac, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(IsChineseUi() ? "修改后校验失败：当前 MAC 与目标值不一致。" : "Post-change verification failed: the current MAC does not match the requested value.");
                },
                (original, token) => VerifyMacAddressAsync(existingBackup, original, token),
                "Adapter MAC change",
                OperationToken,
                onRestored: () =>
                {
                    adapter.MacAddress = preOperationMac;
                    TxtMac.Text = preOperationMac;
                    AdapterBox.Items.Refresh();
                    if (!hadBackup) _originalAdapterMacs.Remove(key);
                    else existingBackup.RestoreOnExit = previousRestoreOnExit;
                    if (!SaveMacBackups())
                        throw new IOException("MAC was restored, but its recovery record could not be updated; retain the record for retry.");
                },
                onRecoveryRequired: recoveryError =>
                {
                    existingBackup.RestoreOnExit = true;
                    _originalAdapterMacs[key] = existingBackup;
                    if (!SaveMacBackups()) _logger.Error("MAC recovery record remains in memory but could not be saved after compensation failed", recoveryError);
                });

            SetBusyPhase(IsChineseUi() ? "保存退出时的 MAC 恢复选项..." : "Saving the MAC restoration preference for exit...");
            existingBackup.RestoreOnExit = dialog.RestoreOnExit;
            if (!dialog.RestoreOnExit) _originalAdapterMacs.Remove(key);
            if (!SaveMacBackups())
            {
                existingBackup.RestoreOnExit = true;
                _originalAdapterMacs[key] = existingBackup;
                _logger.Warn("MAC changed successfully, but the selected normal-exit restoration preference could not be saved; the original MAC remains scheduled for restoration.");
                if (!_closingCleanupStarted)
                    AppDialog.Show(this, _lang.T("change.mac"), IsChineseUi()
                        ? "MAC 已修改，但无法保存退出恢复选项；为保证可恢复，原 MAC 仍会在正常退出时恢复。"
                        : "The MAC changed, but the exit-restoration preference could not be saved. The original MAC remains scheduled for restoration on normal exit.", danger: true);
            }
            adapter.MacAddress = requestedMac;
            TxtMac.Text = requestedMac;
            AdapterBox.Items.Refresh();
            _logger.Info($"Adapter MAC changed: idx={adapter.InterfaceIndex} name={adapter.Name} old={existingBackup.OriginalMacAddress} new={requestedMac} restoreOnExit={existingBackup.RestoreOnExit}");
            AddOperationHistory("MAC", "", requestedMac, "Changed / 已修改", $"{existingBackup.OriginalMacAddress} -> {requestedMac}", scope: adapter.Name, rollbackAvailable: existingBackup.RestoreOnExit);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"Adapter MAC change canceled: idx={adapter.InterfaceIndex} name={adapter.Name}");
            if (!macRecoveryWorkflowStarted)
            {
                if (!hadBackup) _originalAdapterMacs.Remove(key);
                else existingBackup!.RestoreOnExit = previousRestoreOnExit;
                SaveMacBackups();
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Adapter MAC change failed: idx={adapter.InterfaceIndex} name={adapter.Name} requested={requestedMac}", ex);
            if (!macRecoveryWorkflowStarted)
            {
                if (!hadBackup) _originalAdapterMacs.Remove(key);
                else existingBackup!.RestoreOnExit = previousRestoreOnExit;
                SaveMacBackups();
            }
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("change.mac"), ExplainFailure(ex, _lang.T("help.change.mac")), danger: true);
        }
        finally
        {
            _adapterActionInProgress = false;
            UpdateAdapterActionButtons();
            SetBusy(false);
            EndNetworkWorkflow(workflow);
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
        NetworkWorkflowLease? workflow = null;
        if (apply)
        {
            if (!_routeJournalWritable)
            {
                ShowStorageWriteBlocked("Static route recovery journal / 静态路由恢复日志");
                return null;
            }
            if (!EnsureSafetyOnboarding()) return null;
            workflow = TryBeginNetworkWorkflow("Static route apply");
            if (workflow is null) return null;
        }
        else if (_activeNetworkWorkflow is not null || _operationCts is not null || _dhcpServer.IsRunning)
        {
            ShowActionFeedback("当前网络操作期间不能刷新路由预览。", "Route preview is unavailable during another network operation.", error: true);
            return null;
        }

        try
        {
            var routeAssessment = ProfileValidation.AssessRoutes(StaticRoutes);
            if (routeAssessment.Status == ProfileSectionStatus.Invalid)
                throw new InvalidOperationException(FormatProfileSection("Routes", routeAssessment));
            var targets = BuildStaticRouteTargets();
            SetBusy(true,
                apply ? "Preparing route changes... / 正在准备路由变更..." : "Preparing route preview... / 正在准备路由预览...",
                totalPhases: apply ? 4 : 2);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var plan = await _routeService.PreviewAsync(targets, _appliedStaticRoutes.ToList(), OperationToken);
            var summary = BuildRoutePreviewText(plan, apply);
            SetBusyPhase(apply
                ? (IsChineseUi() ? "等待路由应用确认..." : "Waiting for route-application confirmation...")
                : (IsChineseUi() ? "展示路由预览..." : "Showing the route preview..."));
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

            SetBusyPhase(IsChineseUi() ? "清理本工具旧路由并应用新路由..." : "Removing old app-owned routes and applying the new routes...");
            if (_appliedStaticRoutes.Count > 0)
            {
                var cleanupFailures = new List<string>();
                if (!await ClearAppliedRoutesAsync(OperationToken, cleanupFailures))
                {
                    ShowRouteCleanupFailure(IsChineseUi() ? "无法应用新路由" : "Cannot Apply New Routes", cleanupFailures);
                    return plan;
                }
            }
            var results = await _routeService.ApplyAsync(
                targets,
                onCreated: applied =>
                {
                    var intent = _appliedStaticRoutes.FirstOrDefault(x =>
                        !x.OwnershipVerified
                        && x.RuleId.Equals(applied.RuleId, StringComparison.OrdinalIgnoreCase)
                        && x.InterfaceIndex == applied.InterfaceIndex
                        && x.DestinationPrefix.Equals(applied.DestinationPrefix, StringComparison.OrdinalIgnoreCase)
                        && x.NextHop.Equals(applied.NextHop, StringComparison.OrdinalIgnoreCase));
                    if (intent is not null) _appliedStaticRoutes.Remove(intent);
                    _appliedStaticRoutes.Add(applied);
                    if (!SaveStaticRouteSession())
                        throw new IOException("The applied route could not be saved to the recovery journal.");
                    UpdateRecoveryBanner();
                },
                ct: OperationToken,
                onCreating: intent =>
                {
                    _appliedStaticRoutes.Add(intent);
                    if (!SaveStaticRouteSession())
                    {
                        _appliedStaticRoutes.Remove(intent);
                        throw new IOException("The route creation intent could not be saved to the recovery journal; no route was created.");
                    }
                    UpdateRecoveryBanner();
                },
                onRemoved: RemoveAppliedRouteRecord);
            foreach (var result in results) result.Rule.Status = result.StatusText;
            if (!SaveStaticRouteSession())
                throw new IOException("The applied routes could not be saved to the recovery journal.");
            RouteGrid.Items.Refresh();
            SetBusyPhase(IsChineseUi() ? "刷新路由表并核对应用结果..." : "Refreshing the route table and verifying the applied routes...");
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
            if (!_closingCleanupStarted)
            {
                var remaining = _appliedStaticRoutes.Count;
                var message = IsChineseUi()
                    ? remaining == 0
                        ? "路由操作已取消；已创建的路由均已核实回滚。"
                        : $"路由操作已取消；仍有 {remaining} 条路由恢复记录待处理。未核实归属的记录不会自动删除，请查看恢复中心。"
                    : remaining == 0
                        ? "The route operation was canceled; all created routes were verified as rolled back."
                        : $"The route operation was canceled; {remaining} route recovery record(s) remain. Unverified ownership is never deleted automatically; review Recovery Center.";
                AppDialog.Show(this, _lang.T("apply.routes"), message, danger: remaining > 0);
            }
            return null;
        }
        catch (Exception ex)
        {
            if (apply && _appliedStaticRoutes.Count > 0)
            {
                try
                {
                    var cleanupFailures = new List<string>();
                    if (!await ClearAppliedRoutesAsync(failureDetails: cleanupFailures))
                        _logger.Error($"Route failure cleanup retained {_appliedStaticRoutes.Count} recovery record(s): {string.Join("; ", cleanupFailures)}", ex);
                }
                catch (Exception cleanupEx) { _logger.Error("Route failure cleanup failed", cleanupEx); }
            }
            _logger.Error("Static route operation failed", ex);
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("apply.routes"), ExplainFailure(ex, _lang.T("help.apply.routes")), danger: true);
            return null;
        }
        finally
        {
            SetBusy(false);
            if (workflow is not null) EndNetworkWorkflow(workflow);
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
        if (!_routeJournalWritable)
        {
            ShowStorageWriteBlocked("Static route recovery journal / 静态路由恢复日志");
            return;
        }
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
        var workflow = TryBeginNetworkWorkflow("Clear applied routes");
        if (workflow is null) return;
        try
        {
            SetBusy(true, "Clearing routes... / 正在清除路由...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var failureDetails = new List<string>();
            var cleared = await ClearAppliedRoutesAsync(OperationToken, failureDetails);
            if (!cleared)
            {
                ShowRouteCleanupFailure(IsChineseUi() ? "清除静态路由" : "Clear Static Routes", failureDetails);
            }
            else
            {
                ShowActionFeedback("已清除本次运行创建的静态路由。", "Static routes created by this run were cleared.");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Clear static routes canceled by user or timeout");
            if (!_closingCleanupStarted && _appliedStaticRoutes.Count > 0)
            {
                var remaining = _appliedStaticRoutes
                    .Select(x => $"{x.DestinationPrefix} on {x.AdapterName}: 清理已取消，记录保留 / cleanup canceled; record retained")
                    .ToList();
                ShowRouteCleanupFailure(IsChineseUi() ? "清除静态路由已取消" : "Clear Static Routes Canceled", remaining);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Clear static routes failed", ex);
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("clear.applied.routes"), ExplainFailure(ex, _lang.T("help.clear.applied.routes")), danger: true);
        }
        finally
        {
            SetBusy(false);
            EndNetworkWorkflow(workflow);
        }
    }

    private async void RemoveRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteGrid.SelectedItem is not StaticRouteRule rule) { ShowActionFeedbackKey("selection.route"); return; }
        NetworkWorkflowLease? workflow = null;
        List<AppliedStaticRoute>? recoverySnapshot = null;
        var journalCommitted = false;
        try
        {
            var applied = _appliedStaticRoutes.Where(x => x.RuleId.Equals(rule.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (applied.Count > 0)
            {
                recoverySnapshot = _appliedStaticRoutes.ToList();
                if (!_routeJournalWritable)
                {
                    ShowStorageWriteBlocked("Static route recovery journal / 静态路由恢复日志");
                    return;
                }
                if (!EnsureSafetyOnboarding()) return;
                if (!AppDialog.Show(this,
                    IsChineseUi() ? "删除已应用路由" : "Remove Applied Route",
                    IsChineseUi()
                        ? $"目标：{rule.DestinationPrefix}\n网卡：{rule.AdapterName}\n\n将删除本工具本次创建的系统路由。确认继续？"
                        : $"Destination: {rule.DestinationPrefix}\nAdapter: {rule.AdapterName}\n\nThe system route created by this run will be removed. Continue?",
                    confirm: true,
                    danger: true)) return;
                workflow = TryBeginNetworkWorkflow("Remove applied route");
                if (workflow is null) return;
                SetBusy(true, "Removing route... / 正在删除路由...");
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                foreach (var route in applied)
                {
                    var adapter = ResolveAdapterForRoute(route);
                    if (adapter == null) throw new InvalidOperationException("The original adapter is unavailable / 原网卡当前不可用");
                    await _routeService.RemoveAsync(route, adapter, OperationToken);
                    _appliedStaticRoutes.Remove(route);
                }
                if (!SaveStaticRouteSession())
                    throw new IOException("The route recovery journal could not be updated after removal.");
                journalCommitted = true;
            }
            StaticRoutes.Remove(rule);
            RouteGrid.SelectedItem = null;
            UpdateEmptyStates();
            UpdateSessionStatus();
        }
        catch (OperationCanceledException)
        {
            RestoreUncommittedRouteJournal();
            _logger.Warn("Remove static route canceled by user or timeout");
        }
        catch (Exception ex)
        {
            RestoreUncommittedRouteJournal();
            _logger.Error("Remove static route failed", ex);
            if (!_closingCleanupStarted)
                AppDialog.Show(this, _lang.T("remove.route"), ExplainFailure(ex, _lang.T("help.remove.route")), danger: true);
        }
        finally
        {
            if (workflow is not null)
            {
                SetBusy(false);
                EndNetworkWorkflow(workflow);
            }
        }

        void RestoreUncommittedRouteJournal()
        {
            if (recoverySnapshot is null || journalCommitted) return;
            _appliedStaticRoutes.Clear();
            _appliedStaticRoutes.AddRange(recoverySnapshot);
            UpdateRouteStatuses();
            UpdateRecoveryBanner();
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
        var adapter = SelectedAdapter;
        if (adapter == null) return;
        if (!EnsureSafetyOnboarding()) return;
        var workflow = TryBeginNetworkWorkflow("Manual adapter restore");
        if (workflow is null) return;
        AdapterIpv4Snapshot? preRestoreSnapshot = null;
        var restoreAttempted = false;
        try
        {
            SetBusy(true,
                "Capturing the current adapter state... / 正在读取当前网卡状态...",
                totalPhases: 3);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            preRestoreSnapshot = await _adapterService.CaptureIPv4ConfigAsync(adapter, OperationToken);
            restoreAttempted = true;
            SetBusyPhase("Restoring and verifying the saved adapter configuration... / 正在恢复并核对已保存的网卡配置...");
            await RestoreOriginalAdapterConfigAsync(adapter, OperationToken);
            SetBusyPhase("Refreshing the adapter list... / 正在刷新网卡列表...");
            await RefreshAdaptersAsync(allowDuringNetworkWorkflow: true, cancellationToken: CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Restore manual adapter canceled by user or timeout");
            if (restoreAttempted && preRestoreSnapshot is not null)
                await CompensateAdapterConfigAsync(adapter, preRestoreSnapshot, "Manual adapter restore cancellation");
        }
        catch (Exception ex)
        {
            _logger.Error("Restore manual adapter failed", ex);
            if (restoreAttempted && preRestoreSnapshot is not null)
                await CompensateAdapterConfigAsync(adapter, preRestoreSnapshot, "Manual adapter restore failure");
            if (!_closingCleanupStarted)
                AppDialog.Show(this, "Restore / 恢复网卡", ExplainFailure(ex, _lang.T("help.restore.scan")), danger: true);
        }
        finally
        {
            SetBusy(false);
            EndNetworkWorkflow(workflow);
        }
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (!_favoritesWritable) { ShowStorageWriteBlocked("Favorites / 收藏"); return; }
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
            var previous = SnapshotFavorites();
            _allFavorites.Add(fav);
            if (!SaveFavoritesOrRestore(previous)) return;
            FilterFavorites();
            _logger.Info("Favorite added: " + fav.Name);
        }
    }

    private void NewFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (!_favoritesWritable) { ShowStorageWriteBlocked("Favorites / 收藏"); return; }
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
            var previous = SnapshotFavorites();
            _allFavorites.Add(fav);
            if (!SaveFavoritesOrRestore(previous)) return;
            FilterFavorites();
            _logger.Info("Favorite manually added / 手动新增收藏: " + fav.Name);
        }
    }

    private void EditFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (!_favoritesWritable) { ShowStorageWriteBlocked("Favorites / 收藏"); return; }
        if (FavoriteGrid.SelectedItem is not FavoriteConfig original) { ShowActionFeedbackKey("selection.favorite"); return; }
        var editingCopy = FavoriteStore.Clone(original);
        var dialog = new FavoriteWindow(editingCopy) { Owner = this };
        dialog.ApplyOwnerTheme(this);
        if (dialog.ShowDialog() != true) return;

        editingCopy.Id = original.Id;
        editingCopy.CreatedAt = original.CreatedAt;
        editingCopy.LastUsedAt = original.LastUsedAt;
        var previous = SnapshotFavorites();
        var index = _allFavorites.FindIndex(x => x.Id.Equals(original.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        _allFavorites[index] = editingCopy;
        if (!SaveFavoritesOrRestore(previous)) return;
        FilterFavorites();
        AddOperationHistory("Favorite", "", "", "Updated / 已更新", editingCopy.Name, scope: "Local favorite / 本地收藏夹");
        _logger.Info("Favorite edited: " + editingCopy.Name);
    }

    private void RestoreFavoritePresets_Click(object sender, RoutedEventArgs e)
    {
        if (!_favoritesWritable) { ShowStorageWriteBlocked("Favorites / 收藏"); return; }
        var stateLoad = FavoritePresetStore.Load(_paths.FavoritePresetStateFile, _logger);
        _favoritePresetStateWritable = stateLoad.IsWritable;
        if (!stateLoad.IsWritable)
        {
            UpdateFavoriteButtons();
            ShowActionFeedback("预设删除记录无法读取，已阻止恢复操作。", "Preset deletion state is unreadable; restoration was blocked.", error: true);
            return;
        }

        var defaults = Defaults.DefaultFavorites();
        var defaultIds = defaults.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingIds = _allFavorites.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = defaults.Where(x => !existingIds.Contains(x.Id)
            && !_allFavorites.Any(existing => FavoriteMergePlanner.SameIdentity(existing, x))).ToList();
        var hasDeletedPresetIntent = stateLoad.State.DeletedPresetIds.Any(defaultIds.Contains);
        if (missing.Count == 0 && !hasDeletedPresetIntent)
        {
            ShowActionFeedback("没有已删除的内置预设需要恢复。", "There are no deleted built-in presets to restore.");
            return;
        }

        var prompt = IsChineseUi()
            ? $"将恢复 {missing.Count} 个缺失的内置预设。现有收藏及其备注、凭据和使用记录会保留。是否继续？"
            : $"Restore {missing.Count} missing built-in preset(s). Existing favorites, notes, credentials, and usage history will be preserved. Continue?";
        if (!AppDialog.Show(this, IsChineseUi() ? "恢复内置预设" : "Restore Built-in Presets", prompt, confirm: true)) return;

        var previousFavorites = SnapshotFavorites();
        var previousState = new FavoritePresetState { DeletedPresetIds = [.. stateLoad.State.DeletedPresetIds] };
        var nextState = new FavoritePresetState
        {
            DeletedPresetIds = stateLoad.State.DeletedPresetIds.Where(x => !defaultIds.Contains(x)).ToList()
        };
        try
        {
            if (!previousState.DeletedPresetIds.SequenceEqual(nextState.DeletedPresetIds, StringComparer.OrdinalIgnoreCase))
                FavoritePresetStore.Save(_paths.FavoritePresetStateFile, nextState, _logger);
            var countBeforeRestore = _allFavorites.Count;
            FavoritePresetStore.AddMissingPresets(_allFavorites, defaults, nextState);
            var restoredCount = _allFavorites.Count - countBeforeRestore;
            if (!SaveFavoritesOrRestore(previousFavorites))
            {
                FavoritePresetStore.Save(_paths.FavoritePresetStateFile, previousState, _logger);
                return;
            }
            FilterFavorites();
            AddOperationHistory("Favorite Presets", "", "", "Restored / 已恢复", $"added={restoredCount}", scope: "Built-in favorites / 内置收藏");
            ShowActionFeedback($"已恢复 {restoredCount} 个内置预设。", $"Restored {restoredCount} built-in preset(s).");
        }
        catch (Exception ex)
        {
            _allFavorites = previousFavorites;
            FilterFavorites();
            _logger.Error("Restore built-in favorite presets failed", ex);
            AppDialog.Show(this, IsChineseUi() ? "恢复预设失败" : "Restore Presets Failed", ExplainFailure(ex, _lang.T("help.restore.favorite.presets")), danger: true);
            try { FavoritePresetStore.Save(_paths.FavoritePresetStateFile, previousState, _logger); }
            catch (Exception restoreError) { _favoritePresetStateWritable = false; _logger.Error("Restore favorite preset deletion state failed", restoreError); }
            UpdateFavoriteButtons();
        }
    }

    private void TemplateFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (!_favoritesWritable) { ShowStorageWriteBlocked("Favorites / 收藏"); return; }
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
            var previous = SnapshotFavorites();
            _allFavorites.Add(template);
            if (!SaveFavoritesOrRestore(previous)) return;
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
        if (_activeNetworkWorkflow is not null || _dhcpServer.IsRunning || _closingCleanupStarted) return;
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav) { ShowActionFeedbackKey("selection.favorite"); return; }
        LoadFavorite(fav);
    }

    private async void ApplyFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_activeNetworkWorkflow is not null || _dhcpServer.IsRunning || _closingCleanupStarted || _scanRunning) return;
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav) { ShowActionFeedbackKey("selection.favorite"); return; }
        LoadFavorite(fav);
        await ApplyAndScanAsync();
    }

    private void DeleteFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (!_favoritesWritable) { ShowStorageWriteBlocked("Favorites / 收藏"); return; }
        if (FavoriteGrid.SelectedItem is not FavoriteConfig fav) { ShowActionFeedbackKey("selection.favorite"); return; }
        if (!AppDialog.Show(this,
            IsChineseUi() ? "删除收藏夹" : "Delete Favorite",
            IsChineseUi()
                ? $"收藏夹：{fav.Name}\n\n删除后不会影响其它收藏夹。确认删除？"
                : $"Favorite: {fav.Name}\n\nOther saved favorites will not be affected. Delete it?",
            confirm: true,
            danger: true)) return;
        var isBuiltInPreset = Defaults.DefaultFavorites().Any(x => x.Id.Equals(fav.Id, StringComparison.OrdinalIgnoreCase));
        var tombstoneAdded = false;
        if (isBuiltInPreset)
        {
            var state = FavoritePresetStore.Load(_paths.FavoritePresetStateFile, _logger);
            _favoritePresetStateWritable = state.IsWritable;
            if (!state.IsWritable)
            {
                UpdateFavoriteButtons();
                ShowActionFeedback("预设删除记录无法读取，已阻止删除。", "Preset deletion state is unreadable; deletion was blocked.", error: true);
                return;
            }
            if (!state.State.DeletedPresetIds.Contains(fav.Id, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    FavoritePresetStore.SetDeleted(_paths.FavoritePresetStateFile, fav.Id, deleted: true, logger: _logger);
                    tombstoneAdded = true;
                }
                catch (Exception ex)
                {
                    _logger.Error("Save favorite preset deletion intent failed", ex);
                    AppDialog.Show(this, IsChineseUi() ? "删除失败" : "Delete Failed", ExplainFailure(ex, _lang.T("help.delete.favorite")), danger: true);
                    return;
                }
            }
        }
        var previous = SnapshotFavorites();
        _allFavorites.RemoveAll(x => x.Id == fav.Id);
        if (!SaveFavoritesOrRestore(previous))
        {
            if (tombstoneAdded)
            {
                try { FavoritePresetStore.SetDeleted(_paths.FavoritePresetStateFile, fav.Id, deleted: false, logger: _logger); }
                catch (Exception ex) { _favoritePresetStateWritable = false; _logger.Error("Rollback favorite preset deletion intent failed", ex); }
            }
            UpdateFavoriteButtons();
            return;
        }
        FilterFavorites();
        _logger.Info("Favorite deleted: " + fav.Name);
        UpdateSessionStatus();
    }

    private void ImportFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (!_favoritesWritable) { ShowStorageWriteBlocked("Favorites / 收藏"); return; }
        var dialog = new OpenFileDialog
        {
            Title = "Import Favorites / 导入收藏夹",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var importResult = FavoriteStore.LoadWithStatus(dialog.FileName, _logger);
            if (!importResult.HasData)
            {
                var detail = importResult.Status == DataLoadStatus.Missing ? "The selected file is missing." : importResult.Error?.Message ?? "The selected file and its backup could not be read.";
                AppDialog.Show(this, IsChineseUi() ? "导入收藏夹失败" : "Import Favorites Failed", detail, danger: true);
                return;
            }
            if (importResult.Status == DataLoadStatus.RestoredFromBackup)
                _logger.Warn($"Favorites import recovered from backup: {importResult.SourcePath}");
            var loaded = importResult.Favorites;
            var invalid = loaded.Where(x => !IsValidFavorite(x)).ToList();
            var imported = loaded.Where(IsValidFavorite).Select(FavoriteStore.PrepareImported).ToList();
            var duplicateImported = 0;
            var distinctImported = new List<FavoriteConfig>();
            foreach (var item in imported)
            {
                var duplicateIndex = distinctImported.FindIndex(x => FavoriteMergePlanner.SameIdentity(x, item));
                if (duplicateIndex >= 0)
                {
                    distinctImported[duplicateIndex] = FavoriteMergePlanner.Merge(distinctImported[duplicateIndex], item).Favorite;
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
                        ? $"文件读取 {loaded.Count} 条，但没有可用记录。\n跳过：{invalid.Count} 条（名称或扫描 IP、掩码、目标范围无效）。"
                        : $"Read {loaded.Count} record(s), but none are usable.\nSkipped: {invalid.Count} (invalid name, IPv4 scan range, mask, or target).",
                    danger: true);
                return;
            }

            var plans = distinctImported.Select(item =>
            {
                var existing = _allFavorites.FirstOrDefault(local => FavoriteMergePlanner.SameIdentity(local, item));
                if (existing is null)
                {
                    var newFavorite = FavoriteStore.Clone(item);
                    if (string.IsNullOrWhiteSpace(newFavorite.Id)) newFavorite.Id = Guid.NewGuid().ToString("N");
                    return (Imported: item, LocalId: (string?)null, Merged: (FavoriteMergeResult?)null, NewFavorite: newFavorite);
                }
                var merge = FavoriteMergePlanner.Merge(existing, item);
                return (Imported: item, LocalId: existing.Id, Merged: (FavoriteMergeResult?)merge, NewFavorite: (FavoriteConfig?)null);
            }).ToList();
            var addCount = plans.Count(x => x.LocalId is null);
            var updateCount = distinctImported.Count - addCount;
            var credentialCount = distinctImported.Count(x => x.HasUsablePassword);
            var credentialReplacementCount = plans.Count(x => x.Merged?.CredentialReplaced == true);
            var fieldConflicts = plans.Where(x => x.Merged is not null && x.Merged.ReplacedFields.Count > 0)
                .SelectMany(x => x.Merged!.Changes.Select(change =>
                    $"{x.Imported.Name}: {FormatFavoriteImportChange(change, IsChineseUi())}"))
                .Take(8).ToList();
            var conflictPreview = fieldConflicts.Count == 0
                ? ""
                : Environment.NewLine + Environment.NewLine + (IsChineseUi() ? "将覆盖字段及新旧值（凭据和敏感字段值隐藏）：" : "Fields to replace and old/new values (credential and sensitive values are hidden):")
                  + Environment.NewLine + string.Join(Environment.NewLine, fieldConflicts);
            var preview = IsChineseUi()
                ? $"文件：{Path.GetFileName(dialog.FileName)}\n读取：{loaded.Count} 条\n可导入：{distinctImported.Count} 条\n新增：{addCount} 条\n合并：{updateCount} 条\n跳过无效：{invalid.Count} 条\n文件内重复：{duplicateImported} 条\n包含可用凭据：{credentialCount} 条\n将替换本地凭据：{credentialReplacementCount} 条\n\n空字段保留本地值；自定义字段按字段名合并。密码不会显示。{conflictPreview}"
                : $"File: {Path.GetFileName(dialog.FileName)}\nRead: {loaded.Count} record(s)\nUsable: {distinctImported.Count}\nAdd: {addCount}\nMerge: {updateCount}\nInvalid skipped: {invalid.Count}\nDuplicates in file: {duplicateImported}\nUsable credentials included: {credentialCount}\nLocal credentials to replace: {credentialReplacementCount}\n\nBlank fields preserve local values; custom fields merge by field name. Passwords are never shown.{conflictPreview}";
            if (!AppDialog.Show(this, IsChineseUi() ? "确认导入收藏夹" : "Review Imported Favorites", preview, confirm: true,
                danger: credentialCount > 0 || credentialReplacementCount > 0 || fieldConflicts.Count > 0 || invalid.Count > 0 || duplicateImported > 0)) return;

            var previous = SnapshotFavorites();
            var nextFavorites = SnapshotFavorites();
            var added = 0;
            var updated = 0;
            foreach (var plan in plans)
            {
                if (plan.LocalId is null)
                {
                    plan.NewFavorite!.UpdatedAt = DateTime.Now;
                    nextFavorites.Add(plan.NewFavorite);
                    added++;
                }
                else
                {
                    var index = nextFavorites.FindIndex(x => x.Id.Equals(plan.LocalId, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) throw new InvalidOperationException("Local favorite changed while import was being reviewed.");
                    nextFavorites[index] = plan.Merged!.Favorite;
                    updated++;
                }
            }
            _allFavorites = nextFavorites;
            if (!SaveFavoritesOrRestore(previous)) return;
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
        if (_favoritesWritable)
        {
            var previousLastUsedAt = fav.LastUsedAt;
            fav.LastUsedAt = DateTime.Now;
            if (!SaveFavorites()) fav.LastUsedAt = previousLastUsedAt;
        }
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
        var assessment = ProfileValidation.AssessDhcp(CaptureDhcpSettingsFromForms());
        message = FormatProfileSection("DHCP", assessment);
        return assessment.Status == ProfileSectionStatus.Valid;
    }

    private DefaultDhcpSettings CaptureDhcpSettingsFromForms() => new()
    {
        ServerIp = DhcpServerIp.Text.Trim(),
        SubnetMask = DhcpMask.Text.Trim(),
        PoolStart = DhcpStart.Text.Trim(),
        PoolEnd = DhcpEnd.Text.Trim(),
        Gateway = DhcpGateway.Text.Trim(),
        Dns = DhcpDns.Text.Trim(),
        LeaseSeconds = int.TryParse(DhcpLeaseSeconds.Text.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var leaseSeconds) ? leaseSeconds : 0
    };

    private string FormatProfileSection(string name, ProfileSectionAssessment assessment)
    {
        var chinese = IsChineseUi();
        var state = assessment.Status switch
        {
            ProfileSectionStatus.Valid => chinese ? "有效" : "effective",
            ProfileSectionStatus.Empty => chinese ? "未配置" : "not configured",
            ProfileSectionStatus.Draft => chinese ? "草稿" : "draft",
            _ => chinese ? "无效" : "invalid"
        };
        var label = chinese ? name switch
        {
            "Manual scan" => "手动扫描",
            "Routes" => "静态路由",
            _ => name
        } : name;
        var issues = assessment.Issues.Distinct().Select(FormatProfileIssue).ToList();
        return $"{label}: {state}" + (issues.Count == 0 ? "" : " — " + string.Join(chinese ? "；" : "; ", issues));
    }

    private string FormatProfileAssessment(NetworkProfile profile)
    {
        var assessment = ProfileValidation.Assess(profile);
        var chinese = IsChineseUi();
        var state = assessment.OverallStatus switch
        {
            ProfileSectionStatus.Valid => chinese ? "有效配置" : "effective configuration",
            ProfileSectionStatus.Draft => chinese ? "草稿配置" : "draft configuration",
            _ => chinese ? "无效配置" : "invalid configuration"
        };
        return $"{state}: {FormatProfileSection("DHCP", assessment.Dhcp)}; {FormatProfileSection("Manual scan", assessment.ManualScan)}; {FormatProfileSection("Routes", assessment.Routes)}";
    }

    private string FormatProfileIssue(ProfileIssueCode issue)
    {
        if (IsChineseUi()) return issue switch
        {
            ProfileIssueCode.DhcpServerIpMissing => "缺少 DHCP 本机 IP",
            ProfileIssueCode.DhcpMaskMissing => "缺少 DHCP 掩码",
            ProfileIssueCode.DhcpPoolMissing => "缺少完整 DHCP 地址池",
            ProfileIssueCode.DhcpServerIpInvalid => "DHCP 本机 IP 无效",
            ProfileIssueCode.DhcpMaskInvalid => "DHCP 掩码无效",
            ProfileIssueCode.DhcpPoolInvalid => "DHCP 地址池地址无效",
            ProfileIssueCode.DhcpPoolOutsideSubnet => "地址池不在本机网段",
            ProfileIssueCode.DhcpPoolNotUsable => "DHCP 地址池包含不可用主机地址",
            ProfileIssueCode.DhcpPoolOrderInvalid => "地址池起点大于终点",
            ProfileIssueCode.DhcpServerInsidePool => "地址池包含 DHCP 本机地址",
            ProfileIssueCode.DhcpGatewayInvalid => "DHCP 网关无效",
            ProfileIssueCode.DhcpDnsInvalid => "DHCP DNS 无效",
            ProfileIssueCode.DhcpLeaseOutOfRange => "租期必须在 1 到 604800 秒之间",
            ProfileIssueCode.ManualScanIncomplete => "手动扫描字段未填写完整",
            ProfileIssueCode.ManualScanInvalid => "手动扫描范围无效或超过 T11 的 4096 地址上限",
            ProfileIssueCode.RouteAdapterMissing => "路由缺少网卡身份",
            ProfileIssueCode.RouteAddressFamilyMismatch => "路由地址族与目标前缀不一致",
            ProfileIssueCode.DuplicateRoute => "存在重复路由",
            _ => "路由无效"
        };
        return issue switch
        {
            ProfileIssueCode.DhcpServerIpMissing => "DHCP local IP is missing",
            ProfileIssueCode.DhcpMaskMissing => "DHCP mask is missing",
            ProfileIssueCode.DhcpPoolMissing => "DHCP pool is incomplete",
            ProfileIssueCode.DhcpServerIpInvalid => "DHCP local IP is invalid",
            ProfileIssueCode.DhcpMaskInvalid => "DHCP mask is invalid",
            ProfileIssueCode.DhcpPoolInvalid => "DHCP pool address is invalid",
            ProfileIssueCode.DhcpPoolOutsideSubnet => "The pool is outside the server subnet",
            ProfileIssueCode.DhcpPoolNotUsable => "The pool contains an unusable host address",
            ProfileIssueCode.DhcpPoolOrderInvalid => "Pool start is greater than pool end",
            ProfileIssueCode.DhcpServerInsidePool => "The pool contains the DHCP server address",
            ProfileIssueCode.DhcpGatewayInvalid => "DHCP gateway is invalid",
            ProfileIssueCode.DhcpDnsInvalid => "DHCP DNS is invalid",
            ProfileIssueCode.DhcpLeaseOutOfRange => "Lease must be between 1 and 604800 seconds",
            ProfileIssueCode.ManualScanIncomplete => "Manual scan fields are incomplete",
            ProfileIssueCode.ManualScanInvalid => "Manual scan is invalid or exceeds T11's 4096 target limit",
            ProfileIssueCode.RouteAdapterMissing => "Route adapter identity is missing",
            ProfileIssueCode.RouteAddressFamilyMismatch => "Route address family does not match its prefix",
            ProfileIssueCode.DuplicateRoute => "Duplicate route",
            _ => "Invalid route"
        };
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

    private bool TryGetManualScanPlan(out ScanRangePlan? plan, out string message)
    {
        if (ScanRangePlan.TryCreate(ManualIp.Text, ManualMask.Text, ManualTargetIp.Text, out plan, out var error))
        {
            message = "";
            return true;
        }

        message = FormatScanRangeError(error, IsChineseUi());
        return false;
    }

    private static string FormatScanRangeError(ScanRangeError error, bool chinese) => error switch
        {
            ScanRangeError.InvalidLocalIp => chinese ? "本机 IP 必须是有效的 IPv4 地址。" : "Local IP must be a valid IPv4 address.",
            ScanRangeError.InvalidSubnetMask => chinese ? "子网掩码必须是连续的 IPv4 掩码。" : "Subnet mask must be a contiguous IPv4 mask.",
            ScanRangeError.InvalidLocalHost => chinese ? "本机 IP 不能是网络地址或广播地址。" : "Local IP cannot be the network or broadcast address.",
            ScanRangeError.InvalidTargetIp => chinese ? "目标必须是有效的 IPv4 地址；留空才会扫描网段。" : "Target must be a valid IPv4 address; leave it empty to scan the subnet.",
            ScanRangeError.TargetOutsideSubnet => chinese ? "目标 IP 必须位于本机同一网段；留空才会扫描网段。" : "Target IP must be in the local subnet; leave it empty to scan the subnet.",
            ScanRangeError.TargetNotUsableHost => chinese ? "目标 IP 不能是网络地址或广播地址。" : "Target IP cannot be the network or broadcast address.",
            ScanRangeError.NoSubnetTargets => chinese ? "该网段没有可探测的其他主机；请填写明确的目标 IP。" : "This subnet has no other probe targets; enter an explicit target IP.",
            ScanRangeError.TooManyTargets => chinese
                ? $"扫描范围超过 {ScanRangePlan.MaxProbeTargets} 个实际目标；请缩小网段或填写单个目标 IP。"
                : $"The scan range exceeds {ScanRangePlan.MaxProbeTargets} actual targets; use a smaller subnet or enter one target IP.",
            _ => chinese ? "扫描范围无效。" : "Invalid scan range."
        };

    private static IPAddress GetNearbyHost(IPAddress ip, IPAddress mask, uint preferredOffset)
    {
        var network = IpNetwork.ToUInt32(ip) & IpNetwork.ToUInt32(mask);
        var broadcast = network | ~IpNetwork.ToUInt32(mask);
        if (!IpNetwork.TryGetPrefixLength(mask, out var prefix)) return ip;
        if (prefix == 31) return IpNetwork.FromUInt32(IpNetwork.ToUInt32(ip) == network ? broadcast : network);
        if (prefix == 32) return ip;
        var candidate = IpNetwork.ToUInt32(ip) + preferredOffset;
        if (candidate >= broadcast || candidate == IpNetwork.ToUInt32(ip)) candidate = network + 1;
        if (candidate == IpNetwork.ToUInt32(ip) && candidate + 1 < broadcast) candidate++;
        return IpNetwork.FromUInt32(candidate);
    }

    private void UpdateManualScanButtons()
    {
        if (!IsInitialized) return;
        var hasAdapter = SelectedAdapter != null;
        var validRange = TryGetManualScanPlan(out var scanPlan, out var rangeError);
        var workflowBlocked = _scanRunning || _dhcpServer.IsRunning || _activeNetworkWorkflow is not null || _closingCleanupStarted;
        var canApply = hasAdapter && validRange && !workflowBlocked && _safetyOnboardingCompleted;
        if (ScanValidation != null)
        {
            ScanValidation.Text = !hasAdapter
                ? (IsChineseUi() ? "请先选择网卡。" : "Select an adapter first.")
                : !validRange
                    ? rangeError
                    : workflowBlocked
                        ? (IsChineseUi() ? "其他网络操作运行期间，手动配置与扫描已锁定。" : "Manual configuration and scanning are locked while another network operation runs.")
                        : !_safetyOnboardingCompleted
                            ? (IsChineseUi() ? "请先完成安全引导，网络变更按钮才会解锁。" : "Complete the Safety Guide before network-change actions are unlocked.")
                            : IsChineseUi()
                                ? $"将扫描 {scanPlan!.TargetCount} 个实际目标（上限 {ScanRangePlan.MaxProbeTargets}）。"
                                : $"Scan {scanPlan!.TargetCount} actual target(s) (limit {ScanRangePlan.MaxProbeTargets}).";
            ScanValidation.Foreground = canApply ? Brushes.ForestGreen : Brushes.Brown;
        }
        BtnPreviewScan.IsEnabled = hasAdapter && validRange && !workflowBlocked;
        BtnApplyScan.IsEnabled = canApply;
        BtnStopScan.IsEnabled = _scanRunning;
        BtnRestoreScan.IsEnabled = hasAdapter && SelectedAdapter != null && _originalAdapterConfigs.ContainsKey(AdapterSnapshotKey(SelectedAdapter)) && !workflowBlocked && _safetyOnboardingCompleted;
        BtnAddFavorite.IsEnabled = validRange && !_scanRunning && _activeNetworkWorkflow is null && !_dhcpServer.IsRunning && _favoritesWritable;
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
        var enabled = SelectedAdapter != null && _safetyOnboardingCompleted && !_adapterActionInProgress && !_closingCleanupStarted && !_dhcpServer.IsRunning && _activeNetworkWorkflow is null;
        BtnRestartAdapter.IsEnabled = enabled;
        BtnChangeMac.IsEnabled = enabled && _macBackupsWritable;
        BtnRestartAdapter.Background = enabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnChangeMac.Background = BtnChangeMac.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        RefreshButtonStateColors();
    }

    private void UpdateFavoriteButtons()
    {
        if (!IsInitialized) return;
        var selected = FavoriteGrid.SelectedItem is FavoriteConfig;
        var workflowBlocked = _activeNetworkWorkflow is not null || _dhcpServer.IsRunning || _closingCleanupStarted;
        FavoriteGrid.IsEnabled = !workflowBlocked && !_scanRunning;
        BtnNewFavorite.IsEnabled = !_scanRunning && _favoritesWritable;
        BtnEditFavorite.IsEnabled = selected && !_scanRunning && _favoritesWritable;
        BtnRestoreFavoritePresets.IsEnabled = !_scanRunning && _favoritesWritable && _favoritePresetStateWritable;
        BtnImportFavorite.IsEnabled = !_scanRunning && _favoritesWritable;
        BtnExportFavorite.IsEnabled = !_scanRunning && _allFavorites.Count > 0;
        BtnFavoriteColumns.IsEnabled = !_scanRunning;
        BtnLoadFavorite.IsEnabled = selected && !_scanRunning && !workflowBlocked;
        BtnApplyFavorite.IsEnabled = selected && !_scanRunning && !workflowBlocked && SelectedAdapter != null;
        BtnDeleteFavorite.IsEnabled = selected && !_scanRunning && _favoritesWritable;
        BtnOpenFavorite.IsEnabled = selected && !_scanRunning && FavoriteGrid.SelectedItem is FavoriteConfig openFavorite && IPAddress.TryParse(openFavorite.TargetIp, out _);
        BtnNewFavorite.Background = BtnNewFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnEditFavorite.Background = BtnEditFavorite.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
        BtnRestoreFavoritePresets.Background = BtnRestoreFavoritePresets.IsEnabled ? Brushes.LightBlue : Brushes.LightGray;
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
        var leaseSeconds = int.TryParse(DhcpLeaseSeconds.Text.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var lease) ? lease : 0;
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

    private bool TrySaveProfiles(List<NetworkProfile> previous)
    {
        if (!_profilesWritable)
        {
            _allProfiles = previous;
            RefreshProfilesView();
            ShowStorageWriteBlocked("Network profiles / 网络方案");
            return false;
        }
        try
        {
            ProfileStore.Save(_paths.ProfilesFile, _allProfiles, _logger);
            return true;
        }
        catch (Exception ex)
        {
            _profilesWritable = false;
            _allProfiles = previous;
            RefreshProfilesView();
            _logger.Error("Save profiles failed; existing files were retained", ex);
            AppDialog.Show(this, IsChineseUi() ? "保存方案失败" : "Save Profiles Failed", ExplainFailure(ex, _lang.T("help.profile.import")), danger: true);
            return false;
        }
    }

    private List<NetworkProfile> SnapshotProfiles() => _allProfiles.Select(ProfileStore.Clone).ToList();

    private void UpdateProfileButtons()
    {
        if (!IsInitialized) return;
        var selected = ProfileGrid.SelectedItem is NetworkProfile;
        var blocked = _scanRunning || _dhcpServer.IsRunning || _activeNetworkWorkflow is not null || _closingCleanupStarted;
        ProfileGrid.IsEnabled = !blocked;
        BtnSaveProfile.IsEnabled = !blocked && _profilesWritable;
        BtnLoadProfile.IsEnabled = selected && !blocked;
        BtnCompareProfile.IsEnabled = selected && !blocked;
        BtnDuplicateProfile.IsEnabled = selected && !blocked && _profilesWritable;
        BtnImportProfile.IsEnabled = !blocked && _profilesWritable;
        BtnExportProfile.IsEnabled = _allProfiles.Count > 0 && !blocked;
        BtnDeleteProfile.IsEnabled = selected && !blocked && _profilesWritable;
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
        BtnEditFavorite.Background = ThemedActionBrush(BtnEditFavorite.IsEnabled);
        BtnRestoreFavoritePresets.Background = ThemedActionBrush(BtnRestoreFavoritePresets.IsEnabled);
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
        if (!_profilesWritable) { ShowStorageWriteBlocked("Network profiles / 网络方案"); return; }
        if (_scanRunning || _dhcpServer.IsRunning) return;
        var dialog = new ProfileEditorWindow(IsChineseUi()) { Owner = this };
        dialog.ApplyOwnerTheme(this);
        if (dialog.ShowDialog() != true) return;
        var existing = _allProfiles.FirstOrDefault(x => x.Name.Equals(dialog.ProfileName, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !AppDialog.Show(this, IsChineseUi() ? "覆盖配置方案" : "Replace Profile",
            IsChineseUi() ? $"“{dialog.ProfileName}”已存在，是否覆盖？" : $"“{dialog.ProfileName}” already exists. Replace it?",
            confirm: true, danger: true)) return;

        var previous = SnapshotProfiles();
        var profile = CaptureCurrentProfile(dialog.ProfileName, dialog.ProfileDescription);
        if (existing != null)
        {
            profile.Id = existing.Id;
            profile.CreatedAt = existing.CreatedAt;
            _allProfiles.Remove(existing);
        }
        _allProfiles.Add(profile);
        if (!TrySaveProfiles(previous)) return;
        RefreshProfilesView(profile);
        var assessment = ProfileValidation.Assess(profile);
        var status = assessment.OverallStatus == ProfileSectionStatus.Valid ? "Saved effective / 已保存有效配置" : "Saved draft / 已保存草稿";
        AddOperationHistory("Profile", "", "", status, FormatProfileAssessment(profile), scope: "Local profile / 本地方案");
        ShowActionFeedback(
            $"方案已保存到本地；当前状态：{FormatProfileAssessment(profile)}。保存不会应用网络设置。",
            $"Profile saved locally; status: {FormatProfileAssessment(profile)}. Saving does not apply network settings.");
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_activeNetworkWorkflow is not null || _dhcpServer.IsRunning || _closingCleanupStarted) return;
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
        SetDhcpOptionalFieldState(LblDhcpGateway, DhcpGateway, BtnShowDhcpGateway, d.Gateway);
        SetDhcpOptionalFieldState(LblDhcpDns, DhcpDns, BtnShowDhcpDns, d.Dns);
        ManualIp.Text = profile.ManualIp;
        ManualMask.Text = profile.ManualMask;
        ManualTargetIp.Text = profile.ManualTargetIp;
        StaticRoutes.Clear();
        foreach (var source in profile.Routes ?? [])
        {
            var rule = CloneRoute(source);
            var adapter = Adapters.FirstOrDefault(x => !string.IsNullOrWhiteSpace(rule.AdapterId)
                && x.Id.Equals(rule.AdapterId, StringComparison.OrdinalIgnoreCase));
            if (adapter is null && !string.IsNullOrWhiteSpace(rule.AdapterMac))
                adapter = Adapters.FirstOrDefault(x => x.MacAddress.Equals(rule.AdapterMac, StringComparison.OrdinalIgnoreCase));
            if (adapter is null && !string.IsNullOrWhiteSpace(rule.AdapterName))
            {
                var namedAdapters = Adapters.Where(x => x.Name.Equals(rule.AdapterName, StringComparison.OrdinalIgnoreCase)).ToList();
                adapter = namedAdapters.FirstOrDefault(x => !string.IsNullOrWhiteSpace(rule.AdapterMac)
                    && x.MacAddress.Equals(rule.AdapterMac, StringComparison.OrdinalIgnoreCase))
                    ?? (namedAdapters.Count == 1 ? namedAdapters[0] : null);
            }
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
        var status = FormatProfileAssessment(profile);
        ShowActionFeedback(
            $"方案已加载到表单；{status}。加载不会修改网卡、路由或 DHCP 服务。",
            $"Profile loaded into forms; {status}. Loading does not change adapters, routes, or the DHCP service.");
    }

    private static void SetDhcpOptionalFieldState(TextBlock label, TextBox input, Button addButton, string? value)
    {
        var hasValue = !string.IsNullOrWhiteSpace(value);
        label.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        input.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        addButton.Visibility = hasValue ? Visibility.Collapsed : Visibility.Visible;
    }

    private string ResolveProfileAdapterIdentity(StaticRouteRule route)
    {
        var adapter = !string.IsNullOrWhiteSpace(route.AdapterId)
            ? Adapters.FirstOrDefault(x => x.Id.Equals(route.AdapterId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (adapter is null && !string.IsNullOrWhiteSpace(route.AdapterMac))
            adapter = Adapters.FirstOrDefault(x => x.MacAddress.Equals(route.AdapterMac, StringComparison.OrdinalIgnoreCase));
        if (adapter is null && !string.IsNullOrWhiteSpace(route.AdapterName))
        {
            var namedAdapters = Adapters.Where(x => x.Name.Equals(route.AdapterName, StringComparison.OrdinalIgnoreCase)).ToList();
            adapter = namedAdapters.FirstOrDefault(x => !string.IsNullOrWhiteSpace(route.AdapterMac)
                && x.MacAddress.Equals(route.AdapterMac, StringComparison.OrdinalIgnoreCase))
                ?? (namedAdapters.Count == 1 ? namedAdapters[0] : null);
        }
        return adapter is null ? ProfileValidation.AdapterIdentity(route) : "id:" + adapter.Id.Trim().ToUpperInvariant();
    }

    private string FormatProfileDifferenceField(string field)
    {
        if (!IsChineseUi()) return field switch
        {
            "Dhcp.ServerIp" => "DHCP local IP",
            "Dhcp.SubnetMask" => "DHCP mask",
            "Dhcp.PoolStart" => "DHCP pool start",
            "Dhcp.PoolEnd" => "DHCP pool end",
            "Dhcp.Gateway" => "DHCP gateway",
            "Dhcp.Dns" => "DHCP DNS",
            "Dhcp.LeaseSeconds" => "DHCP lease seconds",
            "ManualIp" => "Manual scan local IP",
            "ManualMask" => "Manual scan mask",
            "ManualTargetIp" => "Manual scan target",
            _ => "Route"
        };
        return field switch
        {
            "Dhcp.ServerIp" => "DHCP 本机 IP",
            "Dhcp.SubnetMask" => "DHCP 掩码",
            "Dhcp.PoolStart" => "DHCP 地址池起点",
            "Dhcp.PoolEnd" => "DHCP 地址池终点",
            "Dhcp.Gateway" => "DHCP 网关",
            "Dhcp.Dns" => "DHCP DNS",
            "Dhcp.LeaseSeconds" => "DHCP 租期秒数",
            "ManualIp" => "手动扫描本机 IP",
            "ManualMask" => "手动扫描掩码",
            "ManualTargetIp" => "手动扫描目标",
            _ => "静态路由"
        };
    }

    private void CompareProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGrid.SelectedItem is not NetworkProfile profile) { ShowActionFeedbackKey("selection.profile"); return; }
        var current = CaptureCurrentProfile("current", "");
        var differences = ProfileComparer.Compare(profile, current, ResolveProfileAdapterIdentity);
        var differenceLines = differences.Select(difference =>
        {
            var label = FormatProfileDifferenceField(difference.Field);
            return IsChineseUi()
                ? $"{label}：方案 [{difference.SavedValue}]，当前 [{difference.CurrentValue}]"
                : $"{label}: profile [{difference.SavedValue}], current [{difference.CurrentValue}]";
        });
        var differenceText = string.Join(Environment.NewLine, differenceLines);
        var message = (FormatProfileAssessment(profile) + Environment.NewLine + Environment.NewLine)
            + (differences.Count == 0
            ? (IsChineseUi() ? "当前表单与方案完全一致，未发现差异。" : "The current forms match the profile; no differences found.")
            : differenceText);
        AppDialog.Show(this, IsChineseUi() ? $"方案对比：{profile.Name}" : $"Profile comparison: {profile.Name}", message);
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!_profilesWritable) { ShowStorageWriteBlocked("Network profiles / 网络方案"); return; }
        if (ProfileGrid.SelectedItem is not NetworkProfile source) { ShowActionFeedbackKey("selection.profile"); return; }
        var defaultName = source.Name + (IsChineseUi() ? "（副本）" : " (Copy)");
        var dialog = new ProfileEditorWindow(IsChineseUi(), defaultName, source.Description) { Owner = this };
        dialog.ApplyOwnerTheme(this);
        if (dialog.ShowDialog() != true) return;
        var previous = SnapshotProfiles();
        var profile = ProfileStore.Clone(source);
        profile.Id = Guid.NewGuid().ToString("N");
        profile.Name = dialog.ProfileName;
        profile.Description = dialog.ProfileDescription;
        profile.CreatedAt = DateTime.Now;
        profile.UpdatedAt = DateTime.Now;
        _allProfiles.Add(profile);
        if (!TrySaveProfiles(previous)) return;
        RefreshProfilesView(profile);
        AddOperationHistory("Profile", "", "", "Duplicated / 已复制", profile.Name, scope: "Local profile / 本地方案");
    }

    private void ImportProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (!_profilesWritable) { ShowStorageWriteBlocked("Network profiles / 网络方案"); return; }
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = IsChineseUi() ? "导入配置方案" : "Import Network Profiles" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var importResult = ProfileStore.LoadWithStatus(dialog.FileName, _logger);
            if (!importResult.HasData)
            {
                var detail = importResult.Status == DataLoadStatus.Missing ? "The selected file is missing." : importResult.Error?.Message ?? "The selected file and its backup could not be read.";
                AppDialog.Show(this, IsChineseUi() ? "导入方案失败" : "Import Profiles Failed", detail, danger: true);
                return;
            }
            var imported = importResult.Value ?? [];
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
            var draftProfiles = validProfiles
                .Where(x => ProfileValidation.Assess(x).OverallStatus != ProfileSectionStatus.Valid)
                .ToList();
            var replaceCount = validProfiles.Count(profile => _allProfiles.Any(x => x.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)
                || x.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)));
            var newCount = validProfiles.Count - replaceCount;
            var preview = IsChineseUi()
                ? $"文件：{Path.GetFileName(dialog.FileName)}\n读取：{imported.Count} 个方案\n将新增：{newCount} 个\n将覆盖：{replaceCount} 个\n有效配置：{validProfiles.Count - draftProfiles.Count} 个\n草稿配置：{draftProfiles.Count} 个\n将跳过：{skipped.Count + duplicateImported} 个\n\n"
                  + (skipped.Count == 0 && duplicateImported == 0 ? "没有发现格式问题。" : "发现问题的方案不会写入：")
                : $"File: {Path.GetFileName(dialog.FileName)}\nRead: {imported.Count} profile(s)\nNew: {newCount}\nReplace: {replaceCount}\nEffective: {validProfiles.Count - draftProfiles.Count}\nDraft: {draftProfiles.Count}\nSkipped: {skipped.Count + duplicateImported}\n\n"
                  + (skipped.Count == 0 && duplicateImported == 0 ? "No format problems found." : "Profiles with issues will not be written:");
            if (draftProfiles.Count > 0)
            {
                preview += Environment.NewLine + (IsChineseUi() ? "以下方案将作为草稿保存，执行前仍需补全并通过对应校验：" : "These profiles will be saved as drafts and must pass the relevant validation before execution:");
                preview += Environment.NewLine + string.Join(Environment.NewLine,
                    draftProfiles.Take(8).Select(x => $"{x.Name}: {FormatProfileAssessment(x)}"));
                if (draftProfiles.Count > 8) preview += Environment.NewLine + (IsChineseUi() ? $"……另有 {draftProfiles.Count - 8} 个草稿。" : $"...and {draftProfiles.Count - 8} more draft(s).");
            }
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

            var previous = SnapshotProfiles();
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
            if (!TrySaveProfiles(previous)) return;
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
        if (profile is null)
        {
            issue = IsChineseUi() ? "方案条目不是对象" : "profile entry is not an object";
            return false;
        }
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
        profile.Dhcp ??= new DefaultDhcpSettings();
        profile.Routes ??= [];
        if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
        var assessment = ProfileValidation.Assess(profile);
        if (assessment.OverallStatus == ProfileSectionStatus.Invalid)
        {
            issue = string.Join(IsChineseUi() ? "；" : "; ", new[]
            {
                FormatProfileSection("DHCP", assessment.Dhcp),
                FormatProfileSection("Manual scan", assessment.ManualScan),
                FormatProfileSection("Routes", assessment.Routes)
            }.Where(x => x.Contains(IsChineseUi() ? "无效" : "invalid", StringComparison.OrdinalIgnoreCase)
                || x.Contains(IsChineseUi() ? "重复" : "Duplicate", StringComparison.OrdinalIgnoreCase)
                || x.Contains(IsChineseUi() ? "缺少网卡" : "identity is missing", StringComparison.OrdinalIgnoreCase)
                || x.Contains(IsChineseUi() ? "族" : "family", StringComparison.OrdinalIgnoreCase)));
            if (string.IsNullOrWhiteSpace(issue))
                issue = IsChineseUi() ? "配置校验失败" : "profile validation failed";
            return false;
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
        if (!_profilesWritable) { ShowStorageWriteBlocked("Network profiles / 网络方案"); return; }
        if (ProfileGrid.SelectedItem is not NetworkProfile profile) { ShowActionFeedbackKey("selection.profile"); return; }
        if (!AppDialog.Show(this, IsChineseUi() ? "删除配置方案" : "Delete Profile",
            IsChineseUi() ? $"确认删除“{profile.Name}”？不会修改网络配置。" : $"Delete “{profile.Name}”? Network configuration will not be changed.",
            confirm: true, danger: true)) return;
        var previous = SnapshotProfiles();
        _allProfiles.RemoveAll(x => x.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
        if (!TrySaveProfiles(previous)) return;
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

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_scanRunning)
        {
            ShowActionFeedback("扫描进行中，完成后再清空操作历史。", "Clear operation history after the active scan completes.", error: true);
            return;
        }
        if (!_operationHistoryWritable)
        {
            ShowStorageWriteBlocked("操作历史");
            return;
        }
        if (_operationHistory.Count == 0) return;
        if (!AppDialog.Show(this, IsChineseUi() ? "清空操作历史" : "Clear Operation History",
            IsChineseUi() ? "只清空本地操作历史，不会删除运行日志，也不会撤销网络配置。是否继续？" : "Only local operation history will be cleared. Runtime logs and network configuration will not be changed. Continue?",
            confirm: true, danger: true)) return;
        if (!await FlushScanHistoryAsync(force: true))
        {
            ShowActionFeedback("历史尚未成功保存，未执行清空。", "History could not be saved, so it was not cleared.", error: true);
            return;
        }
        var previous = _operationHistory.ToList();
        _operationHistory.Clear();
        try
        {
            await _operationHistoryWriter.SaveAsync(_operationHistory.ToArray());
            _operationHistorySaveFailed = false;
            _scanHistoryFlushTimer.Stop();
            _pendingScanHistoryCount = 0;
        }
        catch (Exception ex)
        {
            _operationHistory.AddRange(previous);
            _operationHistorySaveFailed = true;
            _logger.Error("Clear operation history failed; the in-memory snapshot was retained", ex);
            RefreshOperationHistoryView();
            UpdateLastOperationPresentation();
            ShowActionFeedback("操作历史未能清空，原文件和记录已保留。", "Operation history was not cleared; the source file and rows were retained.", error: true);
            return;
        }
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
        Interlocked.Increment(ref _adapterSelectionGeneration);
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
        RefreshSelectedAdapterStatus();
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
        var backup = _adapterBackups.LastOrDefault(x =>
            ResolveAdapterForBackup(x)?.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase) == true);
        if (backup == null)
        {
            AppDialog.Show(this, "Rollback / 回滚", "No saved backup for this adapter / 此网卡没有已保存备份。");
            return;
        }
        if (!AppDialog.Show(this, "Rollback / 回滚", $"{backup.AdapterName}\n{backup.CapturedAt:yyyy-MM-dd HH:mm:ss}\n{(backup.DhcpEnabled ? "DHCP" : backup.IpAddress)}\n\nRestore this backup? / 确认恢复此备份？", confirm: true, danger: true)) return;
        var workflow = TryBeginNetworkWorkflow("Adapter rollback");
        if (workflow is null) return;
        AdapterIpv4Snapshot? preRollbackSnapshot = null;
        var rollbackAttempted = false;
        try
        {
            SetBusy(true,
                "Capturing the current adapter state... / 正在读取当前网卡状态...",
                totalPhases: 3);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            preRollbackSnapshot = await _adapterService.CaptureIPv4ConfigAsync(adapter, OperationToken);
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
            rollbackAttempted = true;
            SetBusyPhase("Restoring and verifying the saved adapter configuration... / 正在恢复并核对备份配置...");
            await _adapterService.RestoreIPv4ConfigAsync(adapter, snapshot, OperationToken);
            await VerifyRestoredAdapterConfigAsync(adapter, snapshot, OperationToken);
            AddOperationHistory("Rollback", backup.IpAddress, adapter.MacAddress, "Completed / 已完成", $"{(backup.DhcpEnabled ? "DHCP" : "Static")} {backup.IpAddress}/{backup.PrefixLength}", scope: adapter.Name);
            SetBusyPhase("Refreshing the adapter list... / 正在刷新网卡列表...");
            await RefreshAdaptersAsync(allowDuringNetworkWorkflow: true, cancellationToken: CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Adapter rollback canceled by user or timeout");
            if (rollbackAttempted && preRollbackSnapshot is not null)
                await CompensateAdapterConfigAsync(adapter, preRollbackSnapshot, "Adapter rollback cancellation");
        }
        catch (Exception ex)
        {
            _logger.Error("Adapter rollback failed", ex);
            if (rollbackAttempted && preRollbackSnapshot is not null)
                await CompensateAdapterConfigAsync(adapter, preRollbackSnapshot, "Adapter rollback failure");
            if (!_closingCleanupStarted)
                AppDialog.Show(this, "Rollback / 回滚", ExplainFailure(ex, _lang.T("help.rollback")), danger: true);
        }
        finally
        {
            SetBusy(false);
            EndNetworkWorkflow(workflow);
        }
    }

    private async void ExportResults_Click(object sender, RoutedEventArgs e)
    {
        var rows = Tabs.SelectedItem == TabDhcp
            ? Leases.Select(x => new[] { x.Time.ToString(), x.MacAddress, x.IpAddress, x.Hostname, x.LeaseStart.ToString(), x.LeaseEnd.ToString(), x.Status, x.SessionText, x.PingLatencyMs.ToString(), x.Remark })
            : ScanResults.Select(x => new[] { x.LastSeen.ToString(), x.IpAddress, x.MacAddress, x.Hostname, x.StatusText, x.LatencyMs.ToString(), x.Remark });
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"NetBoot-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        if (!TryBeginStandaloneOperation("Exporting results... / 正在导出结果...")) return;
        try
        {
            var header = Tabs.SelectedItem == TabDhcp
                ? (IsChineseUi() ? new[] { "最后上线", "MAC", "IP", "主机名", "租约开始", "租约结束", "租约状态", "会话", "Ping毫秒", "备注" } : new[] { "LastOnline", "MAC", "IP", "Hostname", "LeaseStart", "LeaseEnd", "LeaseState", "Session", "PingMs", "Remark" })
                : (IsChineseUi() ? new[] { "最后发现", "IP", "MAC", "主机名", "状态", "Ping毫秒", "备注" } : new[] { "LastSeen", "IP", "MAC", "Hostname", "Status", "PingMs", "Remark" });
            var cancellationToken = OperationToken;
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllLines(dialog.FileName, new[] { Csv(header) }.Concat(rows.Select(Csv)), new UTF8Encoding(true));
            }, cancellationToken);
            _logger.Info("Results exported: " + dialog.FileName);
            ShowActionFeedback("结果已导出。", "Results exported.");
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Results export canceled");
        }
        catch (Exception ex)
        {
            _logger.Error("Export results failed", ex);
            if (!_closingCleanupStarted)
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
            $"{(_originalAdapterConfigs.ContainsKey(AdapterSnapshotKey(adapter)) ? "✅" : "ℹ️")} {(IsChineseUi() ? "回滚备份" : "Rollback backup")}: {(_originalAdapterConfigs.ContainsKey(AdapterSnapshotKey(adapter)) ? (IsChineseUi() ? "本次运行可用" : "Available this run") : (IsChineseUi() ? "未捕获" : "Not captured"))}"
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
        if (!_settingsWritable)
        {
            ShowStorageWriteBlocked("Settings / 设置");
            _safetyOnboardingCompleted = false;
            return false;
        }
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
        if (!TrySaveSettings())
        {
            _safetyOnboardingCompleted = false;
            _settings.SafetyOnboardingCompleted = false;
            UpdateManualScanButtons();
            UpdateAdapterActionButtons();
            SetDhcpRunningState(_dhcpServer.IsRunning);
            UpdateSessionStatus();
            return false;
        }
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
        if (!TryBeginStandaloneOperation("Creating support package... / 正在生成支持包...")) return;
        try
        {
            if (!await FlushScanHistoryAsync(force: true) || !_operationHistoryWritable)
                throw new IOException("Operation history could not be committed, so the support package was not created.");
            await _operationHistoryWriter.SaveAsync(_operationHistory.ToArray());
            var cancellationToken = OperationToken;
            var manifest = IsChineseUi()
                ? $"本支持包不包含收藏夹凭据。包含运行日志、设置、网卡备份、配置方案和操作历史。网络地址脱敏：{(redact ? "是" : "否")}。\n"
                : $"This support package excludes Favorite credentials. It contains runtime logs, settings, adapter backups, profiles, and operation history. Network address redaction: {(redact ? "enabled" : "disabled")}.\n";
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var archive = ZipFile.Open(dialog.FileName, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log")) AddSupportFile(archive, file, Path.Combine("logs", Path.GetFileName(file)), redact);
                if (File.Exists(_paths.SettingsFile)) AddSupportFile(archive, _paths.SettingsFile, "appsettings.json", redact);
                if (File.Exists(_paths.AdapterBackupsFile)) AddSupportFile(archive, _paths.AdapterBackupsFile, "adapter-backups.json", redact);
                if (File.Exists(_paths.MacBackupsFile)) AddSupportFile(archive, _paths.MacBackupsFile, "mac-backups.json", redact);
                if (File.Exists(_paths.ProfilesFile)) AddSupportFile(archive, _paths.ProfilesFile, "network-profiles.json", redact);
                if (File.Exists(_paths.OperationHistoryFile)) AddSupportFile(archive, _paths.OperationHistoryFile, "operation-history.json", redact);
                using var writer = new StreamWriter(archive.CreateEntry("manifest.txt").Open(), Encoding.UTF8);
                writer.Write(manifest);
            }, cancellationToken);
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
            if (!_closingCleanupStarted)
                AppDialog.Show(this, "Support / 支持包", ExplainFailure(ex, _lang.T("help.package.logs")), danger: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    internal static void AddSupportFile(ZipArchive archive, string sourcePath, string entryName, bool redact)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!redact)
        {
            using var target = archive.CreateEntry(entryName).Open();
            source.CopyTo(target);
            return;
        }

        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        writer.Write(RedactSupportText(reader.ReadToEnd()));
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
        var previousTheme = _darkTheme;
        _darkTheme = !_darkTheme;
        _settings.DarkTheme = _darkTheme;
        if (!TrySaveSettings())
        {
            _darkTheme = previousTheme;
            _settings.DarkTheme = previousTheme;
            return;
        }
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
        lock (_logDisplaySync)
        {
            _logDisplayQueue.Clear();
            _logDisplayOmitted = 0;
        }
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
        if (!TrySaveSettings())
        {
            _settings.Language = _lang.CurrentLanguage;
            ApplyLanguage();
            return;
        }
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
        TxtSettingsStatus.Text = !_settingsWritable
            ? (IsChineseUi() ? "设置文件不可写；更改不会保存" : "Settings are read-only; changes cannot be saved")
            : _settingsDirty
                ? (IsChineseUi() ? "有未保存更改（Ctrl+S 保存）" : "Unsaved changes (Ctrl+S to save)")
                : (IsChineseUi() ? "设置已保存" : "Settings saved");
        TxtSettingsStatus.Foreground = !_settingsWritable
            ? (Resources["DangerBrush"] as Brush ?? Brushes.Firebrick)
            : _settingsDirty
                ? (Resources["WarningBrush"] as Brush ?? Brushes.DarkOrange)
                : (Resources["MutedTextBrush"] as Brush ?? Brushes.Gray);
        BtnSaveSettings.IsEnabled = _settingsDirty && _settingsWritable;
        BtnSaveSettings.Background = ThemedActionBrush(BtnSaveSettings.IsEnabled);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.AllowDhcpOnAdapterWithGateway = AllowGateway.IsChecked == true;
        _settings.DetectExistingDhcpBeforeStart = DetectExistingDhcp.IsChecked == true;
        _settings.AllowRestartOnAnyAdapter = AllowRestartAnyAdapter.IsChecked == true;
        _settings.AllowMacChangeOnAnyAdapter = AllowMacChangeAnyAdapter.IsChecked == true;
        _settings.RestoreIpOnDhcpStop = RestoreOnStop.IsChecked == true;
        _settings.RedactSupportPackage = RedactSupportPackage.IsChecked == true;
        if (!TrySaveSettings()) return;
        _settingsDirty = false;
        _logger.Info($"Settings saved: dhcpGateway={_settings.AllowDhcpOnAdapterWithGateway} detectExistingDhcp={_settings.DetectExistingDhcpBeforeStart} restartAnyAdapter={_settings.AllowRestartOnAnyAdapter} macAnyAdapter={_settings.AllowMacChangeOnAnyAdapter} restoreIp={_settings.RestoreIpOnDhcpStop} redactSupport={_settings.RedactSupportPackage}");
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

    internal void ProcessLeaseEvent(DhcpLease incoming)
    {
        if (_closingCleanupStarted) return;
        incoming.IsCurrentSession = !string.IsNullOrWhiteSpace(incoming.SessionId)
            && string.Equals(incoming.SessionId, _activeDhcpUiSessionId, StringComparison.Ordinal);
        var clientKey = GetLeaseClientKey(incoming);
        var existing = Leases.LastOrDefault(x => x.IsActiveLease
            && GetLeaseClientKey(x).Equals(clientKey, StringComparison.OrdinalIgnoreCase)
            && x.SessionId.Equals(incoming.SessionId, StringComparison.Ordinal));
        if (existing == null)
        {
            existing = incoming;
            Leases.Add(existing);
            _logger.Info($"UI lease added: {existing.MacAddress} {existing.IpAddress} {existing.Status}");
        }
        else
        {
            var addressChanged = !existing.IpAddress.Equals(incoming.IpAddress, StringComparison.OrdinalIgnoreCase);
            existing.IpAddress = incoming.IpAddress;
            existing.ClientKey = incoming.ClientKey;
            existing.ClientIdentifier = incoming.ClientIdentifier;
            existing.SessionId = incoming.SessionId;
            existing.IsCurrentSession = incoming.IsCurrentSession;
            existing.Hostname = incoming.Hostname;
            existing.LeaseStart = incoming.LeaseStart;
            existing.LeaseEnd = incoming.LeaseEnd;
            existing.Status = incoming.Status;
            existing.IsActiveLease = incoming.IsActiveLease;
            if (addressChanged)
            {
                existing.Time = DateTime.MinValue;
                existing.PingLatencyMs = -1;
                existing.HttpOk = false;
                existing.HttpsOk = false;
            }
            _logger.Info($"UI lease updated: {existing.MacAddress} {existing.IpAddress} {existing.Status}");
        }

        var bindingKey = new LeaseProbeBindingKey(existing.SessionId, clientKey);
        var generation = NextLeaseProbeGeneration(bindingKey);
        if (incoming.Status is "Released" or "Declined" or "Expired")
        {
            existing.IsActiveLease = false;
            existing.PingLatencyMs = -1;
            existing.HttpOk = false;
            existing.HttpsOk = false;
            _leaseProbeCoordinator.InvalidateBinding(bindingKey);
        }
        else if (existing.IsCurrentSession && existing.IsActiveLease && existing.LeaseEnd > DateTime.Now)
        {
            var identity = new LeaseProbeIdentity(existing.SessionId, clientKey, existing.IpAddress, generation);
            var scheduled = _leaseProbeCoordinator.Schedule(new LeaseProbeRequest(identity, IncludeWeb: true));
            if (scheduled == LeaseProbeScheduleResult.QueueFull)
                _logger.Warn($"DHCP lease event probe deferred because the bounded queue is full: session={identity.SessionId} client={identity.ClientKey}");
        }
        else
        {
            _leaseProbeCoordinator.InvalidateBinding(bindingKey);
        }

        AddOperationHistory("DHCP Lease", existing.IpAddress, existing.MacAddress, existing.Status, existing.Hostname);
        LeaseGrid.Items.Refresh();
        UpdateEmptyStates();
    }

    private long NextLeaseProbeGeneration(LeaseProbeBindingKey bindingKey)
    {
        var generation = _leaseProbeGenerations.TryGetValue(bindingKey, out var previous)
            ? checked(previous + 1)
            : 1;
        _leaseProbeGenerations[bindingKey] = generation;
        return generation;
    }

    private static string GetLeaseClientKey(DhcpLease lease) =>
        string.IsNullOrWhiteSpace(lease.ClientKey) ? "MAC:" + lease.MacAddress : lease.ClientKey;

    private void RefreshLeasePing()
    {
        if (_closingCleanupStarted) return;
        var activeLeases = Leases.Where(lease => lease.IsCurrentSession && lease.IsActiveLease && lease.LeaseEnd > DateTime.Now).ToArray();
        if (activeLeases.Length == 0)
        {
            _leaseProbeRefreshCursor = 0;
            return;
        }

        var start = Math.Abs(_leaseProbeRefreshCursor % activeLeases.Length);
        var rotation = Math.Min(LeaseProbeCoordinator.DefaultMaximumConcurrency + LeaseProbeCoordinator.DefaultMaximumQueued, activeLeases.Length);
        _leaseProbeRefreshCursor = (start + rotation) % activeLeases.Length;
        var deferred = 0;
        for (var offset = 0; offset < activeLeases.Length; offset++)
        {
            var lease = activeLeases[(start + offset) % activeLeases.Length];
            var key = new LeaseProbeBindingKey(lease.SessionId, GetLeaseClientKey(lease));
            if (!_leaseProbeGenerations.TryGetValue(key, out var generation))
                _leaseProbeGenerations[key] = generation = 1;
            var identity = new LeaseProbeIdentity(key.SessionId, key.ClientKey, lease.IpAddress, generation);
            if (_leaseProbeCoordinator.Schedule(new LeaseProbeRequest(identity, IncludeWeb: false)) == LeaseProbeScheduleResult.QueueFull)
                deferred++;
        }

        if (deferred > 0)
            _logger.Info($"DHCP lease probe cycle deferred {deferred} binding(s); active={_leaseProbeCoordinator.ActiveCount} queued={_leaseProbeCoordinator.PendingCount}");
    }

    private async Task<LeaseProbeResult> ProbeLeaseAsync(LeaseProbeRequest request, CancellationToken cancellationToken)
    {
        var latency = await PingLatencyAsync(request.Identity.IpAddress, 800, cancellationToken).ConfigureAwait(false);
        bool? http = null;
        bool? https = null;
        if (request.IncludeWeb)
        {
            var result = await _probe.ProbeAsync(request.Identity.IpAddress, _settings.HttpTimeoutMs, cancellationToken).ConfigureAwait(false);
            http = result.http;
            https = result.https;
        }
        return new LeaseProbeResult(request.Identity, latency, http, https);
    }

    private async Task ApplyLeaseProbeResultAsync(LeaseProbeResult result, CancellationToken cancellationToken)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            await Dispatcher.InvokeAsync(() => ApplyLeaseProbeResult(result), DispatcherPriority.Background, cancellationToken);
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
        }
    }

    private void ApplyLeaseProbeResult(LeaseProbeResult result)
    {
        var identity = result.Identity;
        if (_closingCleanupStarted) return;
        var bindingKey = identity.BindingKey;
        if (!_leaseProbeGenerations.TryGetValue(bindingKey, out var generation) || generation != identity.Generation) return;
        var lease = Leases.LastOrDefault(item => item.IsCurrentSession && item.IsActiveLease
            && item.LeaseEnd > DateTime.Now
            && item.SessionId.Equals(identity.SessionId, StringComparison.Ordinal)
            && GetLeaseClientKey(item).Equals(identity.ClientKey, StringComparison.OrdinalIgnoreCase)
            && item.IpAddress.Equals(identity.IpAddress, StringComparison.OrdinalIgnoreCase));
        if (lease is null) return;

        var wasReachable = lease.PingLatencyMs >= 0;
        lease.PingLatencyMs = result.PingLatencyMs;
        if (result.PingLatencyMs >= 0 && !wasReachable) lease.Time = DateTime.Now;
        if (result.HttpOk.HasValue) lease.HttpOk = result.HttpOk.Value;
        if (result.HttpsOk.HasValue) lease.HttpsOk = result.HttpsOk.Value;
        LeaseGrid.Items.Refresh();
    }

    internal Task WaitForLeaseProbesAsync() => _leaseProbeCoordinator.WaitForIdleAsync();
    internal int ActiveLeaseProbeCount => _leaseProbeCoordinator.ActiveCount;

    private void MarkLeaseSessionHistorical(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        foreach (var lease in Leases.Where(x => x.SessionId.Equals(sessionId, StringComparison.Ordinal)))
        {
            lease.IsCurrentSession = false;
            _leaseProbeCoordinator.InvalidateBinding(new LeaseProbeBindingKey(sessionId, GetLeaseClientKey(lease)));
        }
        LeaseGrid.Items.Refresh();
    }

    private async Task EndLeaseProbeSessionAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        MarkLeaseSessionHistorical(sessionId);
        await _leaseProbeCoordinator.CancelSessionAsync(sessionId);
        foreach (var key in _leaseProbeGenerations.Keys.Where(key => key.SessionId.Equals(sessionId, StringComparison.Ordinal)).ToArray())
            _leaseProbeGenerations.Remove(key);
        _logger.Info($"DHCP lease probe session canceled and drained: session={sessionId}");
    }

    private static async Task<long> PingLatencyAsync(string ip, int timeoutMs = 800, CancellationToken cancellationToken = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(IPAddress.Parse(ip), TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)), new byte[32], new PingOptions(), cancellationToken);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return -1;
        }
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
        var workflowActive = _activeNetworkWorkflow is not null;
        var networkBlocked = running || workflowActive || _closingCleanupStarted;
        AdapterBox.IsEnabled = !networkBlocked;
        BtnRefresh.IsEnabled = !networkBlocked && !_adapterRefreshInProgress;
        ManualIp.IsEnabled = !networkBlocked;
        ManualMask.IsEnabled = !networkBlocked;
        ManualTargetIp.IsEnabled = !networkBlocked;
        DhcpServerIp.IsEnabled = !networkBlocked;
        DhcpMask.IsEnabled = !networkBlocked;
        DhcpStart.IsEnabled = !networkBlocked;
        DhcpEnd.IsEnabled = !networkBlocked;
        DhcpGateway.IsEnabled = !networkBlocked;
        DhcpDns.IsEnabled = !networkBlocked;
        BtnShowDhcpGateway.IsEnabled = !networkBlocked;
        BtnShowDhcpDns.IsEnabled = !networkBlocked;
        DhcpLeaseSeconds.IsEnabled = !networkBlocked;
        DhcpConfirm.IsEnabled = !networkBlocked;
        RestoreOnStop.IsEnabled = !networkBlocked;
        BtnPreviewDhcp.IsEnabled = !networkBlocked;
        BtnRollback.IsEnabled = !networkBlocked && _safetyOnboardingCompleted;
        BtnRollbackHistory.IsEnabled = !networkBlocked && _safetyOnboardingCompleted;
        BtnStartDhcp.IsEnabled = !networkBlocked && _safetyOnboardingCompleted;
        BtnStopDhcp.IsEnabled = running && !workflowActive && !_closingCleanupStarted;
        BtnStartDhcp.Background = running ? Brushes.LightGray : Brushes.LightGreen;
        BtnStopDhcp.Background = running ? Brushes.OrangeRed : Brushes.LightGray;
        BtnStopDhcp.Foreground = running ? Brushes.White : Brushes.Black;
        RouteGrid.IsEnabled = !networkBlocked;
        CurrentRouteGrid.IsEnabled = !networkBlocked;
        BtnAddRoute.IsEnabled = !networkBlocked;
        BtnRemoveRoute.IsEnabled = !networkBlocked && _safetyOnboardingCompleted;
        BtnPreviewRoutes.IsEnabled = !networkBlocked;
        BtnApplyRoutes.IsEnabled = !networkBlocked && _safetyOnboardingCompleted && _routeJournalWritable;
        BtnClearAppliedRoutes.IsEnabled = !networkBlocked && _safetyOnboardingCompleted && _routeJournalWritable;
        UpdateAdapterActionButtons();
        UpdateDhcpInputValidation();
        UpdateProfileButtons();
        UpdateSessionStatus();
    }

    private async Task CleanupWorkEnvironmentAsync()
    {
        _cleanupFailureCount = 0;
        PersistStateBeforeExit();
        var pendingDhcpRestores = _dhcpSessionRecoveries
            .Where(x => x.RequiresRestore)
            .Reverse()
            .ToArray();
        SetBusyTotalPhases(4 + pendingDhcpRestores.Length);
        _leasePingTimer.Stop();
        _leaseHintCts?.Cancel();
        _scanCts?.Cancel();
        _dhcpServer.Stop();

        await RunCleanupStepAsync(
            "Cleaning temporary firewall rules... / 正在清理临时防火墙规则...",
            async ct =>
            {
                if (!await RemoveDhcpFirewallRulesAsync(ct) || _dhcpFirewallRules != null)
                    throw new InvalidOperationException("Temporary DHCP firewall rules remain.");
            });

        await RunCleanupStepAsync(
            "Removing temporary routes... / 正在清理临时路由...",
            async ct =>
            {
                var failures = new List<string>();
                if (!await ClearAppliedRoutesAsync(ct, failures))
                {
                    throw new InvalidOperationException($"{_appliedStaticRoutes.Count} temporary static route recovery record(s) remain: {string.Join("; ", failures)}");
                }
            });

        StaticRoutes.Clear();
        CurrentStaticRoutes.Clear();

        foreach (var recovery in pendingDhcpRestores)
        {
            await RunCleanupStepAsync(
                $"Restoring DHCP adapter {recovery.AdapterIdentity.AdapterName}... / 正在恢复 DHCP 网卡...",
                ct => RestoreDhcpSessionAsync(recovery, ct));
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

        _activeDhcpSession = null;
        _logger.Info($"Window closing cleanup completed: failures={_cleanupFailureCount}");
    }

    private async Task RunCleanupStepAsync(string text, Func<CancellationToken, Task> action)
    {
        SetBusyPhase(text);
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
        if (!_macBackupsWritable)
        {
            _logger.Error("MAC restoration was not attempted because the recovery records could not be read");
            return false;
        }
        var pending = _originalAdapterMacs.Values.Where(x => x.RestoreOnExit).ToList();
        if (pending.Count == 0)
        {
            _logger.Info("No adapter MAC restoration required on normal exit");
            return true;
        }

        var succeeded = true;
        foreach (var backup in pending)
        {
            ct.ThrowIfCancellationRequested();
            var adapter = ResolveAdapterForMacBackup(backup);
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
        return SaveMacBackups() && succeeded;
    }

    private async Task<bool> RemoveDhcpFirewallRulesAsync(CancellationToken ct = default)
    {
        var lease = _dhcpFirewallRules;
        if (lease == null || lease.Rules.Count == 0)
        {
            _dhcpFirewallRules = null;
            return true;
        }
        if (!_dhcpFirewallJournalWritable)
        {
            AddOperationHistory("DHCP firewall cleanup", "", "", "Pending / 待处理", "Recovery journal is unreadable; no firewall rule was changed / 恢复日志无法读取，未更改防火墙规则", scope: lease.InterfaceAlias, rollbackAvailable: true);
            return false;
        }
        try
        {
            await _adapterService.RemoveDhcpFirewallRulesAsync(lease, CommitDhcpFirewallLease, ct);
            if (lease.Rules.Count == 0) _dhcpFirewallRules = null;
            return lease.Rules.Count == 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.Warn("Remove DHCP firewall rules canceled by user or timeout");
            AddOperationHistory("DHCP firewall cleanup", "", "", "Pending / 待处理", "Cleanup was canceled; ownership records remain / 清理已取消，归属记录仍保留", scope: lease.InterfaceAlias, rollbackAvailable: true);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error("Remove DHCP firewall rules failed; recovery records were retained", ex);
            AddOperationHistory("DHCP firewall cleanup", "", "", "Pending / 待处理", ex.Message, scope: lease.InterfaceAlias, rollbackAvailable: true);
            return false;
        }
    }

    private async Task<bool> ClearAppliedRoutesAsync(CancellationToken ct = default, List<string>? failureDetails = null)
    {
        if (!_routeJournalWritable)
        {
            _logger.Error("Static route cleanup was not attempted because the recovery journal could not be read");
            failureDetails?.Add("Static route recovery journal is unreadable / 静态路由恢复日志无法读取");
            return false;
        }
        if (_appliedStaticRoutes.Count == 0)
        {
            UpdateRouteStatuses();
            return true;
        }

        try
        {
            foreach (var route in _appliedStaticRoutes.ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (!route.OwnershipVerified || string.IsNullOrWhiteSpace(route.InstanceId))
                {
                    var reason = $"{route.DestinationPrefix} on {route.AdapterName}: ownership is unverified; retained";
                    failureDetails?.Add(reason);
                    _logger.Warn($"Static route cleanup skipped: {reason}");
                    continue;
                }
                var adapter = ResolveAdapterForRoute(route);
                if (adapter == null)
                {
                    var reason = $"{route.DestinationPrefix} on {route.AdapterName}: adapter identity unavailable; retained";
                    failureDetails?.Add(reason);
                    _logger.Warn($"Static route cleanup skipped: {reason}");
                    continue;
                }
                try
                {
                    await _routeService.RemoveAsync(route, adapter, ct);
                    var index = _appliedStaticRoutes.FindIndex(x => SameAppliedRouteIdentity(x, route));
                    if (index < 0) throw new InvalidOperationException("The route recovery record changed during cleanup.");
                    var removedRecord = _appliedStaticRoutes[index];
                    _appliedStaticRoutes.RemoveAt(index);
                    if (!SaveStaticRouteSession())
                    {
                        _appliedStaticRoutes.Insert(Math.Min(index, _appliedStaticRoutes.Count), removedRecord);
                        var reason = $"{route.DestinationPrefix} on {adapter.Name}: recovery journal save failed; record retained";
                        failureDetails?.Add(reason);
                        _logger.Error($"Static route cleanup stopped because {reason}");
                        break;
                    }
                    _logger.Info($"Static route removed: {route.DestinationPrefix} adapter={adapter.Name}");
                    UpdateRouteStatuses();
                    UpdateRecoveryBanner();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var reason = $"{route.DestinationPrefix} on {adapter.Name}: {ex.Message}";
                    failureDetails?.Add(reason);
                    _logger.Error($"Static route cleanup failed: {reason}", ex);
                }
            }
        }
        finally
        {
            UpdateRouteStatuses();
            UpdateRecoveryBanner();
        }
        return _appliedStaticRoutes.Count == 0;
    }

    private static bool SameAppliedRouteIdentity(AppliedStaticRoute left, AppliedStaticRoute right) =>
        left.InterfaceIndex == right.InterfaceIndex
        && string.Equals(left.AdapterId, right.AdapterId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.InstanceId, right.InstanceId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.PolicyStore, right.PolicyStore, StringComparison.OrdinalIgnoreCase);

    private void RemoveAppliedRouteRecord(AppliedStaticRoute route)
    {
        if (!_routeJournalWritable)
            throw new IOException("The static route recovery journal is not writable after route removal.");
        var index = _appliedStaticRoutes.FindIndex(x => SameAppliedRouteIdentity(x, route));
        if (index < 0) return;
        var removedRecord = _appliedStaticRoutes[index];
        _appliedStaticRoutes.RemoveAt(index);
        if (!SaveStaticRouteSession())
        {
            _appliedStaticRoutes.Insert(Math.Min(index, _appliedStaticRoutes.Count), removedRecord);
            throw new IOException("The route was removed, but its recovery journal record could not be saved as cleared.");
        }
        UpdateRouteStatuses();
        UpdateRecoveryBanner();
    }

    private void ShowRouteCleanupFailure(string title, IReadOnlyList<string> failures)
    {
        var remaining = _appliedStaticRoutes.Count;
        var details = failures.Count == 0
            ? (IsChineseUi() ? "未完成项详见日志。" : "See the log for the item details.")
            : string.Join(Environment.NewLine, failures.Take(12).Select(x => "• " + x))
                + (failures.Count > 12 ? Environment.NewLine + (IsChineseUi() ? $"另有 {failures.Count - 12} 项。" : $"And {failures.Count - 12} more item(s).") : "");
        var message = IsChineseUi()
            ? $"仍有 {remaining} 条恢复记录未清除：{Environment.NewLine}{details}"
            : $"{remaining} route recovery record(s) remain:{Environment.NewLine}{details}";
        AppDialog.Show(this, title, message, danger: true);
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

    private bool SaveStaticRouteSession()
    {
        if (!_routeJournalWritable) return false;
        try
        {
            JsonStore.Save(_paths.StaticRouteSessionFile, _appliedStaticRoutes);
            return true;
        }
        catch (Exception ex)
        {
            _routeJournalWritable = false;
            _logger.Error("Save static route session failed", ex);
            SetDhcpRunningState(_dhcpServer.IsRunning);
            return false;
        }
    }

    private async Task RecoverStaleRoutesAsync()
    {
        var load = JsonStore.Load<List<AppliedStaticRoute>>(_paths.StaticRouteSessionFile, _logger);
        _routeJournalWritable = load.Status != DataLoadStatus.Failed;
        RecordStorageLoad("Static route recovery journal / 静态路由恢复日志", load.Status, load.SourcePath, load.Error, missingIsExpected: true);
        if (load.Status == DataLoadStatus.Failed)
        {
            UpdateRecoveryBanner();
            SetDhcpRunningState(_dhcpServer.IsRunning);
            AppDialog.Show(this,
                IsChineseUi() ? "恢复日志无法读取" : "Recovery Journal Unreadable",
                IsChineseUi() ? "静态路由恢复日志及其备份均无法读取。为避免漏清理上次创建的路由，路由应用和清理操作已锁定。原文件已保留；请修复或恢复日志后重新启动工具。" : "The static route recovery journal and its backup could not be read. Route apply and cleanup are locked so routes from the previous run are not mistaken for absent. The source files were preserved; repair or restore the journal, then restart the tool.",
                danger: true);
            return;
        }
        if (load.Status == DataLoadStatus.RestoredFromBackup)
        {
            AppDialog.Show(this,
                IsChineseUi() ? "已从恢复日志备份恢复" : "Recovery Journal Restored",
                IsChineseUi() ? $"已从有效备份 {Path.GetFileName(load.SourcePath)} 恢复静态路由记录；损坏的主文件已保留。" : $"Static route records were restored from valid backup {Path.GetFileName(load.SourcePath)}. The damaged primary file was preserved.",
                danger: true);
        }
        if (!load.HasData)
        {
            UpdateRecoveryBanner();
            SetDhcpRunningState(_dhcpServer.IsRunning);
            return;
        }
        var staleRoutes = load.Value ?? [];
        if (staleRoutes.Count == 0) return;

        _appliedStaticRoutes.Clear();
        _appliedStaticRoutes.AddRange(staleRoutes);
        UpdateRecoveryBanner();
        var pendingOwnership = staleRoutes.Where(x => !x.OwnershipVerified || string.IsNullOrWhiteSpace(x.InstanceId)).ToList();
        var verifiedRoutes = staleRoutes.Where(x => x.OwnershipVerified && !string.IsNullOrWhiteSpace(x.InstanceId)).ToList();
        if (pendingOwnership.Count > 0)
        {
            AppDialog.Show(this,
                IsChineseUi() ? "路由归属待核实" : "Route Ownership Needs Review",
                IsChineseUi()
                    ? $"有 {pendingOwnership.Count} 条路由创建记录缺少已验证的实例标识。为避免删除其他程序的同路径路由，工具不会自动清理；记录已保留在恢复中心供核查。"
                    : $"{pendingOwnership.Count} route creation record(s) have no verified instance identity. To avoid removing another program's route with the same path, automatic cleanup was skipped; the records remain in Recovery Center for review.",
                danger: true);
        }
        if (verifiedRoutes.Count == 0) return;
        var summary = string.Join(Environment.NewLine, verifiedRoutes.Select(x => $"{x.DestinationPrefix} -> {x.AdapterName} ({x.NextHop})"));
        _logger.Warn($"Stale static route session detected: count={staleRoutes.Count}");
        if (!EnsureSafetyOnboarding()) return;
        if (!AppDialog.Show(this,
            IsChineseUi() ? "发现上次未清理的静态路由" : "Stale Static Routes Found",
            summary + Environment.NewLine + Environment.NewLine + (IsChineseUi() ? "是否立即清理？" : "Clean them up now?"),
            confirm: true,
            danger: true)) return;
        var workflow = TryBeginNetworkWorkflow("Stale route cleanup");
        if (workflow is null) return;

        try
        {
            SetBusy(true, "Cleaning stale routes... / 正在清理上次残留路由...");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var failureDetails = new List<string>();
            if (!await ClearAppliedRoutesAsync(OperationToken, failureDetails))
            {
                ShowRouteCleanupFailure(IsChineseUi() ? "清理残留路由" : "Clean Stale Routes", failureDetails);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Stale route cleanup canceled");
            if (!_closingCleanupStarted && _appliedStaticRoutes.Count > 0)
            {
                var remaining = _appliedStaticRoutes
                    .Select(x => $"{x.DestinationPrefix} on {x.AdapterName}: 清理已取消，记录保留 / cleanup canceled; record retained")
                    .ToList();
                ShowRouteCleanupFailure(IsChineseUi() ? "清理残留路由已取消" : "Stale Route Cleanup Canceled", remaining);
            }
        }
        finally
        {
            SetBusy(false);
            EndNetworkWorkflow(workflow);
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
        if (_updateState.CheckResult is not { Succeeded: true, IsNewVersion: true } result || string.IsNullOrWhiteSpace(result.DownloadUrl)) return;
        var localizedReleaseNotes = ReleaseNotesLocalizer.SelectLocalizedSection(result.ReleaseNotes, _lang.CurrentLanguage);
        var changes = !string.IsNullOrWhiteSpace(localizedReleaseNotes)
            ? localizedReleaseNotes
            : result.Changes.Count > 0
                ? string.Join(Environment.NewLine, result.Changes.Select(x => "• " + x))
                : result.ReleaseNotes;
        if (string.IsNullOrWhiteSpace(changes)) changes = IsChineseUi() ? "发布方未提供详细更新内容。" : "The publisher did not provide detailed release notes.";
        var sourceDetails = FormatUpdateSpeedDetails(result.DownloadSpeeds, result.DownloadUrl);
        if (string.IsNullOrWhiteSpace(sourceDetails))
            sourceDetails = IsChineseUi() ? $"当前首选下载源：{GetUpdateSourceName(result.DownloadUrl)}" : $"Preferred download source: {GetUpdateSourceName(result.DownloadUrl)}";
        var message = IsChineseUi()
            ? $"版本 v{result.LatestVersion}\n\n更新内容：\n{changes}\n\n下载源测速：\n{sourceDetails}\n\n确认后将在后台下载并校验更新包。{(result.SelectedPackage is null ? "该版本不支持自动升级，下载文件将保存到‘下载’文件夹。" : "下载完成后可点击‘重启升级’，程序会正常关闭、替换文件并启动新版本。")}下载过程中可随时点击‘取消’停止。"
            : $"Version v{result.LatestVersion}\n\nChanges:\n{changes}\n\nDownload source speed:\n{sourceDetails}\n\nAfter confirmation the package will download and be verified in the background. {(result.SelectedPackage is null ? "Automatic upgrade is unavailable for this release; the file will be saved to Downloads." : "When ready, click Restart to upgrade. The app will close normally, replace its files, and start the new version.")} You can click Cancel at any time to stop the download.";
        if (!AppDialog.Show(this, IsChineseUi() ? "发现新版本" : "New Version", message, confirm: true)) return;
        if (_updateState.DownloadInProgress)
        {
            AppDialog.Show(this, IsChineseUi() ? "更新下载" : "Update Download", IsChineseUi() ? "更新已在后台下载中。" : "The update is already downloading.");
            return;
        }
        _ = DownloadUpdateAsync(result);
    }

    private void CancelUpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        if (!_updateState.DownloadInProgress || _updateState.DownloadCancellationRequested) return;
        _updateController.CancelDownload();
    }

    private async Task DownloadUpdateAsync(UpdateCheckResult result)
    {
        var fileName = result.SelectedPackage?.FileName;
        var automaticInstall = result.SelectedPackage is not null;
        if (string.IsNullOrWhiteSpace(fileName)) fileName = string.IsNullOrWhiteSpace(result.ArchiveName) ? $"NetBootDhcpTool-v{result.LatestVersion}.7z" : Path.GetFileName(result.ArchiveName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"NetBootDhcpTool-v{result.LatestVersion}.7z";
        var destinationDirectory = automaticInstall
            ? Path.Combine(_paths.DataDirectory, "updates", "staging")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, fileName);
        if (!automaticInstall && File.Exists(destination)) destination = Path.Combine(destinationDirectory, Path.GetFileNameWithoutExtension(fileName) + $"-{DateTime.Now:yyyyMMdd-HHmmss}" + Path.GetExtension(fileName));
        try
        {
            _logger.Info($"Update background download started: version={result.LatestVersion} kind={result.SelectedPackage?.Kind ?? "legacy-manual"} destination={destination}");
            var downloaded = await _updateController.DownloadAsync(result, destination);
            _logger.Info($"Update background download completed: version={result.LatestVersion} path={downloaded.FilePath} sha256={downloaded.Sha256}");
        }
        catch (OperationCanceledException) when (_updateController.State.DownloadCanceled)
        {
            _logger.Info("Update background download canceled");
        }
        catch (Exception ex)
        {
            _logger.Error("Update background download failed", ex);
        }
    }

    private void UpdateDownloadProgressPresentation(UpdateDownloadProgress progress)
    {
        if (!_updateState.DownloadInProgress || _closingCleanupStarted) return;
        var sourceName = GetUpdateSourceName(progress.DownloadUrl);
        if (progress.BytesReceived == 0 && progress.BytesPerSecond == 0)
        {
            TxtUpdateStatus.Text = progress.IsSourceFallback
                ? IsChineseUi() ? $"首选源不可用，正在切换到 {sourceName}..." : $"Preferred source unavailable; switching to {sourceName}..."
                : IsChineseUi() ? $"正在从 {sourceName} 下载更新..." : $"Downloading update from {sourceName}...";
        }
        else
        {
            var size = progress.TotalBytes.HasValue
                ? $"{progress.BytesReceived / 1024d:0.0}/{progress.TotalBytes.Value / 1024d:0.0} KB"
                : $"{progress.BytesReceived / 1024d:0.0} KB";
            var prefix = progress.IsSourceFallback
                ? IsChineseUi() ? $"已切换到 {sourceName}：" : $"Switched to {sourceName}: "
                : IsChineseUi() ? $"{sourceName} 下载：" : $"{sourceName} download: ";
            TxtUpdateStatus.Text = IsChineseUi()
                ? $"{prefix}{size}，速度 {FormatBytesPerSecond(progress.BytesPerSecond)}"
                : $"{prefix}{size}, {FormatBytesPerSecond(progress.BytesPerSecond)}";
        }
        TxtUpdateStatus.ToolTip = FormatUpdateSpeedDetails(_updateState.CheckResult?.DownloadSpeeds ?? [], progress.DownloadUrl);
    }

    private void UpdateController_StateChanged(UpdateControllerState state) => ApplyUpdateControllerState(state);

    internal void ApplyUpdateControllerState(UpdateControllerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(() => ApplyUpdateControllerState(state), DispatcherPriority.DataBind); }
            catch (InvalidOperationException) { }
            return;
        }
        if (_closingCleanupStarted || state.Revision < _updateState.Revision) return;
        var prior = _updateState;
        if (state.CheckResult == null && prior.CheckResult != null)
        {
            state = state with
            {
                CheckStarted = prior.CheckStarted,
                CheckResult = prior.CheckResult,
                SourceSpeeds = prior.SourceSpeeds
            };
        }
        _updateState = state;
        UpdateVersionPresentation();
        BtnCancelUpdateDownload.Visibility = state.DownloadInProgress ? Visibility.Visible : Visibility.Collapsed;
        BtnCancelUpdateDownload.IsEnabled = state.DownloadInProgress && !state.DownloadCancellationRequested;
        var readyToInstall = state.DownloadedResult is { ReadyToInstall: true };
        BtnRestartUpgrade.Visibility = readyToInstall ? Visibility.Visible : Visibility.Collapsed;
        if (readyToInstall)
        {
            TxtUpdateLink.Visibility = Visibility.Collapsed;
            UpdateLink.IsEnabled = false;
        }
        if (state.DownloadCancellationRequested)
            TxtUpdateStatus.Text = IsChineseUi() ? "正在取消更新下载..." : "Canceling update download...";
        else if (state.PackageVerificationInProgress)
            TxtUpdateStatus.Text = IsChineseUi() ? "下载完成，正在校验签名、安装基线和更新包内容..." : "Download complete; verifying the signature, installation baseline, and package contents...";
        else if (state.DownloadProgress is { } progress) UpdateDownloadProgressPresentation(progress);
        else if (state.DownloadedResult is { } downloaded)
        {
            TxtUpdateStatus.Text = downloaded.ReadyToInstall
                ? IsChineseUi()
                    ? $"{(downloaded.FellBackToFullPackage ? "本机文件与 OTA 基线不一致，已改用 Full 包。" : "") }更新包已校验（{downloaded.Package?.Kind}）：点击‘重启升级’应用 v{_updateState.CheckResult?.LatestVersion}"
                    : $"{(downloaded.FellBackToFullPackage ? "The installed files did not match the OTA baseline; switched to the Full package. " : "")}Update package verified ({downloaded.Package?.Kind}); click Restart to upgrade to v{_updateState.CheckResult?.LatestVersion}"
                : IsChineseUi()
                    ? $"更新包已从 {GetUpdateSourceName(downloaded.DownloadUrl)} 下载（未安装）：{downloaded.FilePath}"
                    : $"Update downloaded from {GetUpdateSourceName(downloaded.DownloadUrl)} (not installed): {downloaded.FilePath}";
            TxtUpdateStatus.ToolTip = FormatUpdateSpeedDetails(state.CheckResult?.DownloadSpeeds ?? [], downloaded.DownloadUrl);
        }
        else if (state.DownloadCanceled) TxtUpdateStatus.Text = _lang.T("update.download.canceled");
        else if (state.DownloadError is { } error)
        {
            TxtUpdateStatus.Text = IsChineseUi() ? "更新下载失败，请查看日志或联系作者" : "Update download failed; see logs or contact the author";
            if (!_closingCleanupStarted)
                AppDialog.Show(this, IsChineseUi() ? "更新下载失败" : "Update Download Failed", error + "\n\n1406829360@qq.com", danger: true);
        }
        if (!prior.LowSpeedWarningRaised && state.LowSpeedWarningRaised)
        {
            var progress = state.DownloadProgress;
            _logger.Warn($"Update download speed below 3 KB/s for {progress?.LowSpeedDuration.TotalSeconds ?? 10:0} seconds");
            AppDialog.Show(this,
                IsChineseUi() ? "下载速度过慢" : "Download Too Slow",
                IsChineseUi() ? "下载速度连续 10 秒低于 3 KB/s，可以通过邮件向作者获取更新包：1406829360@qq.com" : "Download speed stayed below 3 KB/s for 10 seconds. You can email the author for the update package: 1406829360@qq.com",
                danger: true);
        }
    }

    internal Task<UpdateDownloadResult> StartUpdateDownloadForUiTest(UpdateCheckResult result, string destinationPath) =>
        _updateController.DownloadAsync(result, destinationPath);

    internal async Task RunAutomaticUpdateForIntegrationAsync()
    {
        if (!UpdateTestEnvironment.IsActive || UpdateTestEnvironment.Root is null)
            throw new InvalidOperationException("Automatic lifecycle tests require a marked isolated root.");
        var report = Path.Combine(_paths.DataDirectory, "integration-auto-update.json");
        UpdateTestEnvironment.RequirePath(report);
        try
        {
            var result = await _updateController.CheckAsync()
                ?? throw new InvalidDataException("The integration update check was cancelled.");
            if (!result.Succeeded || !result.IsNewVersion || !result.SignatureVerified || result.SelectedPackage is null)
                throw new InvalidDataException("No verified automatic package was discovered: " + result.Error);
            var destination = Path.Combine(_paths.DataDirectory, "updates", "staging", result.SelectedPackage.FileName);
            var downloaded = await _updateController.DownloadAsync(result, destination);
            var install = _updateController.InstallContext ?? throw new InvalidDataException("The installed context is missing.");
            await new UpdateActivationService().ActivateAsync(_paths, install, downloaded);
            UpdateResultProtocol.WriteDurably(report, System.Text.Json.JsonSerializer.Serialize(new {
                Phase = "accepted", Version = result.LatestVersion!.ToString(3), downloaded.Package!.FileName,
                downloaded.Package.Format, downloaded.Sha256, downloaded.DownloadUrl, Speeds = result.DownloadSpeeds
            }, JsonStore.Options));
            Close();
        }
        catch (Exception ex)
        {
            _logger.Error("Isolated automatic update lifecycle failed", ex);
            UpdateResultProtocol.WriteDurably(report, System.Text.Json.JsonSerializer.Serialize(new { Phase = "failed", Error = ex.ToString() }, JsonStore.Options));
            Close();
        }
    }

    private async void RestartUpgrade_Click(object sender, RoutedEventArgs e)
    {
        if (_updateState.DownloadedResult is not { ReadyToInstall: true } downloaded
            || _updateController.InstallContext is not { } install) return;
        if (_settingsDirty && !_unsavedSettingsExitConfirmed && !AppDialog.Show(this,
            IsChineseUi() ? "未保存设置" : "Unsaved Settings",
            IsChineseUi() ? "设置页有未保存更改。继续升级会退出程序并丢弃这些更改，是否继续？" : "The Settings page has unsaved changes. Continuing the upgrade will close the app and discard them. Continue?",
            confirm: true)) return;
        if (_settingsDirty) _unsavedSettingsExitConfirmed = true;
        var confirmation = IsChineseUi()
            ? $"将升级到 v{_updateState.CheckResult?.LatestVersion}。程序会先正常关闭并完成恢复/保存流程，再替换受管程序文件、核验并启动新版本。个人数据目录不会被替换。现在重启升级吗？"
            : $"Upgrade to v{_updateState.CheckResult?.LatestVersion}. The app will close normally and finish its cleanup, then replace managed program files, verify them, and start the new version. Your user data directory will not be replaced. Restart to upgrade now?";
        if (!AppDialog.Show(this, IsChineseUi() ? "重启升级" : "Restart to Upgrade", confirmation, confirm: true)) return;
        BtnRestartUpgrade.IsEnabled = false;
        TxtUpdateStatus.Text = IsChineseUi() ? "正在准备并验证更新器..." : "Preparing and validating the updater...";
        try
        {
            var activation = new UpdateActivationService();
            await activation.ActivateAsync(_paths, install, downloaded);
            _logger.Info($"Update transaction accepted by updater: target={downloaded.SignedUpdateManifestVersion()}");
            TxtUpdateStatus.Text = IsChineseUi() ? "更新器已受理，正在正常关闭程序..." : "Updater accepted the transaction; closing normally...";
            Close();
        }
        catch (Exception ex)
        {
            BtnRestartUpgrade.IsEnabled = true;
            _logger.Error("Update activation failed before the application closed", ex);
            TxtUpdateStatus.Text = IsChineseUi() ? "更新器未受理，程序仍在运行；可重试升级。" : "Updater did not accept the transaction; the app is still running and you can retry.";
            AppDialog.Show(this, IsChineseUi() ? "升级未启动" : "Upgrade Not Started", ex.Message, danger: true);
        }
    }

    private static string GetUpdateSourceName(string? url) => new UpdateSourceSpeed(url ?? "", null).SourceName;

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

    private void SetBusy(bool busy, string? text = null, int? totalPhases = null)
    {
        if (busy)
        {
            if (_activeNetworkWorkflow is not null)
            {
                _activeOperationText = text ?? _activeNetworkWorkflow.Name;
                if (_operationStartedTimestamp == 0) _operationStartedTimestamp = Stopwatch.GetTimestamp();
            }
            else if (_operationCts is null)
            {
                _operationCts = new CancellationTokenSource();
                _standaloneOperationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _operationStartedTimestamp = Stopwatch.GetTimestamp();
                _activeOperationText = text;
            }
            BtnCancelOperation.IsEnabled = true;
            BtnCancelOperation.Visibility = Visibility.Visible;
            var phaseText = text ?? "Please wait... / 请稍后...";
            if (_busyOperationStopwatch is null)
                StartBusyProgress(phaseText, totalPhases);
            else
            {
                if (!string.IsNullOrWhiteSpace(text)) SetBusyPhase(text);
                if (totalPhases.HasValue) SetBusyTotalPhases(totalPhases.Value);
            }
        }
        else
        {
            FinishBusyProgress();
            if (_activeNetworkWorkflow is null)
            {
                _activeOperationText = null;
                _operationCts?.Dispose();
                _operationCts = null;
                _standaloneOperationCompletion?.TrySetResult();
                _standaloneOperationCompletion = null;
                _operationStartedTimestamp = 0;
                BtnCancelOperation.Visibility = Visibility.Collapsed;
            }
        }
        BusyText.Text = text ?? "Please wait... / 请稍后...";
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateSessionStatus();
    }

    private void StartBusyProgress(string initialPhase, int? totalPhases)
    {
        _busyOperationId = Guid.NewGuid().ToString("N")[..8];
        _busyOperationStopwatch = Stopwatch.StartNew();
        _busyPhaseStopwatch = Stopwatch.StartNew();
        _busyPhaseName = initialPhase;
        _busyPhaseNumber = 1;
        _busyCompletedPhaseCount = 0;
        _busyTotalPhaseCount = totalPhases is > 0 ? totalPhases : null;
        _logger.Info($"UI operation started: id={_busyOperationId} plannedPhases={_busyTotalPhaseCount?.ToString() ?? "unknown"} phase={initialPhase}");
        _busyProgressTimer.Start();
        UpdateBusyProgressDisplay();
    }

    private void SetBusyTotalPhases(int totalPhases)
    {
        _busyTotalPhaseCount = totalPhases > 0 ? totalPhases : null;
        UpdateBusyProgressDisplay();
    }

    private void SetBusyPhase(string phase)
    {
        if (_busyOperationStopwatch is null || _busyPhaseStopwatch is null)
        {
            StartBusyProgress(phase, null);
        }
        else if (!string.Equals(_busyPhaseName, phase, StringComparison.Ordinal))
        {
            CompleteBusyPhase();
            _busyPhaseNumber = _busyCompletedPhaseCount + 1;
            _busyPhaseName = phase;
            _busyPhaseStopwatch.Restart();
            if (_activeNetworkWorkflow is not null || _operationCts is not null) _activeOperationText = phase;
            _logger.Info($"UI operation phase started: id={_busyOperationId} step={FormatBusyStep()} phase={phase}");
        }

        BusyText.Text = phase;
        UpdateBusyProgressDisplay();
        UpdateSessionStatus();
    }

    private void CompleteBusyPhase()
    {
        if (_busyPhaseStopwatch is null || _busyPhaseName is null) return;
        _busyCompletedPhaseCount++;
        _logger.Info($"UI operation phase completed: id={_busyOperationId} step={FormatBusyStep(_busyPhaseNumber)} phase={_busyPhaseName} elapsedMs={_busyPhaseStopwatch.ElapsedMilliseconds} operationElapsedMs={_busyOperationStopwatch?.ElapsedMilliseconds ?? 0}");
        UpdateBusyProgressDisplay();
    }

    private void FinishBusyProgress()
    {
        if (_busyOperationStopwatch is null)
        {
            _busyProgressTimer.Stop();
            return;
        }

        CompleteBusyPhase();
        _logger.Info($"UI operation ended: id={_busyOperationId} phasesCompleted={_busyCompletedPhaseCount} totalElapsedMs={_busyOperationStopwatch.ElapsedMilliseconds}");
        _busyProgressTimer.Stop();
        _busyOperationStopwatch = null;
        _busyPhaseStopwatch = null;
        _busyOperationId = null;
        _busyPhaseName = null;
        _busyPhaseNumber = 0;
        _busyCompletedPhaseCount = 0;
        _busyTotalPhaseCount = null;
    }

    private void UpdateBusyElapsedText()
    {
        if (_busyOperationStopwatch is null || _busyPhaseStopwatch is null) return;
        UpdateBusyProgressDisplay();
    }

    private void UpdateBusyProgressDisplay()
    {
        if (BusyStageText is null || BusyElapsedText is null || BusyStageProgress is null) return;

        var operationElapsed = _busyOperationStopwatch?.Elapsed ?? TimeSpan.Zero;
        var phaseElapsed = _busyPhaseStopwatch?.Elapsed ?? TimeSpan.Zero;
        BusyStageText.Text = _busyTotalPhaseCount is int total
            ? (IsChineseUi() ? $"阶段 {_busyPhaseNumber}/{total}" : $"Step {_busyPhaseNumber} of {total}")
            : (IsChineseUi() ? $"阶段 {_busyPhaseNumber}" : $"Step {_busyPhaseNumber}");
        BusyElapsedText.Text = IsChineseUi()
            ? $"本阶段 {FormatBusyDuration(phaseElapsed)} · 总耗时 {FormatBusyDuration(operationElapsed)}"
            : $"This step {FormatBusyDuration(phaseElapsed)} · Total {FormatBusyDuration(operationElapsed)}";
        BusyStageProgress.Visibility = _busyTotalPhaseCount.HasValue ? Visibility.Visible : Visibility.Collapsed;
        BusyStageProgress.Maximum = _busyTotalPhaseCount ?? 1;
        BusyStageProgress.Value = _busyTotalPhaseCount.HasValue
            ? Math.Min(_busyCompletedPhaseCount, _busyTotalPhaseCount.Value)
            : 0;
    }

    private string FormatBusyStep(int? step = null)
    {
        var current = step ?? _busyPhaseNumber;
        return _busyTotalPhaseCount is int total
            ? $"{current}/{total}"
            : current.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatBusyDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    private CancellationToken OperationToken => _activeNetworkWorkflow?.Token ?? _operationCts?.Token ?? CancellationToken.None;

    private bool TryBeginStandaloneOperation(string text)
    {
        if (_closingCleanupStarted || _activeNetworkWorkflow is not null || _operationCts is not null)
        {
            ShowActionFeedback(
                "当前有其他操作正在运行，请等待后重试。",
                "Another operation is active. Wait and try again.",
                error: true);
            return false;
        }
        SetBusy(true, text);
        return _operationCts is not null;
    }

    private NetworkWorkflowLease? TryBeginNetworkWorkflow(string name, bool allowWhileDhcpRunning = false)
    {
        if (_closingCleanupStarted) return null;
        if (_activeNetworkWorkflow is not null || _operationCts is not null || (_dhcpServer.IsRunning && !allowWhileDhcpRunning))
        {
            ShowActionFeedback(
                "当前有其他操作正在运行，或 DHCP 服务尚未停止。请等待后重试。",
                "Another operation is active or DHCP is still running. Wait and try again.",
                error: true);
            return null;
        }

        var lease = _networkWorkflows.TryAcquire(name);
        if (lease is null)
        {
            ShowActionFeedback(
                "另一个网络变更正在运行，请等待其完成或取消。",
                "Another network change is running. Wait for it to finish or cancel it.",
                error: true);
            return null;
        }

        _activeNetworkWorkflow = lease;
        Interlocked.Increment(ref _adapterWorkflowGeneration);
        _adapterStatusRefreshDirty = true;
        _activeOperationText = name;
        _operationStartedTimestamp = Stopwatch.GetTimestamp();
        UpdateManualScanButtons();
        UpdateAdapterActionButtons();
        UpdateFavoriteButtons();
        SetDhcpRunningState(_dhcpServer.IsRunning);
        return lease;
    }

    private void EndNetworkWorkflow(NetworkWorkflowLease lease)
    {
        if (ReferenceEquals(_activeNetworkWorkflow, lease))
        {
            _activeNetworkWorkflow = null;
            Interlocked.Increment(ref _adapterWorkflowGeneration);
            lease.Dispose();
            _activeOperationText = null;
            _operationStartedTimestamp = 0;
            if (_operationCts is null) BtnCancelOperation.Visibility = Visibility.Collapsed;
            SetDhcpRunningState(_dhcpServer.IsRunning);
            if (_adapterStatusRefreshDirty && !_closingCleanupStarted) RefreshSelectedAdapterStatus();
        }
        else
        {
            lease.Dispose();
        }
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_activeNetworkWorkflow is null && _operationCts == null) return;
        _logger.Warn("User requested cancellation of the active operation");
        if (_activeNetworkWorkflow is not null) _activeNetworkWorkflow.Cancel();
        else _operationCts?.Cancel();
        BusyText.Text = IsChineseUi() ? "正在取消，请稍候..." : "Canceling, please wait...";
        BtnCancelOperation.IsEnabled = false;
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKeyboardCommand(e.Key, Keyboard.Modifiers, sender)) e.Handled = true;
    }

    internal bool HandleKeyboardCommand(Key key, ModifierKeys modifiers, object sender)
    {
        if (key == Key.Escape)
        {
            if (_activeNetworkWorkflow is not null) _activeNetworkWorkflow.Cancel();
            else _operationCts?.Cancel();
            _scanCts?.Cancel();
            return true;
        }
        if (key == Key.F5 && modifiers == ModifierKeys.None)
        {
            if (!_adapterRefreshInProgress && !_closingCleanupStarted && _activeNetworkWorkflow is null)
                _ = RefreshAdaptersAsync();
            return true;
        }
        if (modifiers != ModifierKeys.Control) return false;
        if (key == Key.Enter && !_closingCleanupStarted)
        {
            var previewTriggered = false;
            if (Tabs.SelectedItem == TabDhcp && BtnPreviewDhcp.IsEnabled)
            {
                PreviewDhcp_Click(sender, new RoutedEventArgs());
                previewTriggered = true;
            }
            else if (Tabs.SelectedItem == TabScan && BtnPreviewScan.IsEnabled)
            {
                PreviewScan_Click(sender, new RoutedEventArgs());
                previewTriggered = true;
            }
            else if (Tabs.SelectedItem == TabRoutes && BtnPreviewRoutes.IsEnabled)
            {
                PreviewRoutes_Click(sender, new RoutedEventArgs());
                previewTriggered = true;
            }
            if (previewTriggered) return true;
        }
        switch (key)
        {
            case Key.R:
                if (!_adapterRefreshInProgress && !_closingCleanupStarted && _activeNetworkWorkflow is null)
                    _ = RefreshAdaptersAsync();
                return true;
            case Key.L:
                SetLogPanelExpanded(LogBox.Visibility != Visibility.Visible);
                return true;
            case Key.C when HistoryGrid.IsKeyboardFocusWithin:
                CopyHistory_Click(sender, new RoutedEventArgs());
                return true;
            case Key.S when Tabs.SelectedItem == TabSettings && _settingsDirty && !_closingCleanupStarted:
                SaveSettings_Click(sender, new RoutedEventArgs());
                return true;
            default:
                return false;
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
        if (expanded) DrainLogDisplayQueue(force: true);
        UpdateLogSummary();
    }

    private void UpdateLogSummary()
    {
        if (TxtLogSummary == null) return;
        var count = LogBox.Document.Blocks.Count;
        var omitted = Interlocked.Read(ref _logDisplayOmitted);
        TxtLogSummary.Text = omitted > 0
            ? IsChineseUi() ? $"当前显示 {count} 条，省略 {omitted} 条" : $"{count} visible line(s), {omitted} omitted"
            : IsChineseUi() ? $"当前显示 {count} 条" : $"{count} visible line(s)";
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

    private void Logger_LineWritten(string line)
    {
        lock (_logDisplaySync)
        {
            if (_logDisplayClosed) return;
            while (_logDisplayQueue.Count >= 500)
            {
                _logDisplayQueue.Dequeue();
                Interlocked.Increment(ref _logDisplayOmitted);
            }
            _logDisplayQueue.Enqueue(line);
        }
    }

    internal int PendingLogDisplayCount
    {
        get { lock (_logDisplaySync) return _logDisplayQueue.Count; }
    }

    internal long LogDisplayOmittedCount => Interlocked.Read(ref _logDisplayOmitted);
    internal int LogDisplayScrollCount => Volatile.Read(ref _logDisplayScrollCount);

    internal int DrainLogDisplayQueueForUiTest(bool force = true) => DrainLogDisplayQueue(force);

    private int DrainLogDisplayQueue(bool force = false)
    {
        if (!force && (LogBox.Visibility != Visibility.Visible || _logDisplayClosed)) return 0;
        var lines = new List<string>(128);
        lock (_logDisplaySync)
        {
            var limit = force ? int.MaxValue : 128;
            while (lines.Count < limit && _logDisplayQueue.Count > 0) lines.Add(_logDisplayQueue.Dequeue());
        }
        if (lines.Count == 0) return 0;
        foreach (var line in lines) AppendLogLineCore(line);
        while (LogBox.Document.Blocks.Count > 500)
        {
            LogBox.Document.Blocks.Remove(LogBox.Document.Blocks.FirstBlock);
            Interlocked.Increment(ref _logDisplayOmitted);
        }
        if (ChkLogAutoScroll?.IsChecked != false)
        {
            LogBox.ScrollToEnd();
            Interlocked.Increment(ref _logDisplayScrollCount);
        }
        UpdateLogSummary();
        return lines.Count;
    }

    private void AppendLogLineCore(string line)
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
    }

    private async Task<(AdapterConfigBackup Backup, AdapterIpv4Snapshot Snapshot)> RememberAdapterConfigAsync(
        NetworkAdapterInfo adapter,
        CancellationToken ct = default,
        bool forceFreshSnapshot = false,
        bool preserveBackupHistory = false,
        bool verifyAdapterAfterCapture = false)
    {
        if (string.IsNullOrWhiteSpace(adapter.InterfaceIndex))
            throw new InvalidOperationException("The adapter has no current interface index and cannot be configured.");
        if (string.IsNullOrWhiteSpace(adapter.Id))
            throw new InvalidOperationException(IsChineseUi()
                ? "缺少稳定网卡身份，已取消系统修改。"
                : "The adapter has no stable identity; no system change was attempted.");
        if (verifyAdapterAfterCapture)
            await RefreshAdapterByIdentityAsync(adapter, ct);
        var snapshotKey = AdapterSnapshotKey(adapter);
        if (!_lastAdapterIps.ContainsKey(adapter.InterfaceIndex))
        {
            _lastAdapterIps[adapter.InterfaceIndex] = adapter.IPv4Address;
        }
        AdapterIpv4Snapshot snapshot;
        if (forceFreshSnapshot || !_originalAdapterConfigs.ContainsKey(snapshotKey))
        {
            var captureIndex = adapter.InterfaceIndex;
            snapshot = await _adapterService.CaptureIPv4ConfigAsync(adapter, ct);
            snapshot.NormalizeLegacyFields();
            if (snapshot.DnsMode == AdapterDnsMode.Unknown || !snapshot.AdapterEnabled.HasValue)
                throw new InvalidOperationException(IsChineseUi()
                    ? "无法完整读取原始 DNS 模式或网卡启用状态，已取消系统修改。"
                    : "The original DNS mode or adapter enabled state could not be captured completely; no system change was attempted.");
            if (snapshot.DhcpEnabled && snapshot.Routes.Any(x => string.IsNullOrWhiteSpace(x.Protocol)))
                throw new InvalidOperationException(IsChineseUi()
                    ? "无法区分原默认路由是否由 DHCP 分配，已取消系统修改。"
                    : "The original default routes could not be classified as DHCP or static; no system change was attempted.");
            if (verifyAdapterAfterCapture)
            {
                await RefreshAdapterByIdentityAsync(adapter, ct);
                if (!string.Equals(adapter.InterfaceIndex, captureIndex, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(IsChineseUi()
                        ? "保存网卡快照后接口索引发生变化，已取消系统修改。"
                        : "The interface index changed after saving the adapter snapshot; no system change was attempted.");
            }
        }
        else snapshot = _originalAdapterConfigs[snapshotKey];
        var nextBackups = _adapterBackups.ToList();
        if (!preserveBackupHistory)
        {
            nextBackups.RemoveAll(x =>
                (!string.IsNullOrWhiteSpace(adapter.Id) && string.Equals(x.AdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase))
                || (string.IsNullOrWhiteSpace(x.AdapterId) && x.InterfaceIndex == adapter.InterfaceIndex));
        }
        var backup = new AdapterConfigBackup
        {
            InterfaceIndex = adapter.InterfaceIndex,
            AdapterId = adapter.Id,
            AdapterName = adapter.Name,
            AdapterMac = adapter.MacAddress,
            CapturedAt = DateTime.Now,
            DhcpEnabled = snapshot.DhcpEnabled,
            IpAddress = snapshot.IpAddress,
            PrefixLength = snapshot.PrefixLength,
            Gateway = snapshot.Gateway,
            Dns = snapshot.Dns,
            DnsMode = snapshot.DnsMode,
            AdapterEnabled = snapshot.AdapterEnabled,
            Addresses = snapshot.Addresses,
            Routes = snapshot.Routes,
            AutomaticMetric = snapshot.AutomaticMetric,
            InterfaceMetric = snapshot.InterfaceMetric
        };
        nextBackups.Add(backup);
        JsonStore.Save(_paths.AdapterBackupsFile, nextBackups);
        _adapterBackups = nextBackups;
        _originalAdapterConfigs[snapshotKey] = snapshot;
        return (backup, snapshot);
    }

    private static string AdapterSnapshotKey(NetworkAdapterInfo adapter)
    {
        if (!string.IsNullOrWhiteSpace(adapter.Id)) return "id:" + adapter.Id;
        if (!string.IsNullOrWhiteSpace(adapter.InterfaceIndex)
            && !string.IsNullOrWhiteSpace(adapter.Name)
            && !string.IsNullOrWhiteSpace(adapter.MacAddress))
            return $"legacy:{adapter.InterfaceIndex}|{adapter.Name}|{adapter.MacAddress}";
        throw new InvalidOperationException("A stable adapter identity is required to keep a recoverable IPv4 snapshot.");
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

    private void NetworkChanged(object? sender, EventArgs e) => QueueAdapterNetworkChangeRefresh();

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => QueueAdapterNetworkChangeRefresh();

    private void QueueAdapterNetworkChangeRefresh()
    {
        Interlocked.Increment(ref _adapterNetworkChangeVersion);
        if (Interlocked.Exchange(ref _adapterNetworkChangeDispatchPending, 1) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_closingCleanupStarted)
                {
                    Interlocked.Exchange(ref _adapterNetworkChangeDispatchPending, 0);
                    return;
                }
                _adapterNetworkChangeObservedVersion = Interlocked.Read(ref _adapterNetworkChangeVersion);
                _adapterNetworkChangeTimer.Stop();
                _adapterNetworkChangeTimer.Start();
            }, DispatcherPriority.Send);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _adapterNetworkChangeDispatchPending, 0);
        }
    }

    private void AdapterNetworkChangeDebounceTick()
    {
        var currentVersion = Interlocked.Read(ref _adapterNetworkChangeVersion);
        if (currentVersion != _adapterNetworkChangeObservedVersion)
        {
            _adapterNetworkChangeObservedVersion = currentVersion;
            _adapterNetworkChangeTimer.Stop();
            _adapterNetworkChangeTimer.Start();
            return;
        }
        _adapterNetworkChangeTimer.Stop();
        Interlocked.Exchange(ref _adapterNetworkChangeDispatchPending, 0);
        _adapterStatusRefreshDirty = true;
        RefreshSelectedAdapterStatus();
    }

    private void RefreshSelectedAdapterStatus()
    {
        if (_closingCleanupStarted) return;
        var adapter = SelectedAdapter;
        if (adapter == null || _adapterRefreshInProgress) return;
        if (_activeNetworkWorkflow is not null)
        {
            _adapterStatusRefreshDirty = true;
            return;
        }
        _adapterStatusRefreshDirty = false;
        _adapterStatusCoordinator.Request(new AdapterStatusRefreshRequest(
            adapter.Id,
            adapter.InterfaceIndex,
            Volatile.Read(ref _adapterSelectionGeneration),
            Volatile.Read(ref _adapterWorkflowGeneration)));
    }

    private bool CommitSelectedAdapterStatus(AdapterStatusRefreshRequest request, NetworkAdapterInfo latest)
    {
        var adapter = SelectedAdapter;
        if (_closingCleanupStarted || _activeNetworkWorkflow is not null
            || request.SelectionGeneration != _adapterSelectionGeneration
            || request.WorkflowGeneration != _adapterWorkflowGeneration
            || adapter == null
            || !adapter.Id.Equals(request.AdapterId, StringComparison.OrdinalIgnoreCase)
            || !adapter.InterfaceIndex.Equals(request.InterfaceIndex, StringComparison.OrdinalIgnoreCase)
            || !latest.Id.Equals(request.AdapterId, StringComparison.OrdinalIgnoreCase)
            || !latest.InterfaceIndex.Equals(request.InterfaceIndex, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!AdapterStatusComparer.HasChanges(adapter, latest)) return true;

        var previousIp = adapter.IPv4Address;
        var previousStatus = adapter.Status;
        adapter.Status = latest.Status;
        adapter.IPv4Address = latest.IPv4Address;
        adapter.SubnetMask = latest.SubnetMask;
        adapter.Gateway = latest.Gateway;
        adapter.Dns = latest.Dns;
        adapter.MacAddress = latest.MacAddress;
        adapter.LinkSpeedMbps = latest.LinkSpeedMbps;
        if (!previousIp.Equals(latest.IPv4Address, StringComparison.OrdinalIgnoreCase)) AddIpHistory(previousIp, latest.IPv4Address);
        AdapterBox.Items.Refresh();
        UpdateAdapterIpText(adapter);
        TxtManualAdapterIp.Text = BuildAdapterIpDisplay(adapter);
        TxtMac.Text = adapter.MacAddress;
        TxtGateway.Text = adapter.Gateway;
        TxtStatus.Text = adapter.Status;
        TxtStatus.Foreground = adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) ? Brushes.ForestGreen : Brushes.Firebrick;
        if (!previousStatus.Equals(adapter.Status, StringComparison.OrdinalIgnoreCase)) SystemSounds.Exclamation.Play();
        _logger.Info($"Adapter status changed: {adapter.DisplayName} status={adapter.Status} ip={adapter.IPv4Address} gateway={adapter.Gateway} speedMbps={adapter.LinkSpeedMbps}");
        return true;
    }

    private async Task<NetworkAdapterInfo> RefreshAdapterByIdentityAsync(NetworkAdapterInfo adapter, CancellationToken ct)
    {
        var identity = new AdapterConfigBackup { AdapterId = adapter.Id };
        var currentAdapters = await Task.Run(() => _adapterService.GetAdapters(logAdapters: false), ct);
        var matches = AdapterIdentityMatcher.FindMatches(currentAdapters, identity);
        if (matches.Count == 0) throw new InvalidOperationException(Ui("dhcp.start.adapter.unavailable"));
        if (matches.Count > 1) throw new InvalidOperationException(Ui("dhcp.start.adapter.ambiguous"));

        var current = matches[0];
        adapter.Name = current.Name;
        adapter.Description = current.Description;
        adapter.InterfaceIndex = current.InterfaceIndex;
        adapter.MacAddress = current.MacAddress;
        adapter.IPv4Address = current.IPv4Address;
        adapter.SubnetMask = current.SubnetMask;
        adapter.Gateway = current.Gateway;
        adapter.Dns = current.Dns;
        adapter.Status = current.Status;
        adapter.LinkSpeedMbps = current.LinkSpeedMbps;
        adapter.IsWifi = current.IsWifi;
        adapter.IsVirtual = current.IsVirtual;
        AdapterBox.Items.Refresh();
        return adapter;
    }

    private async Task RestoreDhcpSessionAsync(DhcpSessionRecovery recovery, CancellationToken ct)
    {
        try
        {
            var currentAdapters = await Task.Run(() => _adapterService.GetAdapters(logAdapters: false), ct);
            await DhcpSessionRestoreCoordinator.RestoreAsync(
                recovery,
                currentAdapters,
                (adapter, token) => _adapterService.CaptureIPv4ConfigAsync(adapter, token),
                async (adapter, snapshot, token) =>
                {
                    await _adapterService.RestoreIPv4ConfigAsync(adapter, snapshot, token);
                    var restored = await _adapterService.CaptureIPv4ConfigAsync(adapter, token);
                    if (!AdapterIpv4SnapshotComparer.Equivalent(snapshot, restored))
                        throw new InvalidOperationException(Ui("verify.restore.failed"));

                    var visibleAdapter = Adapters.FirstOrDefault(x =>
                        string.Equals(x.Id, recovery.AdapterIdentity.AdapterId, StringComparison.OrdinalIgnoreCase));
                    if (visibleAdapter != null)
                    {
                        visibleAdapter.IPv4Address = snapshot.IpAddress;
                        UpdateAdapterIpText(visibleAdapter);
                    }
                    if (SelectedAdapter?.Id.Equals(recovery.AdapterIdentity.AdapterId, StringComparison.OrdinalIgnoreCase) == true)
                        TxtManualAdapterIp.Text = BuildAdapterIpDisplay(SelectedAdapter);
                },
                ct);

            _logger.Info($"DHCP session adapter restored: id={recovery.AdapterIdentity.AdapterId} idx={recovery.AdapterIdentity.InterfaceIndex} state={recovery.State}");
            _dhcpSessionRecoveries.Remove(recovery);
        }
        catch (DhcpSessionRestoreException ex)
        {
            _logger.Warn($"DHCP session adapter restore blocked: id={recovery.AdapterIdentity.AdapterId} reason={ex.Reason}");
            throw new InvalidOperationException(DhcpSessionRestoreMessage(ex.Reason), ex);
        }
        catch (Exception ex)
        {
            recovery.MarkRestoreFailed();
            _logger.Error($"DHCP session adapter restore failed and remains pending: id={recovery.AdapterIdentity.AdapterId}", ex);
            throw;
        }
    }

    private string DhcpSessionRestoreMessage(DhcpSessionRestoreFailure reason) => reason switch
    {
        DhcpSessionRestoreFailure.AdapterUnavailable => Ui("dhcp.restore.adapter.unavailable"),
        DhcpSessionRestoreFailure.AdapterIdentityAmbiguous => Ui("dhcp.restore.adapter.ambiguous"),
        DhcpSessionRestoreFailure.ExpectedSnapshotUnavailable => Ui("dhcp.restore.snapshot.missing"),
        DhcpSessionRestoreFailure.ExternalConfigurationChanged => Ui("dhcp.restore.conflict"),
        _ => Ui("verify.restore.failed")
    };

    private async Task RestoreOriginalAdapterConfigAsync(NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adapter.InterfaceIndex)) return;
        var snapshotKey = AdapterSnapshotKey(adapter);
        if (!_originalAdapterConfigs.TryGetValue(snapshotKey, out var snapshot))
            throw new InvalidOperationException(Ui("adapter.restore.snapshot.missing"));

        await _adapterService.RestoreIPv4ConfigAsync(adapter, snapshot, ct);
        await VerifyRestoredAdapterConfigAsync(adapter, snapshot, ct);
        adapter.IPv4Address = snapshot.IpAddress;
        UpdateAdapterIpText(adapter);
        TxtManualAdapterIp.Text = BuildAdapterIpDisplay(adapter);
        _logger.Info($"Adapter restored to original config: id={adapter.Id} idx={adapter.InterfaceIndex} {snapshot.DisplayText}");
    }

    private async Task CompensateAdapterConfigAsync(NetworkAdapterInfo adapter, AdapterIpv4Snapshot snapshot, string operation)
    {
        try
        {
            await _adapterService.RestoreIPv4ConfigAsync(adapter, snapshot, CancellationToken.None);
            await VerifyRestoredAdapterConfigAsync(adapter, snapshot, CancellationToken.None);
            adapter.IPv4Address = snapshot.IpAddress;
            UpdateAdapterIpText(adapter);
            if (ReferenceEquals(SelectedAdapter, adapter)) TxtManualAdapterIp.Text = BuildAdapterIpDisplay(adapter);
            _logger.Info($"{operation} compensated: idx={adapter.InterfaceIndex} {snapshot.DisplayText}");
        }
        catch (Exception ex)
        {
            _logger.Error($"{operation} compensation failed; captured recovery data remains available", ex);
        }
    }

    private async Task VerifyStaticIPv4Async(NetworkAdapterInfo adapter, string expectedIp, string expectedMask, CancellationToken ct)
    {
        var expectedAddress = IPAddress.Parse(expectedIp.Trim()).ToString();
        var expectedPrefix = IpNetwork.PrefixLength(IPAddress.Parse(expectedMask.Trim()));
        var actual = await _adapterService.CaptureIPv4ConfigAsync(adapter, ct);
        var hasOriginal = _originalAdapterConfigs.TryGetValue(AdapterSnapshotKey(adapter), out var original);
        var matched = hasOriginal
            && !actual.DhcpEnabled
            && actual.Addresses.Count == 1
            && actual.Addresses[0].IpAddress.Equals(expectedAddress, StringComparison.OrdinalIgnoreCase)
            && actual.Addresses[0].PrefixLength == expectedPrefix
            && actual.Routes.Count == 0
            && actual.DnsMode == AdapterDnsMode.Automatic
            && !actual.AutomaticMetric
            && actual.InterfaceMetric == NetworkAdapterService.DhcpHostAdapterMetric
            && actual.AdapterEnabled == original!.AdapterEnabled;
        _logger.Info($"Post-change adapter verification: idx={adapter.InterfaceIndex} expected={expectedAddress}/{expectedPrefix} actual={actual.DisplayText} matched={matched}");
        if (!matched) throw new InvalidOperationException(Ui("verify.static.failed"));
    }

    private async Task VerifyRestoredAdapterConfigAsync(NetworkAdapterInfo adapter, AdapterIpv4Snapshot expected, CancellationToken ct)
    {
        expected.NormalizeLegacyFields();
        var actual = await _adapterService.CaptureIPv4ConfigAsync(adapter, ct);
        var matched = AdapterIpv4SnapshotComparer.Equivalent(expected, actual);
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
        var observedAdapters = await Task.Run(() => _adapterService.GetAdapters(logAdapters: false), ct);
        var observed = AdapterIdentityMatcher.Find(observedAdapters, backup);
        var actual = observed == null || string.IsNullOrWhiteSpace(observed.MacAddress) ? "" : NetworkAdapterService.NormalizeMacAddress(observed.MacAddress);
        var matched = observed != null && actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        _logger.Info($"Post-change MAC verification: idx={backup.InterfaceIndex} expected={expected} actual={actual} matched={matched}");
        if (!matched) throw new InvalidOperationException(IsChineseUi() ? "恢复后校验失败：MAC 与目标值不一致。" : "Post-change verification failed: the MAC does not match the target value.");
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
            ["最后上线", "MAC", "IP", "主机名", "开始", "结束", "状态", "会话", "延迟(ms)", "备注"],
            ["Last Online", "MAC", "IP", "Hostname", "Start", "End", "Lease state", "Session", "Ping ms", "Remark"]);
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
            && ScanRangePlan.TryCreate(item.LocalIp, item.SubnetMask, item.TargetIp, out _, out _);
    }

    private static string FormatFavoriteImportField(string field, bool chinese)
    {
        if (field.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
            return chinese ? $"自定义字段 {field[7..]}" : $"Custom field {field[7..]}";
        return field switch
        {
            nameof(FavoriteConfig.Name) => chinese ? "名称" : "Name",
            nameof(FavoriteConfig.DeviceNumber) => chinese ? "设备号" : "Device number",
            nameof(FavoriteConfig.SerialNumber) => chinese ? "序列号" : "Serial number",
            nameof(FavoriteConfig.RemarkName) => chinese ? "备注" : "Remark",
            nameof(FavoriteConfig.Description) or nameof(FavoriteConfig.MemoryText) => chinese ? "说明/记忆" : "Description/memory",
            nameof(FavoriteConfig.Username) => chinese ? "账号" : "Username",
            nameof(FavoriteConfig.AdapterName) or nameof(FavoriteConfig.AdapterMac) => chinese ? "网卡标识" : "Adapter identity",
            nameof(FavoriteConfig.LocalIp) => chinese ? "本机 IP" : "Local IP",
            nameof(FavoriteConfig.SubnetMask) => chinese ? "掩码" : "Subnet mask",
            nameof(FavoriteConfig.Gateway) => chinese ? "网关" : "Gateway",
            nameof(FavoriteConfig.Dns) => chinese ? "DNS" : "DNS",
            nameof(FavoriteConfig.TargetIp) => chinese ? "目标 IP" : "Target IP",
            nameof(FavoriteConfig.PreferHttps) => chinese ? "HTTPS 偏好" : "HTTPS preference",
            "Credentials" => chinese ? "凭据" : "Credentials",
            _ => field
        };
    }

    private static string FormatFavoriteImportChange(FavoriteFieldChange change, bool chinese)
    {
        var label = FormatFavoriteImportField(change.Field, chinese);
        if (change.IsCredential || IsSensitiveFavoriteField(change.Field))
            return chinese ? $"{label}：将替换（值不显示）" : $"{label}: replacement (value hidden)";
        var oldValue = SummarizeImportValue(change.ExistingValue);
        var newValue = SummarizeImportValue(change.ImportedValue);
        return chinese ? $"{label}：{oldValue} → {newValue}" : $"{label}: {oldValue} → {newValue}";
    }

    private static bool IsSensitiveFavoriteField(string field) =>
        new[] { "password", "passwd", "credential", "secret", "token", "privatekey" }
            .Any(sensitive => field.Contains(sensitive, StringComparison.OrdinalIgnoreCase));

    private static string SummarizeImportValue(string value)
    {
        var safe = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return safe.Length <= 60 ? safe : safe[..57] + "...";
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
