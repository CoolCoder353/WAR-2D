

public struct ClientUnit
{
    public int2 position;
    public NetworkTransform owner;
    public string spriteName;

    //NOTE: THIS COULD BE A ulong if we need more characters
    //See https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/integral-numeric-types for comparison
    public uint id;


}

public struct ServerUnit
{
    public uint id;
    public string spriteName;
}

public class UnitIdAuthoring : MonoBehaviour
{
    public uint id;
    public string spriteName;
    private class Baker : Baker<UnitIdAuthoring>
    {
        public override void Bake(UnitIdAuthoring authoring)
        {
            var entity = GetEntity(authoring, TransformUsageFlags.Dynamic);
            AddComponent(entity, new ClientUnit { id = authoring.id, spriteName = authoring.spriteName });
        }
    }

