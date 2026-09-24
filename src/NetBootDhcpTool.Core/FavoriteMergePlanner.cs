namespace NetBootDhcpTool.Core;

public sealed record FavoriteFieldChange(string Field, string ExistingValue, string ImportedValue, bool IsCredential = false);
public sealed record FavoriteMergeResult(FavoriteConfig Favorite, IReadOnlyList<string> ReplacedFields,
    IReadOnlyList<FavoriteFieldChange> Changes, bool CredentialReplaced);

/// <summary>Plans conservative imports without mutating the local favorite or dropping local-only fields.</summary>
public static class FavoriteMergePlanner
{
    public static bool SameIdentity(FavoriteConfig left, FavoriteConfig right)
    {
        if (!string.IsNullOrWhiteSpace(left.Id) && !string.IsNullOrWhiteSpace(right.Id)
            && left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(left.Name?.Trim(), right.Name?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.LocalIp?.Trim(), right.LocalIp?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.TargetIp?.Trim(), right.TargetIp?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static FavoriteMergeResult Merge(FavoriteConfig local, FavoriteConfig incoming)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(incoming);
        var merged = FavoriteStore.Clone(local);
        var replaced = new List<string>();
        var changes = new List<FavoriteFieldChange>();
        Copy(nameof(FavoriteConfig.Name), local.Name, incoming.Name, value => merged.Name = value, required: true);
        Copy(nameof(FavoriteConfig.DeviceNumber), local.DeviceNumber, incoming.DeviceNumber, value => merged.DeviceNumber = value);
        Copy(nameof(FavoriteConfig.SerialNumber), local.SerialNumber, incoming.SerialNumber, value => merged.SerialNumber = value);
        Copy(nameof(FavoriteConfig.RemarkName), local.RemarkName, incoming.RemarkName, value => merged.RemarkName = value);
        Copy(nameof(FavoriteConfig.Description), local.Description, incoming.Description, value => merged.Description = value);
        Copy(nameof(FavoriteConfig.Username), local.Username, incoming.Username, value => merged.Username = value);
        Copy(nameof(FavoriteConfig.MemoryText), local.MemoryText, incoming.MemoryText, value => merged.MemoryText = value);
        Copy(nameof(FavoriteConfig.AdapterName), local.AdapterName, incoming.AdapterName, value => merged.AdapterName = value);
        Copy(nameof(FavoriteConfig.AdapterMac), local.AdapterMac, incoming.AdapterMac, value => merged.AdapterMac = value);
        Copy(nameof(FavoriteConfig.LocalIp), local.LocalIp, incoming.LocalIp, value => merged.LocalIp = value, required: true);
        Copy(nameof(FavoriteConfig.SubnetMask), local.SubnetMask, incoming.SubnetMask, value => merged.SubnetMask = value, required: true);
        Copy(nameof(FavoriteConfig.Gateway), local.Gateway, incoming.Gateway, value => merged.Gateway = value);
        Copy(nameof(FavoriteConfig.Dns), local.Dns, incoming.Dns, value => merged.Dns = value);
        Copy(nameof(FavoriteConfig.TargetIp), local.TargetIp, incoming.TargetIp, value => merged.TargetIp = value);
        merged.PreferHttps = local.PreferHttps || incoming.PreferHttps;
        if (!local.PreferHttps && incoming.PreferHttps)
        {
            replaced.Add(nameof(FavoriteConfig.PreferHttps));
            changes.Add(new FavoriteFieldChange(nameof(FavoriteConfig.PreferHttps), "false", "true"));
        }
        merged.CustomFields = MergeCustomFields(local.CustomFields, incoming.CustomFields, replaced, changes);

        var credentialReplaced = incoming.HasUsablePassword
            && (!local.HasUsablePassword || !string.Equals(local.Password, incoming.Password, StringComparison.Ordinal));
        if (incoming.HasUsablePassword)
        {
            merged.Password = incoming.Password;
            merged.PasswordUnavailable = false;
            merged.PublicPassword = "";
            merged.ProtectedPassword = "";
            merged.IsPublicDefault = false;
            if (credentialReplaced)
            {
                replaced.Add("Credentials");
                changes.Add(new FavoriteFieldChange("Credentials", "", "", IsCredential: true));
            }
        }
        else if (!local.HasUsablePassword && incoming.PasswordUnavailable)
        {
            merged.Password = "";
            merged.PasswordUnavailable = true;
            merged.ProtectedPassword = incoming.ProtectedPassword;
            merged.PublicPassword = "";
            merged.IsPublicDefault = false;
        }

        merged.Id = local.Id;
        merged.CreatedAt = local.CreatedAt;
        merged.LastUsedAt = local.LastUsedAt;
        merged.UpdatedAt = DateTime.Now;
        return new FavoriteMergeResult(merged, replaced.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), changes, credentialReplaced);

        void Copy(string field, string localValue, string importedValue, Action<string> assign, bool required = false)
        {
            var value = importedValue?.Trim() ?? "";
            if (value.Length == 0 && !required) return;
            if (!string.Equals(localValue, value, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(localValue))
                {
                    replaced.Add(field);
                    changes.Add(new FavoriteFieldChange(field, localValue, value));
                }
                assign(value);
            }
        }
    }

    private static List<FavoriteField> MergeCustomFields(IEnumerable<FavoriteField>? localFields,
        IEnumerable<FavoriteField>? importedFields, List<string> replaced, List<FavoriteFieldChange> changes)
    {
        var result = (localFields ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new FavoriteField { Name = x.Name.Trim(), Value = x.Value?.Trim() ?? "" }).ToList();
        foreach (var imported in importedFields ?? [])
        {
            var name = imported.Name?.Trim() ?? "";
            var value = imported.Value?.Trim() ?? "";
            if (name.Length == 0 || value.Length == 0) continue;
            var current = result.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                result.Add(new FavoriteField { Name = name, Value = value });
            }
            else if (!string.Equals(current.Value, value, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(current.Value))
                {
                    var fieldName = "Custom:" + current.Name;
                    replaced.Add(fieldName);
                    changes.Add(new FavoriteFieldChange(fieldName, current.Value, value));
                }
                current.Value = value;
            }
        }
        return result;
    }
}
