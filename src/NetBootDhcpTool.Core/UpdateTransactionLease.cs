namespace NetBootDhcpTool.Core;

/// <summary>Unlike the app's data lease, this lock spans replacement, launch and health confirmation.</summary>
public static class UpdateTransactionLease
{
    public static FileStream Acquire(string dataDirectory)
    {
        UpdateTestEnvironment.RequirePath(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var path = PackagePathCanonicalizer.ResolveUnderRoot(dataDirectory, "updates/installation.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException ex) { throw new UpdateOperationException(InstallExitCode.PreflightFailure, "Another installation transaction is active; retry after it completes.", ex); }
    }
}
