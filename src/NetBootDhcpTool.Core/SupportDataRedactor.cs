using System.Text.RegularExpressions;

namespace NetBootDhcpTool.Core;

/// <summary>
/// Removes network identifiers from support material while retaining useful structure.
/// Kept in Core so the redaction behavior can be tested without starting the WPF app.
/// </summary>
public static class SupportDataRedactor
{
    private static readonly Regex Ipv4Regex = new(
        @"(?<![\d.])(?:(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(?![\d.])",
        RegexOptions.Compiled);
    private static readonly Regex MacRegex = new(
        @"\b(?:[0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}\b",
        RegexOptions.Compiled);
    private static readonly Regex Ipv6Regex = new(
        @"(?<![A-Za-z0-9])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![A-Za-z0-9])",
        RegexOptions.Compiled);

    public static string RedactNetworkValues(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = MacRegex.Replace(text, "XX-XX-XX-XX-XX-XX");
        redacted = Ipv4Regex.Replace(redacted, "x.x.x.x");
        return Ipv6Regex.Replace(redacted, "xxxx:xxxx::xxxx");
    }
}
