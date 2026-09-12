using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using NetBootDhcpTool.App;
using NetBootDhcpTool.Core;

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
        Defaults.EnsureFiles(paths);
        var settings = JsonStore.LoadOrDefault(paths.SettingsFile, new AppSettings());
        settings.SafetyOnboardingCompleted = true;
        settings.Language = "en-US";
        JsonStore.Save(paths.SettingsFile, settings);

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        var completed = false;
        var failed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        var started = DateTime.UtcNow;

        timer.Tick += (_, _) =>
        {
            if (completed) return;
            try
            {
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

                Console.WriteLine("UI_SMOKE_OK: non-admin WPF window at compact bounds, automation names, recovery action, and persistent-history action verified.");
                completed = true;
                timer.Stop();
                window.Close();
                application.Shutdown();
            }
            catch (Exception) when ((DateTime.UtcNow - started).TotalSeconds < 15)
            {
                // Adapter enumeration may take a few seconds on a disconnected host.
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
            if (!completed)
            {
                failed = true;
                completed = true;
                timer.Stop();
                Console.Error.WriteLine("UI_SMOKE_FAILED: main window closed before checks completed.");
                application.Shutdown();
            }
        };

        window.Show();
        timer.Start();
        application.Run();
        return failed ? 1 : 0;
    }

    private static AutomationElement? FindByName(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
}
