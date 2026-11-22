using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using WAR2D.Audio;
using WAR2D.EditorTools;

/// <summary>
/// Checks every built sound effect for defects a listener hears but a spec doesn't show (asset generation spec
/// §3.7): clipping, a DC offset, a click at the start or end. Report-only for the sounds that fail today
/// (<see cref="Known"/>, measured 2026-11-15 after the owner's wave review): they are fixed with the synth work (spec §5.1) and each fix takes its
/// row out. A sound that isn't listed must pass, and a listed one that now passes must be taken off the list.
/// </summary>
public class AudioLintTests
{
    /// <summary>Full-scale samples in a row before it counts as clipping.</summary>
    private const int ClipRun = 2;
    /// <summary>The mean of the samples may be this far from zero (0.5 % of full scale).</summary>
    private const float MaxDc = 0.005f;
    /// <summary>The first and last sample may be this far from zero (1 % of full scale).</summary>
    private const float MaxEdge = 0.01f;

    /// <summary>The sounds that fail today, with the rules they fail: "dc", "start", "end", "clip".</summary>
    private static readonly Dictionary<string, string> Known = new Dictionary<string, string>
    {
        // A pulse with a duty other than 50 % sits off centre, and so does low noise over a short sound.
        ["alert_gift"] = "dc", ["alert_upkeep"] = "dc", ["jammer_online"] = "dc", ["shell_incoming"] = "dc",
        ["building_destroyed"] = "dc",
        // No attack: the first sample is already loud.
        ["alert"] = "start", ["gem_mined"] = "start", ["radar_ping"] = "start",
        ["shot_builder"] = "start", ["shot_heavy"] = "start", ["shot_scout"] = "start",
        ["turret_fire_loop"] = "start", ["ui_click"] = "start", ["ui_error"] = "start", ["ui_hover"] = "start",
        ["victory"] = "start",
        ["alert_resources_low"] = "dc start", ["armour_ping"] = "dc start", ["building_placed"] = "dc start",
        ["construction_finished"] = "dc start", ["shot_tank"] = "dc start", ["unit_produced"] = "dc start",
    };

    /// <summary>The rules <paramref name="samples"/> break, in a fixed order, separated by spaces ("" when clean).</summary>
    public static string Lint(float[] samples)
    {
        var broken = new List<string>();
        if (samples.Length == 0) return "empty";
        int run = 0, longest = 0;
        double sum = 0;
        foreach (float v in samples)
        {
            run = Math.Abs(v) >= 0.999f ? run + 1 : 0;
            longest = Math.Max(longest, run);
            sum += v;
        }
        if (longest > ClipRun) broken.Add("clip");
        if (Math.Abs(sum / samples.Length) > MaxDc) broken.Add("dc");
        if (Math.Abs(samples[0]) > MaxEdge) broken.Add("start");
        if (Math.Abs(samples[samples.Length - 1]) > MaxEdge) broken.Add("end");
        return string.Join(" ", broken);
    }

    [Test]
    public void LintNamesEachDefect()
    {
        Assert.AreEqual("", Lint(new[] { 0f, 0.5f, -0.5f, 0f }));
        Assert.AreEqual("clip", Lint(new[] { 0f, 1f, 1f, 1f, -1f, -1f, -1f, 0f }));
        Assert.AreEqual("dc", Lint(new[] { 0f, 0.5f, 0.5f, 0f }));
        Assert.AreEqual("start", Lint(new[] { 0.5f, -0.5f, 0f }));
        Assert.AreEqual("end", Lint(new[] { 0f, 0.5f, -0.5f }));
    }

    [Test]
    public void EverySoundIsCleanOrOnTheKnownList()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>();
        foreach (string path in Directory.GetFiles(AudioBuild.SfxSource, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            seen.Add(id);
            SfxrParams p = AudioBuild.ReadSfx(path);
            string broken = Lint(Sfxr.Render(p, p.seed, AudioBuild.SampleRate));
            Known.TryGetValue(id, out string known);
            if (broken != (known ?? ""))
                problems.Add(known == null ? $"{id}: {broken}" : $"{id}: now '{broken}', listed as '{known}' (update AudioLintTests.Known)");
        }
        problems.AddRange(Known.Keys.Where(id => !seen.Contains(id)).Select(id => $"{id}: listed in AudioLintTests.Known but has no spec"));
        Assert.IsEmpty(problems, string.Join("\n", problems));
    }
}
