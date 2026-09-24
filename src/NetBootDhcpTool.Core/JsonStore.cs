using System.Collections;
using System.Text;
using System.Text.Json;

namespace NetBootDhcpTool.Core;

public enum DataLoadStatus
{
    Missing,
    LoadedEmpty,
    Loaded,
    RestoredFromBackup,
    Failed
}

public sealed record DataLoadResult<T>(
    DataLoadStatus Status,
    T? Value,
    string? SourcePath,
    Exception? Error = null,
    Exception? BackupError = null)
{
    public bool HasData => Status is DataLoadStatus.LoadedEmpty or DataLoadStatus.Loaded or DataLoadStatus.RestoredFromBackup;
}

public static class JsonStore
{
    private static readonly object SaveLock = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static DataLoadResult<T> Load<T>(string path, ILogger? logger = null) =>
        LoadUsing(path, text => JsonSerializer.Deserialize<T>(text, Options)
            ?? throw new InvalidDataException("JSON document deserialized to null."), logger);

    public static DataLoadResult<T> LoadUsing<T>(string path, Func<string, T> deserialize, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(deserialize);
        var backup = path + ".bak";
        var primary = TryRead(path, deserialize);
        if (primary.Success)
        {
            var state = IsEmpty(primary.Value) ? DataLoadStatus.LoadedEmpty : DataLoadStatus.Loaded;
            logger?.Info($"Loaded JSON: path={path} state={state}");
            return new DataLoadResult<T>(state, primary.Value, path);
        }

        var backupResult = TryRead(backup, deserialize);
        if (backupResult.Success)
        {
            logger?.Warn($"Loaded JSON backup after primary failure: path={path} backup={backup}");
            return new DataLoadResult<T>(DataLoadStatus.RestoredFromBackup, backupResult.Value, backup, primary.Error);
        }

        if (primary.Missing && backupResult.Missing)
            return new DataLoadResult<T>(DataLoadStatus.Missing, default, null);

        if (!primary.Missing) logger?.Error($"Load JSON failed: {path}", primary.Error!);
        if (!backupResult.Missing) logger?.Error($"Load JSON backup failed: {backup}", backupResult.Error!);
        var error = primary.Error ?? backupResult.Error ?? new InvalidDataException("No valid JSON source was found.");
        return new DataLoadResult<T>(DataLoadStatus.Failed, default, null, error, backupResult.Error);
    }

    public static T LoadOrDefault<T>(string path, T fallback, ILogger? logger = null)
    {
        var result = Load<T>(path, logger);
        if (result.HasData) return result.Value!;
        if (result.Status == DataLoadStatus.Missing)
        {
            Save(path, fallback);
            return fallback;
        }

        logger?.Warn($"Using in-memory fallback without overwriting unreadable JSON: {path}");
        return fallback;
    }

    public static void Save<T>(string path, T value)
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var backup = path + ".bak";
            var primary = TryRead(path, text => JsonSerializer.Deserialize<T>(text, Options)
                ?? throw new InvalidDataException("JSON document deserialized to null."));
            var priorBackup = TryRead(backup, text => JsonSerializer.Deserialize<T>(text, Options)
                ?? throw new InvalidDataException("JSON backup deserialized to null."));

            if (!primary.Missing && !primary.Success && !priorBackup.Success)
                throw new InvalidDataException($"Refusing to overwrite unreadable JSON without a valid backup: {path}", primary.Error ?? priorBackup.Error);
            if (primary.Missing && !priorBackup.Missing && !priorBackup.Success)
                throw new InvalidDataException($"Refusing to overwrite JSON while its only backup is unreadable: {backup}", priorBackup.Error);

            var temporary = NewTemporaryPath(path);
            try
            {
                WriteFlushed(temporary, JsonSerializer.Serialize(value, Options));
                if (primary.Success)
                {
                    File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
                }
                else if (primary.Missing)
                {
                    File.Move(temporary, path);
                }
                else
                {
                    PreserveDamagedSource(path);
                    // The valid .bak is the recovery copy. Replacing a damaged primary
                    // without a second backup keeps that source intact.
                    File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
            }
            finally
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>
    /// Writes stores whose ordinary File.Replace backup could expose protected fields.
    /// The caller supplies a validated, already-sanitized prior value; that backup is
    /// committed before the primary file is changed.
    /// </summary>
    public static void SaveWithVerifiedBackup<T>(string path, T value, T? safeBackup, bool hasSafeBackup, bool primaryIsValid)
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            if (File.Exists(path) && !hasSafeBackup)
                throw new InvalidDataException($"Refusing to replace existing data without a verified safe backup: {path}");

            var backup = path + ".bak";
            var primaryTemp = NewTemporaryPath(path);
            var backupTemp = NewTemporaryPath(backup);
            try
            {
                if (hasSafeBackup)
                {
                    WriteFlushed(backupTemp, JsonSerializer.Serialize(safeBackup, Options));
                    if (File.Exists(backup)) File.Replace(backupTemp, backup, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    else File.Move(backupTemp, backup);
                }

                if (File.Exists(path) && !primaryIsValid) PreserveDamagedSource(path);
                WriteFlushed(primaryTemp, JsonSerializer.Serialize(value, Options));
                if (File.Exists(path)) File.Replace(primaryTemp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else File.Move(primaryTemp, path);
            }
            finally
            {
                TryDelete(primaryTemp);
                TryDelete(backupTemp);
            }
        }
    }

    private static FileReadResult<T> TryRead<T>(string path, Func<string, T> deserialize)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return FileReadResult<T>.MissingResult; }
        catch (DirectoryNotFoundException) { return FileReadResult<T>.MissingResult; }
        catch (Exception ex) { return FileReadResult<T>.Failure(ex); }

        try { return FileReadResult<T>.Valid(deserialize(text)); }
        catch (Exception ex) { return FileReadResult<T>.Failure(ex); }
    }

    private static bool IsEmpty<T>(T? value) => value is ICollection collection && collection.Count == 0;

    private static string NewTemporaryPath(string path) => path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static void WriteFlushed(string path, string contents)
    {
        File.WriteAllText(path, contents, Utf8);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void PreserveDamagedSource(string path)
    {
        var archive = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json";
        File.Copy(path, archive, overwrite: false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record FileReadResult<T>(bool Missing, bool Success, T? Value, Exception? Error)
    {
        public static FileReadResult<T> MissingResult => new(true, false, default, null);
        public static FileReadResult<T> Valid(T value) => new(false, true, value, null);
        public static FileReadResult<T> Failure(Exception error) => new(false, false, default, error);
    }
}
