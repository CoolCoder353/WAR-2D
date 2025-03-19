using System;
using System.Collections.Generic;
using Mirror;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;


public class BuildingButtonManager : MonoBehaviour
{
    public List<Button> buttons = new List<Button>();

    public List<BuildingType> buildingTypes = new List<BuildingType>();

    public GameObject previewBuilding;

    public BuildingType selectedBuildingType;

    [ClientCallback]
    public void Start()
    {
        foreach (var button in buttons)
        {
            button.onClick.AddListener(() => OnButtonClicked(button));

            button.GetComponentInChildren<Image>().sprite = Resources.Load<Sprite>(buildingTypes[buttons.IndexOf(button)].ToString());
        }

        if (buildingTypes.Count != buttons.Count)
        {
            Debug.LogError("Building types and buttons count do not match. Did you forget to assign a building type to a button?");
        }
    }
    [ClientCallback]
    public void Update()
    {
        if (previewBuilding.activeInHierarchy)
        {
            Vector3 position = RoundVector3(UnitCommander.GetMouseWorldPosition());

            previewBuilding.transform.position = new Vector3((int)position.x, (int)position.y, 0);
        }
        if (Input.GetMouseButtonDown(0) && previewBuilding.activeInHierarchy)
        {
            TrySpawnBuilding();
            previewBuilding.SetActive(false);
            previewBuilding.transform.position = new Vector3(0, 0, 0);
            previewBuilding.GetComponent<SpriteRenderer>().sprite = null;

        }
    }

    [Client]
    private void TrySpawnBuilding()
    {
        Vector3 position = RoundVector3(UnitCommander.GetMouseWorldPosition());
        int2 convertedPosition = new int2((int)position.x, (int)position.y);

        Debug.Log($"Trying to spawn building at {convertedPosition} of type {selectedBuildingType} where the preview building is at {previewBuilding.transform.position} -> client");

        WorldStateManager.Instance.TryAddBuilding(convertedPosition, selectedBuildingType);
    }

    private static Vector3 RoundVector3(Vector3 vector)
    {
        return new Vector3(Mathf.Round(vector.x), Mathf.Round(vector.y), Mathf.Round(vector.z));
    }

    [Client]
    public void OnButtonClicked(Button button)
    {
        selectedBuildingType = buildingTypes[buttons.IndexOf(button)];
        SetupBuildingPreview();
    }

    [Client]
    private void SetupBuildingPreview()
    {
        previewBuilding.GetComponent<SpriteRenderer>().sprite = Resources.Load<Sprite>(selectedBuildingType.ToString());
        previewBuilding.SetActive(true);
    }
}