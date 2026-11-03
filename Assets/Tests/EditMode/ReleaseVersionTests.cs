using NUnit.Framework;
using UnityEngine;

/// <summary>The release version (2026-11-02): one number in GameConfig.xml, shown on the main menu and used by builds.</summary>
public class ReleaseVersionTests
{
    [Test]
    public void TheProjectsVersionMatchesTheConfig()
    {
        // Builds copy <Release><Version> into bundleVersion (ReleaseVersionSync); this catches the editor's copy drifting.
        Config.GameConfigData config = Config.ConfigLoader.LoadConfig();
        Assert.IsTrue(Config.ConfigLoader.IsValid);
        Assert.AreEqual(config.Release.Version, Application.version, "set ProjectSettings' bundleVersion to <Release><Version>");
    }

    [Test]
    public void TheMainMenuLineReadsVersionThenName()
    {
        var release = new Config.ReleaseConfig { Version = "0.16.0", Name = "Clarity" };
        Assert.AreEqual("v0.16.0 Clarity", release.Display);
        release.Name = "";
        Assert.AreEqual("v0.16.0", release.Display);
    }

    [TestCase("0.16")]
    [TestCase("v0.16.0")]
    [TestCase("")]
    public void ABadVersionIsRefused(string version)
    {
        var errors = new System.Collections.Generic.List<string>();
        Config.ConfigParser.Parse(ConfigParserTests.ValidXml.Replace("<Version>0.17.0</Version>", $"<Version>{version}</Version>"), errors);
        Assert.That(errors, Has.Some.Contains("Release"));
    }
}
