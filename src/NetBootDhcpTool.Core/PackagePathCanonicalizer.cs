using System.IO;
using System.Text.RegularExpressions;

namespace NetBootDhcpTool.Core;

/// <summary>Defines the one path representation used by update manifests and archive extractors.</summary>
public static class PackagePathCanonicalizer
{
    private static readonly Regex DrivePath = new(@"^[A-Za-z]:", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DeviceName = new(@"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static StringComparer PathComparer { get; } = StringComparer.OrdinalIgnoreCase;

    public static string Canonicalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\0'))
            throw new InvalidDataException("Update package contains an empty or invalid path.");

        if (path[0] is '/' or '\\' || DrivePath.IsMatch(path) || Path.IsPathRooted(path)
            || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("Update package contains a rooted, drive or UNC path.");
        if (path.Contains(':')) throw new InvalidDataException("Update package path contains a Windows alternate data stream separator.");

        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Length == 0 || parts.Any(part => part.Length == 0 || part is "." or ".." || part.Length > 240
            || part.EndsWith('.') || part.EndsWith(' ')
            || part.Any(ch => ch < 32 || "<>\"|?*".Contains(ch) || Path.GetInvalidFileNameChars().Contains(ch))
            || DeviceName.IsMatch(part)))
            throw new InvalidDataException("Update package contains an ambiguous or unsafe relative path.");

        return string.Join('/', parts);
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        var canonical = Canonicalize(relativePath);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var volumeRoot = Path.GetPathRoot(fullRoot) ?? throw new InvalidDataException("Approved package path root has no volume root.");
        var currentRoot = volumeRoot;
        if (Directory.Exists(currentRoot) && (File.GetAttributes(currentRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Approved package path root cannot cross a reparse point.");
        foreach (var segment in Path.GetRelativePath(volumeRoot, fullRoot).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or ".") continue;
            currentRoot = Path.Combine(currentRoot, segment);
            if (Directory.Exists(currentRoot) && (File.GetAttributes(currentRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Approved package path root cannot cross a reparse point.");
        }
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, canonical.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update package path escapes its approved root.");
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var current = fullRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update package path crosses a reparse point.");
        }
        return fullPath;
    }
}
