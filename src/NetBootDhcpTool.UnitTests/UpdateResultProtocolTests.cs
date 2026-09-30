using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class UpdateResultProtocolTests
{
    [TestMethod]
    public async Task HungUpdaterHasBoundedWaitAndIsNotKilledMidTransaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-result-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var apply = new UpdateApplyRequest(root, "", "", "1.0.20", "1.1.0", "Full", "", "", "", "timeout1");
        var request = new UpdateProcessRequest(apply, 1, root, Path.Combine(root, "request.json"),
            Path.Combine(root, "accepted"), Path.Combine(root, "health"), "", Path.Combine(root, "update.log"));
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        using var process = Process.Start(start)!;
        try
        {
            Assert.AreEqual(InstallExitCode.TimedOut, await UpdateResultProtocol.WaitForFinalResultAsync(request, process, TimeSpan.FromMilliseconds(100)));
            Assert.IsFalse(process.HasExited, "The result waiter must preserve a running transaction for journal recovery.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(InstallExitCode.Success, "completed")]
    [DataRow(InstallExitCode.RolledBack, "rolled-back")]
    [DataRow(InstallExitCode.RollbackFailed, "rollback-failed")]
    public async Task FinalExitRequiresMatchingTransactionAndProcessCode(InstallExitCode code, string phase)
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var apply = new UpdateApplyRequest(root, "", "", "1.0.20", "1.1.0", "Full", "", "", "", "request1");
            var request = new UpdateProcessRequest(apply, 1, root, Path.Combine(root, "request.json"),
                Path.Combine(root, "accepted"), Path.Combine(root, "health"), "", Path.Combine(root, "update.log"));
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("exit"); start.ArgumentList.Add(((int)code).ToString());
            using var process = Process.Start(start)!;
            UpdateResultProtocol.Write(request, new UpdateTransactionStatus("request1", "1.0.20", "1.1.0", phase, "", DateTimeOffset.UtcNow, code));
            Assert.AreEqual(code, await UpdateResultProtocol.WaitForFinalResultAsync(request, process));
            UpdateResultProtocol.Write(request, new UpdateTransactionStatus("another-request", "1.0.20", "1.1.0", phase, "", DateTimeOffset.UtcNow, code));
            Assert.AreEqual(InstallExitCode.UpdaterTerminated, await UpdateResultProtocol.WaitForFinalResultAsync(request, process));
            UpdateResultProtocol.Write(request, new UpdateTransactionStatus("request1", "1.0.20", "1.1.0", "applying", "", DateTimeOffset.UtcNow));
            Assert.AreEqual(InstallExitCode.UpdaterTerminated, await UpdateResultProtocol.WaitForFinalResultAsync(request, process));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void FaultTestsCannotFallBackToProductionDataOrUnmarkedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldRoot = Environment.GetEnvironmentVariable("NETBOOT_TEST_ROOT");
        var oldData = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("NETBOOT_TEST_ROOT", root);
            Assert.ThrowsExactly<InvalidOperationException>(() => UpdateTestEnvironment.RequirePath(Path.Combine(root, "install")));
            File.WriteAllText(Path.Combine(root, UpdateTestEnvironment.MarkerName), "NetBootDhcpTool isolated integration test v1");
            var install = Path.Combine(root, "install");
            UpdateTestEnvironment.RequirePath(install);
            var actualData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBootDhcpTool");
            Assert.ThrowsExactly<InvalidOperationException>(() => UpdateTestEnvironment.RequirePath(actualData));
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", actualData);
            Assert.ThrowsExactly<InvalidOperationException>(() => new AppPaths(install));
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", null);
            var paths = new AppPaths(install);
            Assert.AreEqual(Path.Combine(root, "user-data"), paths.DataDirectory);
            Assert.IsTrue(UpdateTestEnvironment.TemporaryDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETBOOT_TEST_ROOT", oldRoot);
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", oldData);
            Directory.Delete(root, recursive: true);
        }
    }
}
