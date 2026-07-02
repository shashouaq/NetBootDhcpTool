using System.Globalization;

namespace NetBootDhcpTool.Core;

public sealed class LanguageService
{
    private readonly AppPaths _paths;
    private Dictionary<string, string> _items = new();

    public LanguageService(AppPaths paths)
    {
        _paths = paths;
    }

    public string CurrentLanguage { get; private set; } = "en-US";

    public void Load(string language)
    {
        CurrentLanguage = ResolveLanguage(language);
        var file = Path.Combine(_paths.I18nDirectory, CurrentLanguage + ".json");
        _items = JsonStore.LoadOrDefault(file, new Dictionary<string, string>());
    }

    public string T(string key) => _items.TryGetValue(key, out var value) ? value : key;

    private static string ResolveLanguage(string language)
    {
        if (language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) || language.Equals("en-US", StringComparison.OrdinalIgnoreCase))
        {
            return language;
        }

        return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
    }
}
