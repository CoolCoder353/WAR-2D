using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class SettingsStoreTests
{
    private string path;

    [SetUp]
    public void SetUp() => path = Path.Combine(Application.temporaryCachePath, "settings-test.json");

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(path)) File.Delete(path);
    }

    [Test]
    public void SaveLoadRoundTrip()
    {
        var s = new GameSettings { Width = 1280, Height = 720, Fullscreen = false, VSync = true, FpsCap = 60, Master = 0.5f, Music = 0.25f, Sfx = 0f, MenuBattle = false, Palette = Palettes.Colourblind, EdgeScroll = false, Bindings = "{}" };
        SettingsStore.Save(s, path);
        GameSettings back = SettingsStore.Load(path);
        Assert.AreEqual(JsonUtility.ToJson(s), JsonUtility.ToJson(back));
    }

    [Test]
    public void CorruptFileLoadsTheDefaults()
    {
        File.WriteAllText(path, "{ this is not json");
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("could not be read"));
        GameSettings s = SettingsStore.Load(path);
        Assert.AreEqual(JsonUtility.ToJson(new GameSettings()), JsonUtility.ToJson(s));
    }

    [Test]
    public void MissingFileLoadsTheDefaults() => Assert.AreEqual(JsonUtility.ToJson(new GameSettings()), JsonUtility.ToJson(SettingsStore.Load(path)));

    [Test]
    public void OutOfRangeValuesAreClamped()
    {
        File.WriteAllText(path, "{\"Width\":10,\"Height\":99999,\"FpsCap\":5,\"Master\":3.5,\"Music\":-1,\"Palette\":\"neon\"}");
        GameSettings s = SettingsStore.Load(path);
        Assert.AreEqual(640, s.Width);
        Assert.AreEqual(4320, s.Height);
        Assert.AreEqual(30, s.FpsCap);
        Assert.AreEqual(1f, s.Master);
        Assert.AreEqual(0f, s.Music);
        Assert.AreEqual(Palettes.Standard, s.Palette, "an unknown palette falls back to standard");
    }
}
