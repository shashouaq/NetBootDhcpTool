using NetBootDhcpTool.Core;

if (args.Length is < 2 or > 3) throw new ArgumentException("Expected manifest path, version and optional --live.");
var expected = VersionUpdateService.Evaluate(await File.ReadAllTextAsync(args[0]), new Version(0, 0));
if (!expected.Succeeded || expected.LatestVersion != Version.Parse(args[1]) || expected.DownloadUrls.Count != 2)
    throw new InvalidDataException($"Published-tag client rejected the dual-source manifest: {expected.Error}");
if (args.Length == 3)
{
    if (args[2] != "--live") throw new ArgumentException("Unknown client check mode.");
    using var service = new VersionUpdateService();
    var actual = await service.CheckAsync(new Version(0, 0));
    if (!actual.Succeeded || actual.LatestVersion != expected.LatestVersion || actual.ArchiveSha256 != expected.ArchiveSha256
        || !actual.DownloadUrls.Order().SequenceEqual(expected.DownloadUrls.Order()))
        throw new InvalidDataException($"Published-tag client failed live update detection: {actual.Error}");
    Console.WriteLine($"CLIENT_LIVE_OK version={actual.LatestVersion} mirrors={actual.DownloadUrls.Count}");
}
Console.WriteLine($"CLIENT_MANIFEST_OK version={expected.LatestVersion} mirrors={expected.DownloadUrls.Count}");
