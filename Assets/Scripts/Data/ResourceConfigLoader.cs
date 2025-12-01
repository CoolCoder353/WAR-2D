using System;
using System.Collections.Generic;
using System.Xml;
using UnityEngine;

public class ResourceConfigLoader
{
    private static ResourceConfigData _cachedConfig;
    private static bool _isLoaded = false;

    public static ResourceConfigData LoadConfig()
    {
        if (_isLoaded)
        {
            return _cachedConfig;
        }

        _cachedConfig = new ResourceConfigData();

        try
        {
            TextAsset xmlAsset = Resources.Load<TextAsset>("Data/ResourceConfig");
            if (xmlAsset == null)
            {
                Debug.LogError("ResourceConfig.xml not found in Resources/Data folder. Using default values.");
                SetDefaultValues(_cachedConfig);
                _isLoaded = true;
                return _cachedConfig;
            }

            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xmlAsset.text);

            XmlNode root = xmlDoc.SelectSingleNode("ResourceConfig");
            if (root == null)
            {
                Debug.LogError("Invalid ResourceConfig.xml format. Using default values.");
                SetDefaultValues(_cachedConfig);
                _isLoaded = true;
                return _cachedConfig;
            }

            // Load basic values
            _cachedConfig.passiveGenerationRate = ParseFloat(root, "PassiveGenerationRate", 5.0f);
            _cachedConfig.startingResources = ParseFloat(root, "StartingResources", 100.0f);
            _cachedConfig.defaultUnitUpfrontCost = ParseFloat(root, "DefaultUnitUpfrontCost", 50.0f);
            _cachedConfig.defaultUnitRunningCost = ParseFloat(root, "DefaultUnitRunningCost", 2.0f);
            _cachedConfig.defaultBuildingUpfrontCost = ParseFloat(root, "DefaultBuildingUpfrontCost", 100.0f);
            _cachedConfig.defaultBuildingRunningCost = ParseFloat(root, "DefaultBuildingRunningCost", 5.0f);
            _cachedConfig.miningRate = ParseFloat(root, "MiningRate", 10.0f);

            // Load unit-specific costs
            XmlNode unitCostsNode = root.SelectSingleNode("UnitCosts");
            if (unitCostsNode != null)
            {
                foreach (XmlNode unitNode in unitCostsNode.SelectNodes("Unit"))
                {
                    string typeStr = unitNode.Attributes["type"]?.Value;
                    if (string.IsNullOrEmpty(typeStr)) continue;

                    if (Enum.TryParse(typeStr, out UnitType unitType))
                    {
                        ResourceCost cost = new ResourceCost
                        {
                            upfrontCost = ParseFloat(unitNode, "UpfrontCost", _cachedConfig.defaultUnitUpfrontCost),
                            runningCost = ParseFloat(unitNode, "RunningCost", _cachedConfig.defaultUnitRunningCost)
                        };
                        _cachedConfig.unitCosts[unitType] = cost;
                    }
                }
            }

            // Load building-specific costs
            XmlNode buildingCostsNode = root.SelectSingleNode("BuildingCosts");
            if (buildingCostsNode != null)
            {
                foreach (XmlNode buildingNode in buildingCostsNode.SelectNodes("Building"))
                {
                    string typeStr = buildingNode.Attributes["type"]?.Value;
                    if (string.IsNullOrEmpty(typeStr)) continue;

                    if (Enum.TryParse(typeStr, out BuildingType buildingType))
                    {
                        ResourceCost cost = new ResourceCost
                        {
                            upfrontCost = ParseFloat(buildingNode, "UpfrontCost", _cachedConfig.defaultBuildingUpfrontCost),
                            runningCost = ParseFloat(buildingNode, "RunningCost", _cachedConfig.defaultBuildingRunningCost)
                        };
                        _cachedConfig.buildingCosts[buildingType] = cost;
                    }
                }
            }

            Debug.Log("ResourceConfig.xml loaded successfully.");
        }
        catch (Exception e)
        {
            Debug.LogError($"Error loading ResourceConfig.xml: {e.Message}. Using default values.");
            SetDefaultValues(_cachedConfig);
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

    private static void SetDefaultValues(ResourceConfigData config)
    {
        config.passiveGenerationRate = 5.0f;
        config.startingResources = 100.0f;
        config.defaultUnitUpfrontCost = 50.0f;
        config.defaultUnitRunningCost = 2.0f;
        config.defaultBuildingUpfrontCost = 100.0f;
        config.defaultBuildingRunningCost = 5.0f;
        config.miningRate = 10.0f;
    }

    public static ResourceCost GetUnitCost(UnitType unitType)
    {
        ResourceConfigData config = LoadConfig();
        if (config.unitCosts.TryGetValue(unitType, out ResourceCost cost))
        {
            return cost;
        }
        return new ResourceCost
        {
            upfrontCost = config.defaultUnitUpfrontCost,
            runningCost = config.defaultUnitRunningCost
        };
    }

    public static ResourceCost GetBuildingCost(BuildingType buildingType)
    {
        ResourceConfigData config = LoadConfig();
        if (config.buildingCosts.TryGetValue(buildingType, out ResourceCost cost))
        {
            return cost;
        }
        return new ResourceCost
        {
            upfrontCost = config.defaultBuildingUpfrontCost,
            runningCost = config.defaultBuildingRunningCost
        };
    }
}

[System.Serializable]
public class ResourceConfigData
{
    public float passiveGenerationRate;
    public float startingResources;
    public float defaultUnitUpfrontCost;
    public float defaultUnitRunningCost;
    public float defaultBuildingUpfrontCost;
    public float defaultBuildingRunningCost;
    public float miningRate;

    public Dictionary<UnitType, ResourceCost> unitCosts = new Dictionary<UnitType, ResourceCost>();
    public Dictionary<BuildingType, ResourceCost> buildingCosts = new Dictionary<BuildingType, ResourceCost>();
}

[System.Serializable]
public struct ResourceCost
{
    public float upfrontCost;
    public float runningCost;
}
