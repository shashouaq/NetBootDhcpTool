using System.Security.Cryptography;
using NetBootDhcpTool.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class JsonStoreRecoveryTests
{
    [TestMethod]
    public void LoadDistinguishesMissingEmptyAndNonEmptyDocuments()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "data.json");
            Assert.AreEqual(DataLoadStatus.Missing, JsonStore.Load<List<string>>(path).Status);
            var fallbackPath = Path.Combine(directory, "fallback.json");
            CollectionAssert.AreEqual(new[] { "default" }, JsonStore.LoadOrDefault(fallbackPath, new List<string> { "default" }));
            Assert.AreEqual(DataLoadStatus.Loaded, JsonStore.Load<List<string>>(fallbackPath).Status);
            File.WriteAllText(path, "[]");
            Assert.AreEqual(DataLoadStatus.LoadedEmpty, JsonStore.Load<List<string>>(path).Status);
            File.WriteAllText(path, "[\"value\"]");
            var loaded = JsonStore.Load<List<string>>(path);
            Assert.AreEqual(DataLoadStatus.Loaded, loaded.Status);
            CollectionAssert.AreEqual(new[] { "value" }, loaded.Value!);
        });
    }

    [TestMethod]
    public void LoadRestoresValidBackupAndPreservesBothSources()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            JsonStore.Save(path, new AppSettings { Language = "en-US" });
            JsonStore.Save(path, new AppSettings { Language = "zh-CN" });
            File.WriteAllText(path, "{damaged primary");
            var primaryHash = Hash(path);
            var backupHash = Hash(path + ".bak");

            var result = JsonStore.Load<AppSettings>(path);

            Assert.AreEqual(DataLoadStatus.RestoredFromBackup, result.Status);
            Assert.AreEqual("en-US", result.Value!.Language);
            Assert.AreEqual(Path.GetFullPath(path + ".bak"), Path.GetFullPath(result.SourcePath!));
            Assert.AreEqual(primaryHash, Hash(path));
            Assert.AreEqual(backupHash, Hash(path + ".bak"));
        });
    }

    [TestMethod]
    public void FailedSourcesAreNotOverwrittenByFallbackOrSave()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "profiles.json");
            File.WriteAllText(path, "not-json");
            File.WriteAllText(path + ".bak", "also-not-json");
            var primaryHash = Hash(path);
            var backupHash = Hash(path + ".bak");

            var result = JsonStore.Load<List<NetworkProfile>>(path);
            Assert.AreEqual(DataLoadStatus.Failed, result.Status);
            Assert.AreEqual(0, JsonStore.LoadOrDefault(path, new List<NetworkProfile>()).Count);
            Assert.Throws<InvalidDataException>(() => JsonStore.Save(path, new List<NetworkProfile>()));

            Assert.AreEqual(primaryHash, Hash(path));
            Assert.AreEqual(backupHash, Hash(path + ".bak"));
            Assert.AreEqual(DataLoadStatus.Failed, ProfileStore.LoadWithStatus(path).Status);
        });
    }

    [TestMethod]
    public void SaveFailureLeavesLastValidPrimaryUntouched()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            JsonStore.Save(path, new AppSettings { Language = "en-US" });
            var primaryHash = Hash(path);
            Directory.CreateDirectory(path + ".bak");

            Assert.Throws<IOException>(() => JsonStore.Save(path, new AppSettings { Language = "zh-CN" }));

            Assert.AreEqual(primaryHash, Hash(path));
            Assert.AreEqual("en-US", JsonStore.Load<AppSettings>(path).Value!.Language);
        });
    }

    [TestMethod]
    public void ValidPrimaryWinsOverCorruptBackupAndRepairsItOnSave()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            JsonStore.Save(path, new AppSettings { Language = "en-US" });
            File.WriteAllText(path + ".bak", "invalid backup");

            var loaded = JsonStore.Load<AppSettings>(path);
            Assert.AreEqual(DataLoadStatus.Loaded, loaded.Status);
            Assert.AreEqual("en-US", loaded.Value!.Language);

            JsonStore.Save(path, new AppSettings { Language = "zh-CN" });

            Assert.AreEqual("zh-CN", JsonStore.Load<AppSettings>(path).Value!.Language);
            Assert.AreEqual("en-US", JsonStore.Load<AppSettings>(path + ".bak").Value!.Language);
        });
    }

    [TestMethod]
    public void ReadFailureIsNotTreatedAsMissingOrInitialized()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "blocked.json");
            Directory.CreateDirectory(path);

            var loaded = JsonStore.Load<List<string>>(path);

            Assert.AreEqual(DataLoadStatus.Failed, loaded.Status);
            CollectionAssert.AreEqual(new[] { "memory only" }, JsonStore.LoadOrDefault(path, new List<string> { "memory only" }));
            Assert.IsTrue(Directory.Exists(path));
        });
    }

    [TestMethod]
    public void SavePreservesValidBackupWhenPrimaryIsCorrupt()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "settings.json");
            JsonStore.Save(path, new AppSettings { Language = "en-US" });
            JsonStore.Save(path, new AppSettings { Language = "zh-CN" });
            var validBackupHash = Hash(path + ".bak");
            File.WriteAllText(path, "broken");

            JsonStore.Save(path, new AppSettings { Language = "ja-JP" });

            Assert.AreEqual("ja-JP", JsonStore.Load<AppSettings>(path).Value!.Language);
            Assert.AreEqual(validBackupHash, Hash(path + ".bak"));
            Assert.IsTrue(Directory.GetFiles(directory, "settings.json.corrupt-*.json").Length == 1);
        });
    }

    [TestMethod]
    public void ProfileLoadReportsBackupRecoveryAndValidEmptyContent()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "profiles.json");
            ProfileStore.Save(path, [new NetworkProfile { Name = "safe-profile" }]);
            ProfileStore.Save(path, [new NetworkProfile { Name = "new-profile" }]);
            File.WriteAllText(path, "invalid");

            var restored = ProfileStore.LoadWithStatus(path);
            Assert.AreEqual(DataLoadStatus.RestoredFromBackup, restored.Status);
            Assert.AreEqual("safe-profile", restored.Value!.Single().Name);

            File.WriteAllText(path, "[]");
            Assert.AreEqual(DataLoadStatus.LoadedEmpty, ProfileStore.LoadWithStatus(path).Status);
        });
    }

    [TestMethod]
    public void FavoriteMigrationProtectsCurrentAndBackupCredentialCopies()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("DPAPI favorite migration requires Windows.");
        WithTempDirectory(directory =>
        {
            const string secret = "personal-password-migration-test";
            var path = Path.Combine(directory, "favorites.json");
            File.WriteAllText(path, "[{\"Name\":\"Legacy\",\"Password\":\"" + secret + "\"}]");

            var result = FavoriteStore.LoadWithStatus(path, migrateLegacy: true);

            Assert.AreEqual(DataLoadStatus.Loaded, result.Status);
            Assert.AreEqual(secret, result.Favorites.Single().Password);
            foreach (var persistedPath in new[] { path, path + ".bak" })
            {
                var json = File.ReadAllText(persistedPath);
                Assert.IsFalse(json.Contains(secret, StringComparison.Ordinal));
                Assert.IsFalse(json.Contains("\"Password\"", StringComparison.Ordinal));
                Assert.IsTrue(json.Contains("\"ProtectedPassword\"", StringComparison.Ordinal));
            }
            Assert.AreEqual(secret, FavoriteStore.Load(path).Single().Password);
        });
    }

    [TestMethod]
    public void UnavailableProtectedCredentialKeepsFavoriteDataAndRequiresReentry()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "favorites.json");
            const string protectedValue = "dpapi:encrypted-for-another-windows-user";
            File.WriteAllText(path,
                "[{\"Name\":\"Retained favorite\",\"DeviceNumber\":\"asset-42\",\"MemoryText\":\"keep this note\",\"ProtectedPassword\":\"" + protectedValue + "\"}]");

            var result = FavoriteStore.LoadWithStatus(path);

            Assert.AreEqual(DataLoadStatus.Loaded, result.Status);
            var favorite = result.Favorites.Single();
            Assert.AreEqual("Retained favorite", favorite.Name);
            Assert.AreEqual("asset-42", favorite.DeviceNumber);
            Assert.AreEqual("keep this note", favorite.MemoryText);
            Assert.AreEqual("", favorite.Password);
            Assert.IsTrue(favorite.PasswordUnavailable);
            Assert.AreEqual(protectedValue, favorite.ProtectedPassword);
        });
    }

    [TestMethod]
    public void DefaultsDoNotReplaceCorruptFavoritesWhenBackupIsValid()
    {
        WithTempDirectory(directory =>
        {
            var previous = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");
            try
            {
                var app = Path.Combine(directory, "app");
                var data = Path.Combine(directory, "data");
                Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", data);
                var paths = new AppPaths(app);
                paths.Ensure();
                FavoriteStore.Save(paths.FavoritesFile, [new FavoriteConfig { Id = "safe", Name = "Safe" }]);
                FavoriteStore.Save(paths.FavoritesFile, [new FavoriteConfig { Id = "latest", Name = "Latest" }]);
                File.WriteAllText(paths.FavoritesFile, "broken favorite source");
                var primaryHash = Hash(paths.FavoritesFile);
                var backupHash = Hash(paths.FavoritesFile + ".bak");

                Defaults.EnsureFiles(paths);

                Assert.AreEqual(primaryHash, Hash(paths.FavoritesFile));
                Assert.AreEqual(backupHash, Hash(paths.FavoritesFile + ".bak"));
                Assert.AreEqual("Safe", FavoriteStore.LoadWithStatus(paths.FavoritesFile).Favorites.Single().Name);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", previous);
            }
        });
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void WithTempDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "netboot-store-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
