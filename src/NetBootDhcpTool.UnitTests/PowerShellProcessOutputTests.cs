using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class PowerShellProcessOutputTests
{
    [TestMethod]
    public async Task ReadsLargeStandardOutputAndErrorConcurrently()
    {
        const int characterCount = 200_000;
        var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.IsTrue(File.Exists(powerShell), "The Windows PowerShell executable is required for this Windows-targeted test.");
        var script = $"[Console]::Error.Write([string]::new([char]69, {characterCount})); [Console]::Out.Write([string]::new([char]79, {characterCount}))";
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(10));
        var output = await runner.RunAsync(script, "large output regression", CancellationToken.None, logOutput: false);
        Assert.AreEqual(characterCount, output.Length);
        Assert.IsTrue(output.All(x => x == 'O'));
    }

    [TestMethod]
    public async Task ReadsChinesePowerShellOutputAsUtf8()
    {
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(5));

        var output = await runner.RunAsync("Write-Output '网卡路由读取完成'", "Chinese output regression", CancellationToken.None, logOutput: false);

        Assert.AreEqual("网卡路由读取完成", output);
    }

    [TestMethod]
    public async Task ReturnsEmptyOutputForAnEmptyPowerShellScript()
    {
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(5));

        var output = await runner.RunAsync("& { }", "empty output regression", CancellationToken.None, logOutput: false);

        Assert.AreEqual(string.Empty, output);
    }

    [TestMethod]
    public async Task CallerCancellationStopsAndReapsPowerShell()
    {
        var processId = 0;
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(10), processStarted: id => processId = id);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            runner.RunAsync("Start-Sleep -Seconds 30", "cancellation regression", cancellation.Token, logOutput: false));

        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsTrue(processId > 0, "The cancellation scenario must first start a real PowerShell process.");
        Assert.IsTrue(WaitForExit(processId, TimeSpan.FromSeconds(3)), "The PowerShell process remained alive after cancellation.");
    }

    [TestMethod]
    public async Task ExecutionTimeoutIsReportedSeparatelyAndStopsProcess()
    {
        var processId = 0;
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromMilliseconds(250), processStarted: id => processId = id);

        var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            runner.RunAsync("Start-Sleep -Seconds 30", "timeout regression", CancellationToken.None, logOutput: false));

        StringAssert.Contains(error.Message, "timeout regression");
        Assert.IsTrue(processId > 0, "The timeout scenario must first start a real PowerShell process.");
        Assert.IsTrue(WaitForExit(processId, TimeSpan.FromSeconds(3)), "The PowerShell process remained alive after its timeout.");
    }

    [TestMethod]
    public async Task PreCanceledTokenDoesNotStartPowerShell()
    {
        var processStarted = false;
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(1), processStarted: _ => processStarted = true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            runner.RunAsync("'must not run'", "pre-canceled regression", cancellation.Token, logOutput: false));

        Assert.IsFalse(processStarted);
    }

    [TestMethod]
    public async Task NonzeroExitPreservesPowerShellError()
    {
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            runner.RunAsync("throw 'intentional runner error: 中文错误'", "failure regression", CancellationToken.None, logOutput: true));

        StringAssert.Contains(error.Message, "intentional runner error");
        StringAssert.Contains(error.Message, "中文错误");
    }

    private static bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class TestLogger : ILogger
    {
        public event Action<string>? LineWritten { add { } remove { } }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
