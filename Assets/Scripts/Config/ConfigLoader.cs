using System;
using System.Collections.Generic;
using System.Xml;
using UnityEngine;

namespace Config
{
    public static class ConfigLoader
    {
        private static GameConfigData _cachedConfig;
        private static bool _isLoaded = false;

        public static GameConfigData LoadConfig()
        {
            if (_isLoaded)
            {
                return _cachedConfig;
            }

            _cachedConfig = new GameConfigData();

            try
            {
                TextAsset xmlAsset = Resources.Load<TextAsset>("GameConfig");
                if (xmlAsset == null)
                {
                    Debug.LogError("GameConfig.xml not found in Resources folder. Using default values.");
                    return _cachedConfig;
                }

                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(xmlAsset.text);

                XmlNode root = xmlDoc.SelectSingleNode("GameConfig");
                if (root == null)
                {
                    Debug.LogError("Invalid GameConfig.xml format.");
                    return _cachedConfig;
                }

                // Load Resources
                XmlNode resourcesNode = root.SelectSingleNode("Resources");
                if (resourcesNode != null)
                {
                    _cachedConfig.Resources.PassiveGenerationRate = ParseFloat(resourcesNode, "PassiveGenerationRate", 5.0f);
                    _cachedConfig.Resources.StartingResources = ParseFloat(resourcesNode, "StartingResources", 100.0f);
                    _cachedConfig.Resources.MiningRate = ParseFloat(resourcesNode, "MiningRate", 10.0f);
                }

                // Load Units
                XmlNode unitsNode = root.SelectSingleNode("Units");
                if (unitsNode != null)
                {
                    foreach (XmlNode unitNode in unitsNode.SelectNodes("Unit"))
                    {
                        string type = unitNode.Attributes["type"]?.Value;
                        if (!string.IsNullOrEmpty(type))
                        {
                            UnitConfig unitConfig = new UnitConfig();
                            unitConfig.Health = ParseInt(unitNode, "Health", 100);
                            unitConfig.Damage = ParseInt(unitNode, "Damage", 10);
                            unitConfig.MoveSpeed = ParseFloat(unitNode, "MoveSpeed", 5.0f);
                            unitConfig.UpfrontCost = ParseFloat(unitNode, "UpfrontCost", 50.0f);
                            unitConfig.RunningCost = ParseFloat(unitNode, "RunningCost", 2.0f);
                            _cachedConfig.Units[type] = unitConfig;
                        }
                    }
                }

                // Load Buildings
                XmlNode buildingsNode = root.SelectSingleNode("Buildings");
                if (buildingsNode != null)
                {
                    foreach (XmlNode buildingNode in buildingsNode.SelectNodes("Building"))
                    {
                        string type = buildingNode.Attributes["type"]?.Value;
                        if (!string.IsNullOrEmpty(type))
                        {
                            BuildingConfig buildingConfig = new BuildingConfig();
                            buildingConfig.Health = ParseInt(buildingNode, "Health", 200);
                            buildingConfig.UpfrontCost = ParseFloat(buildingNode, "UpfrontCost", 100.0f);
                            buildingConfig.RunningCost = ParseFloat(buildingNode, "RunningCost", 5.0f);
                            buildingConfig.MiningRate = ParseFloat(buildingNode, "MiningRate", 0.0f);
                            buildingConfig.SpawnRate = ParseFloat(buildingNode, "SpawnRate", 0.0f);
                            _cachedConfig.Buildings[type] = buildingConfig;
                        }
                    }
                }

                Debug.Log("GameConfig loaded successfully.");
            }
            catch (Exception e)
            {
                Debug.LogError($"Error loading GameConfig.xml: {e.Message}");
            }

            _isLoaded = true;
            return _cachedConfig;
        }

        private static float ParseFloat(XmlNode parent, string childName, float defaultValue)
        {
            XmlNode node = parent.SelectSingleNode(childName);
            if (node != null && float.TryParse(node.InnerText, out float result))
            {
                return result;
            }
            return defaultValue;
        }

        private static int ParseInt(XmlNode parent, string childName, int defaultValue)
        {
            XmlNode node = parent.SelectSingleNode(childName);
            if (node != null && int.TryParse(node.InnerText, out int result))
            {
                return result;
            }
            return defaultValue;
        }
    }
}
