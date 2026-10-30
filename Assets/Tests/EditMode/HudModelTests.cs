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
        // v0.14: an AI's difficulty and the style the host picked are public lobby data (Random stays Random; what it became is not here).
        CollectionAssert.AreEquivalent(new[] { "OwnerId", "Nickname", "ColourIndex", "OrderIndex", "StartTeam", "Eliminated", "IsAi", "Difficulty", "PickedStyle" }, rowFields);
        foreach (MethodInfo m in typeof(HudModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!m.Name.Contains("Resources")) continue;
            Assert.IsFalse(m.GetParameters().Any(p => p.Name.ToLowerInvariant().Contains("owner")), $"{m.Name} takes an owner");
        }
    }

    // ---- v0.14: the AI tag ----

    [Test]
    public void AnAiRowCarriesItsTagAndAHumanNone()
    {
        var ai = new PlayerRow(AiOwnerIds.First, "Cmdr. Vasquez", 2, 1, false, 2, true, AiDifficulty.Hard, AiStyle.Random);
        Assert.AreEqual("AI · Hard", ai.AiTag);
        Assert.IsNull(new PlayerRow(5, "Oscar", 0, 0, false).AiTag);
        Assert.IsFalse(ai.Equals(new PlayerRow(AiOwnerIds.First, "Cmdr. Vasquez", 2, 1, false, 2, true, AiDifficulty.Easy, AiStyle.Random)), "a difficulty change redraws");
    }

    [Test]
    public void TheEndScreenRevealsWhatRandomBecame()
    {
        Assert.AreEqual("AI · Hard · Turtle (random)", PlayerNameLabel.ResultTagText(AiDifficulty.Hard, AiStyle.Random, AiStyle.Turtle));
        Assert.AreEqual("AI · Normal · Rusher", PlayerNameLabel.ResultTagText(AiDifficulty.Normal, AiStyle.Rusher, AiStyle.Rusher));
        Assert.AreEqual("AI · Easy", PlayerNameLabel.ResultTagText(AiDifficulty.Easy, AiStyle.Random, AiStyle.Random), "unknown: no style");
        var result = new MatchResult { Owners = new[] { 5, AiOwnerIds.First }, AiStyles = new[] { AiStyle.Random, AiStyle.Expander } };
        Assert.AreEqual(AiStyle.Expander, result.PlayedStyle(AiOwnerIds.First));
        Assert.AreEqual(AiStyle.Random, result.PlayedStyle(5));
        Assert.AreEqual(AiStyle.Random, result.PlayedStyle(99));
        Assert.AreEqual(AiStyle.Random, new MatchResult().PlayedStyle(5));
    }

    [Test]
    public void AnAlertNamingAnAiKeepsItsCallsignAndTag()
    {
        var feed = new AlertFeedModel(10f);
        var gift = new Alert { Kind = AlertKind.GiftReceived, OtherOwnerId = AiOwnerIds.First, Amount = 500 };
        string text = AlertFeedModel.Text(gift, _ => "Cmdr. Vasquez", null);
        feed.Add(gift, text, 0f, "Cmdr. Vasquez", "AI · Hard");
        Assert.AreEqual(("Cmdr. Vasquez", "AI · Hard"), (feed.Entries[0].Subject, feed.Entries[0].SubjectTag));
        Assert.AreEqual("Cmdr. Vasquez gifted you 500", feed.Entries[0].Text);
        feed.Add(new Alert { Kind = AlertKind.UnderAttack }, "Your units are under attack", 0f);
        Assert.IsNull(feed.Entries[1].Subject, "Under attack never names anyone");
        Assert.IsTrue(AlertFeedModel.NamesAPlayer(AlertKind.VisionSharedWithYou));
        Assert.IsFalse(AlertFeedModel.NamesAPlayer(AlertKind.UnderAttack));
    }
}
