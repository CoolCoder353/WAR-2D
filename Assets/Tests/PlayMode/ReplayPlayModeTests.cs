using System;
using System.Collections;
using System.IO;
using System.Linq;
using Config;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using WAR2D.Client;
using WAR2D.Client.Replay;
using WAR2D.Net.Replay;

public class ReplayPlayModeTests
{
    private GameConfigData config;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        ReplayPlayer.Instance?.Close();
        yield return PlayModeMatch.TearDown();
    }

    [UnityTest, Timeout(150000)]
    public IEnumerator HostedMatchLeavesAReplayThatPlaysToTheEnd()
    {
        config.Match.Map.Size = 256;
        config.Match.Map.Seed = 7;
        config.Replays.Enabled = true;
        DateTime started = DateTime.UtcNow.AddSeconds(-1);
        yield return PlayModeMatch.StartMatchAsHost(config);
        yield return PlayModeMatch.PlaceHQ(null);
        yield return new WaitForSeconds(0.5f);
        int owner = PlayModeMatch.LocalOwner;
        Assert.That(WorldStateManager.Instance.Recorder, Is.Not.Null, "Replays/Enabled records every hosted match");

        DevApi.AllowForTests = true;
        Unity.Mathematics.int2 hq = default;
        foreach (var e in WAR2D.Net.Replication.ClientBuildings.Current.Entries.Values) if (e.Data.ownerId == owner) hq = (Unity.Mathematics.int2)e.Data.position;
        WorldStateManager wsm = WorldStateManager.Instance;
        for (int dy = -8; dy <= 8; dy++)
            for (int dx = -8; dx <= 8; dx++)
                if (wsm.Map.IsWalkable(hq + new Unity.Mathematics.int2(dx, dy)))
                    wsm.DevSpawnUnit(owner, (Unity.Mathematics.float2)(hq + new Unity.Mathematics.int2(dx, dy)) + 0.5f);
        DevApi.AllowForTests = false;
        yield return new WaitForSeconds(1.5f);
        GameCore.Instance.DeclareWinner(new[] { owner });
        yield return PlayModeMatch.WaitUntil(() => WorldStateManager.Instance.Recorder.Finished, 10f);

        ReplaySummary saved = ReplayLibrary.List().FirstOrDefault(s => s.Utc >= started && s.Header.Seed == 7);
        Assert.That(saved, Is.Not.Null, "a new replay with the match's seed");
        string path = saved.Path;
        using (FileStream f = File.OpenRead(path))
        {
            Assert.IsTrue(ReplayReader.TryOpen(f, out ReplayReader r, out string error), error);
            bool end = false;
            int unitRecords = 0;
            using (r)
                while (r.TryNext(out _, out ReplayChannel c, out _))
                {
                    end |= c == ReplayChannel.End;
                    if (c == ReplayChannel.UnitsReliable || c == ReplayChannel.UnitsUnreliable) unitRecords++;
                }
            Assert.IsTrue(end, "the recording ends with its outcome");
            Assert.Greater(unitRecords, 0, "units were recorded");
        }

        yield return PlayModeMatch.TearDown();
        ReplayPlayer player = ReplayPlayer.Open(path, out string openError);
        Assert.That(player, Is.Not.Null, openError);
        player.Speed = 2f;
        bool sawUnits = false;
        float until = Time.realtimeSinceStartup + 30f;
        while (player.Playing && Time.realtimeSinceStartup < until)
        {
            sawUnits |= ClientWorld.Instance != null && ClientWorld.Instance.KnownCount > 0;
            yield return null;
        }
        Assert.IsFalse(player.Playing, "plays to the end");
        Assert.IsTrue(sawUnits, "the viewer showed the recorded units");
        Assert.That(player.Seconds, Is.GreaterThanOrEqualTo(player.DurationSeconds - 0.1f));
        player.Seek(1f);
        Assert.That(player.Seconds, Is.EqualTo(1f).Within(0.01f));
        player.Close();
        yield return null;
        File.Delete(path);
    }
}
