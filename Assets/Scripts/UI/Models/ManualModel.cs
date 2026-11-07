using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Config;
using UnityEngine;

namespace WAR2D.UI
{
    /// <summary>One line of a page's damage table.</summary>
    public struct ManualDamageRow
    {
        public string Against, Multiplier, PerShot, PerSecond;
    }

    /// <summary>A heading and its paragraphs.</summary>
    public struct ManualSection
    {
        public string Heading;
        public List<string> Paragraphs;
    }

    /// <summary>A cell of the damage grid.</summary>
    public struct ManualGridCell
    {
        public float Multiplier;
        public string Detail;
    }

    /// <summary>A row of the damage grid.</summary>
    public struct ManualGridRow
    {
        public string Name, PageId;
        public ManualGridCell[] Cells;
    }

    /// <summary>One row of the Controls page.</summary>
    public struct ManualBinding
    {
        public string Label, Keys;
    }

    /// <summary>One page of the manual, ready to draw.</summary>
    public sealed class ManualPage
    {
        public string Id, Section, Title, Role, Lead, Diagram, Note, CounterNote;
        /// <summary>The sprite's type name (unit, building or "Wall"), or null.</summary>
        public string Sprite;
        public bool IsBuilding;
        public readonly List<(string label, string value, string link)> Stats = new List<(string, string, string)>();
        public readonly List<string> Body = new List<string>();
        public readonly List<ManualDamageRow> Damage = new List<ManualDamageRow>();
        public readonly List<string> Strong = new List<string>(), Weak = new List<string>(), Makes = new List<string>();
        public readonly List<ManualSection> Sections = new List<ManualSection>();
        public readonly List<ManualGridRow> Grid = new List<ManualGridRow>();
        public bool HasCounters;
        /// <summary>A section's cards: the pages it opens.</summary>
        public readonly List<string> Cards = new List<string>();
    }

    /// <summary>
    /// The Field manual's pages (v0.18, spec section 2): the approved words with every number from the config at the moment the
    /// manual opens, and each key as it is bound now. Pure, apart from reading the config object it is given.
    /// </summary>
    public sealed class ManualModel
    {
        private static readonly (TargetClass cls, string name)[] Classes =
        {
            (TargetClass.LightUnit, "Light units"), (TargetClass.HeavyUnit, "Heavy units"), (TargetClass.Building, "Buildings"), (TargetClass.Wall, "Walls"),
        };

        private readonly GameConfigData config;
        private readonly Func<string, string> keyOf;
        private readonly Dictionary<string, string> pageOfName = new Dictionary<string, string>();
        private readonly string highlight, linkColour, keyColour;

        /// <summary>The sections in order: id and name.</summary>
        public static readonly (string id, string name)[] SectionsInOrder =
        {
            ("units", "Units"), ("buildings", "Buildings"), ("damage", "Damage table"), ("rules", "Rules"), ("controls", "Controls"),
        };

        /// <summary>Every page, by id.</summary>
        public readonly Dictionary<string, ManualPage> Pages = new Dictionary<string, ManualPage>();

        /// <summary>The Controls page's groups.</summary>
        public readonly List<(string group, List<ManualBinding> rows)> Controls = new List<(string, List<ManualBinding>)>();

        /// <param name="keyOf">An input action's name to the key it is bound to now.</param>
        public ManualModel(GameConfigData config, Func<string, string> keyOf, string highlightColour = "#FFC857", string linkColour = "#3FD0FF", string keyColour = "#F0F4F8")
        {
            this.config = config;
            this.keyOf = keyOf;
            highlight = highlightColour;
            this.linkColour = linkColour;
            this.keyColour = keyColour;
            foreach (UnitType t in UnitTypes) pageOfName[NameOf(t.ToString())] = "unit-" + t;
            foreach (BuildingType t in BuildingTypes) pageOfName[NameOf(t.ToString())] = "building-" + t;
            pageOfName["Wall"] = "building-Wall";
            pageOfName["Damage table"] = "damage";
            Build();
        }

        private static readonly UnitType[] UnitOrder = { UnitType.Tank, UnitType.Builder, UnitType.Digger, UnitType.Scout, UnitType.Flamethrower, UnitType.Heavy, UnitType.Artillery, UnitType.Sniper, UnitType.Bomber };
        private static readonly BuildingType[] BuildingOrder = { BuildingType.Base, BuildingType.Miner, BuildingType.Barracks, BuildingType.HeavyFactory, BuildingType.SiegeWorkshop, BuildingType.Mine, BuildingType.Radar, BuildingType.GunTurret, BuildingType.Jammer };

        /// <summary>The unit types with pages.</summary>
        public static IEnumerable<UnitType> UnitTypes
        {
            get
            {
                // The approved order first; any type added later still gets a page.
                foreach (UnitType t in UnitOrder) yield return t;
                foreach (UnitType t in Enum.GetValues(typeof(UnitType))) if (t != UnitType.None && Array.IndexOf(UnitOrder, t) < 0) yield return t;
            }
        }

        /// <summary>The building types with pages.</summary>
        public static IEnumerable<BuildingType> BuildingTypes
        {
            get
            {
                foreach (BuildingType t in BuildingOrder) yield return t;
                foreach (BuildingType t in Enum.GetValues(typeof(BuildingType))) if (t != BuildingType.None && Array.IndexOf(BuildingOrder, t) < 0) yield return t;
            }
        }

        /// <summary>A type's name as the game shows it.</summary>
        public static string NameOf(string type) => type switch
        {
            "Base" => "HQ",
            "HeavyFactory" => "Heavy Factory",
            "SiegeWorkshop" => "Siege Workshop",
            "GunTurret" => "Gun turret",
            _ => type,
        };

        /// <summary>A number as the manual writes it: at most two decimals, a comma in thousands.</summary>
        public static string Number(float x)
        {
            double r = Math.Round(x * 100.0) / 100.0;
            return r >= 1000.0 && r == Math.Floor(r) ? r.ToString("N0", CultureInfo.InvariantCulture) : r.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Pages whose names contain the search (a card's or page's title; sections themselves don't count).</summary>
        public List<ManualPage> Search(string query)
        {
            var hits = new List<ManualPage>();
            if (string.IsNullOrWhiteSpace(query)) return hits;
            foreach (ManualPage page in Pages.Values)
            {
                if (page.Id == "units" || page.Id == "buildings" || page.Id == "rules") continue;
                if (page.Title.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(page);
            }
            return hits;
        }

        /// <summary>The page of a unit or building type.</summary>
        public static string PageOf(UnitType type) => "unit-" + type;

        /// <inheritdoc cref="PageOf(UnitType)"/>
        public static string PageOf(BuildingType type) => "building-" + type;

        private void Build()
        {
            ManualWords words = ManualText.Words;
            Dictionary<string, Dictionary<string, int>> table = ManualRules.Table(config);

            var units = new ManualPage { Id = "units", Section = "units", Title = "Units" };
            Pages[units.Id] = units;
            foreach (UnitType type in UnitTypes)
            {
                ManualPage page = UnitPage(type, words, table);
                Pages[page.Id] = page;
                units.Cards.Add(page.Id);
            }
            var buildings = new ManualPage { Id = "buildings", Section = "buildings", Title = "Buildings" };
            Pages[buildings.Id] = buildings;
            foreach (BuildingType type in BuildingTypes)
            {
                ManualPage page = BuildingPage(type, words, table);
                Pages[page.Id] = page;
                buildings.Cards.Add(page.Id);
            }
            ManualPage wall = WallPage(words);
            Pages[wall.Id] = wall;
            buildings.Cards.Add(wall.Id);

            Pages["damage"] = DamagePage(words);

            var rules = new ManualPage { Id = "rules", Section = "rules", Title = "Rules" };
            Pages[rules.Id] = rules;
            foreach (ManualRuleWords r in words.Rules)
            {
                var page = new ManualPage { Id = "rules-" + r.Id, Section = "rules", Title = r.Title, Lead = Words(r.Lead), Diagram = r.Diagram };
                foreach (ManualRuleSection s in r.Sections)
                {
                    var paragraphs = new List<string>();
                    foreach (string p in s.Paras) paragraphs.Add(Words(p));
                    page.Sections.Add(new ManualSection { Heading = s.Heading, Paragraphs = paragraphs });
                }
                Pages[page.Id] = page;
                rules.Cards.Add(page.Id);
            }

            Pages["controls"] = new ManualPage { Id = "controls", Section = "controls", Title = "Controls" };
            foreach (ManualControlGroup g in words.Controls)
            {
                var rows = new List<ManualBinding>();
                foreach (List<string> row in g.Rows) rows.Add(new ManualBinding { Label = row[0], Keys = Words(row[1]) });
                Controls.Add((g.Group, rows));
            }
        }

        private ManualPage UnitPage(UnitType type, ManualWords words, Dictionary<string, Dictionary<string, int>> table)
        {
            string key = type.ToString();
            ManualPageWords w = words.Units[key];
            UnitConfig u = config.GetUnit(type);
            var page = new ManualPage { Id = "unit-" + key, Section = "units", Title = NameOf(key), Role = w.Role, Sprite = key };
            bool blast = u.BlastDamage > 0f, armed = !blast && u.Damage > 0;
            void Stat(string label, string value, string link = null) => page.Stats.Add((label, value, link));
            Stat("Cost", Number(u.UpfrontCost));
            Stat("Build time", Number(u.BuildSeconds) + " s");
            Stat("Upkeep", Number(u.RunningCost) + " /s");
            Stat("Health", Number(u.Health));
            Stat("Armour", u.Armour == TargetClass.HeavyUnit ? "Heavy" : "Light");
            if (armed) { Stat("Damage", $"{Number(u.Damage)} every {Number(u.AttackInterval)} s"); Stat("Per second", Number(u.Damage / u.AttackInterval)); }
            if (blast) { Stat("Blast", Number(u.BlastDamage) + ", once"); Stat("Blast radius", Number(u.BlastRadius) + " tiles"); }
            if (armed) Stat("Range", Number(u.Range) + " tiles");
            if (u.MinRange > 0f) Stat("Minimum range", Number(u.MinRange) + " tiles");
            if (u.Splash > 0f) Stat("Splash", Number(u.Splash) + " tiles");
            // v0.20 (Figma B3): a shell's blast, armour, blast resistance, fire, aim and reload.
            if (u.FiresShells) Stat("Blast radius", Number(u.BlastRadius) + " tiles");
            if (u.FlatArmour > 0f) Stat("Armour per shot", Number(u.FlatArmour));
            if (u.BlastResist > 0f) Stat("Blast resistance", Number(u.BlastResist * 100f) + "%");
            if (type == UnitType.Flamethrower) Stat("Sets on fire", $"{Number(config.Burn.Dps)} /s for {Number(config.Burn.Seconds)} s");
            if (type == UnitType.Sniper) Stat("Aim time", Number(config.Sniper.AimSeconds) + " s");
            Stat("Sight", Number(u.Sight) + " tiles");
            Stat("Speed", Number(u.MoveSpeed) + " tiles/s");
            Stat("Size", u.SizeClass > 0 ? "Large" : "Small");
            foreach (BuildingType b in BuildingTypes)
                if (SpawnerRules.MayProduce(b, type)) { Stat("Made by", NameOf(b.ToString()), PageOf(b)); break; }
            foreach (string p in w.Body) page.Body.Add(Words(p));
            foreach ((TargetClass cls, string name) in Classes)
            {
                float m = config.Damage.Multiplier(type, cls);
                if (blast) page.Damage.Add(new ManualDamageRow { Against = name, Multiplier = Number(m), PerShot = Number(u.BlastDamage * m), PerSecond = null });
                else if (armed) page.Damage.Add(new ManualDamageRow { Against = name, Multiplier = Number(m), PerShot = Number(u.Damage * m), PerSecond = Number(u.Damage / u.AttackInterval * m) });
            }
            Counters(page, key, table, words, blast);
            return page;
        }

        private void Counters(ManualPage page, string key, Dictionary<string, Dictionary<string, int>> table, ManualWords words, bool blast)
        {
            if (!table.ContainsKey(key))
            {
                page.Note = Words(words.NoFight);
                return;
            }
            ManualRules.Lists lists = ManualRules.Counters(table, key);
            page.HasCounters = true;
            page.Strong.AddRange(lists.Strong);
            page.Weak.AddRange(lists.Weak);
            page.CounterNote = Words(blast ? words.CounterNoteBomber : words.CounterNote);
        }

        private ManualPage BuildingPage(BuildingType type, ManualWords words, Dictionary<string, Dictionary<string, int>> table)
        {
            string key = type.ToString();
            ManualPageWords w = words.Buildings[key];
            BuildingConfig b = config.GetBuilding(type);
            var page = new ManualPage { Id = "building-" + key, Section = "buildings", Title = NameOf(key), Role = w.Role, Sprite = key, IsBuilding = true };
            void Stat(string label, string value) => page.Stats.Add((label, value, null));
            Stat("Size", $"{b.Width} × {b.Height} tiles");
            Stat("Cost", type == BuildingType.Base ? "Free" : Number(b.UpfrontCost));
            Stat("Upkeep", b.RunningCost > 0f ? Number(b.RunningCost) + " /s" : "None");
            Stat("Health", Number(b.Health));
            Stat("Sight", b.Sight > 0f ? Number(b.Sight) + " tiles" : "None");
            if (type != BuildingType.Base) Stat("Time for one Builder", Number(b.Health / b.BuildRate(config.Siege.BuildPower)) + " s");
            switch (type)
            {
                case BuildingType.Miner:
                    Stat("Earns", Number(config.Resources.MiningRate) + " /s");
                    Stat("On a rich gem", Number(config.Resources.MiningRate * config.Resources.RichGemMultiplier) + " /s");
                    break;
                case BuildingType.Mine:
                    Stat("Blast", Number(config.Siege.BlastDamage));
                    Stat("Blast radius", Number(config.Siege.BlastRadius) + " tiles");
                    break;
                case BuildingType.Radar:
                    Stat("Radar range", Number(b.Sight) + " tiles");
                    break;
                case BuildingType.Jammer:
                    Stat("Jam radius", Number(config.Siege.JamRadius) + " tiles");
                    Stat("Sight inside a jam", Number(config.Siege.JamSight) + " tiles");
                    break;
                case BuildingType.GunTurret:
                    Stat("Damage", $"{Number(b.Damage)} every {Number(b.AttackInterval)} s");
                    Stat("Per second", Number(b.Damage / b.AttackInterval));
                    Stat("Range", Number(b.Range) + " tiles");
                    foreach ((TargetClass cls, string name) in Classes)
                    {
                        float m = config.Damage.Multiplier(type, cls);
                        page.Damage.Add(new ManualDamageRow { Against = name, Multiplier = Number(m), PerShot = Number(b.Damage * m), PerSecond = Number(b.Damage / b.AttackInterval * m) });
                    }
                    Counters(page, key, table, words, false);
                    break;
            }
            foreach (string p in w.Body) page.Body.Add(Words(p));
            foreach (UnitType made in SpawnerRules.Produces(type)) page.Makes.Add(made.ToString());
            if (type != BuildingType.Base) page.Note = "Starts as a blueprint that Builders build.";
            return page;
        }

        private ManualPage WallPage(ManualWords words)
        {
            ManualPageWords w = words.Buildings["Wall"];
            var page = new ManualPage { Id = "building-Wall", Section = "buildings", Title = "Wall", Role = w.Role, Sprite = "Wall", IsBuilding = true };
            page.Stats.Add(("Cost", Number(config.Siege.WallCost) + " a segment", null));
            page.Stats.Add(("Upkeep", "None", null));
            page.Stats.Add(("Health", Number(config.Siege.WallHealth) + " a segment", null));
            page.Stats.Add(("Longest drag", Number(config.Siege.MaxWallLine) + " segments", null));
            page.Stats.Add(("Time for one Builder", Number(config.Siege.WallHealth / config.Siege.BuildPower) + " s", null));
            foreach (string p in w.Body) page.Body.Add(Words(p));
            return page;
        }

        private ManualPage DamagePage(ManualWords words)
        {
            var page = new ManualPage { Id = "damage", Section = "damage", Title = "Damage table", Lead = Words(words.DamageTable.Lead) };
            foreach (string n in words.DamageTable.Notes) page.Body.Add(Words(n));
            foreach (UnitType type in UnitTypes)
            {
                UnitConfig u = config.GetUnit(type);
                string name = NameOf(type.ToString());
                var row = new ManualGridRow { Name = name, PageId = PageOf(type), Cells = new ManualGridCell[Classes.Length] };
                for (int c = 0; c < Classes.Length; c++)
                {
                    float m = config.Damage.Multiplier(type, Classes[c].cls);
                    string target = Classes[c].name.ToLowerInvariant();
                    string detail = u.BlastDamage > 0f ? $"{name} against {target}: a blast of {Number(u.BlastDamage * m)}"
                        : u.Damage <= 0 ? $"{name} does no damage"
                        : $"{name} against {target}: {Number(u.Damage * m)} per shot, {Number(u.Damage / u.AttackInterval * m)} per second";
                    row.Cells[c] = new ManualGridCell { Multiplier = m, Detail = detail };
                }
                page.Grid.Add(row);
            }
            BuildingConfig t = config.GetBuilding(BuildingType.GunTurret);
            var turret = new ManualGridRow { Name = "Gun turret", PageId = PageOf(BuildingType.GunTurret), Cells = new ManualGridCell[Classes.Length] };
            for (int c = 0; c < Classes.Length; c++)
            {
                float m = config.Damage.Multiplier(BuildingType.GunTurret, Classes[c].cls);
                turret.Cells[c] = new ManualGridCell { Multiplier = m, Detail = $"Gun turret against {Classes[c].name.ToLowerInvariant()}: {Number(t.Damage * m)} per shot, {Number(t.Damage / t.AttackInterval * m)} per second" };
            }
            page.Grid.Add(turret);
            return page;
        }

        private static readonly Regex Placeholder = new Regex(@"\{([\w/]+)\}", RegexOptions.Compiled);
        private static readonly Regex Bold = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
        private static readonly Regex Link = new Regex(@"\[\[(.+?)\]\]", RegexOptions.Compiled);

        /// <summary>The words with numbers and keys filled in and highlights and links as rich text.</summary>
        public string Words(string text)
        {
            if (text == null) return null;
            string s = Placeholder.Replace(text, m => Fill(m.Groups[1].Value));
            s = Bold.Replace(s, m => $"<color={highlight}>{m.Groups[1].Value}</color>");
            return Link.Replace(s, m =>
            {
                string[] parts = m.Groups[1].Value.Split('|');
                if (!pageOfName.TryGetValue(parts[0], out string id)) throw new ArgumentException($"no page for [[{parts[0]}]]");
                return $"<link=\"{id}\"><color={linkColour}><u>{(parts.Length > 1 ? parts[1] : parts[0])}</u></color></link>";
            });
        }

        private string Fill(string path)
        {
            string[] p = path.Split('/');
            switch (p[0])
            {
                case "Key": return $"<color={keyColour}>[{keyOf(p[1])}]</color>";
                case "Derived": return Number(Derived(p[1]));
                case "Unit": return Number(Read(config.GetUnit((UnitType)Enum.Parse(typeof(UnitType), p[1])), p[2], path));
                case "Building": return Number(Read(config.GetBuilding((BuildingType)Enum.Parse(typeof(BuildingType), p[1])), p[2], path));
            }
            object at = config;
            for (int i = 0; i < p.Length - 1; i++)
            {
                FieldInfo f = at.GetType().GetField(p[i]) ?? throw new ArgumentException("no " + path + " in the config");
                at = f.GetValue(at);
            }
            return Number(Read(at, p[p.Length - 1], path));
        }

        private static float Read(object from, string field, string path)
        {
            FieldInfo f = from.GetType().GetField(field) ?? throw new ArgumentException("no " + path + " in the config");
            return Convert.ToSingle(f.GetValue(from), CultureInfo.InvariantCulture);
        }

        private float Derived(string name) => name switch
        {
            "RichMinerRate" => config.Resources.MiningRate * config.Resources.RichGemMultiplier,
            "MinerPayback" => config.GetBuilding(BuildingType.Miner).UpfrontCost / config.Resources.MiningRate,
            "BarracksMiners" => config.GetBuilding(BuildingType.Barracks).RunningCost / config.Resources.MiningRate,
            "HeavyFactoryMiners" => config.GetBuilding(BuildingType.HeavyFactory).RunningCost / config.Resources.MiningRate,
            "DecaySeconds" => 100f / config.Resources.DecayPercentPerSecond,
            "DemolishPercent" => config.Siege.DemolishRefund * 100f,
            "LastGaspPercent" => config.Bomber.LastGaspHealth * 100f,
            // v0.20
            "BomberBlastResistPercent" => config.GetUnit(UnitType.Bomber).BlastResist * 100f,
            "HeavyBlastResistPercent" => config.GetUnit(UnitType.Heavy).BlastResist * 100f,
            "SniperHeavyMultiplier" => config.Damage.Multiplier(UnitType.Sniper, TargetClass.HeavyUnit),
            "ArtilleryFriendlyPercent" => config.Artillery.FriendlyDamage * 100f,
            _ => throw new ArgumentException("no figure " + name),
        };
    }
}
