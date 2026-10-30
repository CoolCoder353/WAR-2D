using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

/// <summary>v0.14 AI seats: reserved owner ids, callsign draws, and the AI assembly's boundary.</summary>
public class AiSeatTests
{
    [Test]
    public void AiOwnerIdsNeverMeetMirrorNetIds()
    {
        // Mirror counts netIds up from 1 (NetworkIdentity.GetNextNetworkId); a human's owner id is its netId.
        FieldInfo next = typeof(Mirror.NetworkIdentity).GetField("nextNetworkId", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.That(next, Is.Not.Null, "Mirror's netId allocator moved: re-check the reserved range");
        uint current = (uint)next.GetValue(null);
        Assert.That(current, Is.LessThan((uint)AiOwnerIds.First), "the allocator is far below the AI range");
        Assert.That(AiOwnerIds.IsAi(BuildingData.UIntToInt(current)), Is.False);
        // Every netId below the range is a human id, and one past int.MaxValue turns negative (never an AI id).
        foreach (uint netId in new uint[] { 1, 2, 1000001, (uint)AiOwnerIds.First - 1, (uint)int.MaxValue + 1, uint.MaxValue })
            Assert.That(AiOwnerIds.IsAi(BuildingData.UIntToInt(netId)), Is.False, $"netId {netId}");
        Assert.That(AiOwnerIds.IsAi(AiOwnerIds.For(0)), Is.True);
        Assert.That(AiOwnerIds.IsAi(AiOwnerIds.For(AiOwnerIds.Count - 1)), Is.True);
        Assert.That(AiOwnerIds.IsAi(int.MaxValue), Is.False, "the range stops short of int.MaxValue (no overflow)");
        Assert.That(AiOwnerIds.IsAi(0), Is.False);
        Assert.That(AiOwnerIds.Count, Is.GreaterThan(Config.ConfigParser.MinCallsigns * 100), "room for every AI a server ever adds");
    }

    [Test]
    public void CallsignsAreUniqueAndFollowTheSeed()
    {
        string[] list = { "A", "B", "C", "D", "E", "F", "G", "H" };
        var taken = new HashSet<string>();
        for (int i = 0; i < list.Length; i++)
        {
            string name = AiCallsigns.Draw(list, 42u, taken);
            Assert.That(name, Is.Not.Null);
            Assert.That(taken.Add(name), Is.True, "never repeats");
        }
        Assert.That(AiCallsigns.Draw(list, 42u, taken), Is.Null, "none left");
        Assert.That(AiCallsigns.Draw(list, 42u, null), Is.EqualTo(AiCallsigns.Draw(list, 42u, null)), "the same seed draws the same");
        var firsts = new HashSet<string>();
        for (uint seed = 1; seed <= 20; seed++) firsts.Add(AiCallsigns.Draw(list, seed, null));
        Assert.That(firsts.Count, Is.GreaterThan(1), "a reroll (new seed) changes them");
        Assert.That(AiCallsigns.Draw(new string[0], 1u, null), Is.Null);
    }

    [Test]
    public void TheAiAssemblyReachesOnlyWhatAPlayerMay()
    {
        // The asmdef keeps the ECS world, native arrays and the network out of reach at compile time.
        string asmdef = File.ReadAllText(Path.Combine("Assets", "Scripts", "Ai", "WAR2D.Ai.asmdef"));
        foreach (string forbidden in new[] { "Unity.Entities", "Unity.Collections", "Unity.Burst", "\"Mirror\"" })
            Assert.That(asmdef, Does.Not.Contain(forbidden), $"WAR2D.Ai must not reference {forbidden}");
        // And its code never names the server's own state (plan Task 5 extends this with a reflection check).
        var banned = new Regex(@"\b(SimData|SimCommandQueue|SimCommand|SimContext|MapStore|EntityManager|ServerPlayer|WorldStateManager)\b");
        foreach (string file in Directory.GetFiles(Path.Combine("Assets", "Scripts", "Ai"), "*.cs", SearchOption.AllDirectories))
        {
            string code = Regex.Replace(File.ReadAllText(file), @"//.*", ""); // comments may explain the rule
            Match hit = banned.Match(code);
            Assert.That(hit.Success, Is.False, $"{file} names {hit.Value}");
        }
        Assert.That(typeof(AiSeat).Assembly.GetName().Name, Is.EqualTo("WAR2D"), "the seat lives in the game, which can't reference WAR2D.Ai");
        Assert.That(Assembly.Load("WAR2D.Ai"), Is.Not.Null, "the AI assembly is built");
    }
}
