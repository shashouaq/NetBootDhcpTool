using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.App;

public partial class App : Application
{
    public AppPaths Paths { get; private set; } = null!;
    public FileLogger? Logger { get; private set; }
    private SingleInstanceLease? _instanceLease;
    private UpdateStartupHealth? _updateStartupHealth;

    protected override void OnStartup(StartupEventArgs e)
    {
        var startupStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();
        var paths = new AppPaths(AppContext.BaseDirectory);
        try
        {
            _updateStartupHealth = ParseUpdateStartupHealth(e.Args, paths);
            if (_updateStartupHealth is not null)
                UpdatePackageApplier.VerifyInstalledFilesAsync(paths.BaseDirectory, _updateStartupHealth.TargetVersion).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"新版启动文件校验失败，更新器将尝试恢复旧版本。\nThe new version failed startup file verification; the updater will attempt to restore the previous version.\n\n{ex.Message}",
                "NetBoot DHCP Tool Update",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }
        try
        {
            _instanceLease = SingleInstanceLease.TryAcquire(paths);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法锁定应用数据目录，程序不会继续运行。\nUnable to lock the application data directory; the application will not continue.\n\n{ex.Message}", "NetBoot DHCP Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        if (_instanceLease is null)
        {
            MessageBox.Show("此数据目录已有 NetBoot DHCP Tool 实例运行。请切换到已打开的窗口。\nAnother NetBoot DHCP Tool instance already owns this data directory. Switch to the open window.", "NetBoot DHCP Tool", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var instanceLeaseElapsedMs = phaseStopwatch.ElapsedMilliseconds;

        Paths = paths;
        phaseStopwatch.Restart();
        FileLogger logger;
        try
        {
            logger = new FileLogger(paths);
            Logger = logger;
            Defaults.EnsureFiles(paths, logger);
        }
        catch (Exception ex)
        {
            _instanceLease.Dispose();
            _instanceLease = null;
            MessageBox.Show($"无法初始化应用数据目录。\nUnable to initialize the application data directory.\n\n{ex.Message}", "NetBoot DHCP Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "NetBoot DHCP Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args2) => logger.Error("Unhandled exception", args2.ExceptionObject as Exception);
        logger.Info($"Log session started: file={logger.SessionLogPath}");
        logger.Info("Application start");
        if (_updateStartupHealth is not null) logger.Info($"Update startup inventory verified: version={_updateStartupHealth.TargetVersion}");
        logger.Info($"Application startup phase completed: phase=single-instance-lease elapsedMs={instanceLeaseElapsedMs}");
        logger.Info($"Application startup phase completed: phase=data-directory-and-default-files elapsedMs={phaseStopwatch.ElapsedMilliseconds}");
        SessionEnding += (_, args) => logger.Info($"Windows session ending notification: reason={args.ReasonSessionEnding} applicationUptimeMs={startupStopwatch.ElapsedMilliseconds}");
        var isAdmin = IsAdministrator();
        logger.Info("Administrator=" + isAdmin);
        if (!isAdmin)
        {
            logger.Error("Administrator privilege unavailable", null);
            MessageBox.Show("无法获取管理员权限，部分网卡配置和 DHCP 功能不能使用。\nAdministrator privilege is unavailable. Adapter configuration and DHCP may not work.", "NetBoot DHCP Tool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        base.OnStartup(e);
        phaseStopwatch.Restart();
        var mainWindow = new MainWindow(paths, logger);
        logger.Info($"Application startup phase completed: phase=main-window-construction elapsedMs={phaseStopwatch.ElapsedMilliseconds}");
        var firstContentRendered = false;
        mainWindow.ContentRendered += (_, _) =>
        {
            if (firstContentRendered) return;
            firstContentRendered = true;
            logger.Info($"Application startup phase completed: phase=first-content-rendered elapsedMs={startupStopwatch.ElapsedMilliseconds}");
            if (_updateStartupHealth is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_updateStartupHealth.HealthFilePath)!);
                    File.WriteAllText(_updateStartupHealth.HealthFilePath, _updateStartupHealth.HealthToken, new UTF8Encoding(false));
                    logger.Info($"Update startup health accepted: version={_updateStartupHealth.TargetVersion}");
                }
                catch (Exception ex) { logger.Error("Could not write update startup health confirmation", ex); }
            }
            if (_updateStartupHealth is null && UpdateTestEnvironment.IsActive
                && Environment.GetEnvironmentVariable("NETBOOT_TEST_AUTOMATIC_UPDATE") == "1")
                _ = mainWindow.RunAutomaticUpdateForIntegrationAsync();
        };
        MainWindow = mainWindow;
        mainWindow.Show();
        logger.Info($"Application startup phase completed: phase=window-show-requested elapsedMs={startupStopwatch.ElapsedMilliseconds}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Logger?.Info("Application exit");
        }
        catch { }
        try { Logger?.Dispose(); } catch { }
        base.OnExit(e);
        _instanceLease?.Dispose();
        _instanceLease = null;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static UpdateStartupHealth? ParseUpdateStartupHealth(string[] args, AppPaths paths)
    {
        if (!args.Any(argument => argument.StartsWith("--update-", StringComparison.Ordinal))) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--update-", StringComparison.Ordinal) || index + 1 >= args.Length || !values.TryAdd(key, args[++index]))
                throw new InvalidDataException("Update startup arguments are invalid.");
        }
        if (values.Count != 3 || !values.TryGetValue("--update-health-file", out var healthPath)
            || !values.TryGetValue("--update-health-token", out var healthToken)
            || !values.TryGetValue("--update-target-version", out var targetVersion)
            || !VersionUpdateService.TryParseVersion(targetVersion, out var expectedVersion)
            || expectedVersion.ToString(3) != targetVersion
            || expectedVersion.ToString(3) != (typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0)).ToString(3))
            throw new InvalidDataException("Update startup arguments do not match this application version.");
        if (healthToken.Length != 64 || !healthToken.All(Uri.IsHexDigit))
            throw new InvalidDataException("Update startup token is invalid.");
        var updatesRoot = Path.GetFullPath(Path.Combine(paths.DataDirectory, "updates", "health"));
        var fullHealthPath = Path.GetFullPath(healthPath);
        if (!fullHealthPath.StartsWith(updatesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullHealthPath).EndsWith(".health", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update startup health path is outside the application update directory.");
        return new UpdateStartupHealth(fullHealthPath, healthToken, targetVersion);
    }

    private sealed record UpdateStartupHealth(string HealthFilePath, string HealthToken, string TargetVersion);
}
