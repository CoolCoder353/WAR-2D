using UnityEngine;
using NaughtyAttributes;
public class BuildingDataClient : DataClient
{
    public BuildingData buildingData;


    [Button]
    public void PrintBuildingInfo()
    {

        Debug.Log($"Position: {buildingData.position}, ID: {buildingData.id}, Type: {buildingData.buildingType}, OwnerID: {buildingData.ownerId}, Rotation: {buildingData.rotation}");
        Debug.Log($"Health: {healthComponent.currentHealth}/{healthComponent.maxHealth}");
    }
}