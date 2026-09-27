using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetBootDhcpTool.Core;

public enum UpdatePackageKind
{
    Full,
    Ota
}

public sealed class UpdatePackageMetadata
{
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string? DownloadUrl { get; set; }
    public List<string> DownloadMirrors { get; set; } = [];
    public string? BaseVersion { get; set; }
    public string? BaseInstallManifestSha256 { get; set; }
}

public sealed class UpdateInstallManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string ProductId { get; set; } = "";
    public string Version { get; set; } = "";
    public string EntryPoint { get; set; } = "NetBootDhcpTool.exe";
    public List<UpdateFileEntry> Files { get; set; } = [];
}

public sealed class UpdateFileEntry
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class UpdatePackageFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string? BaseSha256 { get; set; }
}

public sealed class UpdatePackageDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string ProductId { get; set; } = "NetBootDhcpTool";
    public string Kind { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string? BaseVersion { get; set; }
    public string? BaseInstallManifestSha256 { get; set; }
    public List<UpdatePackageFile> Files { get; set; } = [];
    public List<string> DeletedFiles { get; set; } = [];
}

public sealed record UpdateInstallContext(string RootDirectory, UpdateInstallManifest Manifest, string ManifestSha256)
{
    public static UpdateInstallContext? TryLoad(string rootDirectory, Version runningVersion)
    {
        try
        {
            var root = Path.GetFullPath(rootDirectory);
            var manifestPath = Path.Combine(root, UpdatePackageApplier.InstallManifestName);
            if (!File.Exists(manifestPath)) return null;
            var bytes = File.ReadAllBytes(manifestPath);
            var manifest = JsonSerializer.Deserialize<UpdateInstallManifest>(bytes, JsonStore.Options);
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.ProductId != "NetBootDhcpTool"
                || !VersionUpdateService.TryParseVersion(manifest.Version, out var version)
                || version.ToString(3) != runningVersion.ToString(3)
                || !manifest.Files.Any(file => file.Path.Equals(manifest.EntryPoint, StringComparison.OrdinalIgnoreCase))
                || !UpdatePackageApplier.IsValidFileInventory(manifest.Files)) return null;
            return new UpdateInstallContext(root, manifest, UpdatePackageApplier.Sha256(bytes));
        }
        catch
        {
            return null;
        }
    }
}

public static class UpdatePackageSelector
{
    public static UpdatePackageMetadata? Select(UpdateCheckResult update, UpdateInstallContext? install)
    {
        if (!update.SignatureVerified || !update.IsNewVersion || install is null
            || !VersionUpdateService.TryParseVersion(install.Manifest.Version, out var installedVersion)
            || installedVersion.ToString(3) != update.CurrentVersion.ToString(3)) return null;

        var ota = update.Packages.FirstOrDefault(package => package.Kind.Equals("Ota", StringComparison.OrdinalIgnoreCase)
            && package.BaseVersion == installedVersion.ToString(3)
            && package.BaseInstallManifestSha256?.Equals(install.ManifestSha256, StringComparison.OrdinalIgnoreCase) == true);
        return ota ?? update.Packages.FirstOrDefault(package => package.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record UpdateApplyRequest(
    string InstallRoot,
    string PackagePath,
    string PackageSha256,
    string CurrentVersion,
    string TargetVersion,
    string PackageKind,
    string BaseInstallManifestSha256,
    string ManifestJson,
    string ManifestSignature,
    string RequestId);

public sealed record UpdateApplyResult(string TargetVersion, string BackupDirectory, IReadOnlyList<string> ReplacedFiles, IReadOnlyList<string> DeletedFiles);

public sealed class UpdateBaseInventoryMismatchException(string message) : Exception(message);

public sealed record UpdateProcessRequest(
    UpdateApplyRequest Apply,
    int ParentProcessId,
    string UserDataDirectory,
    string RequestFilePath,
    string AcceptedFilePath,
    string HealthFilePath,
    string HealthToken,
    string UpdateLogPath);

/// <summary>Validates and transactionally applies a signed, file-level update package.</summary>
public sealed class UpdatePackageApplier
{
    private readonly string _trustedPublicKeyPem;

    public UpdatePackageApplier() : this(UpdateManifestSignature.TrustedPublicKeyPem) { }

    internal UpdatePackageApplier(string trustedPublicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPublicKeyPem);
        _trustedPublicKeyPem = trustedPublicKeyPem;
    }

    public const string InstallManifestName = "install-manifest.json";
    public const string PackageDocumentName = "update-package.json";
    public const string ProductId = "NetBootDhcpTool";
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;
    public const long MaximumExpandedBytes = 1536L * 1024 * 1024;
    public const int MaximumEntries = 10000;

    public async Task<UpdateApplyResult> ApplyAsync(UpdateApplyRequest request, CancellationToken ct = default, bool validateOnly = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.InstallRoot));
        var archivePath = Path.GetFullPath(request.PackagePath);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Update package was not found.", archivePath);
        var archiveInfo = new FileInfo(archivePath);
        if (archiveInfo.Length <= 0 || archiveInfo.Length > MaximumPackageBytes)
            throw new InvalidDataException("Update package size is outside the supported limit.");
        if (!IsSha256(request.PackageSha256) || !IsSha256(request.BaseInstallManifestSha256))
            throw new InvalidDataException("Update request hashes are invalid.");
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 80 || request.RequestId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            throw new InvalidDataException("Update request ID is invalid.");

        var manifestBytes = Convert.FromBase64String(request.ManifestJson);
        var signature = request.ManifestSignature;
        var manifestCheck = VersionUpdateService.Evaluate(Encoding.UTF8.GetString(manifestBytes), signature, new Version(0, 0), _trustedPublicKeyPem);
        if (!manifestCheck.Succeeded || !manifestCheck.SignatureVerified || manifestCheck.LatestVersion is null)
            throw new InvalidDataException("The signed update manifest could not be verified or validated.");
        var latestVersion = manifestCheck.LatestVersion;
        if (!latestVersion.ToString(3).Equals(request.TargetVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The update request version does not match its signed manifest.");
        var signedManifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, JsonStore.Options)
            ?? throw new InvalidDataException("The signed update manifest is empty.");
        if (!VersionUpdateService.TryParseVersion(signedManifest.Version, out var parsedVersion)
            || parsedVersion.ToString(3) != request.TargetVersion
            || !latestVersion.ToString(3).Equals(request.TargetVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The update request version does not match its signed manifest.");
        var signedPackage = manifestCheck.Packages.FirstOrDefault(package =>
            package.Kind.Equals(request.PackageKind, StringComparison.OrdinalIgnoreCase)
            && package.Sha256.Equals(request.PackageSha256, StringComparison.OrdinalIgnoreCase));
        if (signedPackage is null) throw new InvalidDataException("The update package is not listed in the signed manifest.");
        if (!VersionUpdateService.TryParseVersion(request.CurrentVersion, out var currentVersion)
            || latestVersion <= currentVersion)
            throw new InvalidDataException("The update is not newer than the installed version.");

        var actualPackageHash = await HashFileAsync(archivePath, ct).ConfigureAwait(false);
        if (!actualPackageHash.Equals(request.PackageSha256, StringComparison.OrdinalIgnoreCase)
            || archiveInfo.Length != signedPackage.Size)
            throw new InvalidDataException("Update package SHA-256 does not match its signed manifest.");

        var currentManifestPath = Path.Combine(root, InstallManifestName);
        var currentManifestBytes = await File.ReadAllBytesAsync(currentManifestPath, ct).ConfigureAwait(false);
        var currentManifestHash = Sha256(currentManifestBytes);
        if (!currentManifestHash.Equals(request.BaseInstallManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installed package baseline does not match the update request.");
        var currentManifest = JsonSerializer.Deserialize<UpdateInstallManifest>(currentManifestBytes, JsonStore.Options)
            ?? throw new InvalidDataException("The installed package manifest is invalid.");
        if (currentManifest.ProductId != ProductId || currentManifest.Version != request.CurrentVersion
            || !IsValidFileInventory(currentManifest.Files))
            throw new InvalidDataException("The installed package manifest does not match this installation.");

        var requiresExactBaseInventory = request.PackageKind.Equals("Ota", StringComparison.OrdinalIgnoreCase);
        long backupBytes = 0;
        foreach (var managedFile in currentManifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var installedPath = ResolveUnderRoot(root, managedFile.Path);
            if (!File.Exists(installedPath))
            {
                if (requiresExactBaseInventory)
                    throw new UpdateBaseInventoryMismatchException($"Installed file does not match its OTA base inventory: {managedFile.Path}");
                continue;
            }
            var installedInfo = new FileInfo(installedPath);
            var matchesInventory = installedInfo.Length == managedFile.Size
                && managedFile.Sha256.Equals(await HashFileAsync(installedPath, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase);
            if (requiresExactBaseInventory && !matchesInventory)
                throw new UpdateBaseInventoryMismatchException($"Installed file does not match its OTA base inventory: {managedFile.Path}");
            backupBytes = checked(backupBytes + installedInfo.Length);
        }
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is 0 or > MaximumEntries) throw new InvalidDataException("Update package entry count is invalid.");
        var documentEntry = archive.GetEntry(PackageDocumentName) ?? throw new InvalidDataException("Update package manifest is missing.");
        var document = await ReadJsonAsync<UpdatePackageDocument>(documentEntry, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Update package manifest is invalid.");
        ValidatePackageDocument(document, request, signedPackage, latestVersion);
        var currentManagedPaths = currentManifest.Files.Select(file => file.Path).Append(InstallManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fileByPath = new Dictionary<string, UpdatePackageFile>(StringComparer.OrdinalIgnoreCase);
        long totalExpandedBytes = 0;
        foreach (var file in document.Files)
        {
            ValidateRelativePath(file.Path);
            if (!IsSha256(file.Sha256) || file.Size < 0 || !fileByPath.TryAdd(file.Path, file))
                throw new InvalidDataException("Update package contains an invalid or duplicate file record.");
            totalExpandedBytes = checked(totalExpandedBytes + file.Size);
        }
        if (totalExpandedBytes > MaximumExpandedBytes) throw new InvalidDataException("Update package expands beyond the supported limit.");
        EnsureInstallRootWritable(root);
        var requiredBytes = checked(TotalPackageEstimate(totalExpandedBytes, backupBytes));
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.AvailableFreeSpace < requiredBytes)
            throw new IOException("There is not enough free disk space to stage and roll back this update.");
        var stagingDrive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
        if (stagingDrive.AvailableFreeSpace < totalExpandedBytes + 16L * 1024 * 1024)
            throw new IOException("There is not enough free disk space for the verified update staging files.");

        var payloadEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Equals(PackageDocumentName, StringComparison.Ordinal)) continue;
            if (!entry.FullName.StartsWith("payload/", StringComparison.Ordinal))
                throw new InvalidDataException("Update package contains an unexpected entry.");
            var relativePath = entry.FullName["payload/".Length..].Replace('/', Path.DirectorySeparatorChar);
            ValidateRelativePath(relativePath);
            RejectZipLink(entry);
            if (!payloadEntries.TryAdd(relativePath, entry)) throw new InvalidDataException("Update package contains duplicate payload paths.");
        }
        if (payloadEntries.Count != fileByPath.Count || fileByPath.Keys.Any(path => !payloadEntries.ContainsKey(path)))
            throw new InvalidDataException("Update package payload does not match its package manifest.");
        foreach (var file in document.Files)
        {
            if (file.Path.Equals(InstallManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            var destination = ResolveUnderRoot(root, file.Path);
            if (File.Exists(destination) && !currentManagedPaths.Contains(file.Path))
                throw new InvalidDataException($"Update would overwrite an unmanaged file: {file.Path}");
        }
        if (document.DeletedFiles.Any(path => !currentManagedPaths.Contains(path) || path.Equals(InstallManifestName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Update package tries to delete a file outside the current managed inventory.");

        var transactionDirectory = Path.Combine(Path.GetTempPath(), "NetBootDhcpTool-update-" + request.RequestId);
        if (Directory.Exists(transactionDirectory)) throw new IOException("An update transaction with this ID already exists.");
        var payloadDirectory = Path.Combine(transactionDirectory, "payload");
        Directory.CreateDirectory(payloadDirectory);
        try
        {
            foreach (var (relativePath, entry) in payloadEntries)
            {
                ct.ThrowIfCancellationRequested();
                var file = fileByPath[relativePath];
                if (entry.Length != file.Size) throw new InvalidDataException($"Update payload size mismatch: {relativePath}");
                var destination = ResolveUnderRoot(payloadDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    copied = checked(copied + read);
                    if (copied > file.Size) throw new InvalidDataException($"Update payload exceeds its declared size: {relativePath}");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                if (copied != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Update payload hash mismatch: {relativePath}");
            }

            var newManifestPath = Path.Combine(payloadDirectory, InstallManifestName);
            if (!File.Exists(newManifestPath)) throw new InvalidDataException("Update package does not contain the target installation manifest.");
            var newManifest = JsonSerializer.Deserialize<UpdateInstallManifest>(await File.ReadAllBytesAsync(newManifestPath, ct).ConfigureAwait(false), JsonStore.Options)
                ?? throw new InvalidDataException("Target installation manifest is invalid.");
            if (newManifest.ProductId != ProductId || newManifest.Version != request.TargetVersion
                || !IsValidFileInventory(newManifest.Files)
                || !newManifest.Files.Any(file => file.Path.Equals(newManifest.EntryPoint, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Target installation manifest does not match the update.");
            if (document.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase)
                && newManifest.Files.Any(file => !fileByPath.ContainsKey(file.Path)))
                throw new InvalidDataException("Full package does not contain every file in the target installation inventory.");
            var effectiveDeletedFiles = document.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase)
                ? currentManifest.Files.Select(file => file.Path)
                    .Where(path => !newManifest.Files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    .ToArray()
                : document.DeletedFiles.ToArray();
            ValidateResultingInventory(currentManifest, newManifest, document);
            foreach (var file in newManifest.Files)
            {
                var stagedPath = ResolveUnderRoot(payloadDirectory, file.Path);
                if (File.Exists(stagedPath))
                {
                    if (!file.Sha256.Equals(await HashFileAsync(stagedPath, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Target package file hash mismatch: {file.Path}");
                    continue;
                }
                var existingPath = ResolveUnderRoot(root, file.Path);
                if (!File.Exists(existingPath) || !file.Sha256.Equals(await HashFileAsync(existingPath, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Target package is missing a managed file: {file.Path}");
            }

            if (validateOnly)
                return new UpdateApplyResult(latestVersion.ToString(3), "", document.Files.Select(file => file.Path).ToArray(), effectiveDeletedFiles);

            var backupDirectory = Path.Combine(Path.GetDirectoryName(currentManifestPath)!, "updates", "rollback", $"{currentVersion.ToString(3)}-to-{latestVersion.ToString(3)}-{request.RequestId}");
            var changedPaths = document.Files.Select(file => file.Path).Concat(effectiveDeletedFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var backedUp = new List<string>();
            Directory.CreateDirectory(backupDirectory);
            try
            {
                foreach (var relativePath in changedPaths)
                {
                    ct.ThrowIfCancellationRequested();
                    ValidateRelativePath(relativePath);
                    var existing = ResolveUnderRoot(root, relativePath);
                    if (!File.Exists(existing)) continue;
                    var backup = ResolveUnderRoot(backupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(existing, backup, overwrite: false);
                    backedUp.Add(relativePath);
                }
                WriteTransactionJournal(backupDirectory, request, changedPaths, backedUp, "applying", document.Files);

                foreach (var file in document.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var destination = ResolveUnderRoot(root, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temporary = destination + "." + request.RequestId + ".new";
                    File.Copy(ResolveUnderRoot(payloadDirectory, file.Path), temporary, overwrite: false);
                    File.Move(temporary, destination, overwrite: true);
                }
                foreach (var relativePath in effectiveDeletedFiles)
                {
                    var destination = ResolveUnderRoot(root, relativePath);
                    if (File.Exists(destination)) File.Delete(destination);
                }

                foreach (var file in newManifest.Files)
                {
                    var destination = ResolveUnderRoot(root, file.Path);
                    if (!File.Exists(destination) || !file.Sha256.Equals(await HashFileAsync(destination, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Installed file verification failed: {file.Path}");
                }
                WriteTransactionJournal(backupDirectory, request, changedPaths, backedUp, "files-verified-awaiting-startup", document.Files);
                return new UpdateApplyResult(latestVersion.ToString(3), backupDirectory, document.Files.Select(file => file.Path).ToArray(), effectiveDeletedFiles);
            }
            catch (Exception applyFailure)
            {
                var rollbackFailures = new List<Exception>();
                foreach (var relativePath in changedPaths.Reverse())
                {
                    try
                    {
                        var destination = ResolveUnderRoot(root, relativePath);
                        var backup = ResolveUnderRoot(backupDirectory, relativePath);
                        var temporaryNewFile = destination + "." + request.RequestId + ".new";
                        if (File.Exists(temporaryNewFile)) File.Delete(temporaryNewFile);
                        if (File.Exists(backup))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            var temporary = destination + "." + request.RequestId + ".rollback";
                            File.Copy(backup, temporary, overwrite: false);
                            File.Move(temporary, destination, overwrite: true);
                        }
                        else if (File.Exists(destination))
                        {
                            var expected = document.Files.FirstOrDefault(file => file.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
                            if (expected is null || !expected.Sha256.Equals(await HashFileAsync(destination, CancellationToken.None).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                                throw new IOException($"Refusing to remove a file not owned by this update: {relativePath}");
                            File.Delete(destination);
                        }
                    }
                    catch (Exception ex) { rollbackFailures.Add(ex); }
                }
                WriteTransactionJournal(backupDirectory, request, changedPaths, backedUp, rollbackFailures.Count == 0 ? "rolled-back" : "rollback-incomplete", document.Files);
                if (rollbackFailures.Count > 0) throw new AggregateException("Update failed and rollback is incomplete; the transaction backup was preserved.", [applyFailure, .. rollbackFailures]);
                throw;
            }
        }
        finally
        {
            try { Directory.Delete(transactionDirectory, recursive: true); }
            catch { /* The uniquely named staging directory can be removed on the next maintenance pass. */ }
        }
    }

    public static bool IsValidFileInventory(IReadOnlyCollection<UpdateFileEntry>? files)
    {
        if (files is null || files.Count == 0 || files.Count > MaximumEntries) return false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in files)
        {
            try { ValidateRelativePath(file.Path); }
            catch { return false; }
            if (!IsSha256(file.Sha256) || file.Size < 0 || !paths.Add(file.Path)) return false;
            try { totalBytes = checked(totalBytes + file.Size); }
            catch { return false; }
            if (totalBytes > MaximumExpandedBytes) return false;
        }
        return true;
    }

    public static async Task VerifyInstalledFilesAsync(string root, string expectedVersion, CancellationToken ct = default)
    {
        var fullRoot = Path.GetFullPath(root);
        var manifestPath = Path.Combine(fullRoot, InstallManifestName);
        var bytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<UpdateInstallManifest>(bytes, JsonStore.Options)
            ?? throw new InvalidDataException("Installation manifest is invalid.");
        if (manifest.SchemaVersion != 1 || manifest.ProductId != ProductId || manifest.Version != expectedVersion
            || !IsValidFileInventory(manifest.Files)
            || !manifest.Files.Any(file => file.Path.Equals(manifest.EntryPoint, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Installation manifest identity or inventory is invalid.");
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = ResolveUnderRoot(fullRoot, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size
                || !file.Sha256.Equals(await HashFileAsync(path, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Installed file verification failed: {file.Path}");
        }
    }

    public static void MarkHealthy(string backupDirectory, string requestId)
    {
        var journalPath = Path.Combine(Path.GetFullPath(backupDirectory), "transaction.json");
        var journal = ReadTransactionJournal(journalPath, requestId);
        WriteJournalState(journalPath, journal, "healthy");
    }

    public static async Task RollbackAsync(string backupDirectory, string installRoot, string requestId, CancellationToken ct = default)
    {
        var fullBackup = Path.GetFullPath(backupDirectory);
        var journalPath = Path.Combine(fullBackup, "transaction.json");
        var journal = ReadTransactionJournal(journalPath, requestId);
        if (journal.State.Equals("healthy", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A healthy update transaction cannot be rolled back automatically.");
        var root = Path.GetFullPath(installRoot);
        foreach (var relativePath in journal.ChangedPaths.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var destination = ResolveUnderRoot(root, relativePath);
            var backup = ResolveUnderRoot(fullBackup, relativePath);
            if (File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + "." + requestId + ".rollback";
                File.Copy(backup, temporary, overwrite: true);
                File.Move(temporary, destination, overwrite: true);
            }
            else if (File.Exists(destination))
            {
                if (!journal.TargetFileHashes.TryGetValue(relativePath, out var expectedHash)
                    || !expectedHash.Equals(await HashFileAsync(destination, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Refusing to remove an unmanaged file during rollback: {relativePath}");
                File.Delete(destination);
            }
        }
        WriteJournalState(journalPath, journal, "rolled-back");
        await VerifyInstalledFilesAsync(root, journal.CurrentVersion, ct).ConfigureAwait(false);
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<T?> ReadJsonAsync<T>(ZipArchiveEntry entry, CancellationToken ct)
    {
        if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Update package metadata is too large.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonStore.Options, ct).ConfigureAwait(false);
    }

    private static void ValidatePackageDocument(UpdatePackageDocument document, UpdateApplyRequest request, UpdatePackageMetadata signedPackage, Version latestVersion)
    {
        if (document.SchemaVersion != 1 || document.ProductId != ProductId || !string.Equals(document.TargetVersion, latestVersion.ToString(3), StringComparison.Ordinal))
            throw new InvalidDataException("Update package identity or target version is invalid.");
        if (!string.Equals(document.Kind, request.PackageKind, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(document.Kind, signedPackage.Kind, StringComparison.OrdinalIgnoreCase)
            || document.Files is null)
            throw new InvalidDataException("Update package type does not match the signed manifest.");
        if (string.Equals(document.Kind, "Ota", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(document.BaseVersion) || !document.BaseVersion.Equals(request.CurrentVersion, StringComparison.Ordinal)
                || !string.Equals(document.BaseInstallManifestSha256, request.BaseInstallManifestSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(document.BaseInstallManifestSha256, signedPackage.BaseInstallManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OTA package baseline does not match the installed release.");
        }
        else if (!string.Equals(document.Kind, "Full", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update package type is unsupported.");
        if (document.DeletedFiles is null || document.DeletedFiles.Count > MaximumEntries)
            throw new InvalidDataException("Update package delete list is invalid.");
        if (document.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase) && document.DeletedFiles.Count != 0)
            throw new InvalidDataException("Full packages must not declare base-specific deleted files.");
        var deletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in document.DeletedFiles)
        {
            ValidateRelativePath(path);
            if (!deletes.Add(path)) throw new InvalidDataException("Update package contains duplicate delete paths.");
        }
        if (document.Files.Any(file => deletes.Contains(file.Path))) throw new InvalidDataException("Update package both replaces and deletes the same path.");
    }

    private static void ValidateResultingInventory(UpdateInstallManifest current, UpdateInstallManifest target, UpdatePackageDocument document)
    {
        if (document.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase))
        {
            var targetFiles = target.Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
            var packageFiles = document.Files.Where(file => !file.Path.Equals(InstallManifestName, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
            if (targetFiles.Count != packageFiles.Count || targetFiles.Any(pair => !packageFiles.TryGetValue(pair.Key, out var hash)
                || !hash.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Full package payload does not exactly match the target managed-file inventory.");
            return;
        }
        var resulting = current.Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        foreach (var path in document.DeletedFiles) resulting.Remove(path);
        foreach (var file in document.Files)
        {
            if (file.Path.Equals(InstallManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            resulting[file.Path] = file.Sha256;
        }
        var expected = target.Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        if (resulting.Count != expected.Count || resulting.Any(pair => !expected.TryGetValue(pair.Key, out var hash) || !hash.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Update package does not produce the target managed-file inventory.");
    }

    private static long TotalPackageEstimate(long expandedBytes, long backupBytes) => checked(expandedBytes + backupBytes + 64L * 1024 * 1024);

    private static void EnsureInstallRootWritable(string root)
    {
        var probe = Path.Combine(root, ".NetBootDhcpTool-update-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static bool IsSha256(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void ValidateRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException("Update package contains an unsafe path.");
        var segments = path.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.Any(ch => Path.GetInvalidFileNameChars().Contains(ch))
            || IsWindowsDeviceName(segment)))
            throw new InvalidDataException("Update package contains a path traversal entry.");
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var name = segment.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(name, @"^(?:COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        EnsureRootHasNoReparsePoint(fullRoot);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update package path escapes its approved root.");
        EnsureNoExistingReparsePoint(fullRoot, fullPath);
        return fullPath;
    }

    private static void EnsureRootHasNoReparsePoint(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Approved update root does not exist: {root}");
        var volumeRoot = Path.GetPathRoot(root) ?? throw new InvalidDataException("Update root has no volume root.");
        var current = volumeRoot;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Update root contains a reparse point.");
        var relative = Path.GetRelativePath(volumeRoot, root);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or ".") continue;
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update root contains a reparse point.");
        }
    }

    private static void EnsureNoExistingReparsePoint(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update target contains a reparse point.");
        }
    }

    private static void RejectZipLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Update package links are not supported.");
    }

    private static void WriteTransactionJournal(string directory, UpdateApplyRequest request, IReadOnlyList<string> changedPaths, IReadOnlyList<string> backedUp, string state, IReadOnlyList<UpdatePackageFile> targetFiles)
    {
        var journal = new
        {
            schemaVersion = 1,
            requestId = request.RequestId,
            currentVersion = request.CurrentVersion,
            targetVersion = request.TargetVersion,
            state,
            changedPaths,
            backedUp,
            targetFileHashes = targetFiles.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase),
            updatedAt = DateTimeOffset.UtcNow
        };
        var path = Path.Combine(directory, "transaction.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal, JsonStore.Options));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record TransactionJournal(string RequestId, string CurrentVersion, string TargetVersion, string State, string[] ChangedPaths, string[] BackedUp, Dictionary<string, string> TargetFileHashes);

    private static TransactionJournal ReadTransactionJournal(string journalPath, string requestId)
    {
        var journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllBytes(journalPath), JsonStore.Options)
            ?? throw new InvalidDataException("Update transaction journal is invalid.");
        if (!journal.RequestId.Equals(requestId, StringComparison.Ordinal) || journal.ChangedPaths.Length > MaximumEntries
            || journal.TargetFileHashes is null || journal.TargetFileHashes.Count > MaximumEntries)
            throw new InvalidDataException("Update transaction journal does not match this request.");
        foreach (var relativePath in journal.ChangedPaths) ValidateRelativePath(relativePath);
        foreach (var relativePath in journal.BackedUp) ValidateRelativePath(relativePath);
        return journal;
    }

    private static void WriteJournalState(string journalPath, TransactionJournal journal, string state)
    {
        var replacement = journal with { State = state };
        var temporary = journalPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(replacement, JsonStore.Options));
        File.Move(temporary, journalPath, overwrite: true);
    }
}

public static class UpdateManifestSignature
{
    public const string Algorithm = "RSA-PSS-SHA256";
    public const string TrustedPublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAqulH7BNk2VcJ6EAFHF9V
vzV1OIuT2YlEyJ3ZGXI/odHCw+fzxs0dPHUfspEDhSy+nID4+N1b7Nil0t9Y9riH
sw8Io46ZdSk49qyL8nCkc2wj2xeAPWEAcoUhKSm+tEDIl6HWodTFkn2/5YdpcsSv
fI0GBtkawiHi1vdn9mNp8tg6vyf2pgkmt2JJo1bkC5tWXPZp//Sx6gCjfLpE4s8V
EAWwxim6dUDajz5V6mASkI21X6BhxEe5wEpLfuLa+cXaPh2sZdQZmX9ZAp1tmFFs
iSoAmcA8CJZiXKpGHdI71+WN0wbNisD5AXQ5tlptBS6XSezhMZd4ISshBgHguOHX
LplmaM2mLkRK338zcADOudnMNJdrTwgRroHtJbw0txIuzwlTpBCz34wkd7uNsfZW
jfYopHxGlSEymNPvaOdiuktPEi/mAOeG8K5RZLjUSK5onxirj2GuMFCiy3DhoPBy
JjHUkXh7Xq3Jgl7bT6Mgai53VwgdyTatsM8haoHS2mMtAgMBAAE=
-----END PUBLIC KEY-----
""";

    public static bool Verify(ReadOnlySpan<byte> data, string? signatureBase64, string? trustedPublicKeyPem = null)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64)) return false;
        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            using var rsa = RSA.Create();
            rsa.ImportFromPem(string.IsNullOrWhiteSpace(trustedPublicKeyPem) ? TrustedPublicKeyPem : trustedPublicKeyPem);
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
