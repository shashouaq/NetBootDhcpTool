using System.Security.Principal;
using System.Windows;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var paths = new AppPaths(AppContext.BaseDirectory);
        paths.Ensure();
        Defaults.EnsureFiles(paths);
        var logger = new FileLogger(paths);
        DispatcherUnhandledException += (_, args) =>
        {
            logger.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "NetBoot DHCP Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args2) => logger.Error("Unhandled exception", args2.ExceptionObject as Exception);
        logger.Info("Application start");
        var isAdmin = IsAdministrator();
        logger.Info("Administrator=" + isAdmin);
        if (!isAdmin)
        {
            logger.Error("Administrator privilege unavailable", null);
            MessageBox.Show("无法获取管理员权限，部分网卡配置和 DHCP 功能不能使用。\nAdministrator privilege is unavailable. Adapter configuration and DHCP may not work.", "NetBoot DHCP Tool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            new FileLogger(new AppPaths(AppContext.BaseDirectory)).Info("Application exit");
        }
        catch { }
        base.OnExit(e);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
