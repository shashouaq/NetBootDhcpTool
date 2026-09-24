using System.Text.Json;
using NetBootDhcpTool.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class FavoriteLifecycleTests
{
    [TestMethod]
    public void DeletedPresetStaysDeletedUntilExplicitRestore()
    {
        var directory = NewDirectory();
        var priorDataDirectory = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");
        try
        {
            var baseDirectory = Path.Combine(directory, "app");
            var dataDirectory = Path.Combine(directory, "data");
            Directory.CreateDirectory(baseDirectory);
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", dataDirectory);
            var paths = new AppPaths(baseDirectory);
            Defaults.EnsureFiles(paths);
            var preset = Defaults.DefaultFavorites().First();
            var favorites = FavoriteStore.Load(paths.FavoritesFile);
            var edited = favorites.Single(x => x.Id.Equals(preset.Id, StringComparison.OrdinalIgnoreCase));
            edited.RemarkName = "local preset note";
            var retainedPresetId = Defaults.DefaultFavorites().Skip(1).First().Id;
            favorites.Single(x => x.Id.Equals(retainedPresetId, StringComparison.OrdinalIgnoreCase)).RemarkName = "retain across startup";
            FavoriteStore.Save(paths.FavoritesFile, favorites);

            FavoritePresetStore.SetDeleted(paths.FavoritePresetStateFile, preset.Id, deleted: true);
            favorites.RemoveAll(x => x.Id.Equals(preset.Id, StringComparison.OrdinalIgnoreCase));
            FavoriteStore.Save(paths.FavoritesFile, favorites);
            Defaults.EnsureFiles(paths);
            var afterStartup = FavoriteStore.Load(paths.FavoritesFile);
            Assert.IsFalse(afterStartup.Any(x => x.Id.Equals(preset.Id, StringComparison.OrdinalIgnoreCase)),
                "A fresh startup/default reconciliation must honor the persisted deletion intent.");
            Assert.AreEqual("retain across startup", afterStartup.Single(x => x.Id.Equals(retainedPresetId, StringComparison.OrdinalIgnoreCase)).RemarkName,
                "Default reconciliation must not overwrite user-edited fields on other shipped presets.");

            FavoritePresetStore.SetDeleted(paths.FavoritePresetStateFile, preset.Id, deleted: false);
            Defaults.EnsureFiles(paths);
            var restored = FavoriteStore.Load(paths.FavoritesFile).Single(x => x.Id.Equals(preset.Id, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(preset.Id, restored.Id);
            Assert.AreEqual("", restored.RemarkName, "Explicit restore creates the shipped default again.");
            Assert.AreEqual("retain across startup", FavoriteStore.Load(paths.FavoritesFile)
                .Single(x => x.Id.Equals(retainedPresetId, StringComparison.OrdinalIgnoreCase)).RemarkName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", priorDataDirectory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void MissingPresetReconciliationDoesNotOverwriteAnEditedExistingPreset()
    {
        var preset = Defaults.DefaultFavorites().First();
        var edited = FavoriteStore.Clone(preset);
        edited.RemarkName = "my local note";
        edited.Description = "local description";
        edited.MemoryText = "rack 4";
        edited.CustomFields = [new FavoriteField { Name = "Inventory", Value = "ABC-123" }];
        edited.Password = "local-personal-password";
        var favorites = new List<FavoriteConfig> { edited };

        Assert.IsFalse(FavoritePresetStore.AddMissingPresets(favorites, [preset], new FavoritePresetState()));
        Assert.AreEqual(1, favorites.Count);
        Assert.AreEqual("my local note", favorites[0].RemarkName);
        Assert.AreEqual("local description", favorites[0].Description);
        Assert.AreEqual("rack 4", favorites[0].MemoryText);
        Assert.AreEqual("ABC-123", favorites[0].CustomFields.Single().Value);
        Assert.AreEqual("local-personal-password", favorites[0].Password);
    }

    [TestMethod]
    public void CredentialFreeExportErasesPersonalAndPublicCredentialPayloads()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "export.json");
            FavoriteStore.SaveCredentialFreeExport(path,
            [
                new FavoriteConfig { Name = "Public", IsPublicDefault = true, Password = "published-secret", PublicPassword = "published-secret" },
                new FavoriteConfig { Name = "Personal", Password = "private-secret", ProtectedPassword = "opaque-cipher" },
                new FavoriteConfig { Name = "Unavailable", PasswordUnavailable = true, ProtectedPassword = "foreign-dpapi-payload" }
            ]);

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var favorite in json.RootElement.EnumerateArray())
            {
                Assert.IsFalse(favorite.TryGetProperty("Password", out _));
                Assert.AreEqual("", favorite.GetProperty("PublicPassword").GetString());
                Assert.AreEqual("", favorite.GetProperty("ProtectedPassword").GetString());
                Assert.IsFalse(favorite.GetProperty("IsPublicDefault").GetBoolean());
            }
            var text = File.ReadAllText(path);
            Assert.IsFalse(text.Contains("published-secret", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("private-secret", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("opaque-cipher", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("foreign-dpapi-payload", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void UntrustedPublicMarkerIsSavedAsProtectedPersonalCredential()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "favorites.json");
            const string password = "synthetic-public-marker-secret";
            FavoriteStore.Save(path,
            [new FavoriteConfig
            {
                Id = "not-a-shipped-preset",
                Name = "Imported marker",
                IsPublicDefault = true,
                Password = password,
                PublicPassword = password
            }]);

            var json = File.ReadAllText(path);
            Assert.IsFalse(json.Contains(password, StringComparison.Ordinal));
            using var document = JsonDocument.Parse(json);
            var saved = document.RootElement[0];
            Assert.IsFalse(saved.GetProperty("IsPublicDefault").GetBoolean());
            Assert.AreEqual("", saved.GetProperty("PublicPassword").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(saved.GetProperty("ProtectedPassword").GetString()));
            Assert.AreEqual(password, FavoriteStore.Load(path).Single().Password);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void ImportMergePreservesBlankOptionalValuesLocalFieldsCredentialsAndUsage()
    {
        var local = new FavoriteConfig
        {
            Id = "stable-id",
            Name = "Existing",
            DeviceNumber = "old-device",
            RemarkName = "local remark",
            Description = "local description",
            Username = "local-user",
            Password = "local-secret",
            PreferHttps = true,
            MemoryText = "local memory",
            LocalIp = "192.168.1.10",
            SubnetMask = "255.255.255.0",
            TargetIp = "192.168.1.20",
            LastUsedAt = DateTime.Now.AddDays(-1),
            CustomFields =
            [
                new FavoriteField { Name = "Rack", Value = "R1" },
                new FavoriteField { Name = "Asset", Value = "ASSET-9" }
            ]
        };
        var imported = FavoriteStore.PrepareImported(new FavoriteConfig
        {
            Id = "stable-id",
            Name = "Renamed",
            DeviceNumber = "new-device",
            RemarkName = "",
            Description = "",
            Username = "",
            PreferHttps = false,
            MemoryText = "",
            LocalIp = "192.168.1.30",
            SubnetMask = "255.255.255.0",
            TargetIp = "",
            CustomFields =
            [
                new FavoriteField { Name = "rack", Value = "R2" },
                new FavoriteField { Name = "Owner", Value = "Ops" },
                new FavoriteField { Name = "Asset", Value = "" }
            ]
        });

        Assert.IsTrue(FavoriteMergePlanner.SameIdentity(local, imported), "A stable ID takes priority over changed name/address fields.");
        Assert.IsTrue(FavoriteMergePlanner.SameIdentity(local, new FavoriteConfig
        {
            Name = local.Name, LocalIp = local.LocalIp, TargetIp = local.TargetIp
        }), "When IDs are absent, the semantic name/local/target identity should deduplicate the import.");
        var result = FavoriteMergePlanner.Merge(local, imported);

        Assert.AreNotSame(local, result.Favorite);
        Assert.AreEqual("stable-id", result.Favorite.Id);
        Assert.AreEqual("Renamed", result.Favorite.Name);
        Assert.AreEqual("new-device", result.Favorite.DeviceNumber);
        Assert.AreEqual("local remark", result.Favorite.RemarkName);
        Assert.AreEqual("local description", result.Favorite.Description);
        Assert.AreEqual("local-user", result.Favorite.Username);
        Assert.AreEqual("local memory", result.Favorite.MemoryText);
        Assert.AreEqual("192.168.1.30", result.Favorite.LocalIp);
        Assert.AreEqual("192.168.1.20", result.Favorite.TargetIp);
        Assert.IsTrue(result.Favorite.PreferHttps);
        Assert.AreEqual("local-secret", result.Favorite.Password);
        Assert.AreEqual(local.LastUsedAt, result.Favorite.LastUsedAt);
        Assert.AreEqual("R2", result.Favorite.CustomFields.Single(x => x.Name.Equals("Rack", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.AreEqual("ASSET-9", result.Favorite.CustomFields.Single(x => x.Name == "Asset").Value);
        Assert.AreEqual("Ops", result.Favorite.CustomFields.Single(x => x.Name == "Owner").Value);
        CollectionAssert.Contains(result.ReplacedFields.ToList(), "Custom:Rack");
        Assert.IsFalse(result.CredentialReplaced);
    }

    [TestMethod]
    public void CredentialReplacementIsExplicitAndUntrustedPublicFlagIsRemoved()
    {
        var local = new FavoriteConfig { Id = "same", Name = "BMC", LocalIp = "192.168.1.10", SubnetMask = "255.255.255.0", Password = "old-secret" };
        var incoming = FavoriteStore.PrepareImported(new FavoriteConfig
        {
            Id = "same", Name = "BMC", LocalIp = "192.168.1.10", SubnetMask = "255.255.255.0",
            IsPublicDefault = true, PublicPassword = "fake-public", Password = "replacement-secret"
        });

        var merged = FavoriteMergePlanner.Merge(local, incoming);
        Assert.IsTrue(merged.CredentialReplaced);
        CollectionAssert.Contains(merged.ReplacedFields.ToList(), "Credentials");
        Assert.AreEqual("replacement-secret", merged.Favorite.Password);
        Assert.IsFalse(merged.Favorite.IsPublicDefault);
        Assert.AreEqual("", merged.Favorite.PublicPassword);
    }

    [TestMethod]
    public void UnavailableForeignDpapiPayloadNeverErasesAnExistingLocalPassword()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "foreign.json");
            File.WriteAllText(path,
                "[{\"Id\":\"same\",\"Name\":\"BMC\",\"LocalIp\":\"192.168.1.10\",\"SubnetMask\":\"255.255.255.0\",\"ProtectedPassword\":\"not-a-dpapi-payload\"}]");
            var imported = FavoriteStore.PrepareImported(FavoriteStore.Load(path).Single());
            Assert.IsTrue(imported.PasswordUnavailable);

            var local = new FavoriteConfig
            {
                Id = "same", Name = "BMC", LocalIp = "192.168.1.10", SubnetMask = "255.255.255.0", Password = "local-secret"
            };
            var merged = FavoriteMergePlanner.Merge(local, imported);
            Assert.AreEqual("local-secret", merged.Favorite.Password);
            Assert.IsFalse(merged.Favorite.PasswordUnavailable);
            Assert.IsFalse(merged.CredentialReplaced);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "netboot-favorite-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
