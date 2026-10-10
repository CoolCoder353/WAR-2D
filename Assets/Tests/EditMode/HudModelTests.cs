using System.Linq;
using System.Reflection;
using NUnit.Framework;
using WAR2D.UI;

public class HudModelTests
{
    [Test]
    public void RaisesResourcesChangedOnce()
    {
        var model = new HudModel();
        int raised = 0;
        model.ResourcesChanged += () => raised++;
        model.SetResources(100f, 5f, 2f);
        Assert.AreEqual(1, raised);
        model.SetResources(100f, 5f, 2f);
        Assert.AreEqual(1, raised, "an unchanged value does not raise again");
        model.SetResources(120f, 5f, 2f);
        Assert.AreEqual(2, raised);
        Assert.AreEqual(120f, model.Resources);
        Assert.AreEqual(5f, model.IncomePerSecond);
        Assert.AreEqual(2f, model.UpkeepPerSecond);
    }

    [Test]
    public void PlayersChangedOnlyWhenRowsDiffer()
    {
        var model = new HudModel();
        int raised = 0;
        model.PlayersChanged += () => raised++;
        var rows = new[] { new PlayerRow(1, "A", 0, 0, false), new PlayerRow(2, "B", 1, 1, false) };
        model.SetPlayers(rows);
        model.SetPlayers(rows.ToArray());
        Assert.AreEqual(1, raised);
        model.SetPlayers(new[] { new PlayerRow(1, "A", 0, 0, false), new PlayerRow(2, "B", 1, 1, true) });
        Assert.AreEqual(2, raised);
        Assert.IsTrue(model.Players[1].Eliminated);
    }

    [Test]
    public void PlayersFromPublicDataOnly()
    {
        // A player row carries public lobby data only, and nothing on the model takes another
        // player's resources: the only resource setter is for the local player's own numbers.
        string[] rowFields = typeof(PlayerRow).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).ToArray();
        CollectionAssert.AreEquivalent(new[] { "OwnerId", "Nickname", "ColourIndex", "OrderIndex", "StartTeam", "Eliminated" }, rowFields);
        foreach (MethodInfo m in typeof(HudModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!m.Name.Contains("Resources")) continue;
            Assert.IsFalse(m.GetParameters().Any(p => p.Name.ToLowerInvariant().Contains("owner")), $"{m.Name} takes an owner");
        }
    }
}
