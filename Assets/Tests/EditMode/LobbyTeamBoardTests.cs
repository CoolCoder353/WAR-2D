using NUnit.Framework;
using WAR2D.UI;

/// <summary>
/// Playtest 2026-11-05: the unit cap notice (Figma L4). The team board's quick setups were removed on 2026-11-06.
/// </summary>
public class LobbyTeamBoardTests
{
    [Test]
    public void TheUnitCapAlertComesOnceUntilASpawnSucceeds()
    {
        var player = new ServerPlayer(null, 100f);
        Assert.That(player.SetAtUnitCap(true), Is.True, "the first refusal alerts");
        Assert.That(player.SetAtUnitCap(true), Is.False, "later refusals don't");
        Assert.That(player.AtUnitCap, Is.True);
        Assert.That(player.SetAtUnitCap(false), Is.False, "a spawn below the cap clears it quietly");
        Assert.That(player.AtUnitCap, Is.False);
        Assert.That(player.SetAtUnitCap(true), Is.True, "reaching the cap again alerts again");
    }

    [Test]
    public void TheUnitCapAlertSaysTheCap()
    {
        string text = AlertFeedModel.Text(new Alert { Kind = AlertKind.UnitCapReached, Amount = 10000 }, _ => "", null);
        Assert.That(text, Is.EqualTo("Unit cap reached (10,000): producers wait"));
    }
}
