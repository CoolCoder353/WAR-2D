using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Pathing;
using WAR2D.World;

namespace WAR2D.Sim
{
    /// <summary>
    /// The live move orders. Each order is one flow field (a <see cref="SectorFieldCache"/> handle) that
    /// every unit given the order follows; the handle is the unit's <see cref="Unit.OrderSlot"/>. Orders
    /// die when the tick counts no followers left. Everything here runs on the main thread at the tick
    /// boundary, when no job reads the table.
    /// </summary>
    public sealed class OrderBook : IDisposable
    {
        private readonly SectorFieldCache cache;
        private readonly MapGrid map;

        /// <summary>How far (tiles) a goal on rock or a footprint is moved to reach open floor.</summary>
        public const int GoalSnapRadius = 12;
        private OrderFieldTable table;
        private readonly Dictionary<int, int> issuedTick = new Dictionary<int, int>();
        private readonly HashSet<long> missSeen = new HashSet<long>();
        private readonly Dictionary<int, HashSet<int>> ordersByOwner = new Dictionary<int, HashSet<int>>();

        /// <summary>
        /// Most live orders one owner may hold, so a single player cannot fill the shared table
        /// (<see cref="OrderFieldTable.MaxOrders"/>) and leave everyone else's orders refused.
        /// </summary>
        public const int MaxOrdersPerOwner = OrderFieldTable.MaxOrders / 8;

        /// <summary>Most live orders an owner holds before the game's own orders (idle Builders' work, idle units answering fire) wait, so they can't leave a player's click refused.</summary>
        public const int MaxBackgroundOrdersPerOwner = MaxOrdersPerOwner * 3 / 4;
        private JobHandle rebuilds;
        private bool disposed;

        /// <summary>Per order handle: the goal tile centre, for units off the route.</summary>
        public NativeArray<float2> Goal;
        /// <summary>Per order handle: 1 while the order is live.</summary>
        public NativeArray<byte> Live;
        /// <summary>Per order handle: 1 once any sector of the route has been published.</summary>
        public NativeArray<byte> Ready;
        /// <summary>Per order handle: units following it, counted by the movement stage each tick.</summary>
        public NativeArray<int> Followers;
        /// <summary>Per order handle (v0.16): its leading follower's distance to the goal, written by the movement stage.</summary>
        public NativeArray<float> Lead;
        /// <summary>(handle, tile index) pairs where a unit of a ready order found no direction.</summary>
        public NativeQueue<int2> RouteMisses;

        public OrderBook(in MapGrid map)
        {
            this.map = map;
            cache = new SectorFieldCache(map, Allocator.Persistent, cellSize: 1);
            table = cache.CreateTable(Allocator.Persistent);
            Goal = new NativeArray<float2>(OrderFieldTable.MaxOrders, Allocator.Persistent);
            Live = new NativeArray<byte>(OrderFieldTable.MaxOrders, Allocator.Persistent);
            Ready = new NativeArray<byte>(OrderFieldTable.MaxOrders, Allocator.Persistent);
            Followers = new NativeArray<int>(OrderFieldTable.MaxOrders, Allocator.Persistent);
            Lead = new NativeArray<float>(OrderFieldTable.MaxOrders, Allocator.Persistent);
            RouteMisses = new NativeQueue<int2>(Allocator.Persistent);
        }

        /// <summary>The flow-field cache behind the orders.</summary>
        public SectorFieldCache Cache => cache;

        /// <summary>The job-readable directions table.</summary>
        public OrderFieldTable Table => table;

        /// <summary>Live orders.</summary>
        public int LiveCount => issuedTick.Count;

        /// <summary>
        /// Creates (or joins) the order for a goal and size class and returns its handle, or -1 when the
        /// table is full. <paramref name="startTiles"/> (tile indices of the ordered units) decide which
        /// sectors the route covers first. An <paramref name="owner"/> already holding
        /// <see cref="MaxOrdersPerOwner"/> other orders is refused with -1, or
        /// <see cref="MaxBackgroundOrdersPerOwner"/> for an order the player didn't give.
        /// </summary>
        public int Issue(int2 goal, int sizeClass, NativeArray<int> startTiles, int tick, int owner, bool background = false)
        {
            goal = math.clamp(goal, 0, new int2(cache.Width - 1, cache.Height - 1));
            int2 from = startTiles.Length > 0 ? new int2(startTiles[0] % cache.Width, startTiles[0] / cache.Width) : goal;
            if (!SnapGoal(map, goal, from, GoalSnapRadius, out goal)) return -1;
            int handle = cache.Acquire(goal, sizeClass, 1, startTiles);
            if (handle >= OrderFieldTable.MaxOrders)
            {
                cache.Release(handle, 1);
                return -1;
            }
            if (!ordersByOwner.TryGetValue(owner, out HashSet<int> held)) ordersByOwner[owner] = held = new HashSet<int>();
            if (!held.Contains(handle) && held.Count >= (background ? MaxBackgroundOrdersPerOwner : MaxOrdersPerOwner))
            {
                cache.Release(handle, 1);
                return -1;
            }
            held.Add(handle);
            if (issuedTick.ContainsKey(handle))
            {
                cache.Release(handle, 1); // joining a live order: keep one reference per order
                cache.AddStartCells(handle, startTiles);
            }
            issuedTick[handle] = tick;
            Goal[handle] = (float2)goal + 0.5f;
            Live[handle] = 1;
            return handle;
        }

        /// <summary>True when a unit of the size class at <paramref name="from"/> can walk to <paramref name="to"/>.</summary>
        public bool Reaches(int sizeClass, int2 from, int2 to) => cache.Graph.Reaches(sizeClass, from, to);

        /// <summary>The pocket of floor a tile is in for the class (-1 for none): units in one pocket reach the same places.</summary>
        public int PocketOf(int sizeClass, int2 tile) => cache.Graph.PocketOf(sizeClass, tile);

        /// <summary>True when two pockets of the class are joined by floor.</summary>
        public bool Connects(int sizeClass, int a, int b) => cache.Graph.Connects(sizeClass, a, b);

        /// <summary>
        /// The goal itself when it is walkable, else the nearest walkable tile within <paramref name="radius"/>
        /// (ties go to the one nearest <paramref name="from"/>, the side the units come from). False when there
        /// is none, so an order onto unknown rock never sends units wandering after an unreachable goal.
        /// </summary>
        public static bool SnapGoal(in MapGrid map, int2 goal, int2 from, int radius, out int2 snapped)
        {
            snapped = goal;
            if (map.IsWalkable(goal)) return true;
            int best = int.MaxValue, bestFrom = int.MaxValue;
            for (int r = 1; r <= radius; r++)
            {
                for (int y = goal.y - r; y <= goal.y + r; y++)
                for (int x = goal.x - r; x <= goal.x + r; x++)
                {
                    if (math.max(math.abs(x - goal.x), math.abs(y - goal.y)) != r) continue; // this ring only
                    var t = new int2(x, y);
                    if (!map.IsWalkable(t)) continue;
                    int2 dg = t - goal, dfrom = t - from;
                    int d = dg.x * dg.x + dg.y * dg.y, df = dfrom.x * dfrom.x + dfrom.y * dfrom.y;
                    if (d < best || (d == best && df < bestFrom)) { best = d; bestFrom = df; snapped = t; }
                }
                if (best <= r * r) return true; // no tile on a later ring can be nearer
            }
            return best != int.MaxValue;
        }

        /// <summary>
        /// The boundary's flow pipeline: complete last tick's rebuilds and publish them, apply the queued
        /// footprint changes (<see cref="MapStore.QueueUsed"/>) and the terrain changes they make, retire
        /// orders with no followers and extend routes to sectors units wandered into.
        /// </summary>
        public void AtBoundary(MapStore map, int tick)
        {
            cache.CompleteRebuilds(ref table);
            rebuilds = default;

            map.ApplyPendingUsed(); // footprints placed or freed since the last boundary
            if (map.ChangedTiles.Count > 0) cache.Invalidate(map.ChangedTiles);
            map.ChangedTiles.Clear();

            var dead = new List<int>();
            foreach (KeyValuePair<int, int> pair in issuedTick)
                if (Followers[pair.Key] == 0 && pair.Value < tick) dead.Add(pair.Key);
            foreach (int handle in dead)
            {
                issuedTick.Remove(handle);
                ForgetHandle(handle);
                cache.Release(handle, 1);
                cache.Unpublish(ref table, handle);
                Live[handle] = 0;
                Ready[handle] = 0;
            }

            missSeen.Clear();
            var missed = new NativeArray<int>(1, Allocator.Temp);
            while (RouteMisses.TryDequeue(out int2 miss))
            {
                if (Live[miss.x] == 0 || !missSeen.Add(((long)miss.x << 32) | (uint)miss.y)) continue;
                missed[0] = miss.y;
                cache.AddStartCells(miss.x, missed);
            }
            missed.Dispose();

            foreach (int handle in issuedTick.Keys) Ready[handle] = cache.HasPublished(handle) ? (byte)1 : (byte)0;
        }

        private void ForgetHandle(int handle)
        {
            foreach (HashSet<int> held in ordersByOwner.Values) held.Remove(handle);
        }

        /// <summary>Schedules up to <paramref name="maxPerTick"/> rebuilds; the next boundary completes them.</summary>
        public void ScheduleRebuilds(int maxPerTick) => rebuilds = cache.ScheduleRebuilds(maxPerTick);

        /// <summary>Forgets every order (a new match).</summary>
        public void Clear()
        {
            rebuilds.Complete();
            cache.CompleteRebuilds(ref table);
            ordersByOwner.Clear();
            foreach (int handle in issuedTick.Keys)
            {
                cache.Release(handle, 1);
                cache.Unpublish(ref table, handle);
                Live[handle] = 0;
                Ready[handle] = 0;
            }
            issuedTick.Clear();
            while (RouteMisses.TryDequeue(out _)) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            rebuilds.Complete();
            cache.Dispose();
            table.Dispose();
            Goal.Dispose();
            Live.Dispose();
            Ready.Dispose();
            Followers.Dispose();
            Lead.Dispose();
            RouteMisses.Dispose();
        }
    }
}
