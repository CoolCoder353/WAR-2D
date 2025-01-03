

using Mirror;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[System.Serializable]

public struct ClientUnit : IComponentData
{
    public float2 position;
    /// <summary>
    /// public NetworkIdentity owner;
    /// </summary>
    public UnitSprites spriteName;

    //NOTE: THIS COULD BE A ulong if we need more characters
    //See https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/integral-numeric-types for comparison
    public int id;

    public int ownerId;
}

public enum UnitSprites
{
    None,
    Tank
}


public class UnitIdAuthoring : MonoBehaviour
{
    public int id;
    public UnitSprites spriteName;

    private class Baker : Baker<UnitIdAuthoring>
    {
        public override void Bake(UnitIdAuthoring authoring)
        {
            var entity = GetEntity(authoring, TransformUsageFlags.Dynamic);
            AddComponent(entity, new ClientUnit
            {
                id = authoring.id,
                spriteName = authoring.spriteName
            });
        }
    }
}

