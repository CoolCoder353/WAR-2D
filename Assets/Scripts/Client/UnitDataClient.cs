using UnityEngine;
using NaughtyAttributes;
using Mirror;
public class UnitDataClient : DataClient
{
    public ClientUnit unitData;
    public DamageComponent damageComponent;


    [Button]
    public void PrintUnitInfo()
    {

        Debug.Log($"Unit ID: {unitData.id}, Type: {unitData.spriteName}, Position: {unitData.position}, Owner ID: {unitData.ownerId}, Target ID: {unitData.targetId}, Last Attack Time: {unitData.lastAttackTime}");
        Debug.Log($"Health: {healthComponent.currentHealth}/{healthComponent.maxHealth}");

        Debug.Log($"Damage: {damageComponent.damageAmount}, Range: {damageComponent.range}, Attack Speed: {damageComponent.attackSpeed}");
    }




}