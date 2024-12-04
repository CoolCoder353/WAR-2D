using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;
using Unity.Transforms;
using Unity.Entities.Serialization;

public class UnitTestingSpawner : MonoBehaviour
{
    private Button button1;
    private Button button2;
    private Button button3;


    private EntityManager entityManager;
    public SpawnerData spawnerData;

    // Start is called before the first frame update
    void Start()
    {
        entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

        //Find the SpawnerData entity
        var query = entityManager.CreateEntityQuery(typeof(SpawnerData));
        var spawnerDataEntity = query.GetSingletonEntity();
        spawnerData = entityManager.GetComponentData<SpawnerData>(spawnerDataEntity);


        var uiDocument = GetComponent<UIDocument>();
        var root = uiDocument.rootVisualElement;

        button1 = root.Q<Button>("SpawnOneUnit");
        button2 = root.Q<Button>("SpawnThousandUnit");
        button3 = root.Q<Button>("SpawnTenThousandUnit");

        button1.clicked += () => SpawnUnits(1);
        button2.clicked += () => SpawnUnits(1000);
        button3.clicked += () => SpawnUnits(10000);
    }

    private void SpawnUnits(int count)
    {
        spawnerData.count += count;
    }



}