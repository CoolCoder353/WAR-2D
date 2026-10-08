using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace WAR2D.Sim
{
    public partial class SimCommandSystem
    {
        private readonly List<int>[] moversByClass = { new List<int>(), new List<int>() };

        /// <summary>
        /// Gives the owner's units (the listed ids, or every unit inside the box) a move order: one order
        /// (one flow field) per size class present. Ids that are not live or not the owner's are skipped.
        /// Reads the settled SoA, so it must run right after the boundary.
        /// </summary>
        partial void ApplyMove(SimData data, SimContext context, in SimCommand command, int unitCount)
        {
            foreach (List<int> list in moversByClass) list.Clear();
            if (command.Ids != null)
            {
                foreach (int id in command.Ids)
                {
                    int slot = data.SlotOf(id, unitCount);
                    if (slot < 0 || data.OwnerId[slot] != command.OwnerId || data.Health[slot] <= 0f) continue;
                    moversByClass[data.SizeClass[slot] & 1].Add(slot);
                }
            }
            else
            {
                int2 lo = math.min(command.BoxMin, command.BoxMax), hi = math.max(command.BoxMin, command.BoxMax) + 1;
                for (int slot = 0; slot < unitCount; slot++)
                {
                    float2 p = data.Positions[slot];
                    if (data.OwnerId[slot] != command.OwnerId || data.Health[slot] <= 0f) continue;
                    if (p.x < lo.x || p.y < lo.y || p.x >= hi.x || p.y >= hi.y) continue;
                    moversByClass[data.SizeClass[slot] & 1].Add(slot);
                }
            }

            int tick = SystemAPI.GetSingleton<SimClock>().Tick;
            for (int sizeClass = 0; sizeClass < moversByClass.Length; sizeClass++)
            {
                List<int> slots = moversByClass[sizeClass];
                if (slots.Count == 0) continue;
                var starts = new NativeArray<int>(slots.Count, Allocator.Temp);
                for (int i = 0; i < slots.Count; i++)
                {
                    int2 tile = math.clamp((int2)math.floor(data.Positions[slots[i]]), 0, new int2(data.Width - 1, data.Height - 1));
                    starts[i] = tile.y * data.Width + tile.x;
                }
                int handle = context.Orders.Issue(command.Tile, sizeClass, starts, tick);
                starts.Dispose();
                if (handle < 0) continue;
                NativeParallelHashMap<int, int> pending = data.PendingOrders;
                if (pending.Capacity < pending.Count() + slots.Count) pending.Capacity = pending.Count() + slots.Count + 1024;
                foreach (int slot in slots) pending[data.IdOf[slot]] = handle;
            }
        }
    }
}
