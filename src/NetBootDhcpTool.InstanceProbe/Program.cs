using NetBootDhcpTool.Core;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: <dataDirectory> <markerFile> <sideEffectFile> <holdMilliseconds>");
    return 2;
}

var dataDirectory = args[0];
var markerFile = args[1];
var sideEffectFile = args[2];
if (!int.TryParse(args[3], out var holdMilliseconds) || holdMilliseconds < 0)
{
    Console.Error.WriteLine("Invalid hold duration.");
    return 2;
}

Environment.SetEnvironmentVariable("NETBOOT_DATA_DIRECTORY", dataDirectory);
var paths = new AppPaths(AppContext.BaseDirectory);
using var lease = SingleInstanceLease.TryAcquire(paths);
if (lease is null)
{
    File.WriteAllText(markerFile, "BUSY");
    return 0;
}

Defaults.EnsureFiles(paths);
if (!File.Exists(paths.StaticRouteSessionFile))
    File.WriteAllText(paths.StaticRouteSessionFile, "pending recovery record");
Directory.CreateDirectory(Path.GetDirectoryName(sideEffectFile)!);
File.AppendAllText(sideEffectFile, $"{Environment.ProcessId}{Environment.NewLine}");
File.WriteAllText(markerFile, "OWNED");
Thread.Sleep(holdMilliseconds);
return 0;
