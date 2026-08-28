using System.Text.Json;

namespace NetBootDhcpTool.Core;

/// <summary>
/// Loads protected personal favorites and manufacturer-published public presets.
/// Public factory-default credentials are intentionally kept in plaintext because
/// they are published preset values; personal favorites retain DPAPI storage.
/// </summary>
public static class FavoriteStore
{
    public static List<FavoriteConfig> Load(string path, ILogger? logger = null, bool migrateLegacy = false)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Favorite file must contain an array");

            var favorites = new List<FavoriteConfig>();
            var legacyFound = false;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var favorite = element.Deserialize<FavoriteConfig>(JsonStore.Options) ?? new FavoriteConfig();
                if (element.TryGetProperty("Password", out var legacyPassword) && legacyPassword.ValueKind == JsonValueKind.String)
                {
                    favorite.Password = legacyPassword.GetString() ?? "";
                    legacyFound = true;
                }

                if (!string.IsNullOrWhiteSpace(favorite.PublicPassword))
                {
                    favorite.Password = favorite.PublicPassword;
                    favorite.PasswordUnavailable = false;
                }

                if (string.IsNullOrWhiteSpace(favorite.Password) && !string.IsNullOrWhiteSpace(favorite.ProtectedPassword))
                {
                    if (CredentialProtector.TryUnprotect(favorite.ProtectedPassword, out var password))
                    {
                        favorite.Password = password;
                        favorite.PasswordUnavailable = false;
                    }
                    else
                    {
                        favorite.Password = "";
                        favorite.PasswordUnavailable = true;
                        logger?.Warn($"Favorite credential unavailable; re-entry required: {favorite.Name}");
                    }
                }
                favorites.Add(favorite);
            }

            if (migrateLegacy && legacyFound)
            {
                try
                {
                    Save(path, favorites, logger);
                    logger?.Info("Legacy personal favorite credentials migrated to Windows DPAPI; public presets remain plaintext");
                }
                catch (Exception migrationEx)
                {
                    logger?.Error($"Migrate legacy favorite credentials failed: {path}", migrationEx);
                }
            }
            return favorites;
        }
        catch (Exception ex)
        {
            logger?.Error($"Load favorites failed: {path}", ex);
            var backup = path + ".bak";
            if (!File.Exists(backup)) return [];
            try
            {
                return Load(backup, logger, migrateLegacy: false);
            }
            catch (Exception backupEx)
            {
                logger?.Error($"Load favorites backup failed: {backup}", backupEx);
                return [];
            }
        }
    }

    public static void Save(string path, IEnumerable<FavoriteConfig> favorites, ILogger? logger = null)
    {
        var persistent = favorites.Select(ToPersistent).ToList();
        JsonStore.Save(path, persistent);
        TryDeleteFavoriteBackup(path, logger);
        logger?.Info("Favorites saved; public manufacturer presets are plaintext and personal credentials remain protected");
    }

    public static void SaveCredentialFreeExport(string path, IEnumerable<FavoriteConfig> favorites)
    {
        var export = favorites.Select(x =>
        {
            var clone = Clone(x);
            clone.Password = "";
            clone.ProtectedPassword = "";
            clone.PasswordUnavailable = false;
            return clone;
        }).ToList();
        JsonStore.Save(path, export);
        TryDeleteFavoriteBackup(path, null);
    }

    private static FavoriteConfig ToPersistent(FavoriteConfig source)
    {
        var clone = Clone(source);
        if (source.IsPublicDefault)
        {
            clone.PublicPassword = source.Password;
            clone.ProtectedPassword = "";
        }
        else if (!string.IsNullOrWhiteSpace(source.Password))
        {
            clone.ProtectedPassword = CredentialProtector.Protect(source.Password);
            clone.PublicPassword = "";
        }
        else if (!source.PasswordUnavailable)
        {
            clone.ProtectedPassword = "";
            clone.PublicPassword = "";
        }
        clone.Password = "";
        clone.PasswordUnavailable = false;
        return clone;
    }

    private static void TryDeleteFavoriteBackup(string path, ILogger? logger)
    {
        try
        {
            var backup = path + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch (Exception ex)
        {
            logger?.Warn($"Could not remove legacy plaintext favorite backup: {ex.Message}");
        }
    }

    public static FavoriteConfig Clone(FavoriteConfig source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        DeviceNumber = source.DeviceNumber,
        SerialNumber = source.SerialNumber,
        RemarkName = source.RemarkName,
        Description = source.Description,
        Username = source.Username,
        PublicPassword = source.PublicPassword,
        ProtectedPassword = source.ProtectedPassword,
        IsPublicDefault = source.IsPublicDefault,
        PreferHttps = source.PreferHttps,
        MemoryText = source.MemoryText,
        AdapterName = source.AdapterName,
        AdapterMac = source.AdapterMac,
        LocalIp = source.LocalIp,
        SubnetMask = source.SubnetMask,
        Gateway = source.Gateway,
        Dns = source.Dns,
        TargetIp = source.TargetIp,
        CustomFields = (source.CustomFields ?? []).Select(x => new FavoriteField { Name = x.Name, Value = x.Value }).ToList(),
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        LastUsedAt = source.LastUsedAt,
        Password = source.Password,
        PasswordUnavailable = source.PasswordUnavailable
    };
}
