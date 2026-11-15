using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;
using WAR2D.EditorTools;

/// <summary>
/// The art lint over the built unit and building sprites (asset-generation spec §3.7). Report-only for what fails
/// today: <see cref="Known"/> is the work list of phase 2 (§4), measured 2026-11-16, and each fix there takes its row
/// out once the owner has approved the new sprite. A sprite that isn't listed must pass, and a listed one that now
/// passes, or fails differently, must have its row updated.
/// </summary>
public class ArtLintTests
{
    /// <summary>The sprites that fail today, with the rules they fail: "orphan", "outline", "team", "dark".</summary>
    private static readonly Dictionary<string, string> Known = new Dictionary<string, string>
    {
        ["Tank"] = "outline dark", // team 16%, open edge 6%, orphans 0, lift 0.02
        ["Builder"] = "dark", // team 10%, open edge 0%, orphans 0, lift 0.06
        ["Digger"] = "outline dark", // team 12%, open edge 2%, orphans 0, lift 0.02
        ["Scout"] = "dark", // team 15%, open edge 0%, orphans 0, lift 0.08
        ["Flamethrower"] = "team dark", // team 8%, open edge 1%, orphans 0, lift 0.03
        ["Bomber"] = "dark", // team 14%, open edge 0%, orphans 0, lift 0.06
        ["Sniper"] = "dark", // team 12%, open edge 1%, orphans 0, lift 0.06
        ["Heavy"] = "dark", // team 17%, open edge 0%, orphans 0, lift 0.11
        ["Artillery"] = "dark", // team 15%, open edge 1%, orphans 0, lift 0.03
        ["TankHull"] = "outline dark", // team 0%, open edge 6%, orphans 0, lift 0.02
        ["ScoutHull"] = "dark", // team 0%, open edge 0%, orphans 0, lift 0.05
        ["SniperHull"] = "dark", // team 0%, open edge 0%, orphans 0, lift 0.06
        ["HeavyHull"] = "dark", // team 0%, open edge 0%, orphans 0, lift 0.08
        ["ArtilleryHull"] = "dark", // team 0%, open edge 0%, orphans 0, lift 0.02
        ["Base"] = "team", // team 4%, open edge 0%, orphans 0, lift 0.15
        ["Barracks"] = "team dark", // team 5%, open edge 0%, orphans 0, lift 0.11
        ["Miner"] = "outline team dark", // team 6%, open edge 7%, orphans 0, lift 0.06
        ["Mine"] = "dark", // team 35%, open edge 0%, orphans 0, lift 0.03
        ["Radar"] = "team", // team 5%, open edge 0%, orphans 0, lift 0.21
        ["HeavyFactory"] = "team", // team 3%, open edge 0%, orphans 0, lift 0.19
        ["SiegeWorkshop"] = "team", // team 3%, open edge 0%, orphans 0, lift 0.21
        ["GunTurret"] = "dark", // team 15%, open edge 0%, orphans 0, lift 0.00
        ["Jammer"] = "team", // team 6%, open edge 0%, orphans 0, lift 0.21
    };

    [Test]
    public void LintNamesEachDefect()
    {
        var clean = new ArtLint.Measured { Opaque = 100, Team = 20, Edge = 40, Lightness = 0.6f };
        Assert.AreEqual("", ArtLint.Lint(clean, true, 0.4f));
        Assert.AreEqual("", ArtLint.Lint(new ArtLint.Measured { Opaque = 100, Edge = 40, Lightness = 0.6f }, false, 0.4f));
        var bad = new ArtLint.Measured { Opaque = 100, Team = 2, Orphans = 1, Edge = 40, OpenEdge = 10, Lightness = 0.45f };
        Assert.AreEqual("orphan outline team dark", ArtLint.Lint(bad, true, 0.4f));
        Assert.AreEqual("team", ArtLint.Lint(new ArtLint.Measured { Opaque = 100, Team = 70, Edge = 40, Lightness = 0.6f }, true, 0.4f));
    }

    [Test]
    public void EveryUnitAndBuildingIsCleanOrOnTheKnownList()
    {
        float floor = ArtLint.FloorLightness(ArtBuild.LoadBuilt(ArtAtlas.Tiles));
        var problems = new List<string>();
        var report = new List<string>();
        var seen = new HashSet<string>();
        foreach (var (name, kind) in new[] { (ArtAtlas.Units, ArtKind.Unit), (ArtAtlas.Buildings, ArtKind.Building) })
        {
            ArtBuild.BuiltAtlas atlas = ArtBuild.LoadBuilt(name);
            foreach (ArtEntry e in ArtManifest.All.Where(e => e.Kind == kind))
            {
                seen.Add(e.Id);
                ArtLint.Measured m = ArtLint.Measure(atlas, e.Id);
                // A turreted unit's hull and turret layers are parts: the team rule is for the whole unit (its plain entry).
                bool layer = e.Kind == ArtKind.Unit && (e.Id.EndsWith(ArtManifest.HullSuffix) || e.Id.EndsWith(ArtManifest.TurretSuffix));
                string broken = ArtLint.Lint(m, e.TeamMask && !layer, floor);
                if (broken != "")
                    report.Add($"[\"{e.Id}\"] = \"{broken}\", // team {m.TeamShare:P0}, open edge {m.OpenShare:P0}, orphans {m.Orphans}, lift {m.Lightness - floor:F2}");
                Known.TryGetValue(e.Id, out string known);
                if (broken != (known ?? ""))
                    problems.Add(known == null ? $"{e.Id}: {broken}" : $"{e.Id}: now '{broken}', listed as '{known}' (update ArtLintTests.Known)");
            }
        }
        problems.AddRange(Known.Keys.Where(id => !seen.Contains(id)).Select(id => $"{id}: listed in ArtLintTests.Known but is not a unit or building sprite"));
        Assert.IsEmpty(problems, $"floor lightness {floor:F3}\n" + string.Join("\n", report));
    }
}
