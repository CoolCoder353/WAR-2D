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
                config.Lobby.Visibility = Named(lobby, "Visibility", "Lobby", errors, LobbyVisibility.Friends);
            }

            XmlNode ai = Require(root, "Ai", errors);
            if (ai != null) ParseAi(ai, config.Ai, errors);

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
                config.Siege.JamRadius = Float(siege, "JamRadius", s, errors, min: 0.5f, max: 64);
                config.Siege.JamSight = Float(siege, "JamSight", s, errors, min: 0.5f, max: 32);
                if (config.Siege.JamSight >= config.Siege.JamRadius)
                    errors.Add($"Siege: <JamSight> {config.Siege.JamSight} must be less than <JamRadius> {config.Siege.JamRadius}.");
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
            var explicitBuildingEntries = new HashSet<(BuildingType, TargetClass)>();
            XmlNode damage = Require(root, "DamageTable", errors);
            if (damage != null)
            {
                foreach (XmlNode node in damage.SelectNodes("Entry"))
                {
                    string attackerName = node.Attributes?["attacker"]?.Value;
                    string targetName = node.Attributes?["target"]?.Value;
                    // The attacker is a unit type or (v0.13) a building type; the names don't overlap.
                    bool unitAttacker = Enum.TryParse(attackerName, false, out UnitType attacker) && attacker != UnitType.None
                                        && Enum.IsDefined(typeof(UnitType), attacker);
                    BuildingType building = BuildingType.None;
                    bool buildingAttacker = !unitAttacker && Enum.TryParse(attackerName, false, out building) && building != BuildingType.None
                                            && Enum.IsDefined(typeof(BuildingType), building);
                    // "Unit" is shorthand for both unit armour classes.
                    bool bothUnits = targetName == "Unit";
                    TargetClass target = TargetClass.LightUnit;
                    bool targetOk = bothUnits || (Enum.TryParse(targetName, false, out target) && Enum.IsDefined(typeof(TargetClass), target));
                    if (!unitAttacker && !buildingAttacker) { errors.Add($"DamageTable: unknown attacker type \"{attackerName}\"."); continue; }
                    if (!targetOk) { errors.Add($"DamageTable: unknown target class \"{targetName}\"."); continue; }
                    string text = node.InnerText?.Trim();
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float m) || float.IsNaN(m) || m < 0 || m > 100)
                    {
                        errors.Add($"DamageTable: multiplier \"{text}\" for {attackerName} vs {targetName} must be a number in [0, 100].");
                        continue;
                    }
                    if (buildingAttacker)
                    {
                        AddEntry(config.Damage.BuildingEntries, explicitBuildingEntries, building, bothUnits, target, m);
                        continue;
                    }
                    AddEntry(config.Damage.Entries, explicitEntries, attacker, bothUnits, target, m);
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
                    var building = new BuildingConfig
                    {
                        Health = Int(node, "Health", ctx, errors, min: 1),
                        Width = Int(node, "Width", ctx, errors, min: 1, max: 16),
                        Height = Int(node, "Height", ctx, errors, min: 1, max: 16),
                        UpfrontCost = Float(node, "UpfrontCost", ctx, errors, min: 0),
                        RunningCost = Float(node, "RunningCost", ctx, errors, min: 0),
                        Sight = Float(node, "Sight", ctx, errors, min: 0, max: 32),
                    };
                    Weapon(node, ctx, errors, building);
                    config.Buildings[type] = building;
                }
            }

            // A building that attacks needs its own damage-table row; a building that doesn't may not have one.
            foreach (var pair in config.Buildings)
            {
                bool row = config.Damage.HasRow(pair.Key);
                if (pair.Value.IsAttacker && !row) errors.Add($"DamageTable: building attacker {pair.Key} has no <Entry attacker=\"{pair.Key}\">.");
                if (!pair.Value.IsAttacker && row) errors.Add($"DamageTable: {pair.Key} has entries but does not attack (no <Damage> above 0).");
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

        /// <summary>Fewest callsigns &lt;Ai&gt; must list: one per AI seat a full lobby could hold.</summary>
        public const int MinCallsigns = 8;

        /// <summary>The &lt;Ai&gt; block (v0.14): callsigns, Easy/Normal/Hard, the four styles, diplomacy and conceding.</summary>
        private static void ParseAi(XmlNode ai, AiConfig config, List<string> errors)
        {
            string text = ai.SelectSingleNode("Callsigns")?.InnerText;
            if (string.IsNullOrWhiteSpace(text)) errors.Add("Ai: <Callsigns> is missing or empty.");
            else
            {
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in text.Split(','))
                {
                    string name = part.Trim();
                    if (!CommandValidator.TrySanitizeNickname(name, out string clean) || clean != name || name.Length == 0)
                        errors.Add($"Ai: callsign \"{name}\" must be a valid nickname (1 to {CommandValidator.MaxNicknameLength} plain characters).");
                    else if (!seen.Add(name)) errors.Add($"Ai: callsign \"{name}\" is listed twice.");
                    else names.Add(name);
                }
                if (names.Count < MinCallsigns) errors.Add($"Ai: <Callsigns> needs at least {MinCallsigns} different callsigns (got {names.Count}).");
                config.Callsigns = names.ToArray();
            }

            foreach (XmlNode node in ai.SelectNodes("Difficulty"))
            {
                string name = node.Attributes?["name"]?.Value;
                if (!Enum.TryParse(name, false, out AiDifficulty difficulty) || !Enum.IsDefined(typeof(AiDifficulty), difficulty) || difficulty == AiDifficulty.Scripted)
                {
                    errors.Add($"Ai: unknown difficulty \"{name}\" (Easy, Normal or Hard).");
                    continue;
                }
                if (config.Difficulties.ContainsKey(difficulty)) { errors.Add($"Ai: difficulty {difficulty} is listed twice."); continue; }
                string c = $"Ai difficulty {difficulty}";
                config.Difficulties[difficulty] = new AiDifficultyConfig
                {
                    ThinkSeconds = Float(node, "ThinkSeconds", c, errors, min: 0.05f, max: 10),
                    ActionsPerMinute = Int(node, "ActionsPerMinute", c, errors, min: 1, max: 6000),
                    ReactionSeconds = Float(node, "ReactionSeconds", c, errors, min: 0, max: 60),
                    Builders = Int(node, "Builders", c, errors, min: 1, max: 50),
                    BuildersPerExpansion = Int(node, "BuildersPerExpansion", c, errors, min: 0, max: 20),
                    MaxExpansions = Int(node, "MaxExpansions", c, errors, min: 0, max: 16),
                    ExpandAfterSeconds = Float(node, "ExpandAfterSeconds", c, errors, min: 0, max: 7200),
                    KeepsUpkeepPaid = Flag(node, "KeepsUpkeepPaid", c, errors),
                    UpkeepReserveSeconds = Float(node, "UpkeepReserveSeconds", c, errors, min: 0, max: 600),
                    Counters = Named(node, "Counters", c, errors, AiCounters.None),
                    Retreats = Flag(node, "Retreats", c, errors),
                    RetreatRatio = Float(node, "RetreatRatio", c, errors, min: 0, max: 1),
                    FocusFire = Flag(node, "FocusFire", c, errors),
                    KeepsBacklineBack = Flag(node, "KeepsBacklineBack", c, errors),
                    DigsPastWalls = Flag(node, "DigsPastWalls", c, errors),
                    Scouting = Named(node, "Scouting", c, errors, AiScouting.Once),
                    RescoutSeconds = Float(node, "RescoutSeconds", c, errors, min: 10, max: 3600),
                    SpecialUse = Float(node, "SpecialUse", c, errors, min: 0, max: 1),
                    DefendsChokepoints = Flag(node, "DefendsChokepoints", c, errors),
                    AttackRatio = Float(node, "AttackRatio", c, errors, min: 0.1f, max: 10),
                    Peace = Named(node, "Peace", c, errors, AiPeace.Always),
                    GiftsSurplus = Flag(node, "GiftsSurplus", c, errors),
                };
            }
            foreach (AiDifficulty d in new[] { AiDifficulty.Easy, AiDifficulty.Normal, AiDifficulty.Hard })
                if (!config.Difficulties.ContainsKey(d)) errors.Add($"Ai: missing <Difficulty name=\"{d}\">.");

            foreach (XmlNode node in ai.SelectNodes("Style"))
            {
                string name = node.Attributes?["name"]?.Value;
                if (!Enum.TryParse(name, false, out AiStyle style) || !Enum.IsDefined(typeof(AiStyle), style) || style == AiStyle.Random)
                {
                    errors.Add($"Ai: unknown style \"{name}\" (Rusher, Turtle, Expander or Balanced).");
                    continue;
                }
                if (config.Styles.ContainsKey(style)) { errors.Add($"Ai: style {style} is listed twice."); continue; }
                string c = $"Ai style {style}";
                config.Styles[style] = new AiStyleConfig
                {
                    BuildOrder = BuildOrder(node, c, errors),
                    ArmyMix = ArmyMix(node, c, errors),
                    Producers = Producers(node, c, errors),
                    Miners = Int(node, "Miners", c, errors, min: 0, max: 100),
                    MinersPerExpansion = Int(node, "MinersPerExpansion", c, errors, min: 0, max: 100),
                    Expansions = Int(node, "Expansions", c, errors, min: 0, max: 16),
                    ExpandTiming = Float(node, "ExpandTiming", c, errors, min: 0.1f, max: 10),
                    FirstAttackSeconds = Float(node, "FirstAttackSeconds", c, errors, min: 0, max: 7200),
                    AttackArmySize = Int(node, "AttackArmySize", c, errors, min: 1, max: 10000),
                    AttackRatioScale = Float(node, "AttackRatioScale", c, errors, min: 0.1f, max: 10),
                    AttackIntervalSeconds = Float(node, "AttackIntervalSeconds", c, errors, min: 5, max: 3600),
                    Turrets = Int(node, "Turrets", c, errors, min: 0, max: 64),
                    Jammers = Int(node, "Jammers", c, errors, min: 0, max: 16),
                    Bombs = Int(node, "Bombs", c, errors, min: 0, max: 64),
                    Walls = Flag(node, "Walls", c, errors),
                    Radar = Flag(node, "Radar", c, errors),
                    HoldsChokes = Flag(node, "HoldsChokes", c, errors),
                };
            }
            foreach (AiStyle s in new[] { AiStyle.Rusher, AiStyle.Turtle, AiStyle.Expander, AiStyle.Balanced })
                if (!config.Styles.ContainsKey(s)) errors.Add($"Ai: missing <Style name=\"{s}\">.");

            XmlNode diplomacy = Require(ai, "Diplomacy", errors);
            if (diplomacy != null)
            {
                const string c = "Ai diplomacy";
                config.Diplomacy.PeaceSeconds = Float(diplomacy, "PeaceSeconds", c, errors, min: 0, max: 3600);
                config.Diplomacy.GrudgeSeconds = Float(diplomacy, "GrudgeSeconds", c, errors, min: 0, max: 3600);
                config.Diplomacy.WinningRatio = Float(diplomacy, "WinningRatio", c, errors, min: 1, max: 100);
                config.Diplomacy.LosingRatio = Float(diplomacy, "LosingRatio", c, errors, min: 0, max: 1);
                config.Diplomacy.GiftSurplus = Float(diplomacy, "GiftSurplus", c, errors, min: 0, max: 1e7f);
                config.Diplomacy.GiftSurplusSeconds = Float(diplomacy, "GiftSurplusSeconds", c, errors, min: 0, max: 3600);
                config.Diplomacy.GiftShare = Float(diplomacy, "GiftShare", c, errors, min: 0, max: 1);
                config.Diplomacy.GiftPeaceFactor = Float(diplomacy, "GiftPeaceFactor", c, errors, min: 0, max: 1);
            }

            XmlNode concede = Require(ai, "Concede", errors);
            if (concede != null)
            {
                const string c = "Ai concede";
                config.Concede.MinMinutes = Float(concede, "MinMinutes", c, errors, min: 0, max: 600);
                config.Concede.Seconds = Float(concede, "Seconds", c, errors, min: 0, max: 3600);
                config.Concede.Ratio = Float(concede, "Ratio", c, errors, min: 0, max: 1);
                config.Concede.SeenWindowSeconds = Float(concede, "SeenWindowSeconds", c, errors, min: 1, max: 3600);
                config.Concede.IncomeSeconds = Float(concede, "IncomeSeconds", c, errors, min: 0, max: 3600);
            }

            XmlNode brain = Require(ai, "Brain", errors);
            if (brain != null)
            {
                const string c = "Ai brain";
                config.Brain.BudgetBurstSeconds = Float(brain, "BudgetBurstSeconds", c, errors, min: 0, max: 60);
                config.Brain.ProposalWaitSeconds = Float(brain, "ProposalWaitSeconds", c, errors, min: 0, max: 600);
                config.Brain.ThreatRadius = Float(brain, "ThreatRadius", c, errors, min: 1, max: 256);
                config.Brain.ArmyMemorySeconds = Float(brain, "ArmyMemorySeconds", c, errors, min: 1, max: 3600);
                config.Brain.EventMemorySeconds = Float(brain, "EventMemorySeconds", c, errors, min: 1, max: 3600);
            }

            XmlNode play = Require(ai, "Play", errors);
            if (play != null)
            {
                const string c = "Ai play";
                AiPlayConfig p = config.Play;
                p.OptionRadius = Float(play, "OptionRadius", c, errors, min: 1, max: 128);
                p.OptionGemWeight = Float(play, "OptionGemWeight", c, errors, min: 0, max: 1000);
                p.OptionOpenWeight = Float(play, "OptionOpenWeight", c, errors, min: 0, max: 1000);
                p.OptionExpansionWeight = Float(play, "OptionExpansionWeight", c, errors, min: 0, max: 100000);
                p.OptionExpansionReach = Float(play, "OptionExpansionReach", c, errors, min: 1, max: 4096);
                p.OptionTeammateWeight = Float(play, "OptionTeammateWeight", c, errors, min: 0, max: 1000);
                p.OpeningSeconds = Float(play, "OpeningSeconds", c, errors, min: 0, max: 3600);
                p.BaseRadius = Float(play, "BaseRadius", c, errors, min: 4, max: 128);
                p.BuildingGap = Int(play, "BuildingGap", c, errors, min: 0, max: 4);
                p.PlacementTriesPerThink = Int(play, "PlacementTriesPerThink", c, errors, min: 1, max: 100000);
                p.PendingSeconds = Float(play, "PendingSeconds", c, errors, min: 0, max: 60);
                p.BlockedSeconds = Float(play, "BlockedSeconds", c, errors, min: 0, max: 3600);
                p.SearchRetrySeconds = Float(play, "SearchRetrySeconds", c, errors, min: 0, max: 3600);
                p.ReplanSeconds = Float(play, "ReplanSeconds", c, errors, min: 1, max: 3600);
                p.DefenceSpread = Float(play, "DefenceSpread", c, errors, min: 1, max: 64);
                p.BuildersPerBlueprint = Int(play, "BuildersPerBlueprint", c, errors, min: 1, max: 32);
                p.AssignSeconds = Float(play, "AssignSeconds", c, errors, min: 0, max: 600);
                p.RepairBelow = Float(play, "RepairBelow", c, errors, min: 0, max: 1);
                p.QueueAhead = Int(play, "QueueAhead", c, errors, min: 1, max: SpawnerRules.MaxQueue);
                p.QueueBatch = Int(play, "QueueBatch", c, errors, min: 1, max: SpawnerRules.MaxQueue);
                if (!SpawnerRules.IsQueueStep(p.QueueBatch)) errors.Add($"{c}: <QueueBatch> {p.QueueBatch} must be a queue button's step ({string.Join(", ", SpawnerRules.QueueSteps)}).");
                p.UpkeepIncomeShare = Float(play, "UpkeepIncomeShare", c, errors, min: 0, max: 1);
                p.UpkeepFloorSeconds = Float(play, "UpkeepFloorSeconds", c, errors, min: 0, max: 600);
                p.UpkeepBankSeconds = Float(play, "UpkeepBankSeconds", c, errors, min: 0, max: 3600);
                p.UpkeepHeadroom = Float(play, "UpkeepHeadroom", c, errors, min: 0, max: 10000);
                p.CappedExpandResources = Float(play, "CappedExpandResources", c, errors, min: 0, max: 1e7f);
                p.GatherDistance = Float(play, "GatherDistance", c, errors, min: 0, max: 256);
                p.ArmyRadius = Float(play, "ArmyRadius", c, errors, min: 1, max: 256);
                p.OrderSeconds = Float(play, "OrderSeconds", c, errors, min: 0, max: 600);
                p.ReinforceSize = Int(play, "ReinforceSize", c, errors, min: 1, max: 10000);
                p.DefendRadius = Float(play, "DefendRadius", c, errors, min: 1, max: 4096);
                p.CounterShift = Float(play, "CounterShift", c, errors, min: 0, max: 1);
                p.BuildingCounterWeight = Float(play, "BuildingCounterWeight", c, errors, min: 0, max: 100);
                p.LightTurretShare = Float(play, "LightTurretShare", c, errors, min: 0, max: 1);
                p.BacklineDistance = Float(play, "BacklineDistance", c, errors, min: 0, max: 64);
                p.BomberClump = Int(play, "BomberClump", c, errors, min: 1, max: 1000);
                p.BomberChecks = Int(play, "BomberChecks", c, errors, min: 1, max: 10000);
                p.ScoutAfterSeconds = Float(play, "ScoutAfterSeconds", c, errors, min: 0, max: 3600);
                p.DefenceAfterSeconds = Float(play, "DefenceAfterSeconds", c, errors, min: 0, max: 7200);
                p.DefenceDistance = Float(play, "DefenceDistance", c, errors, min: 0, max: 128);
                p.MaxDefendedSpots = Int(play, "MaxDefendedSpots", c, errors, min: 0, max: 16);
                p.WallGap = Int(play, "WallGap", c, errors, min: 0, max: 16);
                p.RetreatSeconds = Float(play, "RetreatSeconds", c, errors, min: 1, max: 600);
                p.TurretStrength = Float(play, "TurretStrength", c, errors, min: 0, max: 100);
            }

            XmlNode counters = Require(ai, "Counters", errors);
            if (counters != null) Counters(counters, config, errors);
        }

        /// <summary>
        /// &lt;Counters&gt;: each &lt;Counter seen="Unit"&gt; or &lt;Counter building="Building"&gt; lists combat units weighted
        /// against that enemy unit or what that building makes (<c>Type:weight</c>, weights in (0, 100]).
        /// </summary>
        private static void Counters(XmlNode counters, AiConfig config, List<string> errors)
        {
            foreach (XmlNode node in counters.SelectNodes("Counter"))
            {
                string seen = node.Attributes?["seen"]?.Value, building = node.Attributes?["building"]?.Value;
                if ((seen == null) == (building == null)) { errors.Add("Ai counters: a <Counter> needs exactly one of seen=\"Unit\" or building=\"Building\"."); continue; }
                string c = seen != null ? $"Ai counter seen={seen}" : $"Ai counter building={building}";
                var weights = new Dictionary<UnitType, float>();
                foreach ((string name, float weight) in Pairs(node, ".", c, errors))
                {
                    if (!Enum.TryParse(name, false, out UnitType unit) || !Enum.IsDefined(typeof(UnitType), unit) || unit == UnitType.None || unit == UnitType.Builder)
                        errors.Add($"{c}: \"{name}\" is not a combat unit type.");
                    else if (!(weight > 0f) || weight > 100f) errors.Add($"{c}: weight {weight} for {name} must be in (0, 100].");
                    else if (weights.ContainsKey(unit)) errors.Add($"{c}: lists {name} twice.");
                    else weights[unit] = weight;
                }
                if (seen != null)
                {
                    if (!Enum.TryParse(seen, false, out UnitType unit) || !Enum.IsDefined(typeof(UnitType), unit) || unit == UnitType.None) errors.Add($"{c}: \"{seen}\" is not a unit type.");
                    else if (config.SeenCounters.ContainsKey(unit)) errors.Add($"{c}: listed twice.");
                    else config.SeenCounters[unit] = weights;
                }
                else
                {
                    if (!Enum.TryParse(building, false, out BuildingType type) || !Enum.IsDefined(typeof(BuildingType), type) || type == BuildingType.None) errors.Add($"{c}: \"{building}\" is not a building type.");
                    else if (config.BuildingCounters.ContainsKey(type)) errors.Add($"{c}: listed twice.");
                    else config.BuildingCounters[type] = weights;
                }
            }
        }

        /// <summary>A style's opening: building and unit names in order (not the HQ, which the opening places first).</summary>
        private static AiBuildStep[] BuildOrder(XmlNode node, string ctx, List<string> errors)
        {
            string text = node.SelectSingleNode("BuildOrder")?.InnerText?.Trim();
            if (string.IsNullOrEmpty(text)) { errors.Add($"{ctx}: <BuildOrder> is missing or empty."); return System.Array.Empty<AiBuildStep>(); }
            var steps = new List<AiBuildStep>();
            foreach (string part in text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse(part, false, out BuildingType building) && Enum.IsDefined(typeof(BuildingType), building) && building != BuildingType.None)
                {
                    if (building == BuildingType.Base) errors.Add($"{ctx}: <BuildOrder> can't hold Base (the HQ comes first anyway).");
                    else steps.Add(new AiBuildStep { Building = building });
                }
                else if (Enum.TryParse(part, false, out UnitType unit) && Enum.IsDefined(typeof(UnitType), unit) && unit != UnitType.None)
                    steps.Add(new AiBuildStep { Unit = unit });
                else errors.Add($"{ctx}: <BuildOrder> step \"{part}\" is not a building or unit type.");
            }
            return steps.ToArray();
        }

        /// <summary>A style's army weights: <c>Type:weight</c> pairs of combat units (not Builders), each weight above 0.</summary>
        private static Dictionary<UnitType, float> ArmyMix(XmlNode node, string ctx, List<string> errors)
        {
            var mix = new Dictionary<UnitType, float>();
            foreach ((string name, float weight) in Pairs(node, "ArmyMix", ctx, errors))
            {
                if (!Enum.TryParse(name, false, out UnitType unit) || !Enum.IsDefined(typeof(UnitType), unit) || unit == UnitType.None || unit == UnitType.Builder)
                    errors.Add($"{ctx}: <ArmyMix> \"{name}\" is not a combat unit type.");
                else if (!(weight > 0f) || weight > 100f) errors.Add($"{ctx}: <ArmyMix> weight {weight} for {name} must be in (0, 100].");
                else if (mix.ContainsKey(unit)) errors.Add($"{ctx}: <ArmyMix> lists {name} twice.");
                else mix[unit] = weight;
            }
            if (mix.Count == 0) errors.Add($"{ctx}: <ArmyMix> needs at least one unit.");
            return mix;
        }

        /// <summary>A style's producers: <c>Building:count</c> pairs of producer buildings (not the HQ), each count a whole number in [0, 8].</summary>
        private static Dictionary<BuildingType, int> Producers(XmlNode node, string ctx, List<string> errors)
        {
            var producers = new Dictionary<BuildingType, int>();
            foreach ((string name, float count) in Pairs(node, "Producers", ctx, errors))
            {
                if (!Enum.TryParse(name, false, out BuildingType building) || !Enum.IsDefined(typeof(BuildingType), building) || building == BuildingType.Base || !SpawnerRules.IsProducer(building))
                    errors.Add($"{ctx}: <Producers> \"{name}\" is not a producer building.");
                else if (count != (int)count || count < 0 || count > 8) errors.Add($"{ctx}: <Producers> count {count} for {name} must be a whole number in [0, 8].");
                else if (producers.ContainsKey(building)) errors.Add($"{ctx}: <Producers> lists {name} twice.");
                else producers[building] = (int)count;
            }
            return producers;
        }

        /// <summary>A required, space-separated list of <c>Name:number</c> pairs.</summary>
        private static List<(string, float)> Pairs(XmlNode node, string name, string ctx, List<string> errors)
        {
            var pairs = new List<(string, float)>();
            string text = node.SelectSingleNode(name)?.InnerText?.Trim();
            if (string.IsNullOrEmpty(text)) { errors.Add($"{ctx}: <{name}> is missing or empty."); return pairs; }
            foreach (string part in text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0 || !float.TryParse(part.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
                {
                    errors.Add($"{ctx}: <{name}> entry \"{part}\" must be Name:number.");
                    continue;
                }
                pairs.Add((part.Substring(0, colon), value));
            }
            return pairs;
        }

        /// <summary>A required 0 or 1.</summary>
        private static bool Flag(XmlNode parent, string name, string ctx, List<string> errors) => Int(parent, name, ctx, errors, min: 0, max: 1) == 1;

        /// <summary>A required enum value by name (case-sensitive).</summary>
        private static T Named<T>(XmlNode parent, string name, string ctx, List<string> errors, T fallback) where T : struct, Enum
        {
            string text = parent.SelectSingleNode(name)?.InnerText?.Trim();
            if (text == null) { errors.Add($"{ctx}: missing <{name}>."); return fallback; }
            if (Enum.TryParse(text, false, out T value) && Enum.IsDefined(typeof(T), value) && !int.TryParse(text, out _)) return value;
            errors.Add($"{ctx}: <{name}> \"{text}\" must be one of {string.Join(", ", Enum.GetNames(typeof(T)))}.");
            return fallback;
        }

        /// <summary>Adds one damage-table entry; the "Unit" shorthand sets both unit classes unless one was given explicitly.</summary>
        private static void AddEntry<T>(Dictionary<(T, TargetClass), float> entries, HashSet<(T, TargetClass)> explicitEntries, T attacker, bool bothUnits, TargetClass target, float m)
        {
            if (bothUnits)
            {
                // An explicit LightUnit or HeavyUnit entry wins over the shorthand, whatever the order.
                if (!explicitEntries.Contains((attacker, TargetClass.LightUnit))) entries[(attacker, TargetClass.LightUnit)] = m;
                if (!explicitEntries.Contains((attacker, TargetClass.HeavyUnit))) entries[(attacker, TargetClass.HeavyUnit)] = m;
            }
            else
            {
                entries[(attacker, target)] = m;
                explicitEntries.Add((attacker, target));
            }
        }

        /// <summary>
        /// A building's optional weapon (v0.13): &lt;Damage&gt;, &lt;Range&gt; and &lt;AttackInterval&gt;, all three or none.
        /// Damage above 0 makes it an attacker, which then needs a positive range and interval.
        /// </summary>
        private static void Weapon(XmlNode node, string ctx, List<string> errors, BuildingConfig building)
        {
            int present = (node.SelectSingleNode("Damage") != null ? 1 : 0) + (node.SelectSingleNode("Range") != null ? 1 : 0) + (node.SelectSingleNode("AttackInterval") != null ? 1 : 0);
            if (present == 0) return;
            if (present != 3)
            {
                errors.Add($"{ctx}: a weapon needs all of <Damage>, <Range> and <AttackInterval>, or none.");
                return;
            }
            building.Damage = Int(node, "Damage", ctx, errors, min: 0, max: 100000);
            building.Range = Float(node, "Range", ctx, errors, max: 32);
            building.AttackInterval = Float(node, "AttackInterval", ctx, errors, max: 600);
            if (!building.IsAttacker) return;
            if (building.Range <= 0f) errors.Add($"{ctx}: <Range> {building.Range} must be above 0 for a building that attacks.");
            if (building.AttackInterval <= 0f) errors.Add($"{ctx}: <AttackInterval> {building.AttackInterval} must be above 0 for a building that attacks.");
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
