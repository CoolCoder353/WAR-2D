using UnityEngine;
using NaughtyAttributes;
using System;
using UnityEngine.UI;

public class DataClient : MonoBehaviour
{
    public HealthComponent healthComponent;

    public GameObject healthBarObject;

    private float lastHealthPercent = 1f;

    private Color originalColor;


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
        originalColor = sprite.color;
        //Scale the red health bar according to health
        while (true)
        {
            sprite.color = originalColor;
            if (healthBarSlider == null)
            {
                Debug.LogError("Health bar slider component not found for " + this.name);
                yield break;
            }

            float healthPercent = (float)healthComponent.currentHealth / (float)healthComponent.maxHealth;

            if (lastHealthPercent > healthPercent && sprite != null)
            {
                //Flash red to show damage taken

                sprite.color = Color.red;
                lastHealthPercent = healthPercent;
            }

            healthBarSlider.value = healthPercent;
            ////Debug.Log("Health percent for " + this.name + ": " + healthPercent + "(Slider value: " + healthBarSlider.value + ")");

            yield return new WaitForSeconds(0.3f); //Update every x frames
        }


    }



    [Button("Test Health Visualization")]
    public void TestHealthVisualization()
    {
        Slider healthBarSlider = healthBarObject.GetComponent<Slider>();

        healthBarSlider.value = testVaule;
    }
}