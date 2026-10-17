using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace Config
{
    /// <summary>
    /// Strict, culture-invariant parser and validator for GameConfig.xml.
    /// Every problem is appended to <c>errors</c>; nothing silently falls back to a default.
    /// </summary>
    public static class ConfigParser
    {
        public static GameConfigData Parse(string xmlText, List<string> errors)
        {
            var config = new GameConfigData();
            XmlDocument doc = new XmlDocument();
            try
            {
                doc.LoadXml(xmlText);
            }
            catch (XmlException e)
            {
                errors.Add($"GameConfig.xml is not valid XML: {e.Message}");
                return config;
            }

            XmlNode root = doc.SelectSingleNode("GameConfig");
            if (root == null)
            {
                errors.Add("Missing <GameConfig> root element.");
                return config;
            }

            XmlNode resources = Require(root, "Resources", errors);
            if (resources != null)
            {
                config.Resources.PassiveGenerationRate = Float(resources, "PassiveGenerationRate", "Resources", errors, min: 0);
                config.Resources.StartingResources = Float(resources, "StartingResources", "Resources", errors, min: 0);
                config.Resources.MiningRate = Float(resources, "MiningRate", "Resources", errors, min: 0);
                config.Resources.DecayPercentPerSecond = Float(resources, "DecayPercentPerSecond", "Resources", errors, min: 0, max: 100);
            }

            XmlNode match = Require(root, "Match", errors);
            if (match != null)
            {
                string scene = match.SelectSingleNode("Scene")?.InnerText?.Trim();
                if (string.IsNullOrEmpty(scene)) errors.Add("Match: <Scene> is missing or empty.");
                else config.Match.Scene = scene;
                config.Match.CountdownSeconds = Float(match, "CountdownSeconds", "Match", errors, min: 0, max: 60);
                XmlNode map = Require(match, "Map", errors);
                if (map != null)
                {
                    config.Match.Map.Size = Int(map, "Size", "Map", errors, min: 0, max: 4096);
                    if (config.Match.Map.Size != 0 && config.Match.Map.Size < 128)
                        errors.Add($"Map: <Size> {config.Match.Map.Size} must be 0 or in [128, 4096].");
                    config.Match.Map.Seed = UInt(map, "Seed", "Map", errors);
                    config.Match.Map.GemChance = Float(map, "GemChance", "Map", errors, min: 0, max: 1);
                    MapConfig m = config.Match.Map;
                    m.LayoutSizes = IntList(map, "LayoutSizes", "Map", errors, 64, 4096);
                    m.StartSites = IntList(map, "StartSites", "Map", errors, 2, 256);
                    m.Expansions = IntList(map, "Expansions", "Map", errors, 0, 256);
                    m.CentralAreas = IntList(map, "CentralAreas", "Map", errors, 0, 64, ascending: false);
                    m.RegionScale = IntList(map, "RegionScale", "Map", errors, 50, 1000, ascending: false);
                    m.LaneScale = IntList(map, "LaneScale", "Map", errors, 50, 1000, ascending: false);
                    m.CaveDepth = IntList(map, "CaveDepth", "Map", errors, 0, 64, ascending: false);
                    if (m.StartSites.Length != m.LayoutSizes.Length || m.Expansions.Length != m.LayoutSizes.Length || m.CentralAreas.Length != m.LayoutSizes.Length
                        || m.RegionScale.Length != m.LayoutSizes.Length || m.LaneScale.Length != m.LayoutSizes.Length || m.CaveDepth.Length != m.LayoutSizes.Length)
                        errors.Add("Map: <LayoutSizes>, <StartSites>, <Expansions>, <CentralAreas>, <RegionScale>, <LaneScale> and <CaveDepth> must have the same number of entries.");
                    m.OutcropShare = Float(map, "OutcropShare", "Map", errors, min: 0, max: 0.3f);
                    m.ChokepointShare = Float(map, "ChokepointShare", "Map", errors, min: 0, max: 1);
                    int[] lane = IntList(map, "LaneWidth", "Map", errors, 2, 64), choke = IntList(map, "ChokeWidth", "Map", errors, 2, 64);
                    if (lane.Length == 2 && choke.Length == 2 && lane[0] <= lane[1] && choke[0] <= choke[1]) { m.LaneWidthMin = lane[0]; m.LaneWidthMax = lane[1]; m.ChokeWidthMin = choke[0]; m.ChokeWidthMax = choke[1]; }
                    else errors.Add("Map: <LaneWidth> and <ChokeWidth> need two values each, min then max.");
                    m.MinStartDistance = Float(map, "MinStartDistance", "Map", errors, min: 0, max: 2);
                    m.MaxStartDistance = Float(map, "MaxStartDistance", "Map", errors, min: 0, max: 4);
                    m.ExpansionGemTiles = Int(map, "ExpansionGemTiles", "Map", errors, min: 1, max: 200);
                }
            }

            XmlNode units = Require(root, "Units", errors);
            if (units != null)
            {
                foreach (XmlNode node in units.SelectNodes("Unit"))
                {
                    string typeName = node.Attributes?["type"]?.Value;
                    if (!Enum.TryParse(typeName, false, out UnitType type) || type == UnitType.None)
                    {
                        errors.Add($"Units: unknown unit type \"{typeName}\".");
                        continue;
                    }
                    string ctx = $"Unit {type}";
                    config.Units[type] = new UnitConfig
                    {
                        Health = Int(node, "Health", ctx, errors, min: 1),
                        Damage = Int(node, "Damage", ctx, errors, min: 0),
                        Range = Float(node, "Range", ctx, errors, min: 0.1f),
                        AttackInterval = Float(node, "AttackInterval", ctx, errors, min: 0.01f),
                        MoveSpeed = Float(node, "MoveSpeed", ctx, errors, min: 0.01f),
                        UpfrontCost = Float(node, "UpfrontCost", ctx, errors, min: 0),
                        RunningCost = Float(node, "RunningCost", ctx, errors, min: 0),
                        Radius = Float(node, "Radius", ctx, errors, min: 0.1f, max: 2),
                        SizeClass = Int(node, "SizeClass", ctx, errors, min: 0, max: 1),
                        Sight = Float(node, "Sight", ctx, errors, min: 1, max: 32),
                        Armour = Armour(node, ctx, errors),
                        BuildSeconds = Float(node, "BuildSeconds", ctx, errors, min: 0.1f, max: 600),
                        Splash = OptionalFloat(node, "Splash", ctx, errors, 0, 8),
                        MinRange = OptionalFloat(node, "MinRange", ctx, errors, 0, 32),
                        BlastRadius = OptionalFloat(node, "BlastRadius", ctx, errors, 0, 8),
                        BlastDamage = OptionalFloat(node, "BlastDamage", ctx, errors, 0, 100000),
                    };
                }
            }

            XmlNode sim = Require(root, "Simulation", errors);
            if (sim != null)
            {
                const string c = "Simulation";
                config.Simulation.TickRate = Int(sim, "TickRate", c, errors, min: 5, max: 60);
                config.Simulation.TargetSearchSliceTicks = Int(sim, "TargetSearchSliceTicks", c, errors, min: 1, max: 32);
                config.Simulation.SeparationIntervalTicks = Int(sim, "SeparationIntervalTicks", c, errors, min: 1, max: 8);
                config.Simulation.SeparationStrength = Float(sim, "SeparationStrength", c, errors, min: 0, max: 4);
                config.Simulation.HashCellSize = Int(sim, "HashCellSize", c, errors, min: 2, max: 16);
                config.Simulation.MaxFieldRebuildsPerTick = Int(sim, "MaxFieldRebuildsPerTick", c, errors, min: 1, max: 16);
                config.Simulation.MaxUnitsPerPlayer = Int(sim, "MaxUnitsPerPlayer", c, errors, min: 1, max: 20000);
                config.Simulation.MaxEntities = Int(sim, "MaxEntities", c, errors, min: 1024, max: 1048576);
                config.Simulation.FogCellSize = Int(sim, "FogCellSize", c, errors, min: 1, max: 8);
                config.Simulation.VisionIntervalTicks = Int(sim, "VisionIntervalTicks", c, errors, min: 1, max: 20);
            }

            XmlNode rep = Require(root, "Replication", errors);
            if (rep != null)
            {
                const string c = "Replication";
                config.Replication.CorrectionIntervalTicks = Int(rep, "CorrectionIntervalTicks", c, errors, min: 1, max: 20);
                config.Replication.CorrectionThreshold = Float(rep, "CorrectionThreshold", c, errors, min: 0.01f, max: 8);
                config.Replication.DeltaScale = Int(rep, "DeltaScale", c, errors, min: 0, max: 127);
                config.Replication.OffscreenThreshold = Float(rep, "OffscreenThreshold", c, errors, min: 0.01f, max: 32);
                config.Replication.OffscreenIntervalTicks = Int(rep, "OffscreenIntervalTicks", c, errors, min: 1, max: 200);
                config.Replication.SnapshotBytesPerSecond = Int(rep, "SnapshotBytesPerSecond", c, errors, min: 16384, max: 16777216);
                config.Replication.RouteTolerance = Float(rep, "RouteTolerance", c, errors, min: 0, max: 4);
            }

            XmlNode orders = Require(root, "Orders", errors);
            if (orders != null)
            {
                config.Orders.MaxQueued = Int(orders, "MaxQueued", "Orders", errors, min: 0, max: 16);
                config.Orders.MaxDigArea = Int(orders, "MaxDigArea", "Orders", errors, min: 1, max: 64);
            }

            XmlNode diplomacy = Require(root, "Diplomacy", errors);
            if (diplomacy != null)
                config.Diplomacy.ChangeCooldownSeconds = Float(diplomacy, "ChangeCooldownSeconds", "Diplomacy", errors, min: 0, max: 60);

            XmlNode lobby = Require(root, "Lobby", errors);
            if (lobby != null)
            {
                config.Lobby.MapSizes = IntList(lobby, "MapSizes", "Lobby", errors, 128, 4096);
                config.Lobby.StartingResources = FloatList(lobby, "StartingResources", "Lobby", errors, 0f, 1e7f);
                int[] options = IntList(lobby, "StartOptions", "Lobby", errors, 1, 8);
                if (options.Length == 3) { config.Lobby.StartOptionsMin = options[0]; config.Lobby.StartOptionsDefault = options[1]; config.Lobby.StartOptionsMax = options[2]; }
                else errors.Add("Lobby: <StartOptions> needs three values: min, default, max.");
                config.Lobby.StartPickSeconds = IntList(lobby, "StartPickSeconds", "Lobby", errors, 0, 3600);
                config.Lobby.StartPickDefault = Int(lobby, "StartPickDefault", "Lobby", errors, min: 0, max: 3600);
                if (System.Array.IndexOf(config.Lobby.StartPickSeconds, config.Lobby.StartPickDefault) < 0)
                    errors.Add("Lobby: <StartPickDefault> must be one of <StartPickSeconds>.");
                config.Lobby.SpareStartSites = Int(lobby, "SpareStartSites", "Lobby", errors, min: 0, max: 16);
            }

            XmlNode menuBattle = Require(root, "MenuBattle", errors);
            if (menuBattle != null)
            {
                config.MenuBattle.UnitsPerArmy = Int(menuBattle, "UnitsPerArmy", "MenuBattle", errors, min: 0, max: 10000);
                config.MenuBattle.MapSize = Int(menuBattle, "MapSize", "MenuBattle", errors, min: 128, max: 1024);
                config.MenuBattle.Seed = (uint)Int(menuBattle, "Seed", "MenuBattle", errors, min: 1, max: int.MaxValue);
            }

            XmlNode gifting = Require(root, "Gifting", errors);
            if (gifting != null)
                config.Gifting.CooldownSeconds = Float(gifting, "CooldownSeconds", "Gifting", errors, min: 0, max: 300);

            XmlNode alerts = Require(root, "Alerts", errors);
            if (alerts != null)
            {
                config.Alerts.ThrottleSeconds = Float(alerts, "ThrottleSeconds", "Alerts", errors, min: 0, max: 300);
                config.Alerts.AreaTiles = Int(alerts, "AreaTiles", "Alerts", errors, min: 1, max: 1024);
                config.Alerts.ShowSeconds = Float(alerts, "ShowSeconds", "Alerts", errors, min: 1, max: 300);
            }

            XmlNode siege = Require(root, "Siege", errors);
            if (siege != null)
            {
                const string s = "Siege";
                config.Siege.BuildPower = Float(siege, "BuildPower", s, errors, min: 0.1f, max: 1000);
                config.Siege.DemolishRefund = Float(siege, "DemolishRefund", s, errors, min: 0, max: 1);
                config.Siege.RepairCostPerHealth = Float(siege, "RepairCostPerHealth", s, errors, min: 0, max: 100);
                config.Siege.WorkReach = Float(siege, "WorkReach", s, errors, min: 0.1f, max: 8);
                config.Siege.DigTime = Float(siege, "DigTime", s, errors, min: 0.05f, max: 60);
                config.Siege.GemPayout = Float(siege, "GemPayout", s, errors, min: 0, max: 100000);
                config.Siege.WallCost = Float(siege, "WallCost", s, errors, min: 0, max: 10000);
                config.Siege.WallHealth = Int(siege, "WallHealth", s, errors, min: 1, max: 100000);
                config.Siege.MaxWallLine = Int(siege, "MaxWallLine", s, errors, min: 1, max: 256);
                config.Siege.BlastRadius = Float(siege, "BlastRadius", s, errors, min: 0.5f, max: 16);
                config.Siege.BlastDamage = Float(siege, "BlastDamage", s, errors, min: 0, max: 100000);
                config.Siege.RadarClearSeconds = Float(siege, "RadarClearSeconds", s, errors, min: 0, max: 600);
                config.Siege.RadarAlertCooldownSeconds = Float(siege, "RadarAlertCooldownSeconds", s, errors, min: 0, max: 600);
            }

            XmlNode audio = Require(root, "Audio", errors);
            if (audio != null)
            {
                config.Audio.BattleDeathsPerSecond = Int(audio, "BattleDeathsPerSecond", "Audio", errors, min: 1, max: 1000);
                config.Audio.BattleHoldSeconds = Float(audio, "BattleHoldSeconds", "Audio", errors, min: 1, max: 600);
            }

            XmlNode replays = Require(root, "Replays", errors);
            if (replays != null)
            {
                config.Replays.Enabled = Int(replays, "Enabled", "Replays", errors, min: 0, max: 1) == 1;
                config.Replays.MaxMegabytes = Int(replays, "MaxMegabytes", "Replays", errors, min: 1, max: 8192);
                config.Replays.SendKilobytesPerSecond = Int(replays, "SendKilobytesPerSecond", "Replays", errors, min: 16, max: 65536);
                config.Replays.KeepCount = Int(replays, "KeepCount", "Replays", errors, min: 1, max: 10000);
            }

            var explicitEntries = new HashSet<(UnitType, TargetClass)>();
            XmlNode damage = Require(root, "DamageTable", errors);
            if (damage != null)
            {
                foreach (XmlNode node in damage.SelectNodes("Entry"))
                {
                    string attackerName = node.Attributes?["attacker"]?.Value;
                    string targetName = node.Attributes?["target"]?.Value;
                    bool attackerOk = Enum.TryParse(attackerName, false, out UnitType attacker) && attacker != UnitType.None
                                      && Enum.IsDefined(typeof(UnitType), attacker);
                    // "Unit" is shorthand for both unit armour classes.
                    bool bothUnits = targetName == "Unit";
                    TargetClass target = TargetClass.LightUnit;
                    bool targetOk = bothUnits || (Enum.TryParse(targetName, false, out target) && Enum.IsDefined(typeof(TargetClass), target));
                    if (!attackerOk) { errors.Add($"DamageTable: unknown attacker type \"{attackerName}\"."); continue; }
                    if (!targetOk) { errors.Add($"DamageTable: unknown target class \"{targetName}\"."); continue; }
                    string text = node.InnerText?.Trim();
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float m) || float.IsNaN(m) || m < 0 || m > 100)
                    {
                        errors.Add($"DamageTable: multiplier \"{text}\" for {attacker} vs {target} must be a number in [0, 100].");
                        continue;
                    }
                    if (bothUnits)
                    {
                        // An explicit LightUnit or HeavyUnit entry wins over the shorthand, whatever the order.
                        if (!explicitEntries.Contains((attacker, TargetClass.LightUnit))) config.Damage.Entries[(attacker, TargetClass.LightUnit)] = m;
                        if (!explicitEntries.Contains((attacker, TargetClass.HeavyUnit))) config.Damage.Entries[(attacker, TargetClass.HeavyUnit)] = m;
                    }
                    else
                    {
                        config.Damage.Entries[(attacker, target)] = m;
                        explicitEntries.Add((attacker, target));
                    }
                }
            }

            XmlNode buildings = Require(root, "Buildings", errors);
            if (buildings != null)
            {
                foreach (XmlNode node in buildings.SelectNodes("Building"))
                {
                    string typeName = node.Attributes?["type"]?.Value;
                    if (!Enum.TryParse(typeName, false, out BuildingType type) || type == BuildingType.None)
                    {
                        errors.Add($"Buildings: unknown building type \"{typeName}\".");
                        continue;
                    }
                    string ctx = $"Building {type}";
                    config.Buildings[type] = new BuildingConfig
                    {
                        Health = Int(node, "Health", ctx, errors, min: 1),
                        Width = Int(node, "Width", ctx, errors, min: 1, max: 16),
                        Height = Int(node, "Height", ctx, errors, min: 1, max: 16),
                        UpfrontCost = Float(node, "UpfrontCost", ctx, errors, min: 0),
                        RunningCost = Float(node, "RunningCost", ctx, errors, min: 0),
                        Sight = Float(node, "Sight", ctx, errors, min: 0, max: 32),
                    };
                }
            }

            foreach (UnitType type in Enum.GetValues(typeof(UnitType)))
            {
                if (type != UnitType.None && !config.Units.ContainsKey(type))
                    errors.Add($"Units: missing <Unit type=\"{type}\">.");
            }
            foreach (BuildingType type in Enum.GetValues(typeof(BuildingType)))
            {
                if (type != BuildingType.None && !config.Buildings.ContainsKey(type))
                    errors.Add($"Buildings: missing <Building type=\"{type}\">.");
            }

            return config;
        }

        /// <summary>The required &lt;Armour&gt; text: Light or Heavy.</summary>
        private static TargetClass Armour(XmlNode parent, string ctx, List<string> errors)
        {
            string text = parent.SelectSingleNode("Armour")?.InnerText?.Trim();
            if (text == "Light") return TargetClass.LightUnit;
            if (text == "Heavy") return TargetClass.HeavyUnit;
            errors.Add($"{ctx}: <Armour> must be Light or Heavy (got \"{text}\").");
            return TargetClass.LightUnit;
        }

        /// <summary>An optional number (0 when the element is missing) in [min, max].</summary>
        private static float OptionalFloat(XmlNode parent, string name, string ctx, List<string> errors, float min, float max) =>
            parent.SelectSingleNode(name) == null ? 0f : Float(parent, name, ctx, errors, min, max);

        private static XmlNode Require(XmlNode parent, string name, List<string> errors)
        {
            XmlNode node = parent.SelectSingleNode(name);
            if (node == null) errors.Add($"Missing <{name}> section.");
            return node;
        }

        /// <summary>A required, space-separated, strictly ascending list of whole numbers in [min, max].</summary>
        private static int[] IntList(XmlNode parent, string name, string ctx, List<string> errors, int min, int max, bool ascending = true)
        {
            float[] values = FloatList(parent, name, ctx, errors, min, max, ascending);
            var ints = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                ints[i] = (int)values[i];
                if (ints[i] != values[i]) errors.Add($"{ctx}: <{name}> value {values[i]} must be a whole number.");
            }
            return ints;
        }

        /// <summary>A required, space-separated, strictly ascending list of numbers in [min, max].</summary>
        private static float[] FloatList(XmlNode parent, string name, string ctx, List<string> errors, float min, float max, bool ascending = true)
        {
            string text = parent.SelectSingleNode(name)?.InnerText?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                errors.Add($"{ctx}: <{name}> is missing or empty.");
                return System.Array.Empty<float>();
            }
            string[] parts = text.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            var values = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[i]) ||
                    float.IsNaN(values[i]) || values[i] < min || values[i] > max)
                {
                    errors.Add($"{ctx}: <{name}> value '{parts[i]}' must be a number in [{min}, {max}].");
                    return System.Array.Empty<float>();
                }
                if (ascending && i > 0 && values[i] <= values[i - 1])
                {
                    errors.Add($"{ctx}: <{name}> must be in ascending order.");
                    return System.Array.Empty<float>();
                }
            }
            return values;
        }

        private static float Float(XmlNode parent, string name, string ctx, List<string> errors, float min = float.MinValue, float max = float.MaxValue)
        {
            string text = parent.SelectSingleNode(name)?.InnerText;
            if (text == null)
            {
                errors.Add($"{ctx}: missing <{name}>.");
                return 0;
            }
            if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
            {
                errors.Add($"{ctx}: <{name}> \"{text}\" is not a number.");
                return 0;
            }
            if (value < min || value > max)
            {
                errors.Add($"{ctx}: <{name}> {value} is outside [{min}, {max}].");
            }
            return value;
        }

        private static uint UInt(XmlNode parent, string name, string ctx, List<string> errors)
        {
            string text = parent.SelectSingleNode(name)?.InnerText;
            if (text == null)
            {
                errors.Add($"{ctx}: missing <{name}>.");
                return 0;
            }
            if (!uint.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value))
            {
                errors.Add($"{ctx}: <{name}> \"{text}\" is not a non-negative whole number.");
                return 0;
            }
            return value;
        }

        private static int Int(XmlNode parent, string name, string ctx, List<string> errors, int min = int.MinValue, int max = int.MaxValue)
        {
            string text = parent.SelectSingleNode(name)?.InnerText;
            if (text == null)
            {
                errors.Add($"{ctx}: missing <{name}>.");
                return 0;
            }
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                errors.Add($"{ctx}: <{name}> \"{text}\" is not a whole number.");
                return 0;
            }
            if (value < min || value > max)
            {
                errors.Add($"{ctx}: <{name}> {value} is outside [{min}, {max}].");
            }
            return value;
        }
    }
}
