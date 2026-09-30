using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class UpdatePackageTests
{
    [TestMethod]
    [DataRow(UpdatePackageFormat.Zip)]
    [DataRow(UpdatePackageFormat.SevenZip)]
    public async Task InterruptedAppliedTransactionRecoversAndCorruptBackupFailsBeforeRestore(UpdatePackageFormat format)
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1, 2, 3] });
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.20", "1.1.0", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [4, 5, 6] }, [], packageFormat: format);
            var result = await new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(package.Request);
            var backup = Path.Combine(result.BackupDirectory, "NetBootDhcpTool.exe");
            await File.WriteAllBytesAsync(backup, [99]);
            await Assert.ThrowsExactlyAsync<UpdateOperationException>(() => UpdatePackageApplier.RecoverInterruptedAsync(root));
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            Assert.AreEqual("rollback-incomplete", UpdatePackageApplier.GetJournalState(root, package.Request.RequestId));
            await File.WriteAllBytesAsync(backup, [1, 2, 3]);
            Assert.IsTrue(await UpdatePackageApplier.RecoverInterruptedAsync(root));
            Assert.AreEqual("rolled-back", UpdatePackageApplier.GetJournalState(root, package.Request.RequestId));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.20");
            Assert.IsFalse(await UpdatePackageApplier.RecoverInterruptedAsync(root));
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task FullInstallMigratesLegacyPayloadPreservesUserDataAndRollsBack()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        var userData = NewRoot();
        var previousDataDirectory = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");
        Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", userData);
        try
        {
            var legacySettings = Encoding.UTF8.GetBytes("{\"preserve\":true}");
            var legacyLog = Encoding.UTF8.GetBytes("legacy-log");
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]>
            {
                ["NetBootDhcpTool.exe"] = [1, 2, 3],
                ["NetBootDhcpTool.Updater.exe"] = [4, 5, 6],
                ["config/appsettings.json"] = legacySettings,
                ["logs/legacy.log"] = legacyLog
            });
            await File.WriteAllTextAsync(Path.Combine(root, "user-owned-note.txt"), "leave installation-owned extras intact");
            var persisted = Path.Combine(userData, "config", "network-profiles.json");
            Directory.CreateDirectory(Path.GetDirectoryName(persisted)!);
            await File.WriteAllTextAsync(persisted, "{\"profile\":\"retain\"}");
            var history = Path.Combine(userData, "network-history.json");
            await File.WriteAllTextAsync(history, "[\"retain\"]");
            var profileHash = await HashFileAsync(persisted);
            var historyHash = await HashFileAsync(history);

            // This is the setup-only legacy data migration; it must not touch product directories before the transaction.
            var migrationPaths = new AppPaths(root);
            migrationPaths.EnsureUserDataForMigration();
            Assert.AreEqual(0, migrationPaths.MigrationWarnings.Count);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "i18n")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "assets")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "docs")));
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.20", "1.1.0", current.ManifestHash,
                new Dictionary<string, byte[]>
                {
                    ["NetBootDhcpTool.exe"] = [9, 8, 7],
                    ["NetBootDhcpTool.Updater.exe"] = [6, 5, 4]
                }, []);
            var fullInstallRequest = package.Request with { IsFullInstall = true };

            var result = await new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(fullInstallRequest);

            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            CollectionAssert.AreEqual(new byte[] { 6, 5, 4 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.Updater.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "config", "appsettings.json")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "logs", "legacy.log")));
            Assert.AreEqual("leave installation-owned extras intact", await File.ReadAllTextAsync(Path.Combine(root, "user-owned-note.txt")));
            Assert.AreEqual("{\"preserve\":true}", await File.ReadAllTextAsync(Path.Combine(userData, "config", "appsettings.json")));
            Assert.AreEqual(profileHash, await HashFileAsync(persisted));
            Assert.AreEqual(historyHash, await HashFileAsync(history));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.1.0");

            await UpdatePackageApplier.RollbackAsync(result.BackupDirectory, root, fullInstallRequest.RequestId);

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.Updater.exe")));
            CollectionAssert.AreEqual(legacySettings, await File.ReadAllBytesAsync(Path.Combine(root, "config", "appsettings.json")));
            CollectionAssert.AreEqual(legacyLog, await File.ReadAllBytesAsync(Path.Combine(root, "logs", "legacy.log")));
            Assert.AreEqual(profileHash, await HashFileAsync(persisted));
            Assert.AreEqual(historyHash, await HashFileAsync(history));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.20");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", previousDataDirectory);
            TryDeleteDirectory(root);
            TryDeleteDirectory(userData);
        }
    }

    [TestMethod]
    public async Task FullInstallWithoutPriorManifestRollsBackToUnmanagedLegacyDirectory()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe"), [1, 2]);
            await File.WriteAllTextAsync(Path.Combine(root, "readme-user.txt"), "keep");
            var package = await CreatePackageAsync(root, rsa, "Full", "0.0.0", "1.1.0", "",
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [3, 4, 5] }, []);
            var fullInstallRequest = package.Request with { IsFullInstall = true };

            var result = await new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(fullInstallRequest);
            Assert.IsTrue(File.Exists(Path.Combine(root, UpdatePackageApplier.InstallManifestName)));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(root, "readme-user.txt")));

            await UpdatePackageApplier.RollbackAsync(result.BackupDirectory, root, fullInstallRequest.RequestId);

            CollectionAssert.AreEqual(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(root, UpdatePackageApplier.InstallManifestName)));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(root, "readme-user.txt")));
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task FullPackageAppliesAtomicallyAndRollbackPreservesUnmanagedFiles()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.1", new Dictionary<string, byte[]>
            {
                ["NetBootDhcpTool.exe"] = [1, 2, 3],
                ["data.bin"] = [4, 5]
            });
            await File.WriteAllTextAsync(Path.Combine(root, "user-note.txt"), "keep me");
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]>
                {
                    ["NetBootDhcpTool.exe"] = [9, 8, 7, 6],
                    ["new-data.bin"] = [10, 11, 12]
                }, []);
            var applier = new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem());

            var result = await applier.ApplyAsync(package.Request);

            Assert.AreEqual("1.0.2", result.TargetVersion);
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "data.bin")));
            Assert.AreEqual("keep me", await File.ReadAllTextAsync(Path.Combine(root, "user-note.txt")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.2");

            await UpdatePackageApplier.RollbackAsync(result.BackupDirectory, root, package.Request.RequestId);

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            CollectionAssert.AreEqual(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(root, "data.bin")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "new-data.bin")));
            Assert.AreEqual("keep me", await File.ReadAllTextAsync(Path.Combine(root, "user-note.txt")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.1");
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    [DataRow(UpdatePackageFormat.Zip)]
    [DataRow(UpdatePackageFormat.SevenZip)]
    public async Task LockedUnchangedFileDoesNotMarkRollbackIncomplete(UpdatePackageFormat packageFormat)
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        FileStream? lockedFile = null;
        try
        {
            var originalApp = new byte[] { 1, 2, 3 };
            var originalLockedFile = new byte[] { 4, 5, 6 };
            var current = WriteInstallation(root, "1.0.1", new Dictionary<string, byte[]>
            {
                ["NetBootDhcpTool.exe"] = originalApp,
                ["zzz-locked.dat"] = originalLockedFile
            });
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]>
                {
                    ["NetBootDhcpTool.exe"] = [9, 8, 7],
                    ["zzz-locked.dat"] = [6, 5, 4]
                }, [], packageFormat: packageFormat);
            var lockedPath = Path.Combine(root, "zzz-locked.dat");
            lockedFile = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            Exception? failure = null;
            try { await new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(package.Request); }
            catch (Exception ex) { failure = ex; }

            Assert.IsNotNull(failure, "Replacing a file held against write/delete access must fail the transaction.");
            Assert.IsTrue(failure is IOException or UnauthorizedAccessException, failure.ToString());
            Assert.IsFalse(failure is AggregateException, "Rollback must not report failure when the locked file still contains its exact pre-update bytes.");
            lockedFile.Dispose();
            lockedFile = null;

            CollectionAssert.AreEqual(originalApp, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            CollectionAssert.AreEqual(originalLockedFile, await File.ReadAllBytesAsync(lockedPath));
            Assert.AreEqual(current.ManifestHash, await HashFileAsync(Path.Combine(root, UpdatePackageApplier.InstallManifestName)));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.1");

            var backupDirectory = Path.Combine(root, "updates", "rollback", $"1.0.1-to-1.0.2-{package.Request.RequestId}");
            using var journal = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(backupDirectory, "transaction.json")));
            Assert.AreEqual("rolled-back", journal.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            lockedFile?.Dispose();
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task OtaAcceptsOnlyExactVerifiedBaseInventoryAndKeepsUnchangedFiles()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.1", new Dictionary<string, byte[]>
            {
                ["NetBootDhcpTool.exe"] = [1],
                ["stable.dat"] = [2, 3],
                ["changed.dat"] = [4]
            });
            var package = await CreatePackageAsync(root, rsa, "Ota", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]>
                {
                    ["NetBootDhcpTool.exe"] = [5],
                    ["changed.dat"] = [6, 7]
                }, [], unchangedFiles: new Dictionary<string, byte[]> { ["stable.dat"] = [2, 3] });
            var applier = new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem());

            var badBaseline = package.Request with { BaseInstallManifestSha256 = new string('a', 64) };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => applier.ApplyAsync(badBaseline));
            await File.WriteAllBytesAsync(Path.Combine(root, "stable.dat"), [99]);
            await Assert.ThrowsExactlyAsync<UpdateBaseInventoryMismatchException>(() => applier.ApplyAsync(package.Request));
            await File.WriteAllBytesAsync(Path.Combine(root, "stable.dat"), [2, 3]);
            CollectionAssert.AreEqual(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));

            var result = await applier.ApplyAsync(package.Request);
            CollectionAssert.AreEqual(new byte[] { 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "stable.dat")));
            CollectionAssert.AreEqual(new byte[] { 6, 7 }, await File.ReadAllBytesAsync(Path.Combine(root, "changed.dat")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.2");
            await UpdatePackageApplier.RollbackAsync(result.BackupDirectory, root, package.Request.RequestId);
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task V1_0_20LegacyOtaCanReplaceRootAppAndUpdaterWhilePreservingNestedConfiguration()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var config = new byte[] { 3, 1, 4, 1 };
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]>
            {
                ["NetBootDhcpTool.exe"] = [1, 2],
                ["NetBootDhcpTool.Updater.exe"] = [3, 4],
                ["config/appsettings.json"] = config
            });
            var package = await CreatePackageAsync(root, rsa, "Ota", "1.0.20", "1.0.21", current.ManifestHash,
                new Dictionary<string, byte[]>
                {
                    ["NetBootDhcpTool.exe"] = [5, 6, 7],
                    ["NetBootDhcpTool.Updater.exe"] = [8, 9, 10]
                }, [], unchangedFiles: new Dictionary<string, byte[]> { ["config/appsettings.json"] = config });
            var result = await new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(package.Request);

            CollectionAssert.AreEqual(new byte[] { 5, 6, 7 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            CollectionAssert.AreEqual(new byte[] { 8, 9, 10 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.Updater.exe")));
            CollectionAssert.AreEqual(config, await File.ReadAllBytesAsync(Path.Combine(root, "config", "appsettings.json")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.21");

            await UpdatePackageApplier.RollbackAsync(result.BackupDirectory, root, package.Request.RequestId);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            CollectionAssert.AreEqual(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.Updater.exe")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.20");
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task NestedZipBackslashEntriesMatchCanonicalSlashManifestAndExplicitDirectories()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var app = new byte[] { 1, 2 };
            var oldConfig = new byte[] { 3, 4 };
            var newConfig = new byte[] { 5, 6, 7 };
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]>
            {
                ["NetBootDhcpTool.exe"] = app,
                ["config/appsettings.json"] = oldConfig
            });
            var package = await CreatePackageAsync(root, rsa, "Ota", "1.0.20", "1.0.21", current.ManifestHash,
                new Dictionary<string, byte[]> { ["config/appsettings.json"] = newConfig }, [],
                unchangedFiles: new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = app },
                alterArchive: archive => archive.CreateEntry("payload\\config\\"),
                useWindowsSeparators: true);

            await new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(package.Request);

            CollectionAssert.AreEqual(newConfig, await File.ReadAllBytesAsync(Path.Combine(root, "config", "appsettings.json")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.21");
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task SevenZipFullPackageUsesTheSameTransactionAndRollbackPipeline()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]>
            {
                ["NetBootDhcpTool.exe"] = [1, 2],
                ["NetBootDhcpTool.Updater.exe"] = [3, 4],
                ["config/appsettings.json"] = [5, 6]
            });
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.20", "1.0.21", current.ManifestHash,
                new Dictionary<string, byte[]>
                {
                    ["NetBootDhcpTool.exe"] = [7, 8, 9],
                    ["NetBootDhcpTool.Updater.exe"] = [10, 11, 12],
                    ["config/appsettings.json"] = [13, 14, 15]
                }, [], packageFormat: UpdatePackageFormat.SevenZip);

            var result = await new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(package.Request);

            CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            CollectionAssert.AreEqual(new byte[] { 10, 11, 12 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.Updater.exe")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.21");
            await UpdatePackageApplier.RollbackAsync(result.BackupDirectory, root, package.Request.RequestId);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            CollectionAssert.AreEqual(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.Updater.exe")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.20");
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task SevenZipPackageHashMismatchFailsBeforeChangingInstallation()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1, 2, 3] });
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.20", "1.0.21", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [4, 5, 6] }, [], packageFormat: UpdatePackageFormat.SevenZip);
            var bytes = await File.ReadAllBytesAsync(package.PackagePath);
            bytes[bytes.Length / 2] ^= 0x5A;
            await File.WriteAllBytesAsync(package.PackagePath, bytes);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(package.Request));

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.20");
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task SevenZipPayloadHashMismatchFailsBeforeChangingInstallation()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1, 2, 3] });
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.20", "1.0.21", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [4, 5, 6] }, [], packageFormat: UpdatePackageFormat.SevenZip,
                packagePayloadOverrides: new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [7, 5, 6] });

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(package.Request));

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.20");
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task TruncatedSignedSevenZipArchiveFailsBeforeChangingInstallation()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1, 2, 3] });
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.20", "1.0.21", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [4, 5, 6] }, [], packageFormat: UpdatePackageFormat.SevenZip);
            var bytes = await File.ReadAllBytesAsync(package.PackagePath);
            await File.WriteAllBytesAsync(package.PackagePath, bytes[..^32]);
            var truncatedRequest = await ReSignPackageRequestAsync(package.Request, rsa);

            var failed = false;
            try { await new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()).ApplyAsync(truncatedRequest); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException) { failed = true; }
            Assert.IsTrue(failed, "The truncated 7z archive should fail closed.");

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.20");
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task SevenZipTraversalAndDuplicateCanonicalPathsFailBeforeChangingInstallation()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1, 2, 3] });
            var applier = new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem());
            var traversal = await CreatePackageAsync(root, rsa, "Full", "1.0.20", "1.0.21", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [4, 5, 6] }, [], packageFormat: UpdatePackageFormat.SevenZip,
                extraSevenZipEntries: new Dictionary<string, byte[]> { ["payload/../escape.bin"] = [9] });
            var duplicate = await CreatePackageAsync(root, rsa, "Full", "1.0.20", "1.0.21", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [4, 5, 6] }, [], packageFormat: UpdatePackageFormat.SevenZip,
                extraSevenZipEntries: new Dictionary<string, byte[]> { ["payload/netbootdhcpTool.exe"] = [9] });

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => applier.ApplyAsync(traversal.Request));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => applier.ApplyAsync(duplicate.Request));

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.20");
            Assert.IsFalse(File.Exists(Path.Combine(root, "escape.bin")));
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    [DataRow(UpdatePackageFormat.Zip)]
    [DataRow(UpdatePackageFormat.SevenZip)]
    public async Task LockedPackageHandlePreventsReplacementAfterPackageHashVerification(UpdatePackageFormat packageFormat)
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.20", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1, 2] });
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.20", "1.0.21", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [3, 4, 5] }, [], packageFormat: packageFormat);
            var replacement = package.PackagePath + ".attacker";
            await File.WriteAllBytesAsync(replacement, [9, 9, 9, 9]);
            var replacementWasBlocked = false;
            var applier = new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem(), packagePath =>
            {
                Assert.AreEqual(Path.GetFullPath(package.PackagePath), packagePath);
                try { File.Move(replacement, packagePath, overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { replacementWasBlocked = true; }
                Assert.IsTrue(replacementWasBlocked, "The verified package path was replaceable while its locked stream was active.");
            });

            await applier.ApplyAsync(package.Request);

            Assert.IsTrue(replacementWasBlocked);
            Assert.IsTrue(File.Exists(replacement));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.21");
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task InvalidSignatureAndUnmanagedCollisionFailBeforeChangingInstallation()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.1", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1] });
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [2], ["extra.bin"] = [3] }, []);
            var applier = new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem());

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => applier.ApplyAsync(package.Request with { ManifestSignature = Convert.ToBase64String(new byte[384]) }));
            await File.WriteAllBytesAsync(Path.Combine(root, "extra.bin"), [42]);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => applier.ApplyAsync(package.Request));

            CollectionAssert.AreEqual(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            CollectionAssert.AreEqual(new byte[] { 42 }, await File.ReadAllBytesAsync(Path.Combine(root, "extra.bin")));
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task FullPackageRepairsDriftedManagedFilesAndRollbackRestoresTheCapturedBytes()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.1", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1, 2] });
            await File.WriteAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe"), [8, 8, 8]);
            var package = await CreatePackageAsync(root, rsa, "Full", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [3, 4, 5] }, []);
            var applier = new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem());

            var result = await applier.ApplyAsync(package.Request);
            CollectionAssert.AreEqual(new byte[] { 3, 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            await UpdatePackageApplier.VerifyInstalledFilesAsync(root, "1.0.2");

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => UpdatePackageApplier.RollbackAsync(result.BackupDirectory, root, package.Request.RequestId));
            CollectionAssert.AreEqual(new byte[] { 8, 8, 8 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public async Task UnsafeZipPathsDuplicatePayloadsAndLinksFailBeforeInstallChanges()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.1", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1] });
            var applier = new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem());
            var addPackageEntry = await CreatePackageAsync(root, rsa, "Full", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [2] }, [],
                alterArchive: archive => archive.CreateEntry("payload/../escape.bin"));
            var duplicatePayload = await CreatePackageAsync(root, rsa, "Full", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [2] }, [],
                alterArchive: archive => archive.CreateEntry("payload/NetBootDhcpTool.exe"));
            var linkedPayload = await CreatePackageAsync(root, rsa, "Full", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [2] }, [],
                alterArchive: archive =>
                {
                    var link = archive.CreateEntry("payload/linked.bin");
                    link.ExternalAttributes = 0xA000 << 16;
                });

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => applier.ApplyAsync(addPackageEntry.Request));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => applier.ApplyAsync(duplicatePayload.Request));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => applier.ApplyAsync(linkedPayload.Request));
            CollectionAssert.AreEqual(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "escape.bin")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "linked.bin")));
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public void InstallInventoriesEnforceEntryAndExpandedSizeLimits()
    {
        var tooManyFiles = Enumerable.Range(0, UpdatePackageApplier.MaximumEntries + 1).Select(index => new UpdateFileEntry
        {
            Path = $"file-{index}.bin",
            Sha256 = new string('a', 64),
            Size = 1
        }).ToArray();
        Assert.IsFalse(UpdatePackageApplier.IsValidFileInventory(tooManyFiles));
        Assert.IsFalse(UpdatePackageApplier.IsValidFileInventory([new UpdateFileEntry
        {
            Path = "large.bin",
            Sha256 = new string('a', 64),
            Size = UpdatePackageApplier.MaximumExpandedBytes + 1
        }]));
    }

    [TestMethod]
    public async Task UpdateControllerFallsBackToFullWhenActualOtaBaseFilesHaveDrifted()
    {
        using var rsa = RSA.Create(3072);
        var root = NewRoot();
        try
        {
            var current = WriteInstallation(root, "1.0.1", new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [1] });
            await File.WriteAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe"), [99]);
            var full = await CreatePackageAsync(root, rsa, "Full", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [2, 3] }, []);
            var fullManifest = JsonSerializer.Deserialize<UpdateManifest>(Convert.FromBase64String(full.Request.ManifestJson), JsonStore.Options)!;
            var fullMetadata = fullManifest.Packages.Single();
            var ota = await CreatePackageAsync(root, rsa, "Ota", "1.0.1", "1.0.2", current.ManifestHash,
                new Dictionary<string, byte[]> { ["NetBootDhcpTool.exe"] = [4, 5] }, [], additionalPackages: [fullMetadata]);
            var otaManifestJson = Encoding.UTF8.GetString(Convert.FromBase64String(ota.Request.ManifestJson));
            var otaManifest = JsonSerializer.Deserialize<UpdateManifest>(otaManifestJson, JsonStore.Options)!;
            var otaMetadata = otaManifest.Packages.Single(item => item.Kind == "Ota");
            var update = new UpdateCheckResult
            {
                CurrentVersion = new Version(1, 0, 1),
                LatestVersion = new Version(1, 0, 2),
                Succeeded = true,
                IsNewVersion = true,
                DownloadUrl = otaMetadata.DownloadUrl!,
                DownloadUrls = [otaMetadata.DownloadUrl!],
                ReleasePageUrl = otaManifest.ReleasePageUrl!,
                ArchiveName = otaManifest.ArchiveName,
                ArchiveSha256 = otaManifest.ArchiveSha256,
                SignatureVerified = true,
                SignedManifestJson = otaManifestJson,
                ManifestSignature = ota.Request.ManifestSignature,
                Packages = otaManifest.Packages,
                SelectedPackage = otaMetadata
            };
            var httpClient = new HttpClient(new DelegateHandler((request, _) =>
            {
                var bytes = request.RequestUri!.AbsoluteUri.Equals(otaMetadata.DownloadUrl, StringComparison.Ordinal)
                    ? File.ReadAllBytes(ota.PackagePath)
                    : File.ReadAllBytes(full.PackagePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }));
            var service = new VersionUpdateService(httpClient, null, TimeSpan.FromSeconds(5), rsa.ExportSubjectPublicKeyInfoPem());
            var install = new UpdateInstallContext(root, current.Manifest, current.ManifestHash);
            var controller = new UpdateController(service, new Version(1, 0, 1), install, new UpdatePackageApplier(rsa.ExportSubjectPublicKeyInfoPem()));
            try
            {
                var destination = Path.Combine(root, "staging", otaMetadata.FileName);
                var downloaded = await controller.DownloadAsync(update, destination);

                Assert.IsTrue(downloaded.ReadyToInstall);
                Assert.IsTrue(downloaded.FellBackToFullPackage);
                Assert.AreEqual("Full", downloaded.Package!.Kind);
                CollectionAssert.AreEqual(new byte[] { 99 }, await File.ReadAllBytesAsync(Path.Combine(root, "NetBootDhcpTool.exe")));
            }
            finally
            {
                await controller.DisposeAsync();
                httpClient.Dispose();
            }
        }
        finally { TryDeleteDirectory(root); }
    }

    [TestMethod]
    public void SignedManifestSelectsExactOtaAndFallsBackToFullForDifferentManifestHash()
    {
        using var rsa = RSA.Create(3072);
        const string manifestJson = """
            {"version":"1.0.2","archiveName":"NetBootDhcpTool-v1.0.2.7z","archiveSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","downloadUrl":"https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.2/NetBootDhcpTool-v1.0.2.7z","releasePageUrl":"https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v1.0.2","packages":[{"kind":"Full","fileName":"NetBootDhcpTool-full-v1.0.2.zip","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","size":100,"downloadUrl":"https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.2/NetBootDhcpTool-full-v1.0.2.zip"},{"kind":"Ota","fileName":"NetBootDhcpTool-ota-v1.0.1-to-v1.0.2.zip","sha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","size":50,"baseVersion":"1.0.1","baseInstallManifestSha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","downloadUrl":"https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.2/NetBootDhcpTool-ota-v1.0.1-to-v1.0.2.zip"}]}
            """;
        var bytes = Encoding.UTF8.GetBytes(manifestJson);
        var signature = Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var update = VersionUpdateService.Evaluate(manifestJson, signature, new Version(1, 0, 1), rsa.ExportSubjectPublicKeyInfoPem());
        Assert.IsTrue(update.SignatureVerified, update.Error);
        Assert.AreEqual(2, update.Packages.Count);

        var inventory = new UpdateInstallManifest
        {
            ProductId = UpdatePackageApplier.ProductId,
            Version = "1.0.1",
            Files = [new UpdateFileEntry { Path = "NetBootDhcpTool.exe", Sha256 = new string('e', 64), Size = 1 }]
        };
        var exact = new UpdateInstallContext("C:\\app", inventory, new string('d', 64));
        var wrongBaseline = exact with { ManifestSha256 = new string('f', 64) };
        Assert.AreEqual("Ota", UpdatePackageSelector.Select(update, exact)?.Kind);
        Assert.AreEqual("Full", UpdatePackageSelector.Select(update, wrongBaseline)?.Kind);
        Assert.IsNull(UpdatePackageSelector.Select(update, null));
    }

    [TestMethod]
    public void SignedManifestKeepsZipFieldsAndSelectsSevenZipBridgeAndFullFallback()
    {
        using var rsa = RSA.Create(3072);
        var manifest = new UpdateManifest
        {
            Version = "1.0.22",
            ArchiveName = "NetBootDhcpTool-v1.0.22.7z",
            ArchiveSha256 = new string('a', 64),
            DownloadUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.22/NetBootDhcpTool-v1.0.22.7z",
            ReleasePageUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v1.0.22",
            Packages =
            [
                new UpdatePackageMetadata
                {
                    Kind = "Full",
                    FileName = "NetBootDhcpTool-full-v1.0.22.zip",
                    Sha256 = new string('b', 64),
                    Size = 100,
                    DownloadUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.22/NetBootDhcpTool-full-v1.0.22.zip"
                }
            ],
            SevenZipPackages =
            [
                new UpdatePackageMetadata
                {
                    Kind = "Full",
                    FileName = "NetBootDhcpTool-full-v1.0.22.7z",
                    Sha256 = new string('c', 64),
                    Size = 120,
                    DownloadUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.22/NetBootDhcpTool-full-v1.0.22.7z"
                },
                new UpdatePackageMetadata
                {
                    Kind = "Ota",
                    FileName = "NetBootDhcpTool-ota-v1.0.21-to-v1.0.22.7z",
                    Sha256 = new string('d', 64),
                    Size = 80,
                    BaseVersion = "1.0.21",
                    BaseInstallManifestSha256 = new string('e', 64),
                    DownloadUrl = "https://github.com/shashouaq/NetBootDhcpTool/releases/download/v1.0.22/NetBootDhcpTool-ota-v1.0.21-to-v1.0.22.7z"
                }
            ]
        };
        var manifestJson = JsonSerializer.Serialize(manifest, JsonStore.Options);
        var signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(manifestJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var update = VersionUpdateService.Evaluate(manifestJson, signature, new Version(1, 0, 21), rsa.ExportSubjectPublicKeyInfoPem());

        Assert.IsTrue(update.Succeeded, update.Error);
        Assert.IsTrue(update.SignatureVerified);
        Assert.AreEqual(3, update.Packages.Count);
        Assert.IsTrue(update.Packages.Any(package => package.Kind == "Full" && package.Format == nameof(UpdatePackageFormat.Zip)));
        Assert.AreEqual("Ota", UpdatePackageSelector.Select(update, CreateInstallContext("1.0.21", new string('e', 64)))?.Kind);
        Assert.AreEqual(nameof(UpdatePackageFormat.SevenZip), UpdatePackageSelector.Select(update, CreateInstallContext("1.0.21", new string('e', 64)))?.Format);
        var otherBase = CreateInstallContext("1.0.21", new string('f', 64));
        Assert.AreEqual("Full", UpdatePackageSelector.Select(update, otherBase)?.Kind);
        Assert.AreEqual(nameof(UpdatePackageFormat.SevenZip), UpdatePackageSelector.Select(update, otherBase)?.Format);
    }

    private static UpdateInstallContext CreateInstallContext(string version, string manifestHash)
    {
        var inventory = new UpdateInstallManifest
        {
            ProductId = UpdatePackageApplier.ProductId,
            Version = version,
            Files = [new UpdateFileEntry { Path = "NetBootDhcpTool.exe", Sha256 = new string('e', 64), Size = 1 }]
        };
        return new UpdateInstallContext("C:\\app", inventory, manifestHash);
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "netboot-update-package-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static (UpdateInstallManifest Manifest, string ManifestHash) WriteInstallation(string root, string version, IReadOnlyDictionary<string, byte[]> files)
    {
        foreach (var (relative, fileBytes) in files)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, fileBytes);
        }
        var manifest = new UpdateInstallManifest
        {
            ProductId = UpdatePackageApplier.ProductId,
            Version = version,
            EntryPoint = "NetBootDhcpTool.exe",
            Files = files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => new UpdateFileEntry
            {
                Path = pair.Key,
                Sha256 = Hash(pair.Value),
                Size = pair.Value.LongLength
            }).ToList()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonStore.Options);
        File.WriteAllBytes(Path.Combine(root, UpdatePackageApplier.InstallManifestName), bytes);
        return (manifest, Hash(bytes));
    }

    private static async Task<(UpdateApplyRequest Request, string PackagePath)> CreatePackageAsync(
        string root,
        RSA rsa,
        string kind,
        string currentVersion,
        string targetVersion,
        string baselineHash,
        IReadOnlyDictionary<string, byte[]> changedFiles,
        IReadOnlyList<string> deletedFiles,
        bool includeFullInventory = false,
        IReadOnlyDictionary<string, byte[]>? unchangedFiles = null,
        Action<ZipArchive>? alterArchive = null,
        IReadOnlyList<UpdatePackageMetadata>? additionalPackages = null,
        UpdatePackageFormat packageFormat = UpdatePackageFormat.Zip,
        bool useWindowsSeparators = false,
        IReadOnlyDictionary<string, byte[]>? packagePayloadOverrides = null,
        IReadOnlyDictionary<string, byte[]>? extraSevenZipEntries = null)
    {
        var inventoryFiles = new Dictionary<string, byte[]>(changedFiles, StringComparer.OrdinalIgnoreCase);
        if (unchangedFiles is not null)
            foreach (var item in unchangedFiles) inventoryFiles[item.Key] = item.Value;
        var targetManifest = new UpdateInstallManifest
        {
            ProductId = UpdatePackageApplier.ProductId,
            Version = targetVersion,
            EntryPoint = "NetBootDhcpTool.exe",
            Files = inventoryFiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => new UpdateFileEntry
            {
                Path = pair.Key,
                Sha256 = Hash(pair.Value),
                Size = pair.Value.LongLength
            }).ToList()
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(targetManifest, JsonStore.Options);
        var payloadRoot = Path.Combine(root, "test-package-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payloadRoot);
        var payload = new Dictionary<string, byte[]>(changedFiles, StringComparer.OrdinalIgnoreCase);
        if (includeFullInventory && unchangedFiles is not null)
            foreach (var item in unchangedFiles) payload[item.Key] = item.Value;
        payload[UpdatePackageApplier.InstallManifestName] = manifestBytes;
        var archivePayload = new Dictionary<string, byte[]>(payload, StringComparer.OrdinalIgnoreCase);
        if (packagePayloadOverrides is not null)
            foreach (var item in packagePayloadOverrides)
            {
                if (!payload.ContainsKey(item.Key) || item.Key.Equals(UpdatePackageApplier.InstallManifestName, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Package payload override is not a managed payload file: {item.Key}", nameof(packagePayloadOverrides));
                archivePayload[item.Key] = item.Value;
            }
        var packageFiles = payload.Select(pair => new UpdatePackageFile { Path = pair.Key, Sha256 = Hash(pair.Value), Size = pair.Value.LongLength }).ToList();
        var document = new UpdatePackageDocument
        {
            Kind = kind,
            TargetVersion = targetVersion,
            BaseVersion = kind == "Ota" ? currentVersion : null,
            BaseInstallManifestSha256 = kind == "Ota" ? baselineHash : null,
            Files = packageFiles,
            DeletedFiles = deletedFiles.ToList()
        };
        var extension = packageFormat == UpdatePackageFormat.SevenZip ? ".7z" : ".zip";
        var packagePath = Path.Combine(root, "update-package-" + Guid.NewGuid().ToString("N") + extension);
        if (packageFormat == UpdatePackageFormat.Zip)
        {
            await using var stream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
            var documentEntry = archive.CreateEntry(UpdatePackageApplier.PackageDocumentName);
            await using (var output = documentEntry.Open()) await JsonSerializer.SerializeAsync(output, document, JsonStore.Options);
            foreach (var (relative, bytes) in archivePayload)
            {
                var archivePath = useWindowsSeparators
                    ? "payload\\" + relative.Replace('/', '\\')
                    : "payload/" + relative.Replace('\\', '/');
                var entry = archive.CreateEntry(archivePath);
                await using var output = entry.Open();
                await output.WriteAsync(bytes);
            }
            alterArchive?.Invoke(archive);
        }
        else
        {
            await using var stream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var options = new SevenZipWriterOptions(CompressionType.LZMA2) { CompressionLevel = 9, LeaveStreamOpen = true };
            using (var writer = SevenZipWriter.OpenWriter(stream, options))
            {
                using (var descriptorInput = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document, JsonStore.Options), writable: false))
                    writer.Write(UpdatePackageApplier.PackageDocumentName, descriptorInput, null);
                foreach (var (relative, bytes) in archivePayload)
                {
                    var canonical = PackagePathCanonicalizer.Canonicalize(relative);
                    var entryPath = "payload/" + canonical;
                    using var input = new MemoryStream(bytes, writable: false);
                    writer.Write(entryPath, input, null);
                }
                if (extraSevenZipEntries is not null)
                {
                    foreach (var (entryPath, bytes) in extraSevenZipEntries)
                    {
                        using var input = new MemoryStream(bytes, writable: false);
                        writer.Write(entryPath, input, null);
                    }
                }
            }
        }

        var packageHash = Hash(await File.ReadAllBytesAsync(packagePath));
        var metadata = new UpdatePackageMetadata
        {
            Kind = kind,
            Format = packageFormat.ToString(),
            FileName = kind == "Full" ? $"NetBootDhcpTool-full-v{targetVersion}{extension}" : $"NetBootDhcpTool-ota-v{currentVersion}-to-v{targetVersion}{extension}",
            Sha256 = packageHash,
            Size = new FileInfo(packagePath).Length,
            DownloadUrl = $"https://github.com/shashouaq/NetBootDhcpTool/releases/download/v{targetVersion}/{(kind == "Full" ? $"NetBootDhcpTool-full-v{targetVersion}{extension}" : $"NetBootDhcpTool-ota-v{currentVersion}-to-v{targetVersion}{extension}")}",
            BaseVersion = kind == "Ota" ? currentVersion : null,
            BaseInstallManifestSha256 = kind == "Ota" ? baselineHash : null
        };
        var releaseManifest = new UpdateManifest
        {
            Version = targetVersion,
            ArchiveName = $"NetBootDhcpTool-v{targetVersion}.7z",
            ArchiveSha256 = new string('a', 64),
            DownloadUrl = $"https://github.com/shashouaq/NetBootDhcpTool/releases/download/v{targetVersion}/NetBootDhcpTool-v{targetVersion}.7z",
            ReleasePageUrl = $"https://github.com/shashouaq/NetBootDhcpTool/releases/tag/v{targetVersion}",
            Packages = packageFormat == UpdatePackageFormat.Zip && kind == "Full"
                ? [metadata]
                : packageFormat == UpdatePackageFormat.Zip && additionalPackages is { Count: > 0 }
                    ? [.. additionalPackages, metadata]
                : packageFormat == UpdatePackageFormat.Zip ?
                [
                    new UpdatePackageMetadata
                    {
                        Kind = "Full",
                        FileName = $"NetBootDhcpTool-full-v{targetVersion}.zip",
                        Sha256 = new string('b', 64),
                        Size = 100,
                        DownloadUrl = $"https://github.com/shashouaq/NetBootDhcpTool/releases/download/v{targetVersion}/NetBootDhcpTool-full-v{targetVersion}.zip"
                    },
                    metadata
                ] : [],
            SevenZipPackages = packageFormat == UpdatePackageFormat.SevenZip ? [metadata] : []
        };
        var signedManifest = JsonSerializer.Serialize(releaseManifest, JsonStore.Options);
        var signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(signedManifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var request = new UpdateApplyRequest(
            root,
            packagePath,
            packageHash,
            currentVersion,
            targetVersion,
            kind,
            baselineHash,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(signedManifest)),
            signature,
            Guid.NewGuid().ToString("N"),
            packageFormat.ToString());
        return (request, packagePath);
    }

    private static async Task<UpdateApplyRequest> ReSignPackageRequestAsync(UpdateApplyRequest request, RSA rsa)
    {
        var packageBytes = await File.ReadAllBytesAsync(request.PackagePath);
        var manifestBytes = Convert.FromBase64String(request.ManifestJson);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonStore.Options)
            ?? throw new InvalidDataException("Test signed manifest could not be read.");
        var metadata = manifest.SevenZipPackages.Single(package => package.Kind.Equals(request.PackageKind, StringComparison.OrdinalIgnoreCase));
        metadata.Size = packageBytes.LongLength;
        metadata.Sha256 = Hash(packageBytes);
        var signedBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonStore.Options);
        return request with
        {
            PackageSha256 = metadata.Sha256,
            ManifestJson = Convert.ToBase64String(signedBytes),
            ManifestSignature = Convert.ToBase64String(rsa.SignData(signedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<string> HashFileAsync(string path) => Hash(await File.ReadAllBytesAsync(path));

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
