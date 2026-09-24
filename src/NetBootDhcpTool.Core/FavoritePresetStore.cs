namespace NetBootDhcpTool.Core;

public sealed class FavoritePresetState
{
    public List<string> DeletedPresetIds { get; set; } = [];
}

public sealed record FavoritePresetStateLoad(DataLoadStatus Status, FavoritePresetState State, Exception? Error = null)
{
    public bool IsWritable => Status != DataLoadStatus.Failed;
}

/// <summary>Persists user deletion intent for shipped favorite presets.</summary>
public static class FavoritePresetStore
{
    public static FavoritePresetStateLoad Load(string path, ILogger? logger = null)
    {
        var result = JsonStore.Load<FavoritePresetState>(path, logger);
        if (result.Status == DataLoadStatus.Missing)
            return new FavoritePresetStateLoad(result.Status, new FavoritePresetState());
        if (result.Status == DataLoadStatus.Failed)
            return new FavoritePresetStateLoad(result.Status, new FavoritePresetState(), result.Error ?? result.BackupError);
        var state = result.Value ?? new FavoritePresetState();
        state.DeletedPresetIds = (state.DeletedPresetIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FavoritePresetStateLoad(result.Status, state, result.Error);
    }

    public static void Save(string path, FavoritePresetState state, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var current = Load(path, logger);
        if (!current.IsWritable)
            throw new InvalidDataException($"Refusing to overwrite unreadable favorite preset state: {path}", current.Error);
        var normalized = new FavoritePresetState
        {
            DeletedPresetIds = state.DeletedPresetIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        var safeBackup = current.Status == DataLoadStatus.Missing ? null : current.State;
        JsonStore.SaveWithVerifiedBackup(path, normalized, safeBackup,
            hasSafeBackup: current.Status != DataLoadStatus.Missing,
            primaryIsValid: current.Status is DataLoadStatus.Loaded or DataLoadStatus.LoadedEmpty);
    }

    public static void SetDeleted(string path, string presetId, bool deleted, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(presetId)) throw new ArgumentException("A preset ID is required.", nameof(presetId));
        var current = Load(path, logger);
        if (!current.IsWritable)
            throw new InvalidDataException($"Refusing to update unreadable favorite preset state: {path}", current.Error);
        var next = new FavoritePresetState { DeletedPresetIds = [.. current.State.DeletedPresetIds] };
        next.DeletedPresetIds.RemoveAll(x => x.Equals(presetId, StringComparison.OrdinalIgnoreCase));
        if (deleted) next.DeletedPresetIds.Add(presetId);
        Save(path, next, logger);
    }

    public static bool AddMissingPresets(List<FavoriteConfig> favorites, IEnumerable<FavoriteConfig> shippedPresets, FavoritePresetState state)
    {
        var deleted = (state.DeletedPresetIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var preset in shippedPresets)
        {
            if (deleted.Contains(preset.Id)) continue;
            if (favorites.Any(x => x.Id.Equals(preset.Id, StringComparison.OrdinalIgnoreCase)
                || FavoriteMergePlanner.SameIdentity(x, preset))) continue;
            favorites.Add(FavoriteStore.Clone(preset));
            changed = true;
        }
        return changed;
    }
}
