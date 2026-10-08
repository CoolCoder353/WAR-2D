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
}
