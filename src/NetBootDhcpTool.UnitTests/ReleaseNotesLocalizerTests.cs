using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class ReleaseNotesLocalizerTests
{
    private const string BilingualNotes = """
        ## v1.0.19

        ### zh-CN / 简体中文
        - 中文升级说明。

        ### en-US / English
        - English upgrade notes.
        """;

    [TestMethod]
    public void SelectLocalizedSectionUsesConfiguredChineseLanguage()
    {
        var section = ReleaseNotesLocalizer.SelectLocalizedSection(BilingualNotes, "zh-CN");

        Assert.AreEqual("- 中文升级说明。", section);
        Assert.IsFalse(section.Contains("English upgrade notes", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SelectLocalizedSectionUsesConfiguredEnglishLanguage()
    {
        var section = ReleaseNotesLocalizer.SelectLocalizedSection(BilingualNotes, "en-US");

        Assert.AreEqual("- English upgrade notes.", section);
        Assert.IsFalse(section.Contains("中文升级说明", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LegacyNotesWithoutLanguageSectionsReturnEmptyForExistingFallback()
    {
        Assert.AreEqual(string.Empty, ReleaseNotesLocalizer.SelectLocalizedSection("Old release notes", "zh-CN"));
    }
}
