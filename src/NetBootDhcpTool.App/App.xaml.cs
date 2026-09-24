using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.App;

public partial class App : Application
{
    public AppPaths Paths { get; private set; } = null!;
    public FileLogger? Logger { get; private set; }
    private SingleInstanceLease? _instanceLease;

    protected override void OnStartup(StartupEventArgs e)
    {
        var startupStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();
        var paths = new AppPaths(AppContext.BaseDirectory);
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
        base.OnExit(e);
        _instanceLease?.Dispose();
        _instanceLease = null;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
