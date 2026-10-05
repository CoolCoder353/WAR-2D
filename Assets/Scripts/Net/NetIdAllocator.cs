using System;

/// <summary>Hands out unique, positive network ids for units and buildings within one match (server only).</summary>
public sealed class NetIdAllocator
{
    private int next = 1;

    public int Allocate()
    {
        if (next == int.MaxValue) throw new InvalidOperationException("Network id space exhausted.");
        return next++;
    }

    public void Reset()
    {
        next = 1;
    }
}
