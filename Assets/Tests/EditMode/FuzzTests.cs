using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

/// <summary>Random and malformed inputs must never throw and must keep validator invariants.</summary>
public class FuzzTests
{
    private const int Iterations = 20000;

    [Test]
    public void Nickname_NeverThrows_AndOutputIsSafe()
    {
        var rng = new System.Random(1234);
        for (int i = 0; i < Iterations; i++)
        {
            int len = rng.Next(0, 80);
            var chars = new char[len];
            for (int c = 0; c < len; c++) chars[c] = (char)rng.Next(0, 0xFFFF);
            if (CommandValidator.TrySanitizeNickname(new string(chars), out string clean))
            {
                Assert.That(clean.Length, Is.InRange(1, CommandValidator.MaxNicknameLength));
                Assert.That(clean.IndexOfAny(new[] { '<', '>' }), Is.EqualTo(-1));
                foreach (char ch in clean) Assert.That(char.IsControl(ch), Is.False);
            }
        }
    }

    [Test]
    public void Placement_NeverThrows()
    {
        var rng = new System.Random(99);
        Func<int2, TileType> get = p => TileType.Wall;
        for (int i = 0; i < Iterations; i++)
        {
            var type = (BuildingType)rng.Next(-5, 10);
            var anchor = new int2(rng.Next(int.MinValue, int.MaxValue), rng.Next(int.MinValue, int.MaxValue));
            float rot = (float)(rng.NextDouble() * 1000 - 500);
            var state = (GameState)rng.Next(0, 5);
            Assert.DoesNotThrow(() => PlacementRules.Check(type, anchor, rot, new int2(rng.Next(1, 4), rng.Next(1, 4)), state, rng.Next(2) == 0, get, p => false, p => false));
        }
    }

    [Test]
    public void Box_NeverThrows()
    {
        var rng = new System.Random(7);
        for (int i = 0; i < Iterations; i++)
        {
            var a = new int2(rng.Next(int.MinValue, int.MaxValue), rng.Next(int.MinValue, int.MaxValue));
            var b = new int2(rng.Next(int.MinValue, int.MaxValue), rng.Next(int.MinValue, int.MaxValue));
            Assert.DoesNotThrow(() => CommandValidator.IsBoxValid(a, b));
        }
    }

    [Test]
    public void OrderIdDecode_NeverThrows()
    {
        var rng = new System.Random(11);
        var into = new List<int>();
        for (int i = 0; i < Iterations; i++)
        {
            var bytes = new byte[rng.Next(0, 64)];
            rng.NextBytes(bytes);
            into.Clear();
            Assert.DoesNotThrow(() => OrderIdCodec.TryDecode(bytes, OrderIdCodec.MaxIdsPerChunk, into));
        }
    }

    [Test]
    public void Order_NeverThrows_AndKeepsInvariants()
    {
        var rng = new System.Random(23);
        int2 min = int2.zero, max = new int2(255, 255);
        for (int i = 0; i < Iterations; i++)
        {
            byte kind = (byte)rng.Next(0, 256);
            var goal = new int2(rng.Next(-512, 512), rng.Next(-512, 512));
            bool valid = false;
            Assert.DoesNotThrow(() => valid = CommandValidator.IsOrderValid(kind, goal, min, max));
            if (kind > (byte)OrderKind.Detonate) Assert.IsFalse(valid);
            else if (kind == (byte)OrderKind.Dig) Assert.AreEqual(CommandValidator.IsInside(goal, min + 1, max - 1), valid, "never the outer ring");
            else if (kind == (byte)OrderKind.Hold || kind == (byte)OrderKind.Stop || kind == (byte)OrderKind.Detonate) Assert.IsTrue(valid, "Stop, Hold and Detonate ignore the goal");
            else Assert.AreEqual(CommandValidator.IsInside(goal, min, max), valid);
        }
    }

    [Test]
    public void Diplomacy_NeverThrows_AndNeverAllowsSelfOrDisabled()
    {
        var rng = new System.Random(31);
        var diplomacy = new WAR2D.Sim.Diplomacy();
        for (int i = 0; i < Iterations; i++)
        {
            int from = rng.Next(int.MinValue, int.MaxValue), to = rng.Next(0, 3) == 0 ? from : rng.Next(int.MinValue, int.MaxValue);
            bool enabled = rng.Next(2) == 1, on = rng.Next(2) == 1;
            var state = (GameState)rng.Next(0, 6);
            PlayerState? sender = rng.Next(3) == 0 ? (PlayerState?)null : (PlayerState)rng.Next(0, 3);
            PlayerState? target = rng.Next(3) == 0 ? (PlayerState?)null : (PlayerState)rng.Next(0, 3);
            double now = rng.NextDouble() * 100, last = rng.Next(2) == 0 ? double.NegativeInfinity : rng.NextDouble() * 100;
            bool allowed = false;
            Assert.DoesNotThrow(() => allowed = DiplomacyRules.CanChange(enabled, state, sender, target, from, to, now, last, 2f));
            if (from == to || !enabled) Assert.IsFalse(allowed);
            int slotFrom = rng.Next(-4, 20), slotTo = rng.Next(-4, 20);
            Assert.DoesNotThrow(() => diplomacy.SetAttack(slotFrom, slotTo, on));
            Assert.DoesNotThrow(() => diplomacy.SetShareVision(slotFrom, slotTo, on));
        }
    }

    [Test]
    public void MenuPause_NeverThrows_AndNeedsExactlyOneHuman()
    {
        var rng = new System.Random(77);
        for (int i = 0; i < Iterations; i++)
        {
            var state = (GameState)rng.Next(-3, 9);
            int humans = rng.Next(int.MinValue, int.MaxValue) % 12;
            bool own = rng.Next(2) == 0;
            bool may = PauseRules.MayMenuPause(state, humans, own);
            if (may) Assert.That(humans == 1 && own, Is.True);
        }
    }

    [Test]
    public void SpawnerQueue_NeverLeavesItsRange()
    {
        var rng = new System.Random(31);
        int[] steps = { -1, 1, 10, 50, 7, -50, int.MinValue, int.MaxValue };
        for (int i = 0; i < Iterations; i++)
        {
            int count = rng.Next(int.MinValue, int.MaxValue);
            int before = count;
            int changed = SpawnerRules.Apply(ref count, steps[rng.Next(steps.Length)]);
            if (changed == 0) Assert.That(count, Is.EqualTo(before), "a refused change leaves the count alone");
            else Assert.That(count, Is.InRange(0, SpawnerRules.MaxQueue));
        }
    }

    [Test]
    public void MatchSettingsAndColours_NeverThrow()
    {
        var rng = new System.Random(77);
        var lobby = new Config.LobbyConfig { MapSizes = new[] { 256, 512, 1024 }, StartingResources = new[] { 500f, 1000f } };
        for (int i = 0; i < Iterations; i++)
        {
            var s = new MatchSettings
            {
                Mode = (MatchMode)rng.Next(0, 256),
                Diplomacy = rng.Next(2) == 0,
                MapSize = rng.Next(int.MinValue, int.MaxValue),
                Seed = (uint)rng.Next(int.MinValue, int.MaxValue),
                StartingResources = (float)(rng.NextDouble() * 1e6 - 5e5),
            };
            bool valid = false;
            Assert.DoesNotThrow(() => valid = MatchSettingsRules.IsValid(s, lobby));
            if (valid) Assert.That(System.Array.IndexOf(lobby.MapSizes, s.MapSize), Is.GreaterThanOrEqualTo(0));
            int colour = rng.Next(-300, 300);
            Assert.DoesNotThrow(() => LobbyRules.IsColourFree(colour, new[] { rng.Next(0, 8) }));
            Assert.DoesNotThrow(() => LobbyRules.IsTeamChoiceValid((MatchMode)rng.Next(0, 256), rng.Next(-10, 10)));
        }
    }

    [Test]
    public void GiftAmounts_NeverAllowMoreThanTheBalance()
    {
        var rng = new System.Random(91);
        for (int i = 0; i < Iterations; i++)
        {
            float balance = (float)(rng.NextDouble() * 10000);
            float amount = rng.Next(4) switch
            {
                0 => float.NaN,
                1 => float.PositiveInfinity,
                _ => (float)(rng.NextDouble() * 20000 - 5000),
            };
            bool valid = false;
            Assert.DoesNotThrow(() => valid = GiftRules.IsValid(amount, balance));
            if (valid) Assert.That(amount, Is.InRange(1f, balance));
            Assert.DoesNotThrow(() => GiftRules.CanGift((GameState)rng.Next(0, 10), (PlayerState)rng.Next(0, 4), null, rng.Next(), rng.Next(), rng.NextDouble(), rng.NextDouble(), 5f));
        }
    }

    [Test]
    public void TerrainDecode_NeverThrows()
    {
        var map = WAR2D.World.MapStore.FromAscii(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Repeat(new string('.', 16), 16)));
        using (map)
        {
            var terrain = new WAR2D.Net.Replication.ClientTerrain();
            terrain.Init(map.Grid);
            var rng = new System.Random(7);
            for (int i = 0; i < 2000; i++)
            {
                var bytes = new byte[rng.Next(0, 64)];
                rng.NextBytes(bytes);
                Assert.DoesNotThrow(() => WAR2D.Net.Replication.TerrainCodec.Apply(terrain, new ArraySegment<byte>(bytes)));
                foreach (byte t in terrain.Tiles) Assert.LessOrEqual(t, (byte)TileType.WallBlueprint);
            }
        }
    }

    [Test]
    public void WallPlacement_NeverThrows_AndStaysOnTheMap()
    {
        var rng = new System.Random(31);
        var size = new int2(64, 48);
        for (int i = 0; i < Iterations; i++)
        {
            int2 a = RandomTile(rng), b = RandomTile(rng);
            bool valid = false;
            Assert.DoesNotThrow(() => valid = WallRules.ValidDrag(a, b, size, 40));
            if (!valid) continue;
            var line = new List<int2>();
            WallRules.Line(a, b, line);
            Assert.LessOrEqual(line.Count, 40);
            foreach (int2 t in line) Assert.IsTrue(t.x >= 0 && t.y >= 0 && t.x < size.x && t.y < size.y);
        }
    }

    private static int2 RandomTile(System.Random rng) =>
        rng.Next(4) == 0 ? new int2(rng.Next(int.MinValue, int.MaxValue), rng.Next(int.MinValue, int.MaxValue)) : new int2(rng.Next(-8, 72), rng.Next(-8, 56));

    [Test]
    public void BuildingRecords_NeverThrow()
    {
        var rng = new System.Random(17);
        var buildings = new WAR2D.Net.Replication.ClientBuildings();
        for (int i = 0; i < 5000; i++)
        {
            var bytes = new byte[rng.Next(0, 96)];
            rng.NextBytes(bytes);
            Assert.DoesNotThrow(() => WAR2D.Net.Replication.BuildingCodec.Apply(buildings, new ArraySegment<byte>(bytes)));
        }
    }
}
