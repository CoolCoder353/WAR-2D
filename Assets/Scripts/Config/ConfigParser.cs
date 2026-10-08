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
                        Acceleration = Float(node, "Acceleration", ctx, errors, min: 0),
                        UpfrontCost = Float(node, "UpfrontCost", ctx, errors, min: 0),
                        RunningCost = Float(node, "RunningCost", ctx, errors, min: 0),
                        Radius = Float(node, "Radius", ctx, errors, min: 0.1f, max: 2),
                        SizeClass = Int(node, "SizeClass", ctx, errors, min: 0, max: 1),
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
            }

            XmlNode damage = Require(root, "DamageTable", errors);
            if (damage != null)
            {
                foreach (XmlNode node in damage.SelectNodes("Entry"))
                {
                    string attackerName = node.Attributes?["attacker"]?.Value;
                    string targetName = node.Attributes?["target"]?.Value;
                    bool attackerOk = Enum.TryParse(attackerName, false, out UnitType attacker) && attacker != UnitType.None
                                      && Enum.IsDefined(typeof(UnitType), attacker);
                    bool targetOk = Enum.TryParse(targetName, false, out TargetClass target) && Enum.IsDefined(typeof(TargetClass), target);
                    if (!attackerOk) { errors.Add($"DamageTable: unknown attacker type \"{attackerName}\"."); continue; }
                    if (!targetOk) { errors.Add($"DamageTable: unknown target class \"{targetName}\"."); continue; }
                    string text = node.InnerText?.Trim();
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float m) || float.IsNaN(m) || m < 0 || m > 100)
                    {
                        errors.Add($"DamageTable: multiplier \"{text}\" for {attacker} vs {target} must be a number in [0, 100].");
                        continue;
                    }
                    config.Damage.Entries[(attacker, target)] = m;
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
                        SpawnRate = Float(node, "SpawnRate", ctx, errors, min: 0, max: 100),
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
            if (config.Buildings.TryGetValue(BuildingType.SmallUnitSpawner, out BuildingConfig spawner) && spawner.SpawnRate <= 0)
                errors.Add("Building SmallUnitSpawner: SpawnRate must be greater than 0.");

            return config;
        }

        private static XmlNode Require(XmlNode parent, string name, List<string> errors)
        {
            XmlNode node = parent.SelectSingleNode(name);
            if (node == null) errors.Add($"Missing <{name}> section.");
            return node;
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
