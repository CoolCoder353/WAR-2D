using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using WAR2D.UI;

/// <summary>v0.18 Field manual: every type has a page, every number is the config's, the words are the approved ones.</summary>
public class ManualTests
{
    private static Config.GameConfigData Config_() { Config.ConfigLoader.ResetForTests(); return Config.ConfigLoader.LoadConfig(); }
    private static ManualModel Model(Config.GameConfigData c = null) => new ManualModel(c ?? Config_(), action => action);

    [Test]
    public void TheCompiledWordsAreTheApprovedFile()
    {
        string approved = File.ReadAllText(Path.Combine(UnityEngine.Application.dataPath, "..", "tools", "manual-text.json")).Replace("\r", "");
        // The compiled literal has the line endings of its checkout (CRLF on Windows), and they are not words.
        Assert.AreEqual(approved, ManualText.Raw.Replace("\r", ""), "run node tools/manual-mock.js --write-text");
    }

    [Test]
    public void EveryTypeHasAPageAndACard()
    {
        ManualModel m = Model();
        foreach (UnitType t in ManualModel.UnitTypes)
        {
            Assert.That(m.Pages, Does.ContainKey(ManualModel.PageOf(t)));
            Assert.That(m.Pages["units"].Cards, Does.Contain(ManualModel.PageOf(t)));
        }
        foreach (BuildingType t in ManualModel.BuildingTypes)
        {
            Assert.That(m.Pages, Does.ContainKey(ManualModel.PageOf(t)));
            Assert.That(m.Pages["buildings"].Cards, Does.Contain(ManualModel.PageOf(t)));
        }
        Assert.That(m.Pages, Does.ContainKey("building-Wall"));
    }

    [Test]
    public void NoPlaceholderIsLeftInAnyPage()
    {
        foreach (ManualPage page in Model().Pages.Values)
        {
            var all = new List<string>(page.Body);
            if (page.Lead != null) all.Add(page.Lead);
            foreach (ManualSection s in page.Sections) all.AddRange(s.Paragraphs);
            foreach (string text in all)
            {
                StringAssert.DoesNotContain("{", text, page.Id);
                StringAssert.DoesNotContain("**", text, page.Id);
                StringAssert.DoesNotContain("[[", text, page.Id);
            }
        }
    }

    /// <summary>Numbers nothing else uses, to show the page reads the config and nothing is typed in.</summary>
    [Test]
    public void TheNumbersAreTheConfigs()
    {
        Config.GameConfigData c = Config_();
        c.GetUnit(UnitType.Tank).Range = 97f;
        c.GetUnit(UnitType.Tank).UpfrontCost = 83f;
        c.Siege.WallHealth = 211;
        c.Resources.MiningRate = 31f;
        ManualModel m = new ManualModel(c, a => a);
        Assert.That(m.Pages["unit-Tank"].Stats, Has.Some.Matches<(string label, string value, string link)>(s => s.label == "Range" && s.value == "97 tiles"));
        Assert.That(m.Pages["unit-Tank"].Stats, Has.Some.Matches<(string label, string value, string link)>(s => s.label == "Cost" && s.value == "83"));
        Assert.That(m.Pages["building-Wall"].Stats, Has.Some.Matches<(string label, string value, string link)>(s => s.value == "211 a segment"));
        StringAssert.Contains("31 a second", m.Pages["building-Miner"].Body[0]);
    }

    /// <summary>The lists <c>tools/manual-mock.js</c> prints for the shipped config (v0.20: with armour, fire, aim time and shells).</summary>
    [TestCase("Tank", "Sniper,Scout", "Artillery,Flamethrower,GunTurret")]
    [TestCase("Digger", "Sniper", "Flamethrower,Artillery,Tank")]
    [TestCase("Scout", "Sniper,Bomber,Artillery", "Flamethrower,GunTurret,Heavy")]
    [TestCase("Flamethrower", "Scout,Tank,Sniper", "Artillery,Bomber,GunTurret")]
    [TestCase("Heavy", "Bomber,GunTurret,Scout", "Sniper")]
    [TestCase("Artillery", "Tank,Flamethrower,GunTurret", "Bomber,Heavy,Scout")]
    [TestCase("Sniper", "GunTurret,Heavy", "Scout,Tank,Flamethrower")]
    [TestCase("Bomber", "Artillery,Flamethrower", "Scout,Heavy,GunTurret")]
    [TestCase("GunTurret", "Scout,Bomber,Flamethrower", "Artillery,Sniper,Heavy")]
    public void TheCountersMatchTheMock(string type, string strong, string weak)
    {
        ManualRules.Lists lists = ManualRules.Counters(ManualRules.Table(Config_()), type);
        Assert.AreEqual(strong, string.Join(",", lists.Strong), "strong");
        Assert.AreEqual(weak, string.Join(",", lists.Weak), "weak");
    }

    [Test]
    public void AMatchupReadsOneWayEverywhere()
    {
        var table = ManualRules.Table(Config_());
        foreach (string a in table.Keys)
        {
            ManualRules.Lists c = ManualRules.Counters(table, a);
            foreach (string b in c.Strong)
            {
                CollectionAssert.DoesNotContain(c.Weak, b, $"{a} both ways against {b}");
                CollectionAssert.DoesNotContain(ManualRules.Counters(table, b).Strong, a, $"{a} and {b} each strong");
            }
        }
    }

    [Test]
    public void ABuilderHasNoListsAndSaysWhy()
    {
        ManualPage page = Model().Pages["unit-Builder"];
        Assert.IsFalse(page.HasCounters);
        Assert.IsNotNull(page.Note);
    }

    [Test]
    public void TheDamageGridHasEveryCell()
    {
        ManualPage page = Model().Pages["damage"];
        Assert.AreEqual(11, page.Grid.Count, "nine units, the Gun turret and the Mine");
        foreach (ManualGridRow row in page.Grid) Assert.AreEqual(4, row.Cells.Length);
    }

    [Test]
    public void SearchFindsPagesByName()
    {
        ManualModel m = Model();
        Assert.That(m.Search("tur"), Has.Some.Matches<ManualPage>(p => p.Id == "building-GunTurret"));
        Assert.That(m.Search("economy"), Has.Some.Matches<ManualPage>(p => p.Id == "rules-economy"));
        CollectionAssert.IsEmpty(m.Search("   "));
    }

    [Test]
    public void KeysAreAsBoundNow()
    {
        ManualModel m = new ManualModel(Config_(), a => a == "Rotate" ? "Y" : a);
        StringAssert.Contains("[Y]", m.Pages["building-Miner"].Body[1]);
    }
}
