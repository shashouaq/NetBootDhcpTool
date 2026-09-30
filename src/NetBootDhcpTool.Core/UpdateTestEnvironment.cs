namespace NetBootDhcpTool.Core;

/// <summary>Opt-in fault tests can only write inside a marked, isolated root.</summary>
public static class UpdateTestEnvironment
{
    public const string MarkerName = ".netboot-test-root";
    public static bool IsActive => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NETBOOT_TEST_ROOT"))
        || Environment.GetEnvironmentVariable("NETBOOT_INTEGRATION_TEST") == "1";

    public static string? Root
    {
        get
        {
            if (!IsActive) return null;
            var value = Environment.GetEnvironmentVariable("NETBOOT_TEST_ROOT");
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                throw new InvalidOperationException("Integration tests require an absolute NETBOOT_TEST_ROOT.");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!Directory.Exists(root) || root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(root, MarkerName)) || File.ReadAllText(Path.Combine(root, MarkerName)).Trim() != "NetBootDhcpTool isolated integration test v1")
                throw new InvalidOperationException("Integration test root is not explicitly marked.");
            var productionData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetBootDhcpTool");
            if (Overlaps(root, productionData) || Overlaps(root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NetBootDhcpTool")))
                throw new InvalidOperationException("Integration test root overlaps a production directory.");
            if (OperatingSystem.IsWindows())
            {
                using var registration = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\NetBootDhcpTool", writable: false);
                if (registration?.GetValue("InstallPath") is string registered && !string.IsNullOrWhiteSpace(registered) && Overlaps(root, registered))
                    throw new InvalidOperationException("Integration test root overlaps the registered production installation.");
            }
            var current = new DirectoryInfo(root);
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Integration test root crosses a reparse point.");
                current = current.Parent;
            }
            return root;
        }
    }

    public static void RequirePath(string path)
    {
        var root = Root;
        if (root is null) return;
        var target = Path.GetFullPath(path);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Integration test target is outside its marked root: " + target);
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Integration test target crosses a reparse point: " + current);
        }
    }

    public static string TemporaryDirectory
    {
        get
        {
            var root = Root;
            return root is null ? Path.GetTempPath() : Path.Combine(root, "temp");
        }
    }

    private static bool Overlaps(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return first.Equals(second, StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Tests pause or fail at real boundaries. Fault hooks cannot operate on a production installation.
    public static void Checkpoint(string phase, string? affectedPath = null)
    {
        if (!IsActive) return;
        var root = Root!;
        if (affectedPath is not null) RequirePath(affectedPath);
        var fault = Environment.GetEnvironmentVariable("NETBOOT_TEST_FAULT");
        if (fault == "fail:" + phase) throw new IOException("Isolated fault injection: " + phase);
        if (fault == "exit:" + phase) Environment.Exit((int)InstallExitCode.UpdaterTerminated);
        if (fault == "pause:" + phase)
        {
            var ready = Path.Combine(root, "fault-" + phase + ".ready");
            File.WriteAllText(ready, affectedPath ?? Environment.ProcessId.ToString());
            var stopAt = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(ready + ".continue") && DateTime.UtcNow < stopAt) Thread.Sleep(50);
            if (!File.Exists(ready + ".continue")) throw new TimeoutException("Isolated fault checkpoint timed out: " + phase);
        }
    }

    public static void RequireSpace(string phase, string path, long bytes)
    {
        RequirePath(path);
        if (IsActive && Environment.GetEnvironmentVariable("NETBOOT_TEST_FAULT") == "disk:" + phase)
            throw new UpdateOperationException(InstallExitCode.InsufficientSpace, "Isolated volume-capacity fault: " + phase);
        var volume = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("Unknown installation volume.");
        if (new DriveInfo(volume).AvailableFreeSpace < bytes)
            throw new UpdateOperationException(InstallExitCode.InsufficientSpace, $"Not enough free space on {volume}: need {bytes} bytes.");
    }

    public static HttpClient? CreateHttpClient()
    {
        if (!IsActive) return null;
        _ = Root;
        var endpoint = Environment.GetEnvironmentVariable("NETBOOT_TEST_HTTP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new InvalidOperationException("Integration HTTP transport must use loopback HTTP.");
        return new HttpClient(new LoopbackHandler(uri)) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private sealed class LoopbackHandler(Uri endpoint) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(endpoint, request.RequestUri!.AbsolutePath.TrimStart('/'));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
