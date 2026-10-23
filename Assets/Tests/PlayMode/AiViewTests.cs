using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Config;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using WAR2D.AiView;
using WAR2D.Client;
using WAR2D.Net.Replication;
using WAR2D.Sim;

/// <summary>
/// v0.14 spec §4's proof in a hosted match: a human and an AI on one team (sharing vision) know the same units and
/// buildings: the AI's <see cref="TeamView"/> equals the human client's stores on the same settled world.
/// </summary>
public class AiViewTests
{
    private GameConfigData config;
    private ClientUnitStore store;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        config = PlayModeMatch.Configure();
        store = new ClientUnitStore(config.Simulation.MaxEntities, config.Simulation.TickSeconds);
        ReplicationClient.Received += OnBatch;
        yield return PlayModeMatch.LoadMenu();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        ReplicationClient.Received -= OnBatch;
        DevApi.AllowForTests = false;
        yield return PlayModeMatch.TearDown();
        store.Dispose();
    }

    private void OnBatch(ReplicationBatch batch) => Assert.IsTrue(store.Apply(batch.Payload, batch.Tick), "malformed batch from the server");

    private static void Spawn(int owner, float2 position, List<int> ids) =>
        SimCommandQueue.Instance.Enqueue(new SimCommand { Kind = SimCommandKind.SpawnUnit, OwnerId = owner, Position = position, UnitType = UnitType.Builder, OnSpawned = ids.Add });

    /// <summary>The open ground tile farthest from every given point (sampled every 3 tiles).</summary>
    private static int2 Farthest(WorldStateManager world, params int2[] from)
    {
        (int2 min, int2 max) = world.MapBounds;
        int2 best = min;
        float bestDistance = -1f;
        for (int y = min.y + 2; y < max.y - 2; y += 3)
        for (int x = min.x + 2; x < max.x - 2; x += 3)
        {
            var t = new int2(x, y);
            if (!world.Map.IsWalkable(t) || world.Map.Grid.TileAt(t) != TileType.Ground) continue;
            float d = from.Min(p => math.distance(t, p));
            if (d > bestDistance) { bestDistance = d; best = t; }
        }
        return best;
    }

    [UnityTest, Timeout(120000)]
    public IEnumerator AnAiTeammatesViewEqualsTheHumanClientsStores()
    {
        GameManager.Instance.HostServer();
        yield return PlayModeMatch.WaitUntil(() => NetworkClient.isConnected && NetworkClient.localPlayer != null, 15f);
        GameCore core = GameCore.Instance;
        MatchSettings settings = core.Settings;
        settings.Mode = MatchMode.Teams;
        core.Settings = settings;
        NetworkClient.localPlayer.GetComponent<ClientPlayer>().lobbyTeam = 0;
        AiSeat ai = core.AddAi(AiDifficulty.Normal, AiStyle.Balanced, lobbyTeam: 0);
        Assert.That(ai, Is.Not.Null);
        int me = PlayModeMatch.LocalOwner;

        DevApi.AllowForTests = true;
        core.Cmd_StartGame();
        yield return PlayModeMatch.WaitUntil(() => SceneManager.GetActiveScene().name == config.Match.Scene && WorldStateManager.Instance != null && NetworkClient.ready, 30f);
        DevApi.AllowForTests = false;
        Assert.That(core.TeamOf(ai.OwnerId), Is.EqualTo(core.TeamOf(me)), "one team");
        Assert.That(ai.Brain, Is.Not.Null, "the AI assembly registered its brain");

        WorldStateManager world = WorldStateManager.Instance;
        Assert.That(world.TryFindBuildableAnchor(BuildingType.Base, 0f, ai.OwnerId, out int2 aiHq), Is.True);
        Assert.That(PlayerActions.PlaceHq(ai.OwnerId, aiHq), Is.True);
        yield return PlayModeMatch.WaitUntil(() => AiSeatPlayModeTests.HasBuilding(ai.OwnerId, BuildingType.Base), 5f);
        int2 hq = default;
        yield return PlayModeMatch.PlaceHQ(a => hq = a);

        // A third player (a Scripted seat, a team of its own): some of its units and buildings in the team's sight, some far off.
        int third = AiOwnerIds.For(5);
        DevApi.AllowForTests = true;
        Assert.That(core.DevAddScriptedAi(third, 1000f), Is.Not.Null);
        int2 far = Farthest(world, hq, aiHq);
        Assert.That(world.TryFindSpawnTile(hq, out int2 near), Is.True);
        Assert.That(world.TryFindBuildableAnchor(BuildingType.Barracks, 0f, me, out int2 nearBarracks), Is.True);
        Assert.That(world.DevPlaceBuilding(third, BuildingType.Barracks, nearBarracks, 1e6f), Is.True);
        // Its HQ, out of the team's sight (without one it would be out, and the match over).
        int2 farHq = AiSeatPlayModeTests.FarFrom(world, hq, aiHq, third, BuildingType.Base);
        Assert.That(world.DevPlaceBuilding(third, BuildingType.Base, farHq, 1e6f), Is.True);
        DevApi.AllowForTests = false;
        var mine = new List<int>();
        var theirs = new List<int>();
        var nearThird = new List<int>();
        var farThird = new List<int>();
        Assert.That(world.TryFindSpawnTile(aiHq, out int2 aiTile), Is.True);
        for (int i = 0; i < 3; i++) { Spawn(me, (float2)near + 0.5f, mine); Spawn(ai.OwnerId, (float2)aiTile + 0.5f, theirs); }
        for (int i = 0; i < 2; i++) { Spawn(third, (float2)near + 0.5f, nearThird); Spawn(third, (float2)far + 0.5f, farThird); }
        yield return PlayModeMatch.WaitUntil(() => mine.Count == 3 && theirs.Count == 3 && nearThird.Count == 2 && farThird.Count == 2, 10f);
        yield return PlayModeMatch.WaitUntil(() => store.IsKnown(NetIdAllocator.IndexOf(nearThird[0])), 10f);
        yield return new UnityEngine.WaitForSeconds(2f); // everything settled into sight (builders are unarmed: nobody dies)

        // The AI's view, built on a settled tick, against the human client's stores once that tick reached it.
        TeamView view = null;
        int viewTick = -1;
        void Capture(SimData data, int tick, int count)
        {
            view = world.AiViews.Build(ai.OwnerId, data, tick, count);
            viewTick = tick;
            world.Sim.Settled -= Capture;
        }
        world.Sim.Settled += Capture;
        yield return PlayModeMatch.WaitUntil(() => view != null, 5f);
        for (int i = 0; i < 10; i++) yield return null;

        var viewUnits = new HashSet<int>(view.OwnUnits.Select(u => u.Id).Concat(view.OtherUnits.Select(u => u.Id)));
        var clientUnits = new HashSet<int>();
        for (int k = 0; k < store.Count; k++) clientUnits.Add(store.IdOf(store.IndexAt(k)));
        Assert.That(viewUnits, Is.EquivalentTo(clientUnits), $"units at tick {viewTick}");
        Assert.That(view.OwnUnits.Select(u => u.Id), Is.EquivalentTo(theirs), "its own units");
        Assert.That(viewUnits, Is.SupersetOf(mine).And.SupersetOf(nearThird), "its teammate's, and the third player's in sight");
        Assert.That(viewUnits.Intersect(farThird), Is.Empty, "never the ones out of sight");

        var viewBuildings = view.Buildings.Select(b => (b.Id, b.Ghost)).OrderBy(b => b.Id).ToArray();
        var clientBuildings = ClientBuildings.Current.Entries.Values.Select(e => (e.Data.id, e.Ghost)).OrderBy(b => b.Item1).ToArray();
        Assert.That(viewBuildings, Is.EqualTo(clientBuildings), "buildings and ghosts");
        Assert.That(view.Buildings.Any(b => b.OwnerId == third && b.Anchor.Equals(nearBarracks)), Is.True, "the third player's Barracks in sight");
        Assert.That(view.Buildings.Any(b => b.OwnerId == third && b.Type == BuildingType.Base), Is.False, "not its HQ out of sight");
        Assert.That(core.CurrentState, Is.EqualTo(GameState.Playing));
        Assert.That(view.SharedWithMe.Contains(me) && view.SharingWith.Contains(me), Is.True, "teammates share vision");
        Assert.That(view.IsEnemy(third) && !view.IsEnemy(me), Is.True);
    }
}
