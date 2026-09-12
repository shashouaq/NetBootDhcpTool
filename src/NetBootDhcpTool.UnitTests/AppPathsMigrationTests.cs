using NetBootDhcpTool.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class AppPathsMigrationTests
{
    [TestMethod]
    public void MigrationReportsFailuresContinuesCopyingAndRetainsLegacySources()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-migration-tests-" + Guid.NewGuid().ToString("N"));
        var appDirectory = Path.Combine(root, "installed-app");
        var dataDirectory = Path.Combine(root, "user-data");
        var previousDataDirectory = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");

        try
        {
            Directory.CreateDirectory(Path.Combine(appDirectory, "config"));
            Directory.CreateDirectory(Path.Combine(appDirectory, "logs"));
            File.WriteAllText(Path.Combine(appDirectory, "config", "appsettings.json"), "legacy settings");
            File.WriteAllText(Path.Combine(appDirectory, "config", "favorites.json"), "legacy favorites");
            File.WriteAllText(Path.Combine(appDirectory, "logs", "blocked.log"), "legacy blocked log");
            File.WriteAllText(Path.Combine(appDirectory, "logs", "copied.log"), "legacy copied log");

            Directory.CreateDirectory(Path.Combine(dataDirectory, "config", "appsettings.json"));
            Directory.CreateDirectory(Path.Combine(dataDirectory, "logs", "blocked.log"));
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", dataDirectory);

            var paths = new AppPaths(appDirectory);

            Assert.IsTrue(File.Exists(paths.FavoritesFile), "A failed settings migration must not stop later config copies.");
            Assert.IsTrue(File.Exists(Path.Combine(paths.LogsDirectory, "copied.log")), "A failed log migration must not stop later log copies.");
            Assert.IsTrue(Directory.Exists(paths.SettingsFile), "The conflicting destination should remain untouched.");
            Assert.IsTrue(Directory.Exists(Path.Combine(paths.LogsDirectory, "blocked.log")), "The conflicting log destination should remain untouched.");
            Assert.AreEqual(2, paths.MigrationWarnings.Count);
            Assert.IsTrue(paths.MigrationWarnings.Any(x => x.Contains("appsettings.json", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(paths.MigrationWarnings.Any(x => x.Contains("blocked.log", StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(File.Exists(Path.Combine(appDirectory, "config", "appsettings.json")));
            Assert.IsTrue(File.Exists(Path.Combine(appDirectory, "config", "favorites.json")));
            Assert.IsTrue(File.Exists(Path.Combine(appDirectory, "logs", "blocked.log")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", previousDataDirectory);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
