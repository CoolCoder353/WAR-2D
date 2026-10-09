using System;
using System.Collections.Generic;
using Config;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.World;

namespace WAR2D.Sim
{
    /// <summary>
    /// The managed half of one match's simulation: the id allocator, the command queue, owner slots and
    /// the callbacks the tick raises on the main thread. It creates the <see cref="SimData"/> and
    /// <see cref="SimClock"/> singletons in its world and frees them on <see cref="Dispose"/>.
    /// The server creates one in <c>WorldStateManager.OnStartServer</c>; tests create their own.
    /// </summary>
    public sealed class SimContext : IDisposable
    {
        /// <summary>The live context, or null.</summary>
        public static SimContext Current { get; private set; }

        /// <summary>Test hook: when set, the tick runs (or not) regardless of the network and game state.</summary>
        public static bool? RunningOverride;

        private readonly Dictionary<int, byte> slotOfOwner = new Dictionary<int, byte>();
        private Entity singleton;
        private bool disposed;

        /// <summary>The world the simulation lives in.</summary>
        public Unity.Entities.World World { get; }

        /// <summary>Network ids for units and buildings.</summary>
        public NetIdAllocator Ids { get; }

        /// <summary>Main-thread requests, applied at the next boundary.</summary>
        public SimCommandQueue Commands { get; } = new SimCommandQueue();

        /// <summary>The map the units move on.</summary>
        public MapStore Map { get; }

        /// <summary>The live move orders and their flow fields.</summary>
        public OrderBook Orders { get; }

        /// <summary>
        /// Who attacks and shares vision with whom, per owner slot. Starts from the teams as each owner
        /// gets a slot; changed only by <see cref="SimCommandKind.SetAttack"/> and
        /// <see cref="SimCommandKind.SetShareVision"/> at the boundary.
        /// </summary>
        public Diplomacy Diplomacy { get; } = new Diplomacy();

        /// <summary>Raised at the boundary after a diplomacy command changed a mask: (from owner, to owner).</summary>
        public event Action<int, int> DiplomacyChanged;

        /// <summary>Shift-queued waypoints per unit (main thread, boundary only).</summary>
        public WaypointBook Waypoints { get; }

        /// <summary>The config the sim was built from.</summary>
        public GameConfigData Config { get; }

        /// <summary>Resources an owner has for upkeep; null means unlimited (tests).</summary>
        public Func<int, float> BudgetOf;

        /// <summary>
        /// The team of an owner id, read when the owner first gets a slot (teams are fixed for a match).
        /// Null means every owner is on a team of its own (<see cref="SimData.TeamOfOwner"/>).
        /// </summary>
        public Func<int, int> TeamResolver;

        /// <summary>The team of an owner id: <see cref="TeamResolver"/>, or a team of its own.</summary>
        public int TeamOf(int ownerId) => TeamResolver != null ? TeamResolver(ownerId) : -ownerId - 1;

        /// <summary>Takes resources an owner's units spent on upkeep; null means nothing is taken (tests).</summary>
        public Action<int, float> Spend;

        /// <summary>Raised at a boundary for each unit that died: (id, position).</summary>
        public event Action<int, float2> UnitDied;

        /// <summary>
        /// Raised at every boundary once the previous tick is settled (jobs complete, dead removed, before
        /// this tick's commands): the replication layer reads the SoA here. Arguments: data, tick, unit count.
        /// </summary>
        public event Action<SimData, int, int> Settled;

        /// <summary>Raised when a building entity was created: (id, entity).</summary>
        public event Action<int, Entity> BuildingCreated;

        private SimContext(Unity.Entities.World world, MapStore map, GameConfigData config)
        {
            World = world;
            Map = map;
            Config = config;
            Ids = new NetIdAllocator(config.Simulation.MaxEntities);
            SimData data = SimData.Create(map.Grid, config, Allocator.Persistent);
            Orders = new OrderBook(map.Grid);
            Waypoints = new WaypointBook(config.Orders.MaxQueued);
            UnitDied += (id, _) => Waypoints.Forget(id);
            data.Orders = Orders.Table;
            data.OrderGoal = Orders.Goal;
            data.OrderLive = Orders.Live;
            data.OrderReady = Orders.Ready;
            data.OrderFollowers = Orders.Followers;
            data.RouteMisses = Orders.RouteMisses;
            singleton = world.EntityManager.CreateEntity(typeof(SimData), typeof(SimClock));
            world.EntityManager.SetComponentData(singleton, data);
            world.EntityManager.SetComponentData(singleton, new SimClock { Dt = config.Simulation.TickSeconds });
            world.EntityManager.SetName(singleton, "SimData");
        }

        /// <summary>Creates the match's simulation in <paramref name="world"/>. Disposes any earlier context.</summary>
        public static SimContext Create(Unity.Entities.World world, MapStore map, GameConfigData config)
        {
            Current?.Dispose();
            Current = new SimContext(world, map, config);
            return Current;
        }

        /// <summary>The owner's slot, assigning the next free one on first use. -1 when every slot is taken.</summary>
        public int SlotOf(int ownerId)
        {
            if (slotOfOwner.TryGetValue(ownerId, out byte slot)) return slot;
            if (slotOfOwner.Count >= SimData.MaxOwners) return -1;
            var used = new bool[SimData.MaxOwners];
            foreach (byte s in slotOfOwner.Values) used[s] = true;
            for (byte s = 0; s < SimData.MaxOwners; s++)
            {
                if (used[s]) continue;
                slotOfOwner[ownerId] = s;
                SimData data = Data;
                data.OwnerIdBySlot[s] = ownerId;
                int team = TeamOf(ownerId);
                data.TeamBySlot[s] = team;
                var others = new List<(int, int)>();
                for (int t = 0; t < SimData.MaxOwners; t++)
                    if (t != s && data.OwnerIdBySlot[t] != 0) others.Add((t, data.TeamBySlot[t]));
                Diplomacy.Join(s, team, others);
                return s;
            }
            return -1;
        }

        /// <summary>The owner's slot, without assigning one.</summary>
        public bool TrySlotOf(int ownerId, out int slot)
        {
            bool found = slotOfOwner.TryGetValue(ownerId, out byte s);
            slot = found ? s : -1;
            return found;
        }

        /// <summary>
        /// The owner's vision grid (its slot), giving the owner a slot first. -1 when every slot is taken.
        /// The grid holds what the owner sees, plus what every player sharing vision with it sees.
        /// </summary>
        public int VisionOf(int ownerId) => SlotOf(ownerId);

        /// <summary>Owner ids that hold a slot.</summary>
        public IEnumerable<int> Owners => slotOfOwner.Keys;

        /// <summary>The singleton's arrays. Only read them on the main thread right after a boundary.</summary>
        public SimData Data => World.EntityManager.GetComponentData<SimData>(singleton);

        /// <summary>The tick clock.</summary>
        public SimClock Clock => World.EntityManager.GetComponentData<SimClock>(singleton);

        internal void RaiseUnitDied(int id, float2 position) => UnitDied?.Invoke(id, position);

        internal void RaiseSettled(SimData data, int tick, int count) => Settled?.Invoke(data, tick, count);

        internal void RaiseDiplomacyChanged(int fromOwner, int toOwner) => DiplomacyChanged?.Invoke(fromOwner, toOwner);

        internal void RaiseBuildingCreated(int id, Entity entity) => BuildingCreated?.Invoke(id, entity);

        /// <summary>Completes the tick's jobs and frees the singletons and their arrays.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Current == this) Current = null;
            if (World == null || !World.IsCreated) { Orders.Dispose(); return; }
            EntityManager em = World.EntityManager;
            em.CompleteAllTrackedJobs();
            foreach (ComponentSystemBase system in World.Systems)
                if (system is SimCommandSystem commands) commands.ResetForNewMatch();
            if (em.Exists(singleton))
            {
                em.GetComponentData<SimData>(singleton).Dispose();
                em.DestroyEntity(singleton);
            }
            using EntityQuery units = em.CreateEntityQuery(typeof(Unit));
            em.DestroyEntity(units);
            Orders.Dispose();
        }
    }
}
