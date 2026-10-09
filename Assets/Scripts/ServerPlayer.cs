using Mirror;

/// <summary>Server-only state for one connected player.</summary>
public class ServerPlayer
{
    public NetworkConnectionToClient connection;
    public PlayerState state = PlayerState.Playing;

    public float Resources { get; private set; }

    /// <summary>Resources earned (passive and mining) during the last whole second.</summary>
    public float IncomeLastSecond { get; private set; }

    /// <summary>Upkeep paid (units and buildings) during the last whole second.</summary>
    public float UpkeepLastSecond { get; private set; }

    private float incomeThisSecond, upkeepThisSecond;

    /// <summary>True when Resources (or the per-second rates) changed since the last sync to the client.</summary>
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

    /// <summary>Adds earned resources and counts them towards this second's income.</summary>
    public void AddIncome(float amount)
    {
        if (!(amount > 0f)) return;
        Add(amount);
        incomeThisSecond += amount;
    }

    /// <summary>Counts upkeep already taken with <see cref="TrySpend"/> towards this second's upkeep.</summary>
    public void AddUpkeep(float amount)
    {
        if (amount > 0f) upkeepThisSecond += amount;
    }

    private bool unpaidThisSecond, unpaidLastSecond;

    /// <summary>True for the second in which some of the player's upkeep first went unpaid (after <see cref="RollSecond"/>).</summary>
    public bool BecameUnpaid { get; private set; }

    /// <summary>Records that some of the player's upkeep (units or buildings) went unpaid this second.</summary>
    public void MarkUnpaid() => unpaidThisSecond = true;

    /// <summary>Publishes this second's income and upkeep and starts a new second. Called once a second.</summary>
    public void RollSecond()
    {
        BecameUnpaid = unpaidThisSecond && !unpaidLastSecond;
        unpaidLastSecond = unpaidThisSecond;
        unpaidThisSecond = false;
        if (incomeThisSecond != IncomeLastSecond || upkeepThisSecond != UpkeepLastSecond) ResourcesDirty = true;
        IncomeLastSecond = incomeThisSecond;
        UpkeepLastSecond = upkeepThisSecond;
        incomeThisSecond = 0f;
        upkeepThisSecond = 0f;
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
