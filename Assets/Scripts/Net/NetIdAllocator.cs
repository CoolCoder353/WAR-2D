using System;
using System.Collections.Generic;

/// <summary>
/// Hands out network ids for units and buildings within one match (server only). An id is
/// <c>(generation &lt;&lt; 20) | index</c>: the index (below <see cref="Capacity"/>, at most 2^20) addresses
/// every per-entity array, and the generation (1..2047, wrapping back to 1) changes each time an index is
/// reused, so a stale id never matches a newer entity. Ids are always positive.
/// </summary>
public sealed class NetIdAllocator
{
    /// <summary>Bits of an id that hold the index.</summary>
    public const int IndexBits = 20;

    /// <summary>Largest capacity an allocator may have.</summary>
    public const int MaxCapacity = 1 << IndexBits;

    private const int IndexMask = MaxCapacity - 1;
    private const int MaxGeneration = 2047;

    private readonly ushort[] generation;
    private readonly bool[] live;
    private readonly Stack<int> free = new Stack<int>();
    private int nextFresh;

    /// <summary>How many ids may be live at once.</summary>
    public int Capacity { get; }

    public NetIdAllocator(int capacity)
    {
        if (capacity < 1 || capacity > MaxCapacity) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, $"must be in [1, {MaxCapacity}]");
        Capacity = capacity;
        generation = new ushort[capacity];
        live = new bool[capacity];
        Reset();
    }

    /// <summary>The array index an id addresses.</summary>
    public static int IndexOf(int id) => id & IndexMask;

    /// <summary>The reuse counter of an id.</summary>
    public static int GenerationOf(int id) => id >> IndexBits;

    /// <summary>Returns a new id, reusing freed indices first (which keeps arrays dense).</summary>
    public int Allocate()
    {
        int index;
        if (free.Count > 0) index = free.Pop();
        else if (nextFresh < Capacity) index = nextFresh++;
        else throw new InvalidOperationException($"Network id space exhausted ({Capacity} live ids).");
        live[index] = true;
        return (generation[index] << IndexBits) | index;
    }

    /// <summary>Frees a live id; its index comes back with the next generation. Stale or unknown ids are ignored.</summary>
    public void Free(int id)
    {
        if (!IsLive(id)) return;
        int index = IndexOf(id);
        live[index] = false;
        generation[index] = (ushort)(generation[index] >= MaxGeneration ? 1 : generation[index] + 1);
        free.Push(index);
    }

    /// <summary>True when the id is the current, allocated id of its index.</summary>
    public bool IsLive(int id)
    {
        if (id <= 0) return false;
        int index = IndexOf(id);
        return index < Capacity && live[index] && generation[index] == GenerationOf(id);
    }

    /// <summary>Frees every id and starts over (generations restart at 1).</summary>
    public void Reset()
    {
        free.Clear();
        nextFresh = 0;
        for (int i = 0; i < Capacity; i++) { generation[i] = 1; live[i] = false; }
    }
}
