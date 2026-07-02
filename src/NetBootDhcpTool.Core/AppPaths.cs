namespace NetBootDhcpTool.Core;

public sealed class AppPaths
{
    public AppPaths(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBootDhcpTool");
        ConfigDirectory = Path.Combine(DataDirectory, "config");
        I18nDirectory = Path.Combine(baseDirectory, "i18n");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        AssetsDirectory = Path.Combine(baseDirectory, "assets");
        DocsDirectory = Path.Combine(baseDirectory, "docs");
        SettingsFile = Path.Combine(ConfigDirectory, "appsettings.json");
        FavoritesFile = Path.Combine(ConfigDirectory, "favorites.json");
        AdapterBackupsFile = Path.Combine(ConfigDirectory, "adapter-backups.json");
        NetworkHistoryFile = Path.Combine(DataDirectory, "network-history.json");
        OperationHistoryFile = Path.Combine(DataDirectory, "operation-history.json");
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
    public string NetworkHistoryFile { get; }
    public string OperationHistoryFile { get; }

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

            if (!File.Exists(SettingsFile) && File.Exists(legacySettings))
            {
                File.Copy(legacySettings, SettingsFile, overwrite: false);
            }
            if (!File.Exists(FavoritesFile) && File.Exists(legacyFavorites))
            {
                File.Copy(legacyFavorites, FavoritesFile, overwrite: false);
            }
            if (Directory.Exists(legacyLogsDir))
            {
                foreach (var file in Directory.EnumerateFiles(legacyLogsDir, "*.log", SearchOption.TopDirectoryOnly))
                {
                    var dest = Path.Combine(LogsDirectory, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        File.Copy(file, dest, overwrite: false);
                    }
                }
            }
        }
        catch
        {
        }
    }
}
