using UnityEngine;
using TMPro;

public class SpawnerClientManager : MonoBehaviour
{
    public BuildingData buildingData;


    public void Start()
    {
        Debug.Log("SpawnerClientManager started for building type: " + buildingData.buildingType + " with ID: " + buildingData.id);
    }
}