using UnityEngine;
using TMPro;
using UnityEngine.EventSystems;

public class SpawnerClientManager : MonoBehaviour, IPointerClickHandler
{

    //NOTE: This could really be replaced with a uid for the building id, but we might want more data later.
    public BuildingData buildingData;

    public void OnPointerClick(PointerEventData eventData)
    {
        Debug.Log("Building clicked with ID: " + buildingData.id);

        WorldStateManager.Instance.BuildingClicked(buildingData.id);
    }


    public void Start()
    {
        Debug.Log("SpawnerClientManager started for building type: " + buildingData.buildingType + " with ID: " + buildingData.id);
    }
}