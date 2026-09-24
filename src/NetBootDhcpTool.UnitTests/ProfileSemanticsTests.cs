using NetBootDhcpTool.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class ProfileSemanticsTests
{
    [TestMethod]
    public void ProfileComparisonIncludesGatewayDnsAndLeaseSeconds()
    {
        var saved = EffectiveProfile();
        var mutations = new (string Field, Action<NetworkProfile> Change)[]
        {
            ("Dhcp.ServerIp", x => x.Dhcp.ServerIp = "192.168.10.3"),
            ("Dhcp.SubnetMask", x => x.Dhcp.SubnetMask = "255.255.0.0"),
            ("Dhcp.PoolStart", x => x.Dhcp.PoolStart = "192.168.10.51"),
            ("Dhcp.PoolEnd", x => x.Dhcp.PoolEnd = "192.168.10.101"),
            ("Dhcp.Gateway", x => x.Dhcp.Gateway = "192.168.10.3"),
            ("Dhcp.Dns", x => x.Dhcp.Dns = "192.168.10.54"),
            ("Dhcp.LeaseSeconds", x => x.Dhcp.LeaseSeconds = 7200),
            ("ManualIp", x => x.ManualIp = "192.168.10.3"),
            ("ManualMask", x => x.ManualMask = "255.255.0.0"),
            ("ManualTargetIp", x => x.ManualTargetIp = "192.168.10.21"),
            ("Route", x => x.Routes[0].NextHop = "192.168.10.1")
        };

        foreach (var (field, change) in mutations)
        {
            var current = ProfileStore.Clone(saved);
            change(current);
            var differences = ProfileComparer.Compare(saved, current);
            Assert.IsTrue(differences.Any(x => x.Field == field), $"Expected {field} to be reported as a difference.");
        }
    }

    [TestMethod]
    public void ProfileComparisonNormalizesMasksAndRoutePrefixesAndIgnoresRouteOrder()
    {
        var saved = EffectiveProfile();
        saved.Dhcp.SubnetMask = "255.255.255.0";
        saved.ManualMask = "255.255.255.0";
        saved.Routes.Add(new StaticRouteRule
        {
            AddressFamily = "IPv6", DestinationPrefix = "2001:db8:1::/64", AdapterId = "adapter-b", AdapterName = "Test B", RouteMetric = 7
        });
        var current = ProfileStore.Clone(saved);
        current.Dhcp.SubnetMask = "/24";
        current.ManualMask = "/24";
        current.Routes[0].DestinationPrefix = "192.168.20.99/24";
        current.Routes[0].NextHop = "0.0.0.0";
        current.Routes.Reverse();

        Assert.AreEqual(0, ProfileComparer.Compare(saved, current).Count,
            "Equivalent subnet masks, normalized network prefixes, direct hops, and route reordering are not effective differences.");
    }

    [TestMethod]
    public void DuplicateCanonicalRoutesAreRejectedAndRouteComparisonKeepsMultiplicity()
    {
        var first = new StaticRouteRule
        {
            AddressFamily = "IPv4", DestinationPrefix = "192.168.20.99/24", AdapterId = "adapter-a", AdapterName = "Test A", RouteMetric = 1
        };
        var duplicate = new StaticRouteRule
        {
            AddressFamily = "IPv4", DestinationPrefix = "192.168.20.0/24", AdapterId = "ADAPTER-A", AdapterName = "Test A", NextHop = "0.0.0.0", RouteMetric = 9
        };
        var assessment = ProfileValidation.AssessRoutes([first, duplicate]);
        Assert.AreEqual(ProfileSectionStatus.Invalid, assessment.Status);
        CollectionAssert.Contains(assessment.Issues.ToList(), ProfileIssueCode.DuplicateRoute);

        var saved = EffectiveProfile();
        saved.Routes = [first, duplicate];
        var current = ProfileStore.Clone(saved);
        current.Routes.RemoveAt(1);
        Assert.AreEqual(1, ProfileComparer.Compare(saved, current).Count,
            "Route comparison must report duplicate-count differences instead of collapsing routes in a HashSet.");

        first.AddressFamily = "InterNetwork";
        Assert.AreEqual(ProfileSectionStatus.Valid, ProfileValidation.AssessRoutes([first]).Status,
            "The route executor's persisted enum name is a compatible legacy family value.");
    }

    [TestMethod]
    public void ProfileAssessmentDistinguishesDraftFromInvalidDhcpAndManualScan()
    {
        var incompleteDhcp = new DefaultDhcpSettings { ServerIp = "192.168.10.2", SubnetMask = "", PoolStart = "", PoolEnd = "", LeaseSeconds = 3600 };
        Assert.AreEqual(ProfileSectionStatus.Draft, ProfileValidation.AssessDhcp(incompleteDhcp).Status);

        var invalidPool = new DefaultDhcpSettings { ServerIp = "192.168.10.2", SubnetMask = "255.255.255.0", PoolStart = "192.168.11.10", PoolEnd = "192.168.10.20", LeaseSeconds = 3600 };
        Assert.AreEqual(ProfileSectionStatus.Invalid, ProfileValidation.AssessDhcp(invalidPool).Status);

        var invalidLease = ProfileValidation.AssessDhcp(new DefaultDhcpSettings { LeaseSeconds = 604801 });
        Assert.AreEqual(ProfileSectionStatus.Invalid, invalidLease.Status, "An out-of-range lease is a concrete invalid value even while other fields are missing.");
        CollectionAssert.Contains(invalidLease.Issues.ToList(), ProfileIssueCode.DhcpLeaseOutOfRange);

        Assert.AreEqual(ProfileSectionStatus.Draft, ProfileValidation.AssessManualScan("192.168.10.2", "", "").Status);
        Assert.AreEqual(ProfileSectionStatus.Valid, ProfileValidation.AssessManualScan("192.168.10.2", "255.255.255.0", "").Status);
        Assert.AreEqual(ProfileSectionStatus.Invalid, ProfileValidation.AssessManualScan("10.1.2.3", "255.0.0.0", "").Status,
            "The T11 scan target cap is shared by saved-profile assessment.");
    }

    [TestMethod]
    public void ProfileStoreRoundTripsAllEffectiveFieldsAndNormalizesLegacyNullSections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "netboot-profile-semantics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profiles.json");
            var profile = EffectiveProfile();
            ProfileStore.Save(path, [profile]);
            var loaded = ProfileStore.Load(path).Single();
            Assert.AreEqual(profile.Dhcp.ServerIp, loaded.Dhcp.ServerIp);
            Assert.AreEqual(profile.Dhcp.Gateway, loaded.Dhcp.Gateway);
            Assert.AreEqual(profile.Dhcp.Dns, loaded.Dhcp.Dns);
            Assert.AreEqual(profile.Dhcp.LeaseSeconds, loaded.Dhcp.LeaseSeconds);
            Assert.AreEqual(profile.ManualTargetIp, loaded.ManualTargetIp);
            Assert.AreEqual(profile.Routes.Single().AdapterId, loaded.Routes.Single().AdapterId);

            File.WriteAllText(path, "[{\"Name\":\"legacy\",\"Dhcp\":null,\"Routes\":null}]");
            var legacy = ProfileStore.Load(path).Single();
            Assert.AreEqual("192.168.100.1", legacy.Dhcp.ServerIp);
            Assert.AreEqual(3600, legacy.Dhcp.LeaseSeconds);
            Assert.AreEqual(0, legacy.Routes.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static NetworkProfile EffectiveProfile() => new()
    {
        Name = "Effective profile",
        Dhcp = new DefaultDhcpSettings
        {
            ServerIp = "192.168.10.2", SubnetMask = "255.255.255.0", PoolStart = "192.168.10.50", PoolEnd = "192.168.10.100",
            Gateway = "192.168.10.1", Dns = "192.168.10.53", LeaseSeconds = 3600
        },
        ManualIp = "192.168.10.2", ManualMask = "255.255.255.0", ManualTargetIp = "192.168.10.20",
        Routes =
        [
            new StaticRouteRule
            {
                AddressFamily = "IPv4", DestinationPrefix = "192.168.20.0/24", AdapterId = "adapter-a", AdapterName = "Test A",
                AdapterMac = "00-11-22-33-44-55", NextHop = "", RouteMetric = 1
            }
        ]
    };
}
