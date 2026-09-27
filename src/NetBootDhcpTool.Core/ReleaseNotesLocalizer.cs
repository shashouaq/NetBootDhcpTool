using System.Text.RegularExpressions;

namespace NetBootDhcpTool.Core;

public static class ReleaseNotesLocalizer
{
    private static readonly Regex LocaleHeading = new(
        @"(?m)^###\s+(?<language>zh-CN|en-US)\b[^\r\n]*\r?\n",
        RegexOptions.CultureInvariant);
    private static readonly Regex NextMarkdownHeading = new(
        @"(?m)^#{2,}\s+",
        RegexOptions.CultureInvariant);

    public static string SelectLocalizedSection(string? releaseNotes, string? language)
    {
        if (string.IsNullOrWhiteSpace(releaseNotes)) return string.Empty;

        var requestedLanguage = string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";
        foreach (Match heading in LocaleHeading.Matches(releaseNotes))
        {
            if (!string.Equals(heading.Groups["language"].Value, requestedLanguage, StringComparison.OrdinalIgnoreCase))
                continue;

            var bodyStart = heading.Index + heading.Length;
            var nextHeading = NextMarkdownHeading.Match(releaseNotes, bodyStart);
            var bodyEnd = nextHeading.Success ? nextHeading.Index : releaseNotes.Length;
            return releaseNotes[bodyStart..bodyEnd].Trim();
        }

        return string.Empty;
    }
}
