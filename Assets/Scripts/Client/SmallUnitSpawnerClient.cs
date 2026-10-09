using UnityEngine;
using TMPro;
using UnityEngine.EventSystems;

public class SpawnerClientManager : MonoBehaviour, IPointerClickHandler
{

    //NOTE: This could really be replaced with a uid for the building id, but we might want more data later.
    public BuildingData buildingData;

    public void OnPointerClick(PointerEventData eventData)
    {
        // Selecting the spawner shows its production queue on the command card.
        UnitCommander.Instance?.SelectBuilding(buildingData);
    }


    public void Start()
    {
        Debug.Log("SpawnerClientManager started for building type: " + buildingData.buildingType + " with ID: " + buildingData.id);
    }
}