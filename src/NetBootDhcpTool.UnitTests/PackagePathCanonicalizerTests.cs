using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class PackagePathCanonicalizerTests
{
    [TestMethod]
    public void CanonicalizesBothSeparatorsToForwardSlash()
    {
        Assert.AreEqual("config/settings.json", PackagePathCanonicalizer.Canonicalize("config/settings.json"));
        Assert.AreEqual("config/settings.json", PackagePathCanonicalizer.Canonicalize("config\\settings.json"));
    }

    [TestMethod]
    public void RejectsRootedDriveUncTraversalAmbiguousAndAdsPaths()
    {
        foreach (var path in new string?[]
        {
            null, "", "/rooted", "\\rooted", "C:\\drive", "C:drive-relative", "//server/share", "\\\\server\\share",
            "../escape", "folder/../escape", "./file", "folder//file", "folder/./file", "folder/", "file:stream", "file\0name"
        })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => PackagePathCanonicalizer.Canonicalize(path), $"Accepted unsafe path: {path}");
        }
    }

    [TestMethod]
    public void CanonicalDuplicatesUseWindowsCaseInsensitiveComparison()
    {
        var paths = new HashSet<string>(PackagePathCanonicalizer.PathComparer);
        Assert.IsTrue(paths.Add(PackagePathCanonicalizer.Canonicalize("Config/Settings.json")));
        Assert.IsFalse(paths.Add(PackagePathCanonicalizer.Canonicalize("config\\settings.json")));
    }

    [TestMethod]
    public void ResolvesCanonicalPathInsideStagingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-canonical-path-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = PackagePathCanonicalizer.ResolveUnderRoot(root, "config\\settings.json");
            Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "config", "settings.json")), result, ignoreCase: true);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
