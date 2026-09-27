using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class UpdatePackageTests
{
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
        IReadOnlyList<UpdatePackageMetadata>? additionalPackages = null)
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
        var packagePath = Path.Combine(root, "update-package-" + Guid.NewGuid().ToString("N") + ".zip");
        await using (var stream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            var documentEntry = archive.CreateEntry(UpdatePackageApplier.PackageDocumentName);
            await using (var output = documentEntry.Open())
                await JsonSerializer.SerializeAsync(output, document, JsonStore.Options);
            foreach (var (relative, bytes) in payload)
            {
                var entry = archive.CreateEntry("payload/" + relative.Replace('\\', '/'));
                await using var output = entry.Open();
                await output.WriteAsync(bytes);
            }
            alterArchive?.Invoke(archive);
        }

        var packageHash = Hash(await File.ReadAllBytesAsync(packagePath));
        var metadata = new UpdatePackageMetadata
        {
            Kind = kind,
            FileName = kind == "Full" ? $"NetBootDhcpTool-full-v{targetVersion}.zip" : $"NetBootDhcpTool-ota-v{currentVersion}-to-v{targetVersion}.zip",
            Sha256 = packageHash,
            Size = new FileInfo(packagePath).Length,
            DownloadUrl = $"https://github.com/shashouaq/NetBootDhcpTool/releases/download/v{targetVersion}/{(kind == "Full" ? $"NetBootDhcpTool-full-v{targetVersion}.zip" : $"NetBootDhcpTool-ota-v{currentVersion}-to-v{targetVersion}.zip")}",
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
            Packages = kind == "Full"
                ? [metadata]
                : additionalPackages is { Count: > 0 }
                    ? [.. additionalPackages, metadata]
                :
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
                ]
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
            Guid.NewGuid().ToString("N"));
        return (request, packagePath);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
