using Mirror;

/// <summary>Server-only state for one connected player.</summary>
public class ServerPlayer
{
    public NetworkConnectionToClient connection;
    public PlayerState state = PlayerState.Playing;

    public float Resources { get; private set; }

    /// <summary>True when Resources changed since the last sync to the client.</summary>
    public bool ResourcesDirty { get; private set; } = true;

    public ServerPlayer(NetworkConnectionToClient connection, float startingResources)
    {
        this.connection = connection;
        Resources = startingResources;
    }

    public void Add(float amount)
    {
        if (!(amount > 0f)) return; // also rejects NaN
        Resources += amount;
        ResourcesDirty = true;
    }

    public bool TrySpend(float amount)
    {
        float balance = Resources;
        if (!UpkeepRules.TryCharge(ref balance, amount)) return false;
        if (balance != Resources)
        {
            Resources = balance;
            ResourcesDirty = true;
        }
        return true;
    }

    public void MarkSynced()
    {
        ResourcesDirty = false;
    }
}

public enum PlayerState
{
    Playing,
    Eliminated,
    Spectating
}
