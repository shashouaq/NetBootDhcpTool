using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NetBootDhcpTool.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class SingleInstanceLeaseTests
{
    [TestMethod]
    public void DataDirectoryAliasesShareOneIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-instance-path-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "shared-data");
        var equivalent = Path.Combine(root, ".", "shared-data") + Path.DirectorySeparatorChar;
        var previous = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");

        try
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", original);
            var first = new AppPaths(AppContext.BaseDirectory);
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", equivalent.ToUpperInvariant());
            var second = new AppPaths(AppContext.BaseDirectory);

            Assert.AreEqual(first.DataDirectoryIdentity, second.DataDirectoryIdentity);
            Assert.IsTrue(string.Equals(first.DataDirectory, second.DataDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(Directory.Exists(original), "Constructing paths must not create or migrate data before lock ownership.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LeaseMayBeReleasedOnAnotherThreadAfterAsyncWork()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "netboot-instance-thread-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");

        try
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", dataDirectory);
            var paths = new AppPaths(AppContext.BaseDirectory);
            var owner = SingleInstanceLease.TryAcquire(paths);
            Assert.IsNotNull(owner);
            Assert.IsNull(SingleInstanceLease.TryAcquire(paths), "A second owner must not enter while the lease is held.");

            await Task.Run(owner.Dispose);

            using var successor = SingleInstanceLease.TryAcquire(paths);
            Assert.IsNotNull(successor, "A different thread must be able to release the cross-process lease after an await.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", previous);
        }
    }

    [TestMethod]
    public void NewSemaphoreLeaseDoesNotCollideWithLegacyMutexDuringBridge()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "netboot-instance-bridge-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");

        try
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", dataDirectory);
            var paths = new AppPaths(AppContext.BaseDirectory);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDirectoryIdentity)));
            using var legacyMutex = new Mutex(initiallyOwned: false, name: $"Global\\NetBootDhcpTool-{digest}");
            Assert.IsTrue(legacyMutex.WaitOne(TimeSpan.Zero));

            using var lease = SingleInstanceLease.TryAcquire(paths);
            Assert.IsNotNull(lease, "The Bridge lease must use a distinct kernel-object name from the legacy Mutex.");
            Assert.IsNull(SingleInstanceLease.TryAcquire(paths), "New App and updater processes must still coordinate on one lease.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", previous);
        }
    }

    [TestMethod]
    public async Task SeparateProcessesPreventCompetingWritesAndAllowTakeoverAfterExit()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-instance-process-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "shared-data");
        var aliasDirectory = Path.Combine(root, ".", "shared-data");
        var effectsFile = Path.Combine(root, "side-effects.txt");
        var firstMarker = Path.Combine(root, "first.marker");
        var secondMarker = Path.Combine(root, "second.marker");
        var thirdMarker = Path.Combine(root, "third.marker");
        var crashMarker = Path.Combine(root, "crash.marker");
        var takeoverMarker = Path.Combine(root, "takeover.marker");
        var alternateProbeDirectory = Path.Combine(root, "alternate-app");
        var children = new List<Process>();

        try
        {
            Directory.CreateDirectory(root);
            CopyProbeToDirectory(alternateProbeDirectory);
            var first = StartProbe(dataDirectory, firstMarker, effectsFile, holdMilliseconds: 4000);
            children.Add(first);
            await WaitForMarkerAsync(firstMarker, "OWNED", TimeSpan.FromSeconds(10));
            var dataBeforeCompetitor = HashDirectory(dataDirectory);
            var recoveryRecord = File.ReadAllText(Path.Combine(dataDirectory, "config", "static-route-session.json"));

            var second = StartProbe(aliasDirectory, secondMarker, effectsFile, holdMilliseconds: 0, probeDirectory: alternateProbeDirectory);
            children.Add(second);
            await WaitForMarkerAsync(secondMarker, "BUSY", TimeSpan.FromSeconds(10));
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(0, second.ExitCode);
            Assert.AreEqual(1, File.ReadAllLines(effectsFile).Length, "The losing process must not perform the protected side effect.");
            Assert.IsTrue(Directory.Exists(dataDirectory), "The lock owner may create its data after ownership is acquired.");
            Assert.AreEqual(dataBeforeCompetitor, HashDirectory(dataDirectory), "The losing process must not migrate, default, save, or clean up shared files.");

            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, first.ExitCode);

            using var third = StartProbe(dataDirectory, thirdMarker, effectsFile, holdMilliseconds: 0);
            children.Remove(third);
            await WaitForMarkerAsync(thirdMarker, "OWNED", TimeSpan.FromSeconds(10));
            await third.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, third.ExitCode);
            Assert.AreEqual(2, File.ReadAllLines(effectsFile).Length, "A later process may acquire the released owner lease.");

            var crashedOwner = StartProbe(dataDirectory, crashMarker, effectsFile, holdMilliseconds: 30000);
            children.Add(crashedOwner);
            await WaitForMarkerAsync(crashMarker, "OWNED", TimeSpan.FromSeconds(10));
            crashedOwner.Kill(entireProcessTree: true);
            await crashedOwner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            using var afterCrash = StartProbe(dataDirectory, takeoverMarker, effectsFile, holdMilliseconds: 0);
            children.Remove(afterCrash);
            await WaitForMarkerAsync(takeoverMarker, "OWNED", TimeSpan.FromSeconds(10));
            await afterCrash.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, afterCrash.ExitCode);
            Assert.AreEqual(4, File.ReadAllLines(effectsFile).Length, "Crash takeover must preserve prior recovery-side-effect records.");
            Assert.AreEqual(recoveryRecord, File.ReadAllText(Path.Combine(dataDirectory, "config", "static-route-session.json")), "Crash recovery must leave the persisted route journal intact.");
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
                child.Dispose();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Process StartProbe(string dataDirectory, string markerFile, string sideEffectFile, int holdMilliseconds, string? probeDirectory = null)
    {
        var probePath = Path.Combine(probeDirectory ?? AppContext.BaseDirectory, "NetBootDhcpTool.InstanceProbe.exe");
        Assert.IsTrue(File.Exists(probePath), $"The process probe was not built beside the tests: {probePath}");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = probePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { dataDirectory, markerFile, sideEffectFile, holdMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        });
        return process ?? throw new AssertFailedException("Could not start the instance probe process.");
    }

    private static void CopyProbeToDirectory(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "NetBootDhcpTool.InstanceProbe.*"))
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "NetBootDhcpTool.Core.dll"), Path.Combine(destinationDirectory, "NetBootDhcpTool.Core.dll"));
        var sharpCompress = Path.Combine(AppContext.BaseDirectory, "SharpCompress.dll");
        if (File.Exists(sharpCompress)) File.Copy(sharpCompress, Path.Combine(destinationDirectory, "SharpCompress.dll"));
    }

    private static string HashDirectory(string directory)
    {
        var entries = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{Path.GetRelativePath(directory, path)}:{Convert.ToBase64String(File.ReadAllBytes(path))}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    private static async Task WaitForMarkerAsync(string path, string expected, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (File.Exists(path) && string.Equals(File.ReadAllText(path), expected, StringComparison.Ordinal)) return;
            }
            catch (IOException)
            {
                // The probe can still hold the marker briefly while File.WriteAllText closes it.
            }
            await Task.Delay(50);
        }
        Assert.Fail($"Probe did not write marker '{expected}' to {path} before timeout.");
    }
}
