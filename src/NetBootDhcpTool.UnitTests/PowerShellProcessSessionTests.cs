using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class PowerShellProcessSessionTests
{
    [TestMethod]
    public async Task DhcpLifetimeRetainsWarmProcessUntilStopScopeAndClosesFailedStart()
    {
        var pids = new List<int>();
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(20), processStarted: pids.Add);
        await using (runner.BeginSession(retainForDhcp: true))
            Assert.AreEqual("started", await runner.RunAsync("'started'", "DHCP start", CancellationToken.None, false));
        using (var warm = Process.GetProcessById(pids.Single())) Assert.IsFalse(warm.HasExited);
        await using (runner.BeginSession())
            Assert.AreEqual("stopped", await runner.RunAsync("'stopped'", "DHCP stop", CancellationToken.None, false));
        Assert.AreEqual(1, pids.Count);
        AssertStopped(pids.Single());
        await using (runner.BeginSession(retainForDhcp: true)) { }
        await runner.EndRetainedSessionAsync();
        Assert.AreEqual(2, pids.Count);
        foreach (var pid in pids) AssertStopped(pid);
    }

    [TestMethod]
    public async Task SessionReusesProcessButIsolatesVariablesAndDrainsLargeErrorStream()
    {
        var pids = new List<int>();
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(20), processStarted: pids.Add);
        var scope = runner.BeginSession();
        try
        {
            Assert.AreEqual("第一次网卡读取", await runner.RunAsync("$privateValue=42; '第一次网卡读取'", "session first", CancellationToken.None, false));
            Assert.AreEqual("clean", await runner.RunAsync("if ($null -ne $privateValue) { throw 'variable leaked' }; 'clean'", "scope isolation", CancellationToken.None, false));
            Assert.AreEqual(string.Empty, await runner.RunAsync("& { }", "empty output", CancellationToken.None, false));
            await using (runner.BeginSession())
                Assert.AreEqual("nested", await runner.RunAsync("'nested'", "nested scope", CancellationToken.None, false));
            var output = await runner.RunAsync("[Console]::Error.Write([string]::new([char]69,200000)); Write-Output ([string]::new([char]79,200000))", "large streams", CancellationToken.None, false);
            Assert.AreEqual(200000, output.Length);
            Assert.AreEqual(1, pids.Count, "Sequential requests must share one child process.");
        }
        finally { await scope.DisposeAsync(); }
        AssertStopped(pids.Single());
    }

    [TestMethod]
    public async Task ScriptErrorIsPreservedAndCompensationGetsFreshProcessWithoutReplay()
    {
        var pids = new List<int>();
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(20), processStarted: pids.Add);
        await using var scope = runner.BeginSession();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            runner.RunAsync("throw 'session intentional 中文错误'", "failed write", CancellationToken.None, false));
        StringAssert.Contains(error.Message, "session intentional 中文错误");
        AssertStopped(pids.Single());
        Assert.AreEqual("compensated", await runner.RunAsync("'compensated'", "independent compensation", CancellationToken.None, false));
        Assert.AreEqual(2, pids.Count);
        await scope.DisposeAsync();
        foreach (var pid in pids) AssertStopped(pid);
    }

    [TestMethod]
    public async Task CancellationStopsSessionAndPreservesCallerToken()
    {
        var pids = new List<int>();
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(20), processStarted: pids.Add);
        await using var scope = runner.BeginSession();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            runner.RunAsync("Start-Sleep -Seconds 30", "session cancellation", cancel.Token, false));
        Assert.AreEqual(cancel.Token, error.CancellationToken);
        Assert.AreEqual(1, pids.Count);
        AssertStopped(pids.Single());
    }

    [TestMethod]
    public async Task DeadlineStopsSessionAndReportsTimeout()
    {
        var pids = new List<int>();
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromMilliseconds(250), processStarted: pids.Add);
        await using var scope = runner.BeginSession();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            runner.RunAsync("Start-Sleep -Seconds 30", "session timeout", CancellationToken.None, false));
        Assert.AreEqual(1, pids.Count);
        AssertStopped(pids.Single());
    }

    [TestMethod]
    [DataRow("exit 0")]
    [DataRow("[Console]::Out.WriteLine('broken frame')")]
    public async Task UnexpectedWorkerExitFailsWithoutReplayAndDisposesIdempotently(string script)
    {
        var pids = new List<int>();
        var runner = new PowerShellProcessRunner(new TestLogger(), timeout: TimeSpan.FromSeconds(20), processStarted: pids.Add);
        var scope = runner.BeginSession();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            runner.RunAsync(script, "unexpected exit", CancellationToken.None, false));
        Assert.AreEqual(1, pids.Count);
        AssertStopped(pids.Single());
        await scope.DisposeAsync();
        await scope.DisposeAsync();
    }

    [TestMethod]
    public async Task PreCanceledRequestDoesNotStartAWorker()
    {
        var starts = 0;
        var runner = new PowerShellProcessRunner(new TestLogger(), processStarted: _ => starts++);
        await using var scope = runner.BeginSession();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            runner.RunAsync("throw 'must not execute'", "pre-cancel", cancel.Token, false));
        Assert.AreEqual(0, starts);
    }

    private static void AssertStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.IsTrue(process.WaitForExit(3000), $"Session process {pid} remains alive.");
        }
        catch (ArgumentException) { }
    }

    private sealed class TestLogger : ILogger
    {
        public event Action<string>? LineWritten { add { } remove { } }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
