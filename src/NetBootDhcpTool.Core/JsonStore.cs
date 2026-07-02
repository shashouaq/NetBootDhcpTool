using System.Text.Json;

namespace NetBootDhcpTool.Core;

public static class JsonStore
{
    private static readonly object SaveLock = new();
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static T LoadOrDefault<T>(string path, T fallback, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                Save(path, fallback);
                return fallback;
            }

            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(text, Options) ?? fallback;
        }
        catch (Exception ex)
        {
            logger?.Error($"Load JSON failed: {path}", ex);
            try
            {
                var backup = path + ".bak";
                if (File.Exists(backup)) return JsonSerializer.Deserialize<T>(File.ReadAllText(backup), Options) ?? fallback;
            }
            catch (Exception backupEx)
            {
                logger?.Error($"Load JSON backup failed: {path}.bak", backupEx);
            }
            return fallback;
        }
    }

    public static void Save<T>(string path, T value)
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            var backup = path + ".bak";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.WriteThrough)) stream.Flush(flushToDisk: true);
            if (File.Exists(path)) File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
    }
}
