using UnityEngine;
using NaughtyAttributes;
using System;
using UnityEngine.UI;

public class DataClient : MonoBehaviour
{
    public HealthComponent healthComponent;

    public GameObject healthBarObject;

    private float lastHealthPercent = 1f;


    public float testVaule = 1f;

    public void Awake()
    {
        StartCoroutine(HealthVisulation());
    }

    private void OnDestroy()
    {
        StopAllCoroutines();
    }

    private System.Collections.IEnumerator HealthVisulation()
    {
        yield return new WaitUntil(() => healthComponent.maxHealth > 0);

        Debug.Log("Health Visualization started for " + this.name);
        if (healthBarObject == null)
        {
            GameObject healthBarPrefab = Resources.Load<GameObject>("UI/HealthBarUI");

            if (healthBarPrefab == null)
            {
                Debug.LogError("Health bar prefab not found in Resources/UI/HealthBarUI");
                yield break;
            }
            healthBarObject = Instantiate(healthBarPrefab, this.transform);

            Debug.Log("Health bar prefab loaded for " + this.name);
        }

        Slider healthBarSlider = healthBarObject.GetComponent<Slider>();
        SpriteRenderer sprite = this.GetComponent<SpriteRenderer>();
        while (true)
        {
            if (healthBarSlider == null)
            {
                Debug.LogError("Health bar slider component not found for " + this.name);
                yield break;
            }

            float healthPercent = (float)healthComponent.currentHealth / (float)healthComponent.maxHealth;
            healthBarSlider.value = healthPercent;

            if (lastHealthPercent > healthPercent && sprite != null)
            {
                // Flash red to show damage taken, then back to whatever tint it had (owner colour, ghost).
                lastHealthPercent = healthPercent;
                Color tint = sprite.color;
                sprite.color = Color.red;
                yield return new WaitForSeconds(0.15f);
                if (sprite != null && sprite.color == Color.red) sprite.color = tint;
            }

            yield return new WaitForSeconds(0.3f);
        }
    }



    [Button("Test Health Visualization")]
    public void TestHealthVisualization()
    {
        Slider healthBarSlider = healthBarObject.GetComponent<Slider>();

        healthBarSlider.value = testVaule;
    }
}