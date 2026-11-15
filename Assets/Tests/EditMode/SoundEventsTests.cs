using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using WAR2D.Art;
using WAR2D.Audio;
using WAR2D.EditorTools;

/// <summary>
/// The sound event table (asset-generation spec §3.8): every sound spec has a row and every row a spec, every row's
/// sound is named somewhere in game code (so a sound that is built but never played fails), and today's rows play
/// every sound exactly as before the table existed.
/// </summary>
public class SoundEventsTests
{
    private const string File_ = "Assets/Resources/Audio/SoundEvents.json";

    /// <summary>
    /// Built but never played today (found 2026-11-16, known-issues): comments in <c>Effects</c> and <c>ClientWorld</c> say a
    /// destroyed building plays <c>explosion_large</c> and a firing turret <c>shot_turret</c>, but no code does. A sound
    /// comes off this list when it is played or deleted.
    /// </summary>
    private static readonly string[] Unplayed = { "explosion_large", "shot_turret" };

    private static SoundEventTable Table() => SoundEvents.Read(File.ReadAllText(File_), File_);

    [Test]
    public void EverySoundHasARowAndEveryRowASound()
    {
        var specs = new HashSet<string>(Directory.GetFiles(AudioBuild.SfxSource, "*.json").Select(Path.GetFileNameWithoutExtension));
        var rows = new HashSet<string>(Table().events.Select(e => e.id));
        Assert.IsEmpty(specs.Except(rows), "sounds with no row in " + File_);
        Assert.IsEmpty(rows.Except(specs), "rows with no sound spec");
    }

    [Test]
    public void EveryRowsSoundIsNamedInGameCode()
    {
        string code = string.Join("\n", Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Contains("/Editor/")).Select(File.ReadAllText));
        var unplayed = Table().events.Select(e => e.id).Where(id => !code.Contains("\"" + id + "\"")).ToList();
        CollectionAssert.AreEquivalent(Unplayed, unplayed, "sounds built but never named in Assets/Scripts (update SoundEventsTests.Unplayed)");
    }

    /// <summary>Phase 1 changes nothing audible: every row is the old fixed cooldown at the built level.</summary>
    [Test]
    public void TodaysRowsPlayEverySoundAsBefore()
    {
        foreach (SoundEvent row in Table().events)
        {
            Assert.AreEqual(AudioService.PerSoundCooldown, row.cooldown, row.id);
            Assert.AreEqual(1f, row.Gain, row.id);
            Assert.AreEqual(1, row.variants, row.id);
        }
    }

    [Test]
    public void GainIsDecibels()
    {
        Assert.AreEqual(1f, new SoundEvent().Gain);
        Assert.AreEqual(0.5f, new SoundEvent { gainDb = -6.0206f }.Gain, 1e-4f);
    }

    [Test]
    public void ABadTableIsRefusedWithItsFileAndField()
    {
        const string twice = "{ \"events\": [ { \"id\": \"a\", \"category\": \"Ui\" }, { \"id\": \"a\", \"category\": \"Ui\" } ] }";
        StringAssert.Contains("listed twice", Assert.Throws<SpecException>(() => SoundEvents.Read(twice, "t.json")).Message);
        const string loud = "{ \"events\": [ { \"id\": \"a\", \"category\": \"Ui\", \"gainDb\": 40 } ] }";
        StringAssert.Contains("events[0]", Assert.Throws<SpecException>(() => SoundEvents.Read(loud, "t.json")).Message);
        const string number = "{ \"events\": [ { \"id\": \"a\", \"category\": 2 } ] }";
        Assert.Throws<SpecException>(() => SoundEvents.Read(number, "t.json"));
    }

    [Test]
    public void AVoicePoolUsesASoundsOwnCooldown()
    {
        var pool = new SfxVoicePool(8, 0.05f, _ => 0.01f, id => id == "slow" ? 1f : 0f);
        var view = new UnityEngine.Rect(-10, -10, 20, 20);
        Assert.IsTrue(pool.TryPlay("slow", UnityEngine.Vector2.zero, view, 0f));
        Assert.IsFalse(pool.TryPlay("slow", UnityEngine.Vector2.zero, view, 0.5f));
        Assert.IsTrue(pool.TryPlay("fast", UnityEngine.Vector2.zero, view, 0.5f));
        Assert.IsTrue(pool.TryPlay("fast", UnityEngine.Vector2.zero, view, 0.52f));
    }
}
