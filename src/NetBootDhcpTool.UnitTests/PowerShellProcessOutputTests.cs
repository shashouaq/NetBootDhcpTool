using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"[Console]::Error.Write([string]::new([char]69, {characterCount})); [Console]::Out.Write([string]::new([char]79, {characterCount}))");

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (output, error) = await PowerShellProcessOutput.ReadStandardStreamsAsync(process, timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        Assert.AreEqual(0, process.ExitCode);
        Assert.AreEqual(characterCount, output.Length);
        Assert.AreEqual(characterCount, error.Length);
        Assert.IsTrue(output.All(x => x == 'O'));
        Assert.IsTrue(error.All(x => x == 'E'));
    }
}
