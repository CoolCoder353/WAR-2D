using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using NUnit.Framework;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>
/// v0.14: the compiled AI assembly (<c>WAR2D.Ai</c>) reaches only what a player may: its team view and
/// <see cref="PlayerActions"/>. Every type it declares is checked (base types, interfaces, fields, signatures, locals),
/// and every method body's IL is scanned for the types, fields and methods it names, so a reference can't hide in a
/// lambda, a local or a generic argument.
/// </summary>
public class AiAssemblyTests
{
    /// <summary>The server's own state the AI must never touch (spec §4, plan Global constraints).</summary>
    private static readonly Type[] Forbidden =
    {
        typeof(SimData), typeof(SimCommandQueue), typeof(SimCommand), typeof(SimContext), typeof(MapStore),
        typeof(Unity.Entities.EntityManager), typeof(ServerPlayer), typeof(WorldStateManager), typeof(GameCore),
        typeof(ClientPlayer), typeof(WAR2D.Net.Replication.ReplicationService),
        typeof(WAR2D.AiView.TeamViewBuilder), typeof(WAR2D.AiView.AiRunner), typeof(WAR2D.AiView.GameCoreFacts),
    };

    /// <summary>Whole namespaces out of reach: the ECS world, native containers, jobs and the network.</summary>
    private static readonly string[] ForbiddenNamespaces = { "Unity.Entities", "Unity.Collections", "Unity.Jobs", "Unity.Burst", "Mirror" };

    /// <summary>A seat's server record (its <see cref="ServerPlayer"/>) is out of reach even through the seat.</summary>
    private static readonly MemberInfo[] ForbiddenMembers = { typeof(AiSeat).GetProperty(nameof(AiSeat.Player)) };

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null))
        .ToDictionary(o => o.Value);

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static Assembly AiAssembly => typeof(WAR2D.Ai.AiBrain).Assembly;

    [Test]
    public void TheAiAssemblyReferencesNothingOfTheServersState()
    {
        Assert.That(AiAssembly.GetName().Name, Is.EqualTo("WAR2D.Ai"));
        var hits = new SortedSet<string>();
        HashSet<string> named = Scan(AiAssembly, hits);
        CollectionAssert.IsEmpty(hits);
        // Its referenced assemblies: the game, mathematics and the runtime only (no Entities, Collections or Mirror).
        foreach (AssemblyName reference in AiAssembly.GetReferencedAssemblies())
            Assert.That(ForbiddenNamespaces.Any(ns => reference.Name.StartsWith(ns, StringComparison.Ordinal)), Is.False, $"references {reference.Name}");
        // A positive control: the scan does see what the AI uses.
        Assert.That(named, Does.Contain(typeof(PlayerActions).FullName), "it acts through PlayerActions");
        Assert.That(named, Does.Contain(typeof(WAR2D.AiView.TeamView).FullName), "and reads its TeamView");
    }

    [Test]
    public void TheScanCatchesAForbiddenReference()
    {
        // PlayerActions itself (in the game assembly) touches ServerPlayer and the EntityManager: the scan must say so.
        var hits = new SortedSet<string>();
        ScanType(typeof(PlayerActions), hits, new HashSet<string>());
        Assert.That(hits.Any(h => h.Contains(nameof(ServerPlayer))), Is.True, string.Join("\n", hits));
        Assert.That(hits.Any(h => h.Contains(nameof(Unity.Entities.EntityManager))), Is.True);
        // And a seat's Player property, read through the seat.
        hits.Clear();
        ScanType(typeof(GameCore), hits, new HashSet<string>());
        Assert.That(hits.Any(h => h.Contains("get_Player")), Is.True, "GameCore reads seat.Player");
    }

    /// <summary>Every type the assembly declares; returns the full names of the types it names (for the positive control).</summary>
    private static HashSet<string> Scan(Assembly assembly, SortedSet<string> hits)
    {
        var named = new HashSet<string>();
        foreach (Type type in assembly.GetTypes()) ScanType(type, hits, named);
        return named;
    }

    private static void ScanType(Type type, SortedSet<string> hits, HashSet<string> named)
    {
        string where = type.FullName;
        CheckType(type.BaseType, where + " base", hits, named);
        foreach (Type i in type.GetInterfaces()) CheckType(i, where + " interface", hits, named);
        foreach (FieldInfo f in type.GetFields(All)) CheckType(f.FieldType, $"{where}.{f.Name}", hits, named);
        foreach (PropertyInfo p in type.GetProperties(All)) CheckType(p.PropertyType, $"{where}.{p.Name}", hits, named);
        foreach (MethodBase m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
        {
            string at = $"{where}.{m.Name}";
            if (m is MethodInfo mi) CheckType(mi.ReturnType, at + " returns", hits, named);
            foreach (ParameterInfo p in m.GetParameters()) CheckType(p.ParameterType, at + " parameter", hits, named);
            MethodBody body = m.GetMethodBody();
            if (body == null) continue;
            foreach (LocalVariableInfo local in body.LocalVariables) CheckType(local.LocalType, at + " local", hits, named);
            foreach (int token in Tokens(body.GetILAsByteArray()))
            {
                MemberInfo member = Resolve(m, token);
                if (member != null) CheckMember(member, at, hits, named);
            }
        }
    }

    /// <summary>The metadata tokens of the field, method, type and token operands in a method body's IL.</summary>
    private static IEnumerable<int> Tokens(byte[] il)
    {
        int i = 0;
        while (i < il.Length)
        {
            short value = il[i++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[i++]));
            if (!OpCodesByValue.TryGetValue(value, out OpCode op)) throw new InvalidOperationException($"unknown opcode 0x{value:X} at {i}");
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    yield return BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                default: i += 4; break; // InlineI, InlineBrTarget, InlineSig, InlineString, ShortInlineR
            }
        }
    }

    private static MemberInfo Resolve(MethodBase method, int token)
    {
        Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        try { return method.Module.ResolveMember(token, typeArgs, methodArgs); }
        catch (ArgumentException) { return null; }
    }

    private static void CheckMember(MemberInfo member, string at, SortedSet<string> hits, HashSet<string> named)
    {
        foreach (MemberInfo forbidden in ForbiddenMembers)
            if (forbidden is PropertyInfo p && (member == p.GetMethod || member == p.SetMethod || member.Name == p.GetMethod.Name && member.DeclaringType == p.DeclaringType))
                hits.Add($"{at} uses {p.DeclaringType.Name}.{member.Name}");
        switch (member)
        {
            case Type t: CheckType(t, at, hits, named); break;
            case FieldInfo f:
                CheckType(f.DeclaringType, $"{at} ({f.Name})", hits, named);
                CheckType(f.FieldType, $"{at} ({f.Name})", hits, named);
                break;
            case MethodBase m:
                CheckType(m.DeclaringType, $"{at} ({m.Name})", hits, named);
                if (m is MethodInfo mi) CheckType(mi.ReturnType, $"{at} ({m.Name})", hits, named);
                foreach (ParameterInfo p in m.GetParameters()) CheckType(p.ParameterType, $"{at} ({m.Name})", hits, named);
                if (m.IsGenericMethod) foreach (Type g in m.GetGenericArguments()) CheckType(g, $"{at} ({m.Name})", hits, named);
                break;
        }
    }

    private static void CheckType(Type type, string at, SortedSet<string> hits, HashSet<string> named)
    {
        if (type == null) return;
        if (type.HasElementType) { CheckType(type.GetElementType(), at, hits, named); return; }
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (Type g in type.GetGenericArguments()) CheckType(g, at, hits, named);
            type = type.GetGenericTypeDefinition();
        }
        if (type.IsGenericParameter) return;
        if (type.FullName != null) named.Add(type.FullName);
        if (Forbidden.Contains(type)) hits.Add($"{at} names {type.FullName}");
        string ns = type.Namespace ?? "";
        foreach (string forbidden in ForbiddenNamespaces)
            if (ns == forbidden || ns.StartsWith(forbidden + ".", StringComparison.Ordinal)) hits.Add($"{at} names {type.FullName}");
    }
}
