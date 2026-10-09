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
            if (kind > (byte)OrderKind.Stop) Assert.IsFalse(valid);
            else if (kind <= (byte)OrderKind.AttackMove) Assert.AreEqual(CommandValidator.IsInside(goal, min, max), valid);
            else Assert.IsTrue(valid, "Stop and Hold ignore the goal");
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
    public void SpawnerQueue_NeverLeavesItsRange()
    {
        var rng = new System.Random(31);
        for (int i = 0; i < Iterations; i++)
        {
            int count = rng.Next(int.MinValue, int.MaxValue);
            int before = count;
            bool changed = rng.Next(2) == 0 ? SpawnerRules.TryEnqueue(ref count) : SpawnerRules.TryDequeue(ref count);
            if (!changed) Assert.That(count, Is.EqualTo(before), "a refused change leaves the count alone");
            else Assert.That(count, Is.InRange(0, SpawnerRules.MaxQueue));
        }
    }
}
