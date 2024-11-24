using UnityEngine;


public class WorldStateManager : MonoBehaviour
{
    public static WorldStateManager Instance { get; private set; }

    public Tilemap tilemap;



    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(this);
        }
    }

    //TODO: Change the entity to a reference to the player id the entity belongs to
    public void LockNode(int2 position, int entity = 1)
    {
        tilemap.GetNode(position).used = entity;
    }

    public void UnlockNode(int2 position)
    {
        tilemap.GetNode(position).used = 0;
    }

    public void FindPath

}