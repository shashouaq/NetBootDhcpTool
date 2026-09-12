using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class AdapterIdentityMatcherTests
{
    [TestMethod]
    public void ConfigurationBackupUsesStableIdAfterInterfaceIndexOrMacChanges()
    {
        var wrongAdapter = Adapter("new-id", "7", "Ethernet 2", "00-00-00-00-00-02");
        var intendedAdapter = Adapter("stable-id", "12", "Renamed Ethernet", "00-00-00-00-00-03");
        var backup = new AdapterConfigBackup
        {
            AdapterId = "stable-id",
            InterfaceIndex = "7",
            AdapterName = "Ethernet",
            AdapterMac = "00-00-00-00-00-01"
        };

        Assert.AreSame(intendedAdapter, AdapterIdentityMatcher.Find([wrongAdapter, intendedAdapter], backup));
    }

    [TestMethod]
    public void LegacyConfigurationBackupDoesNotMatchAReusedInterfaceIndex()
    {
        var adapter = Adapter("new-id", "7", "Replacement Ethernet", "00-00-00-00-00-02");
        var backup = new AdapterConfigBackup { InterfaceIndex = "7", AdapterName = "Original Ethernet" };

        Assert.IsNull(AdapterIdentityMatcher.Find([adapter], backup));
    }

    [TestMethod]
    public void MacBackupWithStableIdNeverFallsBackToAnotherAdapterAtTheOldIndex()
    {
        var adapter = Adapter("different-id", "7", "Ethernet", "00-00-00-00-00-02");
        var backup = new AdapterMacBackup { AdapterId = "original-id", InterfaceIndex = "7", AdapterName = "Ethernet" };

        Assert.IsNull(AdapterIdentityMatcher.Find([adapter], backup));
    }

    private static NetworkAdapterInfo Adapter(string id, string index, string name, string mac) => new()
    {
        Id = id,
        InterfaceIndex = index,
        Name = name,
        MacAddress = mac
    };
}
