using System;
using Unity.Collections;

public struct NativePriorityQueue : IDisposable
{
    private NativeArray<PathNode> _elements;
    private int _count;
    private Allocator _allocator;

    public NativePriorityQueue(int initialCapacity, Allocator allocator)
    {
        _elements = new NativeArray<PathNode>(initialCapacity, allocator);
        _count = 0;
        _allocator = allocator;

    }

    public int Compare(float a, float b)
    {
        return (a).CompareTo(b);
    }

    public int Length => _count;

    public void Enqueue(PathNode item)
    {
        if (_count >= _elements.Length)
        {
            Resize(_elements.Length * 2);
        }

        _elements[_count] = item;
        int c = _count;
        _count++;

        while (c > 0 && Compare(_elements[c].fcost, _elements[(c - 1) / 2].fcost) < 0)
        {
            PathNode tmp = _elements[c];
            _elements[c] = _elements[(c - 1) / 2];
            _elements[(c - 1) / 2] = tmp;
            c = (c - 1) / 2;
        }
    }

    public PathNode Dequeue()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Priority queue is empty.");
        }

        PathNode frontItem = _elements[0];
        _count--;
        _elements[0] = _elements[_count];

        int pi = 0;
        while (true)
        {
            int ci = pi * 2 + 1;
            if (ci >= _count) break;
            int rc = ci + 1;
            if (rc < _count && Compare(_elements[rc].fcost, _elements[ci].fcost) < 0)
                ci = rc;
            if (Compare(_elements[pi].fcost, _elements[ci].fcost) <= 0) break;
            PathNode tmp = _elements[pi];
            _elements[pi] = _elements[ci];
            _elements[ci] = tmp;
            pi = ci;
        }

        return frontItem;
    }

    public PathNode Peek()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Priority queue is empty.");
        }
        return _elements[0];
    }

    private void Resize(int newSize)
    {
        NativeArray<PathNode> newArray = new NativeArray<PathNode>(newSize, _allocator);
        NativeArray<PathNode>.Copy(_elements, newArray, _count);
        _elements.Dispose();
        _elements = newArray;
    }

    public void Dispose()
    {
        if (_elements.IsCreated)
        {
            _elements.Dispose();
        }
    }

    public bool Contains(PathNode item)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_elements[i].Equals(item))
            {
                return true;
            }
        }

        return false;
    }
}