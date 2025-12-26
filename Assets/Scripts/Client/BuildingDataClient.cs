using UnityEngine;
using NaughtyAttributes;
public class BuildingDataClient : MonoBehaviour
{
    public BuildingData buildingData;
    public HealthComponent healthComponent;


    [Button]
    public void PrintBuildingInfo()
    {

        Debug.Log($"Position: {buildingData.position}, ID: {buildingData.id}, Type: {buildingData.buildingType}, OwnerID: {buildingData.ownerId}, Rotation: {buildingData.rotation}");
        Debug.Log($"Health: {healthComponent.currentHealth}/{healthComponent.maxHealth}");
    }
}