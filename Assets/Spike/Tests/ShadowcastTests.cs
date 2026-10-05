using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Spike;
using Random = Unity.Mathematics.Random;

/// <summary>
/// Correctness tests for the spike's line-of-sight fog: the symmetric shadowcast itself (its disc
/// shape, what it hides and its symmetry) and the source dedup and round-robin the fog system runs.
/// </summary>
public class ShadowcastTests
{
    // ---- helpers ----

    /// <summary>
    /// An all-floor map with a blocked border ring, and the given tiles turned to rock. Tile index is
    /// <c>y * size + x</c>.
    /// </summary>
    private static NativeArray<byte> OpenMap(int size, params int2[] rocks)
    {
        var tiles = new NativeArray<byte>(size * size, Allocator.Persistent);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            tiles[y * size + x] = x == 0 || y == 0 || x == size - 1 || y == size - 1
                ? SpikeMap.Border
                : SpikeMap.Floor;
        foreach (int2 rock in rocks) tiles[rock.y * size + rock.x] = SpikeMap.Rock;
        return tiles;
    }

    /// <summary>An open map with a solid rock block and a floor chamber inside it.</summary>
    private static NativeArray<byte> SealedRoomMap(int size, int blockMin, int blockMax, int roomMin, int roomMax)
    {
        var tiles = new NativeArray<byte>(size * size, Allocator.Persistent);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            tiles[y * size + x] = x == 0 || y == 0 || x == size - 1 || y == size - 1
                ? SpikeMap.Border
                : SpikeMap.Floor;
        for (int y = blockMin; y <= blockMax; y++)
        for (int x = blockMin; x <= blockMax; x++)
            tiles[y * size + x] = SpikeMap.Rock;
        for (int y = roomMin; y <= roomMax; y++)
        for (int x = roomMin; x <= roomMax; x++)
            tiles[y * size + x] = SpikeMap.Floor;
        return tiles;
    }

    /// <summary>A map with rock on every tile that rolls under 35 %.</summary>
    private static NativeArray<byte> RandomMap(ref Random rng, int size)
    {
        var tiles = new NativeArray<byte>(size * size, Allocator.Persistent);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            bool border = x == 0 || y == 0 || x == size - 1 || y == size - 1;
            tiles[y * size + x] = border || rng.NextFloat() < 0.35f ? SpikeMap.Rock : SpikeMap.Floor;
        }
        return tiles;
    }

    /// <summary>A persistent array holding the given values.</summary>
    private static NativeArray<T> Filled<T>(params T[] values) where T : unmanaged
    {
        var array = new NativeArray<T>(values.Length, Allocator.Persistent);
        for (int i = 0; i < values.Length; i++) array[i] = values[i];
        return array;
    }

    /// <summary>Casts from <paramref name="origin"/> into a fresh grid and returns the grid.</summary>
    private static NativeArray<byte> Cast(NativeArray<byte> tiles, int size, int2 origin, int radius)
    {
        var visible = new NativeArray<byte>(size * size, Allocator.Persistent);
        Shadowcast.Cast(origin, radius, size, size, tiles, ref visible);
        return visible;
    }

    private static bool Visible(NativeArray<byte> visible, int size, int2 tile) =>
        visible[tile.y * size + tile.x] != 0;

    private static int CountVisible(NativeArray<byte> visible)
    {
        int count = 0;
        for (int i = 0; i < visible.Length; i++) count += visible[i] != 0 ? 1 : 0;
        return count;
    }

    /// <summary>
    /// Asserts the visible grid is exactly the tiles with <c>dx^2 + dy^2 &lt;= radius^2 + radius</c> - the
    /// slightly rounded disc the algorithm draws - and nothing else.
    /// </summary>
    private static void AssertDisc(NativeArray<byte> visible, int size, int2 origin, int radius)
    {
        int inside = 0, missing = 0, extra = 0;
        int2 firstMissing = int2.zero, firstExtra = int2.zero;
        int limit = radius * radius + radius;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int dx = x - origin.x, dy = y - origin.y;
            bool expected = dx * dx + dy * dy <= limit;
            bool actual = visible[y * size + x] != 0;
            if (expected) inside++;
            if (expected == actual) continue;
            if (expected) { if (missing++ == 0) firstMissing = new int2(x, y); }
            else { if (extra++ == 0) firstExtra = new int2(x, y); }
        }

        Assert.Greater(inside, 0, "the disc must hold tiles");
        Assert.AreEqual(0, missing, $"{missing} tiles inside the radius-{radius} disc are not visible, first {firstMissing}");
        Assert.AreEqual(0, extra, $"{extra} tiles outside the radius-{radius} disc are visible, first {firstExtra}");
    }

    /// <summary>A random floor tile strictly inside a map's border.</summary>
    private static int2 RandomFloor(NativeArray<byte> tiles, int size, ref Random rng)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            var tile = new int2(rng.NextInt(1, size - 1), rng.NextInt(1, size - 1));
            if (tiles[tile.y * size + tile.x] == SpikeMap.Floor) return tile;
        }
        Assert.Fail("no floor tile found in 1000 attempts");
        return int2.zero;
    }

    /// <summary>
    /// A random floor tile within <paramref name="radius"/> tiles of <paramref name="from"/> (Euclidean,
    /// so the pair is inside both casts' discs), or <paramref name="from"/> itself if none is found.
    /// </summary>
    private static int2 RandomFloorNear(NativeArray<byte> tiles, int size, int2 from, int radius, ref Random rng)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            // A uniform direction and a square-root radius: an area-uniform point in the disc.
            float angle = rng.NextFloat(0f, 2f * math.PI);
            float reach = radius * math.sqrt(rng.NextFloat());
            var tile = from + new int2((int)math.round(reach * math.cos(angle)), (int)math.round(reach * math.sin(angle)));
            if ((uint)tile.x >= (uint)size || (uint)tile.y >= (uint)size) continue;
            if (tile.x == 0 || tile.y == 0 || tile.x == size - 1 || tile.y == size - 1) continue;
            if (math.lengthsq((float2)(tile - from)) > radius * radius) continue;
            if (tiles[tile.y * size + tile.x] != SpikeMap.Floor) continue;
            return tile;
        }
        return from;
    }

    // ---- the shadowcast ----

    /// <summary>
    /// On an open 64^2 map (border only) a cast is exactly the disc
    /// <c>dx^2 + dy^2 &lt;= 8 * 8 + 8</c>: nothing blocks, so the four quadrants must tile the circle
    /// with no gaps and no leaks - not one tile more, not one less.
    /// </summary>
    [Test]
    public void OpenMapIsADisc()
    {
        const int size = 64, radius = 8;
        var origin = new int2(32, 32);
        using var tiles = OpenMap(size);
        using var visible = Cast(tiles, size, origin, radius);

        AssertDisc(visible, size, origin, radius);

        Assert.Greater(CountVisible(visible), 200, "a radius-8 disc holds a couple of hundred tiles");
        Assert.IsTrue(Visible(visible, size, origin), "the origin sees itself");
    }

    /// <summary>
    /// A single rock at (34, 32) with the origin at (32, 32): the rock is visible (a wall shows
    /// itself), and the tiles behind it along the axis - (35, 32) and (36, 32) - are not. The shadow
    /// is a cone, not a blackout, so a tile beside the rock stays visible.
    /// </summary>
    [Test]
    public void WallHidesWhatIsBehindIt()
    {
        const int size = 64, radius = 8;
        var origin = new int2(32, 32);
        using var tiles = OpenMap(size, new int2(34, 32));
        using var visible = Cast(tiles, size, origin, radius);

        Assert.IsTrue(Visible(visible, size, new int2(34, 32)), "the rock itself is visible");
        Assert.IsFalse(Visible(visible, size, new int2(35, 32)), "the tile directly behind the rock is hidden");
        Assert.IsFalse(Visible(visible, size, new int2(36, 32)), "the tile two behind the rock is hidden");
        Assert.IsTrue(Visible(visible, size, new int2(34, 36)), "a tile beside the rock is still visible");
    }

    /// <summary>
    /// A sealed chamber - a floor room inside a solid rock block - is never visible from outside, no
    /// matter how close it is: every ray that reaches it crossed rock first. The wall of the block
    /// itself is visible, so the cast clearly reaches that far.
    /// </summary>
    [Test]
    public void CannotSeeIntoSealedRoom()
    {
        const int size = 64, radius = 16;
        var origin = new int2(32, 32);
        // A 9x9 rock block at (36..44, 36..44) with a 5x5 floor room inside it at (38..42, 38..42).
        using var tiles = SealedRoomMap(size, blockMin: 36, blockMax: 44, roomMin: 38, roomMax: 42);
        using var visible = Cast(tiles, size, origin, radius);

        for (int y = 38; y <= 42; y++)
        for (int x = 38; x <= 42; x++)
            Assert.IsFalse(Visible(visible, size, new int2(x, y)), $"the sealed tile ({x}, {y}) must not be visible");

        Assert.IsTrue(Visible(visible, size, new int2(36, 36)), "the block's outer corner is visible");
        Assert.IsTrue(Visible(visible, size, new int2(35, 40)), "the floor beside the block is visible");
    }

    /// <summary>
    /// Symmetry: on 200 random 48^2 maps at 35 % rock, for random floor pairs a and b within radius 10,
    /// a sees b exactly when b sees a. This is what makes fog fair - a player cannot be seen from a
    /// tile they cannot see.
    /// </summary>
    [Test]
    public void IsSymmetric()
    {
        const int size = 48, radius = 10, maps = 200, attempts = 60;
        var rng = new Random(20261006);
        int pairs = 0, sees = 0;

        for (int map = 0; map < maps; map++)
        {
            using var tiles = RandomMap(ref rng, size);

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                int2 a = RandomFloor(tiles, size, ref rng);
                int2 b = RandomFloorNear(tiles, size, a, radius, ref rng);
                if (a.Equals(b)) continue;

                using var fromA = Cast(tiles, size, a, radius);
                using var fromB = Cast(tiles, size, b, radius);

                bool aSeesB = Visible(fromA, size, b);
                bool bSeesA = Visible(fromB, size, a);
                pairs++;
                if (aSeesB) sees++;
                Assert.AreEqual(aSeesB, bSeesA,
                    $"map {map}: '{a} sees {b}' is {aSeesB}, but '{b} sees {a}' is {bSeesA}");
            }
        }

        Assert.Greater(pairs, 1000, "the sampled pairs must exercise a real share of the maps");
        Assert.Greater(sees, 0, "some pairs must be visible to each other, or the test proves nothing");
    }

    /// <summary>
    /// The integer slopes must stay exact at the largest radius swept, so a radius-32 cast on an open
    /// 128^2 map is the same disc as the radius-8 one: <c>dx^2 + dy^2 &lt;= 32 * 32 + 32</c>.
    /// </summary>
    [Test]
    public void ExtremeRadiusIsADisc()
    {
        const int size = 128, radius = 32;
        var origin = new int2(64, 64);
        using var tiles = OpenMap(size);
        using var visible = Cast(tiles, size, origin, radius);

        AssertDisc(visible, size, origin, radius);
    }

    // ---- the source collector ----

    /// <summary>
    /// Two units on one tile (vision 6) and a building on the same tile (vision 10) collapse to one
    /// source of radius 10: per tile the largest radius wins, because a larger disc is a superset.
    /// Units on a second tile collapse to one source of the unit radius, another team's units and
    /// buildings are not collected at all, and the scratch grid is left clean.
    /// </summary>
    [Test]
    public void DedupKeepsLargestRadius()
    {
        const int teams = 8, size = 16;
        using var positions = Filled(
            new float2(5.3f, 5.7f), new float2(5.9f, 5.1f), new float2(8.4f, 3.6f), new float2(11.5f, 11.5f));
        using var team = Filled<byte>(0, 0, 0, 1);
        using var health = Filled(100f, 100f, 100f, 100f);
        using var buildingPositions = Filled(new float2(5.5f, 5.5f), new float2(12.5f, 2.5f));
        using var buildingOwners = Filled<byte>(0, 6);
        using var buildingHealth = Filled(300f, 300f);
        using var scratch = new NativeArray<byte>(size * size, Allocator.Persistent);
        using var counters = new NativeArray<int>(1, Allocator.Persistent);
        using var sources = new NativeList<VisionSource>(8, Allocator.Persistent);

        new CollectVisionSourcesJob
        {
            Positions = positions, Team = team, Health = health, Capacity = 4,
            BuildingPositions = buildingPositions, BuildingOwners = buildingOwners,
            BuildingHealth = buildingHealth, BuildingCount = 2,
            Teams = teams, TeamIndex = 0, UnitVision = 6, BuildingVision = 10,
            Width = size, Height = size,
            Scratch = scratch, Sources = sources, RawCount = counters,
        }.Schedule().Complete();

        Assert.AreEqual(2, sources.Length, "one source per occupied tile");
        Assert.AreEqual(new int2(5, 5), sources[0].Tile);
        Assert.AreEqual(10, sources[0].Radius, "the building's larger radius wins on its tile");
        Assert.AreEqual(new int2(8, 3), sources[1].Tile);
        Assert.AreEqual(6, sources[1].Radius, "a unit-only tile keeps the unit radius");
        Assert.AreEqual(4, counters[0], "three team-0 units and one team-0 building were examined");

        for (int i = 0; i < scratch.Length; i++)
            Assert.AreEqual(0, scratch[i], "every scratch tile the collector touched must be cleared");
    }

    // ---- the fog system ----

    /// <summary>
    /// The cadence and the diff semantics Task 9 consumes: a team updates at 5 Hz (one team every other
    /// tick with two teams), a team's first update marks its whole vision as changed and carries it into
    /// Explored, and a second update with nothing moved changes no cell.
    /// </summary>
    [Test]
    public void FogSystemRoundRobinsTeamsAndTracksChangedCells()
    {
        const int size = 32;
        using var tiles = OpenMap(size);
        using var positions = Filled(new float2(10.5f, 16.5f), new float2(21.5f, 16.5f));
        using var team = Filled<byte>(0, 4); // player 0 and player 4: teams 0 and 1 in 4v4
        using var health = Filled(100f, 100f);

        Assert.AreEqual(1, (int)FogTeams.Of(player: 4, teams: FogTeams.FourVFourTeams), "4v4: player 4 is on team 1");
        Assert.AreEqual(4, (int)FogTeams.Of(player: 4, teams: FogTeams.FfaTeams), "FFA: player 4 is on team 4");

        var config = FogConfig.Defaults;
        config.Teams = FogTeams.FourVFourTeams;
        using var fog = new FogSystem(config, size, size, tiles, Allocator.Persistent);
        fog.SetUnits(positions, team, health, 2);

        for (int tick = 0; tick < 4; tick++)
        {
            fog.Tick();
            if (tick == 1) Assert.AreEqual(1, fog.LastTickTeams, "tick 2 refreshes team 0");
            else if (tick == 3) Assert.AreEqual(1, fog.LastTickTeams, "tick 4 refreshes team 1");
            else Assert.AreEqual(0, fog.LastTickTeams, "the other ticks refresh nobody");
        }

        Assert.IsTrue(Visible(fog.Visible(0), size, new int2(10, 16)), "team 0 sees its own unit");
        Assert.IsFalse(Visible(fog.Visible(0), size, new int2(21, 16)), "team 0 does not see team 1's unit");
        Assert.IsTrue(Visible(fog.Visible(1), size, new int2(21, 16)), "team 1 sees its own unit");

        int cells = CountVisible(fog.Visible(0));
        Assert.Greater(cells, 0, "the unit's disc is visible");
        Assert.AreEqual(cells, fog.Changed(0).Length, "a team's first update changes every cell it newly sees");
        Assert.AreEqual(cells, CountVisible(fog.Explored(0)), "and carries them into Explored");
        Assert.AreEqual(1, fog.SourceCount(0), "one deduplicated source for team 0");

        // Nothing moved: the next update of the same team changes no cell.
        fog.Tick();
        fog.Tick();
        Assert.AreEqual(0, fog.Changed(0).Length, "nothing moved, so nothing changed");
        Assert.AreEqual(1, fog.SourceCount(0), "the source count is stable while the unit stands still");
    }
}
