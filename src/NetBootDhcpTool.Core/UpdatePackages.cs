using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetBootDhcpTool.Core;

public enum UpdatePackageKind
{
    Full,
    Ota
}

public sealed class UpdatePackageMetadata
{
    public string Kind { get; set; } = "";
    [JsonIgnore]
    public string Format { get; set; } = "Zip";
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

        var ota = update.Packages.Where(package => package.Kind.Equals("Ota", StringComparison.OrdinalIgnoreCase)
            && package.BaseVersion == installedVersion.ToString(3)
            && package.BaseInstallManifestSha256?.Equals(install.ManifestSha256, StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(package => package.Format.Equals(nameof(UpdatePackageFormat.SevenZip), StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        return ota ?? update.Packages.Where(package => package.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(package => package.Format.Equals(nameof(UpdatePackageFormat.SevenZip), StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
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
    string RequestId,
    string PackageFormat = "Zip")
{
    public bool IsFullInstall { get; init; }
    public bool KeepValidatedStaging { get; init; }
}

public sealed record UpdateApplyResult(string TargetVersion, string BackupDirectory, IReadOnlyList<string> ReplacedFiles, IReadOnlyList<string> DeletedFiles, string StagingDirectory = "");

public sealed class UpdateBaseInventoryMismatchException(string message) : Exception(message);

public sealed record UpdateProcessRequest(
    UpdateApplyRequest Apply,
    int ParentProcessId,
    string UserDataDirectory,
    string RequestFilePath,
    string AcceptedFilePath,
    string HealthFilePath,
    string HealthToken,
    string UpdateLogPath)
{
    public bool IsFullInstall { get; init; }
    public bool WaitForParentExit { get; init; } = true;
    public bool Silent { get; init; }
}

/// <summary>Validates and transactionally applies a signed, file-level update package.</summary>
public sealed class UpdatePackageApplier
{
    private readonly string _trustedPublicKeyPem;
    private readonly Action<string>? _packageHashVerified;

    public UpdatePackageApplier() : this(UpdateManifestSignature.TrustedPublicKeyPem) { }

    internal UpdatePackageApplier(string trustedPublicKeyPem, Action<string>? packageHashVerified = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPublicKeyPem);
        _trustedPublicKeyPem = trustedPublicKeyPem;
        _packageHashVerified = packageHashVerified;
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
        UpdateTestEnvironment.RequirePath(root);
        UpdateTestEnvironment.RequirePath(archivePath);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Update package was not found.", archivePath);
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        if (archiveStream.Length <= 0 || archiveStream.Length > MaximumPackageBytes)
            throw new InvalidDataException("Update package size is outside the supported limit.");
        if (!IsSha256(request.PackageSha256)
            || (!request.IsFullInstall && !IsSha256(request.BaseInstallManifestSha256))
            || (request.IsFullInstall && !string.IsNullOrWhiteSpace(request.BaseInstallManifestSha256) && !IsSha256(request.BaseInstallManifestSha256))
            || (request.IsFullInstall && !request.PackageKind.Equals("Full", StringComparison.OrdinalIgnoreCase))
            || (request.KeepValidatedStaging && !request.IsFullInstall))
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
        if (!Enum.TryParse<UpdatePackageFormat>(request.PackageFormat, ignoreCase: true, out var packageFormat))
            throw new InvalidDataException("Update package format is invalid.");
        var signedPackage = manifestCheck.Packages.FirstOrDefault(package =>
            package.Kind.Equals(request.PackageKind, StringComparison.OrdinalIgnoreCase)
            && package.Format.Equals(packageFormat.ToString(), StringComparison.OrdinalIgnoreCase)
            && package.Sha256.Equals(request.PackageSha256, StringComparison.OrdinalIgnoreCase));
        if (signedPackage is null) throw new InvalidDataException("The update package is not listed in the signed manifest.");
        if (!VersionUpdateService.TryParseVersion(request.CurrentVersion, out var currentVersion)
            || latestVersion <= currentVersion)
            throw new InvalidDataException("The update is not newer than the installed version.");

        var expectedFormat = PackageExtractorFactory.FromFileName(signedPackage.FileName);
        if (expectedFormat != packageFormat) throw new InvalidDataException("The signed package format does not match its extension.");
        if (archiveStream.Length != signedPackage.Size) throw new InvalidDataException("Update package SHA-256 does not match its signed manifest.");
        var actualPackageHash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (!actualPackageHash.Equals(request.PackageSha256, StringComparison.OrdinalIgnoreCase)
            || archiveStream.Length != signedPackage.Size)
            throw new InvalidDataException("Update package SHA-256 does not match its signed manifest.");
        _packageHashVerified?.Invoke(archivePath);
        UpdateTestEnvironment.Checkpoint("package-verified", archivePath);

        var currentManifestPath = Path.Combine(root, InstallManifestName);
        var hasCurrentManifest = File.Exists(currentManifestPath);
        UpdateInstallManifest currentManifest;
        byte[] currentManifestBytes = [];
        if (hasCurrentManifest)
        {
            currentManifestBytes = await File.ReadAllBytesAsync(currentManifestPath, ct).ConfigureAwait(false);
            var currentManifestHash = Sha256(currentManifestBytes);
            if (!currentManifestHash.Equals(request.BaseInstallManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installed package baseline does not match the update request.");
            currentManifest = JsonSerializer.Deserialize<UpdateInstallManifest>(currentManifestBytes, JsonStore.Options)
                ?? throw new InvalidDataException("The installed package manifest is invalid.");
            NormalizeInstallManifestPaths(currentManifest);
            if (currentManifest.ProductId != ProductId || currentManifest.Version != request.CurrentVersion
                || !IsValidFileInventory(currentManifest.Files))
                throw new InvalidDataException("The installed package manifest does not match this installation.");
        }
        else
        {
            if (!request.IsFullInstall || request.CurrentVersion != "0.0.0" || !string.IsNullOrWhiteSpace(request.BaseInstallManifestSha256))
                throw new InvalidDataException("An installed package manifest is required for this update.");
            currentManifest = new UpdateInstallManifest { ProductId = ProductId, Version = request.CurrentVersion, Files = [] };
        }

        var requiresExactBaseInventory = request.PackageKind.Equals("Ota", StringComparison.OrdinalIgnoreCase);
        var currentFileSizes = new Dictionary<string, long>(PackagePathCanonicalizer.PathComparer);
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
            currentFileSizes[PackagePathCanonicalizer.Canonicalize(managedFile.Path)] = installedInfo.Length;
        }
        var extractor = PackageExtractorFactory.Create(packageFormat);
        var inspection = await extractor.InspectAsync(archiveStream, ct).ConfigureAwait(false);
        var document = inspection.Document;
        NormalizePackageDocumentPaths(document);
        ValidatePackageDocument(document, request, signedPackage, latestVersion);
        var currentManagedPaths = currentManifest.Files.Select(file => PackagePathCanonicalizer.Canonicalize(file.Path)).Append(InstallManifestName).ToHashSet(PackagePathCanonicalizer.PathComparer);

        var fileByPath = new Dictionary<string, UpdatePackageFile>(PackagePathCanonicalizer.PathComparer);
        long totalExpandedBytes = 0;
        foreach (var file in document.Files)
        {
            if (!IsSha256(file.Sha256) || file.Size < 0 || !fileByPath.TryAdd(file.Path, file))
                throw new InvalidDataException("Update package contains an invalid or duplicate file record.");
            totalExpandedBytes = checked(totalExpandedBytes + file.Size);
        }
        if (totalExpandedBytes > MaximumExpandedBytes) throw new InvalidDataException("Update package expands beyond the supported limit.");
        var backupPaths = document.Files.Select(file => file.Path).Concat(document.DeletedFiles).Distinct(PackagePathCanonicalizer.PathComparer);
        long backupBytes = 0;
        long largestBackupFileBytes = 0;
        foreach (var path in backupPaths)
        {
            if (currentFileSizes.TryGetValue(path, out var size))
            {
                backupBytes = checked(backupBytes + size);
                largestBackupFileBytes = Math.Max(largestBackupFileBytes, size);
            }
            else if (path.Equals(InstallManifestName, StringComparison.OrdinalIgnoreCase) && File.Exists(currentManifestPath))
            {
                var manifestSize = new FileInfo(currentManifestPath).Length;
                backupBytes = checked(backupBytes + manifestSize);
                largestBackupFileBytes = Math.Max(largestBackupFileBytes, manifestSize);
            }
            else if (request.IsFullInstall)
            {
                var existing = ResolveUnderRoot(root, path);
                if (File.Exists(existing))
                {
                    var existingSize = new FileInfo(existing).Length;
                    backupBytes = checked(backupBytes + existingSize);
                    largestBackupFileBytes = Math.Max(largestBackupFileBytes, existingSize);
                }
            }
        }
        if (Directory.Exists(root)) EnsureInstallRootWritable(root);
        else if (!request.IsFullInstall) throw new DirectoryNotFoundException("The installed application directory was not found.");
        EnsureDiskSpaceForUpdate(root, archivePath, archiveStream.Length, totalExpandedBytes, backupBytes,
            largestBackupFileBytes, fileByPath.Values, document.DeletedFiles);

        foreach (var file in document.Files)
        {
            if (file.Path.Equals(InstallManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            var destination = ResolveUnderRoot(root, file.Path);
            if (File.Exists(destination) && !currentManagedPaths.Contains(file.Path) && !request.IsFullInstall)
                throw new InvalidDataException($"Update would overwrite an unmanaged file: {file.Path}");
        }
        if (document.DeletedFiles.Any(path => !currentManagedPaths.Contains(path) || path.Equals(InstallManifestName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Update package tries to delete a file outside the current managed inventory.");

        var transactionDirectory = Path.Combine(UpdateTestEnvironment.TemporaryDirectory, "NetBootDhcpTool-update-" + request.RequestId);
        if (Directory.Exists(transactionDirectory)) throw new IOException("An update transaction with this ID already exists.");
        var payloadDirectory = Path.Combine(transactionDirectory, "payload");
        Directory.CreateDirectory(payloadDirectory);
        UpdateTestEnvironment.Checkpoint("staging-created", payloadDirectory);
        var preserveValidatedStaging = false;
        try
        {
            await extractor.ExtractPayloadAsync(archiveStream, inspection, payloadDirectory, ct).ConfigureAwait(false);
            foreach (var file in document.Files)
            {
                var stagedPath = ResolveUnderRoot(payloadDirectory, file.Path);
                if (!File.Exists(stagedPath) || new FileInfo(stagedPath).Length != file.Size
                    || !file.Sha256.Equals(await HashFileAsync(stagedPath, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Update payload hash mismatch: {file.Path}");
            }

            var newManifestPath = Path.Combine(payloadDirectory, InstallManifestName);
            if (!File.Exists(newManifestPath)) throw new InvalidDataException("Update package does not contain the target installation manifest.");
            var newManifest = JsonSerializer.Deserialize<UpdateInstallManifest>(await File.ReadAllBytesAsync(newManifestPath, ct).ConfigureAwait(false), JsonStore.Options)
                ?? throw new InvalidDataException("Target installation manifest is invalid.");
            NormalizeInstallManifestPaths(newManifest);
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
            {
                preserveValidatedStaging = request.KeepValidatedStaging;
                return new UpdateApplyResult(latestVersion.ToString(3), "", document.Files.Select(file => file.Path).ToArray(), effectiveDeletedFiles,
                    request.KeepValidatedStaging ? payloadDirectory : "");
            }

            Directory.CreateDirectory(root);
            EnsureInstallRootWritable(root);
            var backupDirectory = Path.Combine(Path.GetDirectoryName(currentManifestPath)!, "updates", "rollback", $"{currentVersion.ToString(3)}-to-{latestVersion.ToString(3)}-{request.RequestId}");
            var changedPaths = document.Files.Select(file => file.Path).Concat(effectiveDeletedFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var backedUp = new List<string>();
            var backupHashes = new Dictionary<string, string>(PackagePathCanonicalizer.PathComparer);
            var replacementStarted = false;
            Directory.CreateDirectory(backupDirectory);
            try
            {
                WriteTransactionJournal(backupDirectory, request, changedPaths, backedUp, "backing-up", document.Files, hasCurrentManifest, backupHashes);
                foreach (var relativePath in changedPaths)
                {
                    ct.ThrowIfCancellationRequested();
                    ValidateRelativePath(relativePath);
                    var existing = ResolveUnderRoot(root, relativePath);
                    if (!File.Exists(existing)) continue;
                    var backup = ResolveUnderRoot(backupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    var originalHash = await HashFileAsync(existing, ct).ConfigureAwait(false);
                    File.Copy(existing, backup, overwrite: false);
                    if (!originalHash.Equals(await HashFileAsync(backup, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Backup content changed during copy: " + relativePath);
                    backedUp.Add(relativePath);
                    backupHashes[relativePath] = originalHash;
                    WriteTransactionJournal(backupDirectory, request, changedPaths, backedUp, "backing-up", document.Files, hasCurrentManifest, backupHashes);
                    UpdateTestEnvironment.Checkpoint("backup-file", backup);
                }
                WriteTransactionJournal(backupDirectory, request, changedPaths, backedUp, "applying", document.Files, hasCurrentManifest, backupHashes);
                replacementStarted = true;
                UpdateTestEnvironment.Checkpoint("applying", backupDirectory);

                var replacedCount = 0;
                foreach (var file in document.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var destination = ResolveUnderRoot(root, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temporary = destination + "." + request.RequestId + ".new";
                    File.Copy(ResolveUnderRoot(payloadDirectory, file.Path), temporary, overwrite: false);
                    File.Move(temporary, destination, overwrite: true);
                    UpdateTestEnvironment.Checkpoint("replace-" + (++replacedCount), destination);
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
                WriteTransactionJournal(backupDirectory, request, changedPaths, backedUp, "files-verified-awaiting-startup", document.Files, hasCurrentManifest, backupHashes);
                return new UpdateApplyResult(latestVersion.ToString(3), backupDirectory, document.Files.Select(file => file.Path).ToArray(), effectiveDeletedFiles);
            }
            catch (Exception applyFailure)
            {
                if (!replacementStarted)
                {
                    WriteTransactionJournal(backupDirectory, request, changedPaths, backedUp, "backup-failed", document.Files, hasCurrentManifest, backupHashes);
                    throw;
                }
                try { await RollbackAsync(backupDirectory, root, request.RequestId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollbackFailure)
                {
                    WriteTransactionJournal(backupDirectory, request, changedPaths, backedUp, "rollback-incomplete", document.Files, hasCurrentManifest, backupHashes);
                    throw new AggregateException("Update failed and rollback is incomplete; the transaction backup was preserved.", applyFailure, rollbackFailure);
                }
                throw;
            }
        }
        finally
        {
            if (!preserveValidatedStaging)
            {
                try { Directory.Delete(transactionDirectory, recursive: true); }
                catch { /* The uniquely named staging directory can be removed on the next maintenance pass. */ }
            }
        }
    }

    public static bool IsValidFileInventory(IReadOnlyCollection<UpdateFileEntry>? files)
    {
        if (files is null || files.Count == 0 || files.Count > MaximumEntries) return false;
        var paths = new HashSet<string>(PackagePathCanonicalizer.PathComparer);
        long totalBytes = 0;
        foreach (var file in files)
        {
            string canonical;
            try { canonical = PackagePathCanonicalizer.Canonicalize(file.Path); }
            catch { return false; }
            if (!IsSha256(file.Sha256) || file.Size < 0 || !paths.Add(canonical)) return false;
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
        NormalizeInstallManifestPaths(manifest);
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
        UpdateTestEnvironment.RequirePath(root);
        UpdateTestEnvironment.RequirePath(fullBackup);
        if (journal.InstallRoot is not null && !Path.GetFullPath(journal.InstallRoot).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Rollback journal belongs to another installation.");
        // Validate every required backup before touching any destination, including files already restored.
        foreach (var relativePath in journal.BackedUp)
        {
            var backup = ResolveUnderRoot(fullBackup, relativePath);
            if (!File.Exists(backup) || journal.BackupFileHashes is not null
                && (!journal.BackupFileHashes.TryGetValue(relativePath, out var hash)
                    || !hash.Equals(await HashFileAsync(backup, ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Required rollback backup is missing or corrupt: " + relativePath);
        }
        foreach (var relativePath in journal.ChangedPaths.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var destination = ResolveUnderRoot(root, relativePath);
            var backup = ResolveUnderRoot(fullBackup, relativePath);
            var pending = destination + "." + requestId + ".new";
            if (File.Exists(pending)) File.Delete(pending);
            if (File.Exists(backup))
            {
                if (File.Exists(destination) && await FilesHaveSameContentAsync(backup, destination, ct).ConfigureAwait(false))
                    continue;
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
        if (journal.CurrentInstallManifestExists is not false)
        {
            await VerifyInstalledFilesAsync(root, journal.CurrentVersion, ct).ConfigureAwait(false);
        }
        else if (File.Exists(Path.Combine(root, InstallManifestName)))
        {
            throw new IOException("Rollback restored an installation that did not have a managed install manifest.");
        }
        WriteJournalState(journalPath, journal, "rolled-back");
    }

    public static async Task<bool> RecoverInterruptedAsync(string installRoot, CancellationToken ct = default)
    {
        UpdateTestEnvironment.RequirePath(installRoot);
        var recoveryRoot = Path.Combine(Path.GetFullPath(installRoot), "updates", "rollback");
        if (!Directory.Exists(recoveryRoot)) return false;
        _ = ResolveUnderRoot(installRoot, "updates/rollback");
        var recovered = false;
        foreach (var directory in Directory.GetDirectories(recoveryRoot).Order(StringComparer.Ordinal))
        {
            var path = ResolveUnderRoot(directory, "transaction.json");
            if (!File.Exists(path)) continue;
            var journal = JsonSerializer.Deserialize<TransactionJournal>(await File.ReadAllBytesAsync(path, ct), JsonStore.Options)
                ?? throw new InvalidDataException("Recovery journal is invalid.");
            if (journal.State is "healthy" or "rolled-back" or "backup-failed") continue;
            if (journal.State == "backing-up")
            {
                // The durable APPLYING journal is always written before the first replacement.
                WriteJournalState(path, journal, "backup-failed");
                CleanupRecoveredStaging(journal.RequestId);
                continue;
            }
            try { await RollbackAsync(directory, installRoot, journal.RequestId, ct); }
            catch (Exception ex)
            {
                WriteJournalState(path, journal, "rollback-incomplete");
                throw new UpdateOperationException(InstallExitCode.RollbackFailed, "Interrupted update could not be recovered; rollback backup is preserved.", ex);
            }
            recovered = true;
            CleanupRecoveredStaging(journal.RequestId);
        }
        return recovered;
    }

    private static void CleanupRecoveredStaging(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 80 || requestId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            throw new InvalidDataException("Recovery staging identifier is invalid.");
        foreach (var suffix in new[] { "", "-setup-preflight" })
        {
            var path = Path.Combine(UpdateTestEnvironment.TemporaryDirectory, "NetBootDhcpTool-update-" + requestId + suffix);
            if (!Directory.Exists(path)) continue;
            try
            {
                _ = PackagePathCanonicalizer.ResolveUnderRoot(UpdateTestEnvironment.TemporaryDirectory, Path.GetFileName(path));
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { /* A locked staging tree is retained; it does not invalidate restored product files. */ }
        }
    }

    public static string? GetJournalState(string installRoot, string requestId)
    {
        var directory = Path.Combine(Path.GetFullPath(installRoot), "updates", "rollback");
        if (!Directory.Exists(directory)) return null;
        foreach (var child in Directory.GetDirectories(directory))
        {
            var path = ResolveUnderRoot(child, "transaction.json");
            if (!File.Exists(path)) continue;
            var journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllBytes(path), JsonStore.Options);
            if (journal?.RequestId == requestId) return journal.State;
        }
        return null;
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private static void EnsureDiskSpaceForUpdate(
        string installRoot,
        string packagePath,
        long compressedBytes,
        long expandedBytes,
        long backupBytes,
        long largestBackupFileBytes,
        IEnumerable<UpdatePackageFile> files,
        IEnumerable<string> deletedFiles)
    {
        var payloadFiles = files.ToArray();
        var largestPayload = payloadFiles.Length == 0 ? 0 : payloadFiles.Max(file => file.Size);
        var journalReserve = checked(payloadFiles.Sum(file => 512L + PackagePathCanonicalizer.Canonicalize(file.Path).Length * 4L)
            + deletedFiles.Sum(path => 256L + PackagePathCanonicalizer.Canonicalize(path).Length * 4L));
        var installTransient = checked(backupBytes + Math.Max(0, expandedBytes - backupBytes)
            + Math.Max(largestBackupFileBytes, largestPayload) + journalReserve);
        var requiredByVolume = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        AddRequirement(packagePath, compressedBytes);
        AddRequirement(UpdateTestEnvironment.TemporaryDirectory, expandedBytes);
        AddRequirement(installRoot, installTransient);
        UpdateTestEnvironment.RequireSpace("staging", UpdateTestEnvironment.TemporaryDirectory, expandedBytes);
        UpdateTestEnvironment.RequireSpace("backup", installRoot, installTransient);

        var packageVolume = Path.GetPathRoot(Path.GetFullPath(packagePath));
        foreach (var (volume, requiredBytes) in requiredByVolume)
        {
            var availableBytes = new DriveInfo(volume).AvailableFreeSpace;
            if (string.Equals(volume, packageVolume, StringComparison.OrdinalIgnoreCase))
                availableBytes = checked(availableBytes + compressedBytes); // The verified package is already allocated and is removed after commit.
            if (availableBytes < requiredBytes)
                throw new UpdateOperationException(InstallExitCode.InsufficientSpace, $"Not enough free space on {volume}: need {requiredBytes} bytes including the signed package, staging, rollback and transaction headroom; available {availableBytes} bytes.");
        }

        void AddRequirement(string path, long bytes)
        {
            var volume = Path.GetPathRoot(Path.GetFullPath(path))
                ?? throw new IOException("Cannot determine the volume for an update path.");
            requiredByVolume[volume] = checked(requiredByVolume.GetValueOrDefault(volume) + bytes);
        }
    }

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

    private static async Task<bool> FilesHaveSameContentAsync(string firstPath, string secondPath, CancellationToken ct)
    {
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        if (!first.Exists || !second.Exists || first.Length != second.Length) return false;
        var firstHash = await HashFileAsync(firstPath, ct).ConfigureAwait(false);
        var secondHash = await HashFileAsync(secondPath, ct).ConfigureAwait(false);
        return firstHash.Equals(secondHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSha256(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void ValidateRelativePath(string? path)
    {
        _ = PackagePathCanonicalizer.Canonicalize(path);
    }

    private static string ResolveUnderRoot(string root, string relativePath)
        => PackagePathCanonicalizer.ResolveUnderRoot(root, relativePath);

    private static void NormalizeInstallManifestPaths(UpdateInstallManifest manifest)
    {
        manifest.EntryPoint = PackagePathCanonicalizer.Canonicalize(manifest.EntryPoint);
        foreach (var file in manifest.Files) file.Path = PackagePathCanonicalizer.Canonicalize(file.Path);
    }

    private static void NormalizePackageDocumentPaths(UpdatePackageDocument document)
    {
        foreach (var file in document.Files) file.Path = PackagePathCanonicalizer.Canonicalize(file.Path);
        document.DeletedFiles = document.DeletedFiles.Select(PackagePathCanonicalizer.Canonicalize).ToList();
    }

    private static void WriteTransactionJournal(string directory, UpdateApplyRequest request, IReadOnlyList<string> changedPaths, IReadOnlyList<string> backedUp, string state, IReadOnlyList<UpdatePackageFile> targetFiles, bool currentInstallManifestExists, IReadOnlyDictionary<string, string> backupFileHashes)
    {
        var journal = new
        {
            schemaVersion = 1,
            requestId = request.RequestId,
            currentVersion = request.CurrentVersion,
            currentInstallManifestExists,
            targetVersion = request.TargetVersion,
            installRoot = Path.GetFullPath(request.InstallRoot),
            state,
            changedPaths,
            backedUp,
            backupFileHashes,
            targetFileHashes = targetFiles.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase),
            updatedAt = DateTimeOffset.UtcNow
        };
        var path = Path.Combine(directory, "transaction.json");
        UpdateTestEnvironment.Checkpoint("journal-write-" + state, path);
        UpdateResultProtocol.WriteDurably(path, JsonSerializer.Serialize(journal, JsonStore.Options));
    }

    private sealed record TransactionJournal(string RequestId, string CurrentVersion, bool? CurrentInstallManifestExists, string TargetVersion, string State, string[] ChangedPaths, string[] BackedUp, Dictionary<string, string> TargetFileHashes, string? InstallRoot = null, Dictionary<string, string>? BackupFileHashes = null);

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
        UpdateResultProtocol.WriteDurably(journalPath, JsonSerializer.Serialize(replacement,
            new JsonSerializerOptions(JsonStore.Options) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
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
