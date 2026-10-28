using System;
using System.IO;
using NUnit.Framework;

public class AlertSoundsTests
{
    private static Alert Of(AlertKind kind) => new Alert { Kind = kind };

    [Test]
    public void EveryKindHasASoundBuiltFromAnSfxJson()
    {
        foreach (AlertKind kind in Enum.GetValues(typeof(AlertKind)))
            foreach (bool jammer in new[] { false, true })
            {
                string id = AlertSounds.SoundOf(kind, jammer);
                Assert.IsTrue(File.Exists($"Assets/Art/Audio/sfx/{id}.json"), $"{kind} plays {id}, which has no SFX JSON");
            }
    }

    [Test]
    public void TheMostUrgentKindInABatchPlays()
    {
        var settings = new GameSettings();
        settings.Clamp();
        string sound = AlertSounds.ForBatch(new[] { Of(AlertKind.GiftReceived), Of(AlertKind.UnderAttack), Of(AlertKind.EnemySpotted) }, settings);
        Assert.AreEqual(AlertSounds.SoundOf(AlertKind.UnderAttack), sound);
    }

    [Test]
    public void AMutedKindStaysSilentAndTheNextPlays()
    {
        var settings = new GameSettings();
        settings.Clamp();
        settings.AlertSound[(int)AlertKind.EnemySpotted] = false;
        Assert.IsNull(AlertSounds.ForBatch(new[] { Of(AlertKind.EnemySpotted) }, settings));
        Assert.AreEqual(AlertSounds.SoundOf(AlertKind.GiftReceived), AlertSounds.ForBatch(new[] { Of(AlertKind.EnemySpotted), Of(AlertKind.GiftReceived) }, settings));
    }

    [Test]
    public void AFinishedJammerHums()
    {
        string sound = AlertSounds.ForBatch(new[] { Of(AlertKind.ConstructionFinished) }, new GameSettings(), _ => true);
        Assert.AreEqual("jammer_online", sound);
    }
}
