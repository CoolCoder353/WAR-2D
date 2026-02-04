using UnityEngine;
using NaughtyAttributes;
using System;

public class DataClient : MonoBehaviour
{
    public HealthComponent healthComponent;

    public GameObject healthBarPrefab;

    public float testVaule = 1f;

    public void Awake()
    {
        StartCoroutine(HealthVisulation());
    }

    private System.Collections.IEnumerator HealthVisulation()
    {
        yield return new WaitUntil(() => healthComponent.maxHealth > 0);

        Debug.Log("Health Visualization started for " + this.name);
        if (healthBarPrefab == null)
        {
            GameObject healthBarObj = Resources.Load<GameObject>("UI/HealthBarUI");

            if (healthBarObj == null)
            {
                Debug.LogError("Health bar prefab not found in Resources/UI/HealthBarUI");
                yield break;
            }
            Instantiate(healthBarObj, this.transform);

            healthBarPrefab = healthBarObj;
            Debug.Log("Health bar prefab loaded for " + this.name);
        }

        GameObject healthBarRed = healthBarPrefab.transform.Find("Red").gameObject;

        //Scale the red health bar according to health
        while (true)
        {
            float healthPercent = (float)healthComponent.currentHealth / (float)healthComponent.maxHealth;
            healthBarRed.transform.localScale = new Vector3(healthPercent, 1, 1);

            Debug.Log("Health percent for " + this.name + ": " + healthPercent);

            yield return new WaitForSeconds(0.3f);
        }


    }



    [Button("Test Health Visualization")]
    public void TestHealthVisualization()
    {
        GameObject healthBarRed = healthBarPrefab.transform.Find("Red").gameObject;

        healthBarRed.transform.localScale = new Vector3(testVaule, 1, 1);
    }
}