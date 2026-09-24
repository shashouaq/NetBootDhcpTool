using System.IO;
using System.Security.Principal;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NetBootDhcpTool.App;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine("UI_SMOKE_FAILED: run this harness from a non-administrator process.");
            return 2;
        }

        var dataDirectory = Path.Combine(Path.GetTempPath(), "NetBootDhcpTool-ui-smoke-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", dataDirectory);
        var paths = new AppPaths(AppContext.BaseDirectory);
        using var instanceLease = SingleInstanceLease.TryAcquire(paths);
        if (instanceLease == null)
        {
            Console.Error.WriteLine("UI_SMOKE_FAILED: could not acquire the isolated test data directory.");
            return 2;
        }
        Defaults.EnsureFiles(paths);
        var settings = JsonStore.LoadOrDefault(paths.SettingsFile, new AppSettings());
        settings.SafetyOnboardingCompleted = true;
        settings.Language = "en-US";
        JsonStore.Save(paths.SettingsFile, settings);
        const string unreadableRouteJournal = "{broken route journal";
        const string unreadableRouteBackup = "{broken route backup";
        File.WriteAllText(paths.StaticRouteSessionFile, unreadableRouteJournal);
        File.WriteAllText(paths.StaticRouteSessionFile + ".bak", unreadableRouteBackup);
        JsonStore.Save(paths.DhcpFirewallSessionFile,
        new List<DhcpFirewallRuleLease>
        {
            new DhcpFirewallRuleLease
            {
                LeaseId = "ui-smoke-lease",
                InterfaceAlias = "Isolated UI smoke adapter",
                ProgramPath = Path.Combine(AppContext.BaseDirectory, "NetBootDhcpTool.exe"),
                CreatedAt = DateTimeOffset.UtcNow,
                Rules =
                [
                    new DhcpFirewallRuleRecord
                    {
                        Name = "NetBootDhcpTool-ui-smoke",
                        DisplayName = "NetBoot DHCP Tool UI smoke",
                        Description = "NetBootDhcpTool Owner=ui-smoke-lease Rule=ui-smoke",
                        Group = "NetBootDhcpTool",
                        Direction = "Inbound",
                        LocalPort = "67",
                        RemotePort = "Any",
                        InterfaceAlias = "Isolated UI smoke adapter",
                        ProgramPath = Path.Combine(AppContext.BaseDirectory, "NetBootDhcpTool.exe"),
                        LeaseId = "ui-smoke-lease",
                        OwnershipVerified = false
                    }
                ]
            }
        });

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow(paths, new FileLogger(paths));
        var busyStageText = (TextBlock)window.FindName("BusyStageText")!;
        var busyElapsedText = (TextBlock)window.FindName("BusyElapsedText")!;
        var busyStageProgress = (ProgressBar)window.FindName("BusyStageProgress")!;
        InvokePrivate(window, "SetBusy", true, "UI smoke progress phase 1", 3);
        if (busyStageText.Text != "Step 1 of 3" || busyStageProgress.Maximum != 3 || busyStageProgress.Value != 0)
            throw new InvalidOperationException("busy overlay did not show the first planned phase");
        InvokePrivate(window, "SetBusyPhase", "UI smoke progress phase 2");
        InvokePrivate(window, "UpdateBusyElapsedText");
        if (busyStageText.Text != "Step 2 of 3" || busyStageProgress.Value != 1 || !busyElapsedText.Text.Contains("Total", StringComparison.Ordinal))
            throw new InvalidOperationException("busy overlay did not advance the phase count and elapsed-time display");
        InvokePrivate(window, "SetBusy", false, null, null);
        if (window.FindName("AllowWifi") != null
            || window.FindName("AllowGateway") is not CheckBox
            || window.FindName("AllowRestartAnyAdapter") is not CheckBox
            || window.FindName("AllowMacChangeAnyAdapter") is not CheckBox)
            throw new InvalidOperationException("The invalid Wi-Fi DHCP override must be absent while other adapter safety settings remain available.");
        var owner = InvokePrivate<NetworkWorkflowLease?>(window, "TryBeginNetworkWorkflow", "UI smoke owner", false);
        if (owner == null) throw new InvalidOperationException("could not reserve a fake in-process workflow");
        var ownerToken = owner.Token;
        var completed = false;
        var failed = false;
        var closeRequested = false;
        var ownerReleased = false;
        var closed = false;
        var routeJournalWarningDismissed = false;
        var retryFailureLogged = false;
        var favoriteDialogScenario = 0;
        var favoriteDialogInteractionDone = false;
        var captureProfileCompareDialog = false;
        var profileComparisonMessage = "";
        var captureRouteCleanupDialog = false;
        var routeCleanupDialogMessage = "";
        var closeRequestedAt = DateTime.MinValue;
        Task? t01RestoreTask = null;
        DhcpSessionRecovery? t01Recovery = null;
        string? t01AdapterId = null;
        var t01RecoveryVerified = false;
        EventManager.RegisterClassHandler(typeof(FavoriteWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not FavoriteWindow favoriteDialog || favoriteDialogScenario == 0) return;
            var scenario = favoriteDialogScenario;
            favoriteDialog.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!favoriteDialog.IsVisible) return;
                favoriteDialogInteractionDone = true;
                if (scenario == 1)
                {
                    ((TextBox)favoriteDialog.FindName("NameBox")!).Text = "Canceled name";
                    favoriteDialog.Close();
                }
                else if (scenario == 2)
                {
                    ((TextBox)favoriteDialog.FindName("NameBox")!).Text = "UI smoke edited favorite";
                    ((PasswordBox)favoriteDialog.FindName("PasswordBox")!).Password = "synthetic-replacement-secret";
                    InvokePrivate(favoriteDialog, "Ok_Click", favoriteDialog, new RoutedEventArgs());
                }
                else if (scenario == 3)
                {
                    ((CheckBox)favoriteDialog.FindName("ClearPasswordBox")!).IsChecked = true;
                    InvokePrivate(favoriteDialog, "Ok_Click", favoriteDialog, new RoutedEventArgs());
                }
                else if (scenario == 4)
                {
                    InvokePrivate(favoriteDialog, "Ok_Click", favoriteDialog, new RoutedEventArgs());
                }
            }));
        }));
        EventManager.RegisterClassHandler(typeof(AppDialog), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not AppDialog dialog) return;
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!dialog.IsVisible) return;
                if (captureProfileCompareDialog)
                {
                    profileComparisonMessage = ((TextBox)dialog.FindName("MessageText")!).Text;
                    captureProfileCompareDialog = false;
                    dialog.Close();
                }
                else if (captureRouteCleanupDialog)
                {
                    routeCleanupDialogMessage = ((TextBox)dialog.FindName("MessageText")!).Text;
                    captureRouteCleanupDialog = false;
                    dialog.Close();
                }
            }));
        }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var started = DateTime.UtcNow;

        timer.Tick += (_, _) =>
        {
            if (completed) return;
            try
            {
                if (!routeJournalWarningDismissed)
                {
                    var journalWritable = (bool)(window.GetType().GetField("_routeJournalWritable", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) ?? true);
                    var warning = application.Windows.OfType<AppDialog>().FirstOrDefault(x => x.IsVisible && x.Title == "Recovery Journal Unreadable");
                    if (warning == null)
                    {
                        if (journalWritable) return;
                        throw new InvalidOperationException("unreadable route journal did not show its warning dialog");
                    }
                    if (journalWritable)
                        throw new InvalidOperationException("unreadable route journal remained writable");
                    if (File.ReadAllText(paths.StaticRouteSessionFile) != unreadableRouteJournal
                        || File.ReadAllText(paths.StaticRouteSessionFile + ".bak") != unreadableRouteBackup)
                        throw new InvalidOperationException("unreadable route journal sources were modified");
                    if (((Button)window.FindName("BtnApplyRoutes")!).IsEnabled
                        || ((Button)window.FindName("BtnClearAppliedRoutes")!).IsEnabled)
                        throw new InvalidOperationException("route apply or cleanup remained enabled with an unreadable ownership journal");
                    warning.Close();
                    routeJournalWarningDismissed = true;
                }

                if (!t01RecoveryVerified)
                {
                    if (t01RestoreTask == null)
                    {
                        t01AdapterId = "ui-smoke-missing-dhcp-adapter-" + Guid.NewGuid().ToString("N");
                        var original = new AdapterIpv4Snapshot
                        {
                            DhcpEnabled = false,
                            DnsMode = AdapterDnsMode.Automatic,
                            AdapterEnabled = true,
                            AutomaticMetric = true
                        };
                        var identity = new AdapterConfigBackup
                        {
                            AdapterId = t01AdapterId,
                            InterfaceIndex = "38",
                            AdapterName = "Missing DHCP test adapter",
                            AdapterMac = "02-11-22-33-44-77",
                            CapturedAt = DateTime.Now,
                            DhcpEnabled = original.DhcpEnabled,
                            DnsMode = original.DnsMode,
                            AdapterEnabled = original.AdapterEnabled,
                            AutomaticMetric = original.AutomaticMetric
                        };
                        t01Recovery = new DhcpSessionRecovery(identity, original);
                        t01Recovery.MarkModificationPending();
                        t01Recovery.SetExpectedSessionSnapshot(new AdapterIpv4Snapshot
                        {
                            DhcpEnabled = false,
                            DnsMode = AdapterDnsMode.Automatic,
                            AdapterEnabled = true,
                            AutomaticMetric = false,
                            InterfaceMetric = NetworkAdapterService.DhcpHostAdapterMetric,
                            Addresses = [new AdapterIpv4AddressSnapshot { IpAddress = "192.0.2.20", PrefixLength = 24 }]
                        });
                        ((List<DhcpSessionRecovery>)window.GetType().GetField("_dhcpSessionRecoveries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Add(t01Recovery);
                        ((List<AdapterConfigBackup>)window.GetType().GetField("_adapterBackups", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Add(identity);
                        t01RestoreTask = (Task)InvokePrivate(window, "RestoreDhcpSessionAsync", t01Recovery, CancellationToken.None)!;
                        return;
                    }
                    if (!t01RestoreTask.IsCompleted) return;

                    InvalidOperationException? restoreFailure = null;
                    try { t01RestoreTask.GetAwaiter().GetResult(); }
                    catch (InvalidOperationException ex) { restoreFailure = ex; }
                    if (restoreFailure == null)
                        throw new InvalidOperationException("a missing DHCP session adapter unexpectedly restored successfully");

                    var expectedMessage = InvokePrivate<string>(window, "DhcpSessionRestoreMessage", DhcpSessionRestoreFailure.AdapterUnavailable);
                    var pendingRecoveries = (List<DhcpSessionRecovery>)window.GetType().GetField("_dhcpSessionRecoveries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    var recoveryEntry = InvokePrivate<List<RecoveryEntryViewModel>>(window, "BuildRecoveryEntries")
                        .SingleOrDefault(x => x.Kind == RecoveryEntryKind.AdapterConfiguration
                            && x.Payload is AdapterConfigBackup backup
                            && string.Equals(backup.AdapterId, t01AdapterId, StringComparison.OrdinalIgnoreCase));
                    if (restoreFailure.Message != expectedMessage
                        || string.IsNullOrWhiteSpace(expectedMessage)
                        || t01Recovery?.State != DhcpSessionRestoreState.RestoreFailed
                        || t01Recovery.RequiresRestore != true
                        || !pendingRecoveries.Contains(t01Recovery)
                        || recoveryEntry == null
                        || recoveryEntry.IsAvailable
                        || !recoveryEntry.StatusDisplay.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("missing DHCP session adapter did not retain a localized pending recovery entry: " + restoreFailure.Message);

                    foreach (var reason in Enum.GetValues<DhcpSessionRestoreFailure>())
                    {
                        if (string.IsNullOrWhiteSpace(InvokePrivate<string>(window, "DhcpSessionRestoreMessage", reason)))
                            throw new InvalidOperationException("DHCP session restore failure has no localized explanation: " + reason);
                    }
                    t01RecoveryVerified = true;
                }

                var recoveryEntries = InvokePrivate<List<RecoveryEntryViewModel>>(window, "BuildRecoveryEntries");
                var firewallRecovery = recoveryEntries.SingleOrDefault(x => x.Kind == RecoveryEntryKind.DhcpFirewall)
                    ?? throw new InvalidOperationException("stale DHCP firewall lease was not shown in Recovery Center");
                if (!firewallRecovery.IsAvailable || !firewallRecovery.StatusDisplay.Contains("pending", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("pending DHCP firewall lease was not offered for exact-ownership recovery");

                var appliedRoutes = (List<AppliedStaticRoute>)window.GetType().GetField("_appliedStaticRoutes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var routeJournalWritable = window.GetType().GetField("_routeJournalWritable", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException("_routeJournalWritable");
                appliedRoutes.Add(new AppliedStaticRoute
                {
                    RuleId = "ui-smoke-missing-route-adapter",
                    DestinationPrefix = "198.18.254.0/24",
                    AdapterId = "{D9A07108-321A-4FE5-95D1-3057D315B3AE}",
                    AdapterName = "Missing route test adapter",
                    AdapterMac = "02-11-22-33-44-78",
                    InterfaceIndex = int.MaxValue,
                    NextHop = "192.0.2.254",
                    RouteMetric = 123,
                    PolicyStore = "ActiveStore",
                    InstanceId = "ui-smoke-route-instance",
                    OwnershipVerified = true
                });
                routeJournalWritable.SetValue(window, true);
                var routeCleanupFailures = new List<string>();
                var clearedRouteFixture = ((Task<bool>)InvokePrivate(window, "ClearAppliedRoutesAsync", CancellationToken.None, routeCleanupFailures)!).GetAwaiter().GetResult();
                if (clearedRouteFixture
                    || appliedRoutes.Count != 1
                    || routeCleanupFailures.Count != 1
                    || !routeCleanupFailures[0].Contains("198.18.254.0/24", StringComparison.Ordinal)
                    || !routeCleanupFailures[0].Contains("adapter identity unavailable; retained", StringComparison.Ordinal))
                    throw new InvalidOperationException("route cleanup did not preserve and describe the unavailable-adapter recovery record");
                captureRouteCleanupDialog = true;
                InvokePrivate(window, "ShowRouteCleanupFailure", "Clear Static Routes", routeCleanupFailures);
                if (captureRouteCleanupDialog
                    || !routeCleanupDialogMessage.Contains("1 route recovery record(s) remain", StringComparison.Ordinal)
                    || !routeCleanupDialogMessage.Contains("198.18.254.0/24 on Missing route test adapter", StringComparison.Ordinal)
                    || !routeCleanupDialogMessage.Contains("adapter identity unavailable; retained", StringComparison.Ordinal))
                    throw new InvalidOperationException("route cleanup dialog did not show the concrete failure and actual remaining count: " + routeCleanupDialogMessage);
                appliedRoutes.Clear();
                routeJournalWritable.SetValue(window, false);

                window.Width = 900;
                window.Height = 560;
                window.UpdateLayout();
                if (window.MinWidth > 900 || window.MinHeight > 560
                    || window.ActualWidth > 940 || window.ActualHeight > 600)
                    throw new InvalidOperationException($"main window did not honor compact bounds: min={window.MinWidth:0}x{window.MinHeight:0} actual={window.ActualWidth:0}x{window.ActualHeight:0}");

                var element = AutomationElement.FromHandle(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                var required = new[]
                {
                    "Network adapter / 网卡",
                    "Main workflows / 主要工作流",
                    "Operation feedback / 操作反馈",
                    "Last operation / 最近操作"
                };
                foreach (var name in required)
                {
                    if (element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)) == null)
                        throw new InvalidOperationException("missing automation element: " + name);
                }

                var leaseGrid = window.FindName("LeaseGrid") as DataGrid ?? throw new InvalidOperationException("missing DHCP lease grid");
                if (leaseGrid.Columns.Count != 10
                    || (leaseGrid.Columns[6] as DataGridTextColumn)?.Binding is not System.Windows.Data.Binding { Path.Path: "StatusText" }
                    || (leaseGrid.Columns[7] as DataGridTextColumn)?.Binding is not System.Windows.Data.Binding { Path.Path: "SessionText" }
                    || (leaseGrid.Columns[8] as DataGridTextColumn)?.Binding is not System.Windows.Data.Binding { Path.Path: "PingLatencyMs" })
                    throw new InvalidOperationException("DHCP lease state, session history, and Ping must be displayed in separate columns.");
                var leaseMac = "02-11-22-33-44-55";
                var oldLease = new NetBootDhcpTool.Dhcp.DhcpLease
                {
                    ClientKey = "MAC:" + leaseMac, MacAddress = leaseMac, IpAddress = "192.0.2.10", Hostname = "before",
                    SessionId = "old-session", IsActiveLease = true, IsCurrentSession = false, Status = "Assigned", PingLatencyMs = 12, HttpOk = true, HttpsOk = true,
                    Time = DateTime.Now.AddMinutes(-1), LeaseEnd = DateTime.Now.AddHours(1)
                };
                ((Task)InvokePrivate(window, "UpdateLeaseAsync", oldLease)!).GetAwaiter().GetResult();
                var renewal = new NetBootDhcpTool.Dhcp.DhcpLease
                {
                    ClientKey = "MAC:" + leaseMac, MacAddress = leaseMac, IpAddress = "192.0.2.10", Hostname = "renewed",
                    SessionId = "old-session", IsActiveLease = true, Status = "Assigned", LeaseEnd = DateTime.Now.AddHours(1)
                };
                ((Task)InvokePrivate(window, "UpdateLeaseAsync", renewal)!).GetAwaiter().GetResult();
                var release = new NetBootDhcpTool.Dhcp.DhcpLease
                {
                    ClientKey = "MAC:" + leaseMac, MacAddress = leaseMac, IpAddress = "192.0.2.10", Hostname = "renewed",
                    SessionId = "old-session", IsActiveLease = false, Status = "Released", LeaseEnd = DateTime.Now
                };
                ((Task)InvokePrivate(window, "UpdateLeaseAsync", release)!).GetAwaiter().GetResult();
                var reusedAddress = new NetBootDhcpTool.Dhcp.DhcpLease
                {
                    ClientKey = "MAC:" + leaseMac, MacAddress = leaseMac, IpAddress = "192.0.2.10", Hostname = "new-session",
                    SessionId = "new-session", IsActiveLease = true, Status = "Assigned", LeaseEnd = DateTime.Now.AddHours(1)
                };
                ((Task)InvokePrivate(window, "UpdateLeaseAsync", reusedAddress)!).GetAwaiter().GetResult();
                var leaseRows = window.Leases.Where(x => x.MacAddress == leaseMac).ToList();
                if (leaseRows.Count != 2 || leaseRows.All(x => x.Hostname != "renewed" || x.Status != "Released" || x.PingLatencyMs != -1 || x.HttpOk || x.HttpsOk)
                    || leaseRows.All(x => x.Hostname != "new-session" || x.Status != "Assigned" || x.PingLatencyMs != -1))
                    throw new InvalidOperationException("lease renewal or IP reuse merged old Ping/status state into another session: "
                        + string.Join(";", leaseRows.Select(x => $"{x.SessionId}/{x.Hostname}/{x.Status}/{x.PingLatencyMs}/current={x.IsCurrentSession}/active={x.IsActiveLease}")));
                var currentSessionMac = "02-11-22-33-44-66";
                window.GetType().GetField("_activeDhcpUiSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, "current-session");
                var currentExpired = new NetBootDhcpTool.Dhcp.DhcpLease
                {
                    ClientKey = "MAC:" + currentSessionMac, MacAddress = currentSessionMac, IpAddress = "192.0.2.10", Hostname = "current-session",
                    SessionId = "current-session", IsActiveLease = false, Status = "Expired", LeaseEnd = DateTime.Now
                };
                ((Task)InvokePrivate(window, "UpdateLeaseAsync", currentExpired)!).GetAwaiter().GetResult();
                if (!currentExpired.IsCurrentSession || !currentExpired.SessionText.Contains("Current", StringComparison.Ordinal))
                    throw new InvalidOperationException("current lease session was not presented separately from historical sessions.");
                InvokePrivate(window, "MarkLeaseSessionHistorical", "current-session");
                if (currentExpired.IsCurrentSession || !currentExpired.SessionText.Contains("History", StringComparison.Ordinal))
                    throw new InvalidOperationException("stopped lease session was not marked as history.");

                if (FindByName(element, "History") == null && FindByName(element, "Operation History") == null
                    && FindByName(element, "历史") == null && FindByName(element, "打开操作历史") == null
                    && FindByName(element, "Open operation history / 打开操作历史") == null)
                    throw new InvalidOperationException("missing history navigation button");
                if (element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Operation feedback / 操作反馈")) == null)
                    throw new InvalidOperationException("missing live feedback element");
                if (FindByName(element, "Recovery") == null && FindByName(element, "Recovery Center") == null
                    && FindByName(element, "Recovery center / 恢复中心") == null
                    && FindByName(element, "恢复") == null && FindByName(element, "恢复中心") == null)
                    throw new InvalidOperationException("missing recovery action");

                var applyScan = window.FindName("BtnApplyScan") as Button
                    ?? throw new InvalidOperationException("missing manual apply button");
                var applyFavorite = window.FindName("BtnApplyFavorite") as Button
                    ?? throw new InvalidOperationException("missing favorite apply button");
                if (applyScan.IsEnabled || applyFavorite.IsEnabled)
                    throw new InvalidOperationException("network write buttons stayed enabled while another workflow owned the gate");

                var keyCommand = window.GetType().GetMethod("HandleKeyboardCommand", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("missing keyboard command seam");
                _ = keyCommand.Invoke(window, [Key.F5, ModifierKeys.None, window]);
                _ = keyCommand.Invoke(window, [Key.R, ModifierKeys.Control, window]);
                var refreshInProgress = (bool)(window.GetType().GetField("_adapterRefreshInProgress", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) ?? false);
                if (refreshInProgress || ownerToken.IsCancellationRequested)
                    throw new InvalidOperationException("refresh shortcuts reentered or canceled the active network operation");

                var manualIp = window.FindName("ManualIp") as TextBox ?? throw new InvalidOperationException("missing manual IP field");
                manualIp.Text = "198.51.100.77";
                var manualMask = window.FindName("ManualMask") as TextBox ?? throw new InvalidOperationException("missing manual mask field");
                var manualTarget = window.FindName("ManualTargetIp") as TextBox ?? throw new InvalidOperationException("missing manual target field");
                manualIp.Text = "192.168.1.10";
                manualMask.Text = "255.255.255.0";
                manualTarget.Text = "192.168.2.5";
                var scanPlanMethod = window.GetType().GetMethod("TryGetManualScanPlan", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("missing shared manual scan validator");
                object?[] scanPlanArgs = [null, null];
                if ((bool)scanPlanMethod.Invoke(window, scanPlanArgs)! || !((string)scanPlanArgs[1]!).Contains("local subnet", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("out-of-subnet target did not receive the shared validation error");
                manualIp.Text = "10.1.2.3";
                manualMask.Text = "255.0.0.0";
                manualTarget.Text = "";
                scanPlanArgs = [null, null];
                if ((bool)scanPlanMethod.Invoke(window, scanPlanArgs)! || !((string)scanPlanArgs[1]!).Contains("4096", StringComparison.Ordinal))
                    throw new InvalidOperationException("oversized subnet did not fail scan-plan validation with the cap");
                manualTarget.Text = "not-an-ip";
                scanPlanArgs = [null, null];
                if ((bool)scanPlanMethod.Invoke(window, scanPlanArgs)! || !((string)scanPlanArgs[1]!).Contains("leave it empty", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("invalid nonempty target was not rejected as a target");
                manualIp.Text = "192.168.1.10";
                manualMask.Text = "255.255.255.0";
                manualTarget.Text = "192.168.1.20";
                scanPlanArgs = [null, null];
                if (!(bool)scanPlanMethod.Invoke(window, scanPlanArgs)! || ((ScanRangePlan)scanPlanArgs[0]!).TargetCount != 1)
                    throw new InvalidOperationException("valid explicit target did not produce exactly one probe plan");
                manualIp.Text = "198.51.100.77";
                var favorite = new FavoriteConfig { Name = "UI smoke favorite", LocalIp = "192.0.2.10", SubnetMask = "255.255.255.0" };
                window.Favorites.Add(favorite);
                (window.FindName("FavoriteGrid") as DataGrid)!.SelectedItem = favorite;
                InvokePrivate(window, "ApplyFavorite_Click", window, new RoutedEventArgs());
                if (manualIp.Text != "198.51.100.77")
                    throw new InvalidOperationException("favorite apply changed form values while the workflow gate was owned");

                var profile = new NetworkProfile { Name = "UI smoke profile", ManualIp = "203.0.113.55", ManualMask = "255.255.255.0" };
                window.Profiles.Add(profile);
                (window.FindName("ProfileGrid") as DataGrid)!.SelectedItem = profile;
                InvokePrivate(window, "LoadProfile_Click", window, new RoutedEventArgs());
                if (manualIp.Text != "198.51.100.77")
                    throw new InvalidOperationException("profile load changed form values while the workflow gate was owned");

                var recovery = new RecoveryEntryViewModel
                {
                    Kind = RecoveryEntryKind.StaticRoute,
                    Payload = new AppliedStaticRoute(),
                    TypeDisplay = "Route",
                    AdapterName = "fake",
                    IdentityDisplay = "fake",
                    CapturedAtDisplay = "now",
                    Summary = "fake route",
                    StatusDisplay = "Available",
                    IsAvailable = true
                };
                var safetyField = window.GetType().GetField("_safetyOnboardingCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!;
                safetyField.SetValue(window, false);
                var recoveryTask = (Task<bool>)window.GetType().GetMethod("ExecuteRecoveryEntryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [recovery])!;
                if (recoveryTask.GetAwaiter().GetResult() || ownerToken.IsCancellationRequested)
                    throw new InvalidOperationException("recovery execution bypassed read-only mode");
                safetyField.SetValue(window, true);
                var blockedRecovery = (Task<bool>)window.GetType().GetMethod("ExecuteRecoveryEntryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [recovery])!;
                if (blockedRecovery.GetAwaiter().GetResult() || ownerToken.IsCancellationRequested)
                    throw new InvalidOperationException("recovery execution bypassed the active workflow gate");
                var blockedFirewallRecovery = (Task<bool>)window.GetType().GetMethod("ExecuteRecoveryEntryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [firewallRecovery])!;
                if (blockedFirewallRecovery.GetAwaiter().GetResult() || ownerToken.IsCancellationRequested)
                    throw new InvalidOperationException("firewall recovery bypassed the active workflow gate");

                InvokePrivate(window, "ApplyScan_Click", window, new RoutedEventArgs());
                if (ownerToken.IsCancellationRequested)
                    throw new InvalidOperationException("manual apply canceled the current owner while being rejected");

                var retainedScanResult = new ScanResult { IpAddress = "192.168.1.20", PingOk = true, MacAddress = "02-00-00-00-00-20" };
                window.ScanResults.Add(retainedScanResult);
                var canceledScanAdapter = new NetworkAdapterInfo
                {
                    Id = Guid.NewGuid().ToString("B"),
                    Name = "UI smoke scan adapter",
                    MacAddress = retainedScanResult.MacAddress
                };
                InvokePrivate(window, "CompleteCanceledScan", canceledScanAdapter);
                var canceledScanHistory = window.OperationHistory.FirstOrDefault(x =>
                    x.Type == "Scan" && (x.Status.Contains("Cancel", StringComparison.OrdinalIgnoreCase) || x.Status.Contains("取消", StringComparison.Ordinal)));
                var scanPhase = (string)window.GetType().GetField("_scanPhase", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var scanStatus = ((TextBlock)window.FindName("ScanStatus")!).Text;
                if (canceledScanHistory is null || !canceledScanHistory.Detail.Contains("1")
                    || !scanStatus.Contains("Cancel", StringComparison.OrdinalIgnoreCase) && !scanStatus.Contains("取消", StringComparison.Ordinal)
                    || !scanPhase.Contains("Cancel", StringComparison.OrdinalIgnoreCase) && !scanPhase.Contains("取消", StringComparison.Ordinal)
                    || !window.ScanResults.Contains(retainedScanResult))
                    throw new InvalidOperationException("canceling a scan did not preserve partial results and record a canceled history item");

                var allFavorites = (List<FavoriteConfig>)window.GetType().GetField("_allFavorites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var smokeFavorite = new FavoriteConfig
                {
                    Id = "ui-smoke-edit-" + Guid.NewGuid().ToString("N"),
                    Name = "UI smoke edit source",
                    RemarkName = "preserved remark",
                    Description = "preserved description",
                    MemoryText = "preserved memory",
                    LocalIp = "192.0.2.20",
                    SubnetMask = "255.255.255.0",
                    Gateway = "192.0.2.1",
                    Dns = "192.0.2.53",
                    TargetIp = "192.0.2.21",
                    ProtectedPassword = "synthetic-unreadable-dpapi-payload",
                    PasswordUnavailable = true,
                    CreatedAt = DateTime.Now.AddDays(-10),
                    LastUsedAt = DateTime.Now.AddDays(-1),
                    CustomFields = [new FavoriteField { Name = "Rack", Value = "R7" }]
                };
                allFavorites.Add(smokeFavorite);
                InvokePrivate(window, "FilterFavorites");
                var favoriteGrid = (DataGrid)window.FindName("FavoriteGrid")!;
                favoriteGrid.SelectedItem = smokeFavorite;
                favoriteDialogScenario = 1;
                favoriteDialogInteractionDone = false;
                InvokePrivate(window, "EditFavorite_Click", window, new RoutedEventArgs());
                favoriteDialogScenario = 0;
                if (!favoriteDialogInteractionDone || !ReferenceEquals(allFavorites.Single(x => x.Id == smokeFavorite.Id), smokeFavorite)
                    || smokeFavorite.Name != "UI smoke edit source" || smokeFavorite.PasswordUnavailable != true)
                    throw new InvalidOperationException("canceling the favorite editor changed the source record");

                favoriteGrid.SelectedItem = smokeFavorite;
                favoriteDialogScenario = 2;
                favoriteDialogInteractionDone = false;
                InvokePrivate(window, "EditFavorite_Click", window, new RoutedEventArgs());
                favoriteDialogScenario = 0;
                var reentered = allFavorites.Single(x => x.Id == smokeFavorite.Id);
                var savedReentered = FavoriteStore.Load(paths.FavoritesFile).Single(x => x.Id == smokeFavorite.Id);
                var savedJson = File.ReadAllText(paths.FavoritesFile);
                if (!favoriteDialogInteractionDone || ReferenceEquals(reentered, smokeFavorite)
                    || reentered.Id != smokeFavorite.Id || reentered.CreatedAt != smokeFavorite.CreatedAt || reentered.LastUsedAt != smokeFavorite.LastUsedAt
                    || reentered.Gateway != smokeFavorite.Gateway || reentered.Dns != smokeFavorite.Dns || reentered.Description != smokeFavorite.Description
                    || reentered.RemarkName != smokeFavorite.RemarkName || reentered.CustomFields.Single().Value != "R7"
                    || savedReentered.Password != "synthetic-replacement-secret" || savedReentered.PasswordUnavailable
                    || savedJson.Contains("synthetic-replacement-secret", StringComparison.Ordinal))
                    throw new InvalidOperationException("editing did not preserve untouched fields and history or re-protect the replacement credential");

                favoriteGrid.SelectedItem = reentered;
                favoriteDialogScenario = 3;
                favoriteDialogInteractionDone = false;
                InvokePrivate(window, "EditFavorite_Click", window, new RoutedEventArgs());
                favoriteDialogScenario = 0;
                var cleared = FavoriteStore.Load(paths.FavoritesFile).Single(x => x.Id == smokeFavorite.Id);
                var clearedJson = File.ReadAllText(paths.FavoritesFile);
                using (var savedDocument = System.Text.Json.JsonDocument.Parse(clearedJson))
                {
                    var saved = savedDocument.RootElement.EnumerateArray().Single(x => x.GetProperty("Id").GetString() == smokeFavorite.Id);
                    if (!favoriteDialogInteractionDone || cleared.Password != "" || cleared.PasswordUnavailable
                        || saved.GetProperty("ProtectedPassword").GetString() != "" || saved.GetProperty("PublicPassword").GetString() != ""
                        || clearedJson.Contains("synthetic-replacement-secret", StringComparison.Ordinal))
                        throw new InvalidOperationException("explicit saved-password clearing did not remove the local credential payload");
                }

                var publicPreset = allFavorites.FirstOrDefault(x => x.IsPublicDefault)
                    ?? throw new InvalidOperationException("the isolated favorites did not include a published default preset");
                var publicPassword = publicPreset.Password;
                favoriteGrid.SelectedItem = publicPreset;
                favoriteDialogScenario = 4;
                favoriteDialogInteractionDone = false;
                InvokePrivate(window, "EditFavorite_Click", window, new RoutedEventArgs());
                favoriteDialogScenario = 0;
                var publicAfterEdit = allFavorites.Single(x => x.Id == publicPreset.Id);
                using (var savedDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(paths.FavoritesFile)))
                {
                    var saved = savedDocument.RootElement.EnumerateArray().Single(x => x.GetProperty("Id").GetString() == publicPreset.Id);
                    if (!favoriteDialogInteractionDone || !publicAfterEdit.IsPublicDefault || publicAfterEdit.Password != publicPassword
                        || saved.GetProperty("PublicPassword").GetString() != publicPassword
                        || saved.GetProperty("ProtectedPassword").GetString() != "")
                        throw new InvalidOperationException("editing unrelated fields downgraded an unchanged published default credential");
                }

                var profileForLoad = new NetworkProfile
                {
                    Id = "ui-smoke-profile-contract",
                    Name = "UI smoke profile contract",
                    Dhcp = new DefaultDhcpSettings
                    {
                        ServerIp = "192.168.10.2", SubnetMask = "255.255.255.0", PoolStart = "192.168.10.50",
                        PoolEnd = "192.168.10.100", Gateway = "192.168.10.1", Dns = "192.168.10.53", LeaseSeconds = 3600
                    },
                    ManualIp = "192.168.10.2", ManualMask = "255.255.255.0", ManualTargetIp = "192.168.10.20"
                };
                InvokePrivate(window, "LoadProfileIntoForms", profileForLoad);
                if (((TextBlock)window.FindName("LblDhcpGateway")!).Visibility != Visibility.Visible
                    || ((TextBox)window.FindName("DhcpGateway")!).Visibility != Visibility.Visible
                    || ((Button)window.FindName("BtnShowDhcpGateway")!).Visibility != Visibility.Collapsed
                    || ((TextBlock)window.FindName("LblDhcpDns")!).Visibility != Visibility.Visible
                    || ((TextBox)window.FindName("DhcpDns")!).Visibility != Visibility.Visible
                    || ((Button)window.FindName("BtnShowDhcpDns")!).Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("loading non-empty optional DHCP fields did not expose both labels and inputs consistently");
                var emptyOptionalProfile = new NetworkProfile { Name = "Empty optional fields", Dhcp = new DefaultDhcpSettings() };
                InvokePrivate(window, "LoadProfileIntoForms", emptyOptionalProfile);
                if (((TextBox)window.FindName("DhcpGateway")!).Text != ""
                    || ((TextBox)window.FindName("DhcpDns")!).Text != ""
                    || ((TextBox)window.FindName("DhcpGateway")!).Visibility != Visibility.Collapsed
                    || ((Button)window.FindName("BtnShowDhcpGateway")!).Visibility != Visibility.Visible
                    || ((TextBox)window.FindName("DhcpDns")!).Visibility != Visibility.Collapsed
                    || ((Button)window.FindName("BtnShowDhcpDns")!).Visibility != Visibility.Visible)
                    throw new InvalidOperationException("loading empty optional DHCP fields retained hidden values or inconsistent add buttons");

                InvokePrivate(window, "LoadProfileIntoForms", profileForLoad);
                var allProfiles = (List<NetworkProfile>)window.GetType().GetField("_allProfiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                allProfiles.Add(profileForLoad);
                InvokePrivate(window, "RefreshProfilesView", profileForLoad);
                ((DataGrid)window.FindName("ProfileGrid")!).SelectedItem = profileForLoad;
                ((TextBox)window.FindName("DhcpGateway")!).Text = "192.168.10.3";
                captureProfileCompareDialog = true;
                InvokePrivate(window, "CompareProfile_Click", window, new RoutedEventArgs());
                if (captureProfileCompareDialog || !profileComparisonMessage.Contains("DHCP gateway", StringComparison.Ordinal)
                    || !profileComparisonMessage.Contains("192.168.10.1", StringComparison.Ordinal)
                    || !profileComparisonMessage.Contains("192.168.10.3", StringComparison.Ordinal)
                    || profileComparisonMessage.Contains("no differences found", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("profile comparison did not report the changed DHCP gateway value");

                completed = true;
                closeRequested = true;
                closeRequestedAt = DateTime.UtcNow;
                window.Close();
                return;
            }
            catch (Exception ex) when ((DateTime.UtcNow - started).TotalSeconds < 15)
            {
                // Adapter enumeration may take a few seconds on a disconnected host.
                if (!retryFailureLogged)
                {
                    Console.Error.WriteLine("UI_SMOKE_RETRY: " + ex.Message);
                    retryFailureLogged = true;
                }
            }
            catch (Exception ex)
            {
                failed = true;
                completed = true;
                timer.Stop();
                Console.Error.WriteLine("UI_SMOKE_FAILED: " + ex.Message);
                window.Close();
                application.Shutdown();
            }
        };
        window.Closed += (_, _) =>
        {
            closed = true;
            if (!completed || !ownerReleased)
            {
                failed = true;
                timer.Stop();
                Console.Error.WriteLine("UI_SMOKE_FAILED: window closed before its active operation released.");
            }
            Console.WriteLine("UI_SMOKE_OK: non-admin WPF window, DHCP session restore fail-closed/unavailable recovery retention, static route cleanup failure detail/count, recovery journal safety, lease state/session/Ping separation and IP reuse, bounded scan-range validation, canceled-scan partial-result history, operation gate, refresh shortcuts, favorite cancel/re-entry/clear, profile DHCP field visibility and comparison, recovery handlers, and close wait verified.");
            timer.Stop();
            application.Shutdown();
        };
        timer.Tick += (_, _) =>
        {
            if (!closeRequested || ownerReleased || closed) return;
            if (DateTime.UtcNow - closeRequestedAt > TimeSpan.FromSeconds(15))
            {
                failed = true;
                owner.Dispose();
                ownerReleased = true;
                window.Close();
                Console.Error.WriteLine("UI_SMOKE_FAILED: close did not complete after releasing the fake operation.");
                return;
            }
            if (!ownerToken.IsCancellationRequested || window.IsEnabled || owner.Completion.IsCompleted)
            {
                failed = true;
                Console.Error.WriteLine("UI_SMOKE_FAILED: close did not cancel and wait for the active operation.");
            }
            InvokePrivate(window, "EndNetworkWorkflow", owner);
            ownerReleased = true;
        };
        window.Show();
        timer.Start();
        application.Run();
        return failed ? 1 : 0;
    }

    private static AutomationElement? FindByName(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

    private static object? InvokePrivate(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, arguments);

    private static T InvokePrivate<T>(object target, string name, params object?[] arguments) =>
        (T)(InvokePrivate(target, name, arguments) ?? throw new InvalidOperationException("private UI test operation returned null: " + name));
}
