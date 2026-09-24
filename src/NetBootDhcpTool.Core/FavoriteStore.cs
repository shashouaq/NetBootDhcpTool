using System.Text.Json;

namespace NetBootDhcpTool.Core;

public sealed record FavoriteLoadResult(
    DataLoadStatus Status,
    List<FavoriteConfig> Favorites,
    string? SourcePath,
    Exception? Error = null,
    Exception? BackupError = null,
    Exception? MigrationError = null)
{
    public bool HasData => Status is DataLoadStatus.LoadedEmpty or DataLoadStatus.Loaded or DataLoadStatus.RestoredFromBackup;
}

/// <summary>
/// Loads protected personal favorites and manufacturer-published public presets.
/// Public factory-default credentials are intentionally kept in plaintext because
/// they are published preset values; personal favorites retain DPAPI storage.
/// </summary>
public static class FavoriteStore
{
    private sealed record ParsedFavorites(List<FavoriteConfig> Favorites, bool LegacyCredentialsFound);

    public static FavoriteLoadResult LoadWithStatus(string path, ILogger? logger = null, bool migrateLegacy = false)
    {
        var result = JsonStore.LoadUsing(path, text => ParseFavorites(text, logger), logger);
        if (!result.HasData)
            return new FavoriteLoadResult(result.Status, [], result.SourcePath, result.Error, result.BackupError);

        var parsed = result.Value!;
        var status = result.Status == DataLoadStatus.Loaded && parsed.Favorites.Count == 0
            ? DataLoadStatus.LoadedEmpty
            : result.Status;
        Exception? migrationError = null;
        if (migrateLegacy && parsed.LegacyCredentialsFound)
        {
            try
            {
                Save(path, parsed.Favorites, logger);
                logger?.Info("Legacy personal favorite credentials migrated to Windows DPAPI; public presets remain plaintext");
            }
            catch (Exception ex)
            {
                migrationError = ex;
                logger?.Error($"Migrate legacy favorite credentials failed: {path}", ex);
            }
        }

        return new FavoriteLoadResult(status, parsed.Favorites, result.SourcePath, result.Error, result.BackupError, migrationError);
    }

    public static List<FavoriteConfig> Load(string path, ILogger? logger = null, bool migrateLegacy = false) =>
        LoadWithStatus(path, logger, migrateLegacy).Favorites;

    private static ParsedFavorites ParseFavorites(string text, ILogger? logger)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Favorite file must contain an array");

        var favorites = new List<FavoriteConfig>();
        var legacyFound = false;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Favorite entries must be JSON objects");
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

        return new ParsedFavorites(favorites, legacyFound);
    }

    public static void Save(string path, IEnumerable<FavoriteConfig> favorites, ILogger? logger = null)
    {
        var prior = LoadWithStatus(path, logger, migrateLegacy: false);
        if (prior.Status == DataLoadStatus.Failed)
            throw new InvalidDataException($"Refusing to overwrite unreadable Favorites data: {path}", prior.Error ?? prior.BackupError);

        var persistent = favorites.Select(ToPersistent).ToList();
        var safeBackup = prior.Favorites.Select(ToPersistent).ToList();
        var primaryIsValid = prior.SourcePath != null
            && string.Equals(Path.GetFullPath(prior.SourcePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        JsonStore.SaveWithVerifiedBackup(path, persistent, safeBackup, prior.Status != DataLoadStatus.Missing, primaryIsValid);
        logger?.Info("Favorites saved; public manufacturer presets are plaintext and personal credentials remain protected");
    }

    public static void SaveCredentialFreeExport(string path, IEnumerable<FavoriteConfig> favorites)
    {
        var export = favorites.Select(x =>
        {
            var clone = Clone(x);
            clone.Password = "";
            clone.ProtectedPassword = "";
            clone.PublicPassword = "";
            clone.IsPublicDefault = false;
            clone.PasswordUnavailable = false;
            return clone;
        }).ToList();
        // Exports must not create a backup copy of arbitrary pre-existing file
        // contents, which could include plaintext personal credentials.
        JsonStore.SaveWithVerifiedBackup(path, export, export, hasSafeBackup: true, primaryIsValid: true);
    }

    public static FavoriteConfig PrepareImported(FavoriteConfig source)
    {
        var clone = Clone(source);
        // Public markers and plaintext public-password fields from external JSON
        // are untrusted. Any readable incoming password becomes a personal value
        // and is protected again by Save(); unavailable DPAPI payloads stay opaque.
        clone.IsPublicDefault = false;
        clone.PublicPassword = "";
        if (!clone.PasswordUnavailable) clone.ProtectedPassword = "";
        return clone;
    }

    private static FavoriteConfig ToPersistent(FavoriteConfig source)
    {
        var clone = Clone(source);
        var publishedPreset = source.IsPublicDefault
            ? Defaults.DefaultFavorites().FirstOrDefault(x => x.Id.Equals(source.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Password, source.Password, StringComparison.Ordinal))
            : null;
        if (publishedPreset is not null)
        {
            clone.PublicPassword = publishedPreset.Password;
            clone.ProtectedPassword = "";
        }
        else
        {
            clone.IsPublicDefault = false;
            if (!string.IsNullOrWhiteSpace(source.Password))
            {
                clone.ProtectedPassword = CredentialProtector.Protect(source.Password);
                clone.PublicPassword = "";
            }
            else if (!source.PasswordUnavailable)
            {
                clone.ProtectedPassword = "";
                clone.PublicPassword = "";
            }
        }
        clone.Password = "";
        clone.PasswordUnavailable = false;
        return clone;
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
