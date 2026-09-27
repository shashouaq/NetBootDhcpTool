using NetBootDhcpTool.Core;

if (args.Length is < 3 or > 4) throw new ArgumentException("Expected manifest path, detached signature path, version and optional --live.");
var manifestJson = await File.ReadAllTextAsync(args[0]);
var signature = await File.ReadAllTextAsync(args[1]);
var expected = VersionUpdateService.Evaluate(manifestJson, signature, new Version(0, 0));
if (!expected.Succeeded || !expected.SignatureVerified || expected.LatestVersion != Version.Parse(args[2])
    || expected.DownloadUrls.Count != 2 || expected.Packages.Count == 0
    || !expected.Packages.Any(package => package.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidDataException($"Published-tag client rejected the dual-source manifest: {expected.Error}");
if (args.Length == 4)
{
    if (args[3] != "--live") throw new ArgumentException("Unknown client check mode.");
    using var service = new VersionUpdateService();
    var actual = await service.CheckAsync(new Version(0, 0));
    if (!actual.Succeeded || !actual.SignatureVerified || actual.LatestVersion != expected.LatestVersion || actual.ArchiveSha256 != expected.ArchiveSha256
        || !actual.DownloadUrls.Order().SequenceEqual(expected.DownloadUrls.Order()))
        throw new InvalidDataException($"Published-tag client failed live update detection: {actual.Error}");
    Console.WriteLine($"CLIENT_LIVE_OK version={actual.LatestVersion} mirrors={actual.DownloadUrls.Count}");
}
Console.WriteLine($"CLIENT_MANIFEST_OK version={expected.LatestVersion} mirrors={expected.DownloadUrls.Count}");
