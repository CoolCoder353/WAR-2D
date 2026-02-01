using UnityEngine;
using NaughtyAttributes;
using System;

public class DataClient : MonoBehaviour
{
    public HealthComponent healthComponent;

    public GameObject healthBarPrefab;

    public void Awake()
    {
        StartCoroutine(HealthVisulation());
    }

    private System.Collections.IEnumerator HealthVisulation()
    {
        yield return new WaitUntil(() => healthComponent.maxHealth > 0);

        if (healthBarPrefab == null)
        {
            GameObject healthBarObj = Resources.Load<GameObject>("Prefabs/UI/HealthBarUI");
            Instantiate(healthBarObj, this.transform);

            healthBarPrefab = healthBarObj;
        }


    }
}