using System;
using System.Collections.Generic;
using Mirror;
using NUnit.Framework;
using Unity.Mathematics;
using WAR2D.Net.Replication;
using WAR2D.World;

/// <summary>Terrain delta encoding and per-client last-seen tracking.</summary>
public class TerrainCodecTests
{
    private static ClientTerrain Terrain(out MapStore map)
    {
        map = MapStore.FromAscii(new[] { "....", "....", "....", "...." });
        var terrain = new ClientTerrain();
        terrain.Init(map.Grid);
        return terrain;
    }

    [Test]
    public void RoundTripsCellsAndReportsChangedTiles()
    {
        var terrain = Terrain(out MapStore map);
        using (map)
        {
            var writer = new NetworkWriter();
            TerrainCodec.Write(writer, new List<TerrainCell>
            {
                new TerrainCell { Index = 5, Type = (byte)TileType.Wall },
                new TerrainCell { Index = 6, Type = (byte)TileType.PlayerWall, OwnerId = 77, Health = 40 },
            });
            IReadOnlyList<int2> changed = null;
            terrain.Changed += c => changed = c;
            Assert.IsTrue(TerrainCodec.Apply(terrain, writer.ToArraySegment()));
            Assert.AreEqual((byte)TileType.Wall, terrain.Tiles[5]);
            Assert.AreEqual((byte)TileType.PlayerWall, terrain.Tiles[6]);
            Assert.AreEqual(77, terrain.WallOwner(6));
            Assert.AreEqual(40, terrain.WallHealth(6));
            CollectionAssert.AreEquivalent(new[] { new int2(1, 1), new int2(2, 1) }, changed);
        }
    }

    [Test]
    public void RejectsOutOfRangeIndicesAndUnknownKinds()
    {
        var terrain = Terrain(out MapStore map);
        using (map)
        {
            var writer = new NetworkWriter();
            TerrainCodec.Write(writer, new List<TerrainCell> { new TerrainCell { Index = 999, Type = 0 } });
            Assert.IsFalse(TerrainCodec.Apply(terrain, writer.ToArraySegment()));
            writer.Position = 0;
            TerrainCodec.Write(writer, new List<TerrainCell> { new TerrainCell { Index = 1, Type = 42 } });
            Assert.IsFalse(TerrainCodec.Apply(terrain, writer.ToArraySegment()));
            Assert.IsFalse(TerrainCodec.Apply(terrain, new ArraySegment<byte>(new byte[] { 3, 1 })), "truncated");
        }
    }

    [Test]
    public void InterestSendsAChangeOnlyOnceAndOnlyWhenSeen()
    {
        byte[] initial = new byte[16];
        var current = new TerrainCell[16];
        for (int i = 0; i < 16; i++) current[i] = new TerrainCell { Index = i };
        current[5].Type = (byte)TileType.Wall;
        var changed = new HashSet<int> { 5 };
        var interest = new TerrainInterest();
        var writer = new NetworkWriter();
        bool seen = false;

        Assert.AreEqual(0, interest.Update(changed, i => current[i], _ => seen, 4, initial, writer, int.MaxValue), "unseen: nothing");
        seen = true;
        Assert.AreEqual(1, interest.Update(changed, i => current[i], _ => seen, 4, initial, writer, int.MaxValue));
        writer.Position = 0;
        Assert.AreEqual(0, interest.Update(changed, i => current[i], _ => seen, 4, initial, writer, int.MaxValue), "already sent");
        current[5].Type = (byte)TileType.Ground; // dug back to its initial kind
        Assert.AreEqual(1, interest.Update(changed, i => current[i], _ => seen, 4, initial, writer, int.MaxValue), "a change back is a change too");
    }
}
