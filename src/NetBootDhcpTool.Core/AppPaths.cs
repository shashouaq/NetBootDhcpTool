namespace NetBootDhcpTool.Core;

public sealed class AppPaths
{
    private readonly List<string> _migrationWarnings = [];

    public AppPaths(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        var testDataDirectory = Environment.GetEnvironmentVariable("NETBOOT_DATA_DIRECTORY");
        DataDirectory = string.IsNullOrWhiteSpace(testDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBootDhcpTool")
            : Path.GetFullPath(testDataDirectory);
        ConfigDirectory = Path.Combine(DataDirectory, "config");
        I18nDirectory = Path.Combine(baseDirectory, "i18n");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        AssetsDirectory = Path.Combine(baseDirectory, "assets");
        DocsDirectory = Path.Combine(baseDirectory, "docs");
        SettingsFile = Path.Combine(ConfigDirectory, "appsettings.json");
        FavoritesFile = Path.Combine(ConfigDirectory, "favorites.json");
        AdapterBackupsFile = Path.Combine(ConfigDirectory, "adapter-backups.json");
        MacBackupsFile = Path.Combine(ConfigDirectory, "mac-backups.json");
        StaticRouteSessionFile = Path.Combine(ConfigDirectory, "static-route-session.json");
        NetworkHistoryFile = Path.Combine(DataDirectory, "network-history.json");
        OperationHistoryFile = Path.Combine(DataDirectory, "operation-history.json");
        ProfilesFile = Path.Combine(ConfigDirectory, "network-profiles.json");
        MigrateLegacyData();
    }

    public string BaseDirectory { get; }
    public string DataDirectory { get; }
    public string ConfigDirectory { get; }
    public string I18nDirectory { get; }
    public string LogsDirectory { get; }
    public string AssetsDirectory { get; }
    public string DocsDirectory { get; }
    public string SettingsFile { get; }
    public string FavoritesFile { get; }
    public string AdapterBackupsFile { get; }
    public string MacBackupsFile { get; }
    public string StaticRouteSessionFile { get; }
    public string NetworkHistoryFile { get; }
    public string OperationHistoryFile { get; }
    public string ProfilesFile { get; }
    public IReadOnlyList<string> MigrationWarnings => _migrationWarnings;

    public void Ensure()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(I18nDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(DocsDirectory);
    }

    private void MigrateLegacyData()
    {
        try
        {
            var legacyConfigDir = Path.Combine(BaseDirectory, "config");
            var legacyLogsDir = Path.Combine(BaseDirectory, "logs");
            var legacySettings = Path.Combine(legacyConfigDir, "appsettings.json");
            var legacyFavorites = Path.Combine(legacyConfigDir, "favorites.json");

            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(ConfigDirectory);
            Directory.CreateDirectory(LogsDirectory);

            CopyLegacyFile(legacySettings, SettingsFile, "settings");
            CopyLegacyFile(legacyFavorites, FavoritesFile, "favorites");
            if (Directory.Exists(legacyLogsDir))
            {
                foreach (var file in Directory.EnumerateFiles(legacyLogsDir, "*.log", SearchOption.TopDirectoryOnly))
                {
                    var dest = Path.Combine(LogsDirectory, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        CopyLegacyFile(file, dest, "log");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _migrationWarnings.Add($"data directory: {ex.Message}");
        }
    }

    private void CopyLegacyFile(string source, string destination, string kind)
    {
        if (File.Exists(destination) || !File.Exists(source)) return;
        try
        {
            File.Copy(source, destination, overwrite: false);
        }
        catch (Exception ex)
        {
            _migrationWarnings.Add($"{kind} ({Path.GetFileName(source)}): {ex.Message}");
        }
    }
}
