using System.IO.Compression;
using System.Text.Json;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace NetBootDhcpTool.Core;

public enum UpdatePackageFormat
{
    Zip,
    SevenZip
}

internal sealed record PackageArchiveEntry(string Key, string CanonicalPath, long Size, bool IsDirectory, bool IsLink);

internal sealed record PackageInspection(
    UpdatePackageDocument Document,
    IReadOnlyDictionary<string, PackageArchiveEntry> PayloadEntries,
    IReadOnlyDictionary<string, PackageArchiveEntry> DirectoryEntries,
    int EntryCount);

/// <summary>Format-specific archive access; descriptor validation, payload validation and transaction logic stay shared.</summary>
internal interface IPackageExtractor
{
    UpdatePackageFormat Format { get; }
    Task<PackageInspection> InspectAsync(Stream packageStream, CancellationToken cancellationToken);
    Task ExtractPayloadAsync(Stream packageStream, PackageInspection inspection, string stagingRoot, CancellationToken cancellationToken);
}

internal static class PackageExtractorFactory
{
    public static IPackageExtractor Create(UpdatePackageFormat format) => format switch
    {
        UpdatePackageFormat.Zip => new ZipPackageExtractor(),
        UpdatePackageFormat.SevenZip => new SevenZipPackageExtractor(),
        _ => throw new InvalidDataException("Update package format is unsupported.")
    };

    public static UpdatePackageFormat FromFileName(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".zip" => UpdatePackageFormat.Zip,
        ".7z" => UpdatePackageFormat.SevenZip,
        _ => throw new InvalidDataException("Update package extension is unsupported.")
    };
}

internal abstract class PackageExtractorBase : IPackageExtractor
{
    protected const int MaximumEntries = UpdatePackageApplier.MaximumEntries;
    protected const long MaximumExpandedBytes = UpdatePackageApplier.MaximumExpandedBytes;
    protected const string DescriptorName = UpdatePackageApplier.PackageDocumentName;

    public abstract UpdatePackageFormat Format { get; }
    public abstract Task<PackageInspection> InspectAsync(Stream packageStream, CancellationToken cancellationToken);
    public abstract Task ExtractPayloadAsync(Stream packageStream, PackageInspection inspection, string stagingRoot, CancellationToken cancellationToken);

    protected static async Task<UpdatePackageDocument> ReadDocumentAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (content.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("Update package metadata is too large.");
            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return JsonSerializer.Deserialize<UpdatePackageDocument>(content.ToArray(), JsonStore.Options)
            ?? throw new InvalidDataException("Update package manifest is invalid.");
    }

    protected static PackageInspection ValidateInspection(UpdatePackageDocument document, IReadOnlyList<PackageArchiveEntry> archiveEntries)
    {
        if (archiveEntries.Count is 0 or > MaximumEntries) throw new InvalidDataException("Update package entry count is invalid.");
        var descriptorCount = archiveEntries.Count(entry => entry.Key.Equals(DescriptorName, StringComparison.Ordinal));
        if (descriptorCount != 1) throw new InvalidDataException("Update package manifest is missing or duplicated.");

        var payload = new Dictionary<string, PackageArchiveEntry>(PackagePathCanonicalizer.PathComparer);
        var directories = new Dictionary<string, PackageArchiveEntry>(PackagePathCanonicalizer.PathComparer);
        var canonicalPaths = new HashSet<string>(PackagePathCanonicalizer.PathComparer);
        foreach (var entry in archiveEntries)
        {
            if (entry.Key.Equals(DescriptorName, StringComparison.Ordinal))
            {
                if (entry.IsDirectory || entry.IsLink) throw new InvalidDataException("Update package manifest entry is invalid.");
                continue;
            }
            if (IsPayloadRoot(entry.Key, entry.IsDirectory)) continue;
            if (!TryGetPayloadPath(entry.Key, entry.IsDirectory, out var archivePath))
                throw new InvalidDataException("Update package contains an unexpected archive entry.");
            var canonical = PackagePathCanonicalizer.Canonicalize(archivePath);
            if (entry.IsLink) throw new InvalidDataException("Update package links are not supported.");
            if (!canonicalPaths.Add(canonical))
                throw new InvalidDataException($"Update package contains duplicate canonical path: {canonical}");
            if (entry.IsDirectory) directories.Add(canonical, entry);
            else payload.Add(canonical, entry);
        }

        var expected = new Dictionary<string, UpdatePackageFile>(PackagePathCanonicalizer.PathComparer);
        long expandedBytes = 0;
        foreach (var file in document.Files ?? [])
        {
            var canonical = PackagePathCanonicalizer.Canonicalize(file.Path);
            if (file.Size < 0 || !UpdatePackageApplier.IsSha256(file.Sha256) || !expected.TryAdd(canonical, file))
                throw new InvalidDataException($"Update package manifest contains an invalid or duplicate canonical path: {canonical}");
            expandedBytes = checked(expandedBytes + file.Size);
            if (expandedBytes > MaximumExpandedBytes) throw new InvalidDataException("Update package expands beyond the supported limit.");
        }
        if (expected.Count == 0 || expected.Count != payload.Count
            || expected.Keys.Any(path => !payload.ContainsKey(path)))
            throw new InvalidDataException("Update package payload does not match its package manifest.");

        foreach (var (path, file) in expected)
        {
            var entry = payload[path];
            if (entry.Size != file.Size) throw new InvalidDataException($"Update package payload size mismatch: {path}");
        }
        EnsureNoFileParentCollision(expected.Keys);
        return new PackageInspection(document, payload, directories, archiveEntries.Count);
    }

    protected static async Task CopyEntryToStageAsync(Stream input, string stageRoot, string canonicalPath, long expectedSize, CancellationToken cancellationToken)
    {
        var destination = PackagePathCanonicalizer.ResolveUnderRoot(stageRoot, canonicalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            copied = checked(copied + read);
            if (copied > expectedSize) throw new InvalidDataException($"Update payload exceeds its declared size: {canonicalPath}");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (copied != expectedSize) throw new InvalidDataException($"Update payload size mismatch: {canonicalPath}");
    }

    protected static void PrepareStage(string stagingRoot)
    {
        var fullRoot = Path.GetFullPath(stagingRoot);
        if (Directory.Exists(fullRoot) && Directory.EnumerateFileSystemEntries(fullRoot).Any())
            throw new IOException("Update staging directory must be new or empty.");
        Directory.CreateDirectory(fullRoot);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Update staging root cannot be a reparse point.");
    }

    protected static bool IsPayloadRoot(string key, bool isDirectory)
    {
        if (!isDirectory) return false;
        var normalized = key.Replace('\\', '/');
        return normalized.Equals("payload", StringComparison.Ordinal) || normalized.Equals("payload/", StringComparison.Ordinal);
    }

    protected static bool TryGetPayloadPath(string key, bool isDirectory, out string relativePath)
    {
        var normalizedKey = key.Replace('\\', '/');
        if (!normalizedKey.StartsWith("payload/", StringComparison.Ordinal))
        {
            relativePath = "";
            return false;
        }
        relativePath = normalizedKey["payload/".Length..];
        if (isDirectory && relativePath.EndsWith('/')) relativePath = relativePath[..^1];
        return true;
    }

    protected static PackageArchiveEntry ResolveDirectoryRecord(string key, PackageInspection inspection)
    {
        if (!TryGetPayloadPath(key, isDirectory: true, out var path))
            throw new InvalidDataException("Update archive contains an unexpected directory entry.");
        var canonical = PackagePathCanonicalizer.Canonicalize(path);
        if (!inspection.DirectoryEntries.TryGetValue(canonical, out var record)
            || !record.Key.Equals(key, StringComparison.Ordinal))
            throw new InvalidDataException("Update archive directory index changed after inspection.");
        return record;
    }

    private static void EnsureNoFileParentCollision(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, PackagePathCanonicalizer.PathComparer);
        foreach (var path in set)
        {
            var separator = path.IndexOf('/');
            while (separator >= 0)
            {
                if (set.Contains(path[..separator])) throw new InvalidDataException($"Update package contains a file/directory path collision: {path}");
                separator = path.IndexOf('/', separator + 1);
            }
        }
    }
}

internal sealed class ZipPackageExtractor : PackageExtractorBase
{
    public override UpdatePackageFormat Format => UpdatePackageFormat.Zip;

    public override async Task<PackageInspection> InspectAsync(Stream packageStream, CancellationToken cancellationToken)
    {
        packageStream.Position = 0;
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        var records = new List<PackageArchiveEntry>(archive.Entries.Count);
        UpdatePackageDocument? document = null;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            var isLink = unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
            if (entry.FullName.Equals(DescriptorName, StringComparison.Ordinal))
            {
                if (document is not null) throw new InvalidDataException("Update package manifest is duplicated.");
                await using var stream = entry.Open();
                document = await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
                records.Add(new PackageArchiveEntry(entry.FullName, DescriptorName, entry.Length, false, isLink));
                continue;
            }
            records.Add(new PackageArchiveEntry(entry.FullName, "", entry.Length, directory, isLink));
        }
        if (document is null) throw new InvalidDataException("Update package manifest is missing.");
        var canonicalRecords = CanonicalizeArchivePaths(records);
        return ValidateInspection(document, canonicalRecords);
    }

    public override async Task ExtractPayloadAsync(Stream packageStream, PackageInspection inspection, string stagingRoot, CancellationToken cancellationToken)
    {
        PrepareStage(stagingRoot);
        packageStream.Position = 0;
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        var seen = new HashSet<string>(PackagePathCanonicalizer.PathComparer);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.Equals(DescriptorName, StringComparison.Ordinal)) continue;
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (IsPayloadRoot(entry.FullName, directory)) continue;
            if (directory)
            {
                _ = ResolveDirectoryRecord(entry.FullName, inspection);
                continue;
            }
            var record = ResolvePayloadRecord(entry.FullName, false, inspection);
            if (!seen.Add(record.CanonicalPath)) throw new InvalidDataException($"Update package contains duplicate canonical path: {record.CanonicalPath}");
            await using var input = entry.Open();
            await CopyEntryToStageAsync(input, stagingRoot, record.CanonicalPath, record.Size, cancellationToken).ConfigureAwait(false);
        }
        if (seen.Count != inspection.PayloadEntries.Count) throw new InvalidDataException("ZIP archive changed after inspection.");
    }

    private static IReadOnlyList<PackageArchiveEntry> CanonicalizeArchivePaths(IEnumerable<PackageArchiveEntry> records)
    {
        var result = new List<PackageArchiveEntry>();
        foreach (var entry in records)
        {
            if (entry.Key.Equals(DescriptorName, StringComparison.Ordinal)) { result.Add(entry); continue; }
            if (IsPayloadRoot(entry.Key, entry.IsDirectory)) { result.Add(entry with { CanonicalPath = "" }); continue; }
            if (!TryGetPayloadPath(entry.Key, entry.IsDirectory, out var path)) { result.Add(entry); continue; }
            result.Add(entry with { CanonicalPath = PackagePathCanonicalizer.Canonicalize(path) });
        }
        return result;
    }

    private static PackageArchiveEntry ResolvePayloadRecord(string key, bool isDirectory, PackageInspection inspection)
    {
        if (!TryGetPayloadPath(key, isDirectory, out var path)) throw new InvalidDataException("ZIP archive changed after inspection.");
        var canonical = PackagePathCanonicalizer.Canonicalize(path);
        if (!inspection.PayloadEntries.TryGetValue(canonical, out var record) || record.IsDirectory != isDirectory || record.Key != key)
            throw new InvalidDataException("ZIP archive changed after inspection.");
        return record;
    }
}

internal sealed class SevenZipPackageExtractor : PackageExtractorBase
{
    public override UpdatePackageFormat Format => UpdatePackageFormat.SevenZip;

    public override async Task<PackageInspection> InspectAsync(Stream packageStream, CancellationToken cancellationToken)
    {
        try
        {
            packageStream.Position = 0;
            using var archive = SevenZipArchive.OpenArchive(packageStream, new ReaderOptions { LeaveStreamOpen = true });
            var sourceEntries = archive.Entries.ToArray();
            var records = new List<PackageArchiveEntry>(sourceEntries.Length);
            UpdatePackageDocument? document = null;
            foreach (var entry in sourceEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = entry.Key ?? throw new InvalidDataException("7z package contains an empty archive path.");
                var sevenEntry = entry as SevenZipArchiveEntry ?? throw new InvalidDataException("7z package contains an unsupported entry type.");
                var link = sevenEntry.LinkTarget is not null || sevenEntry.IsAnti || sevenEntry.IsEncrypted || !sevenEntry.IsComplete
                    || (sevenEntry.Attrib.HasValue && ((Convert.ToUInt32(sevenEntry.Attrib.Value) & (uint)FileAttributes.ReparsePoint) != 0
                        || (Convert.ToUInt32(sevenEntry.Attrib.Value) & 0xF000) == 0xA000));
                if (key.Equals(DescriptorName, StringComparison.Ordinal))
                {
                    if (document is not null) throw new InvalidDataException("Update package manifest is duplicated.");
                    using var input = entry.OpenEntryStream();
                    document = await ReadDocumentAsync(input, cancellationToken).ConfigureAwait(false);
                    records.Add(new PackageArchiveEntry(key, DescriptorName, sevenEntry.Size, sevenEntry.IsDirectory, link));
                }
                else records.Add(new PackageArchiveEntry(key, "", sevenEntry.Size, sevenEntry.IsDirectory, link));
            }
            if (document is null) throw new InvalidDataException("Update package manifest is missing.");
            return ValidateInspection(document, CanonicalizeArchivePaths(records));
        }
        catch (SharpCompress.Common.SharpCompressException ex)
        {
            throw new InvalidDataException("7z update archive is invalid or incomplete.", ex);
        }
    }

    public override async Task ExtractPayloadAsync(Stream packageStream, PackageInspection inspection, string stagingRoot, CancellationToken cancellationToken)
    {
        try
        {
            PrepareStage(stagingRoot);
            packageStream.Position = 0;
            using var archive = SevenZipArchive.OpenArchive(packageStream, new ReaderOptions { LeaveStreamOpen = true });
            using var reader = archive.ExtractAllEntries();
            var seen = new HashSet<string>(PackagePathCanonicalizer.PathComparer);
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                var key = entry.Key ?? throw new InvalidDataException("7z reader returned an empty archive path.");
                if (key.Equals(DescriptorName, StringComparison.Ordinal)) continue;
                if (IsPayloadRoot(key, entry.IsDirectory)) continue;
                if (entry.IsDirectory)
                {
                    _ = ResolveDirectoryRecord(key, inspection);
                    continue;
                }
                var record = ResolvePayloadRecord(key, entry.IsDirectory, inspection);
                if (!seen.Add(record.CanonicalPath)) throw new InvalidDataException($"Update package contains duplicate canonical path: {record.CanonicalPath}");
                using var input = reader.OpenEntryStream();
                await CopyEntryToStageAsync(input, stagingRoot, record.CanonicalPath, record.Size, cancellationToken).ConfigureAwait(false);
            }
            if (seen.Count != inspection.PayloadEntries.Count) throw new InvalidDataException("7z archive changed after inspection.");
        }
        catch (SharpCompress.Common.SharpCompressException ex)
        {
            throw new InvalidDataException("7z update archive is invalid or incomplete.", ex);
        }
    }

    private static IReadOnlyList<PackageArchiveEntry> CanonicalizeArchivePaths(IEnumerable<PackageArchiveEntry> records)
    {
        var result = new List<PackageArchiveEntry>();
        foreach (var entry in records)
        {
            if (entry.Key.Equals(DescriptorName, StringComparison.Ordinal)) { result.Add(entry); continue; }
            if (IsPayloadRoot(entry.Key, entry.IsDirectory)) { result.Add(entry with { CanonicalPath = "" }); continue; }
            if (!TryGetPayloadPath(entry.Key, entry.IsDirectory, out var path)) { result.Add(entry); continue; }
            result.Add(entry with { CanonicalPath = PackagePathCanonicalizer.Canonicalize(path) });
        }
        return result;
    }

    private static PackageArchiveEntry ResolvePayloadRecord(string key, bool isDirectory, PackageInspection inspection)
    {
        if (!TryGetPayloadPath(key, isDirectory, out var path)) throw new InvalidDataException("7z archive changed after inspection.");
        var canonical = PackagePathCanonicalizer.Canonicalize(path);
        if (!inspection.PayloadEntries.TryGetValue(canonical, out var record))
            throw new InvalidDataException($"7z archive changed after inspection: unexpected payload path {key}.");
        if (record.IsDirectory != isDirectory || !record.Key.Equals(key, StringComparison.Ordinal))
            throw new InvalidDataException($"7z archive entry index changed: actual={key}, indexed={record.Key}, actualDirectory={isDirectory}, indexedDirectory={record.IsDirectory}.");
        return record;
    }
}
