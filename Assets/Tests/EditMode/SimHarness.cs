using System;
using System.Linq;
using Config;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using WAR2D.Sim;
using WAR2D.World;

/// <summary>
/// Drives the simulation tick by hand in EditMode: a private world holding only the tick group and its
/// stages, with no rate manager, so each <see cref="Tick"/> runs exactly one tick.
/// </summary>
public sealed class SimHarness : IDisposable
{
    public readonly World World;
    public readonly SimTickGroup Group;
    public readonly SimContext Context;
    public readonly MapStore Map;
    public readonly GameConfigData Config;
    private double time;

    public const int OwnerA = 101, OwnerB = 202;

    public SimHarness(MapStore map, Action<GameConfigData> configure = null)
    {
        ConfigLoader.ResetForTests();
        Config = ConfigLoader.LoadConfig();
        configure?.Invoke(Config);
        Map = map;
        World = new World("SimTest");
        Group = World.GetOrCreateSystemManaged<SimTickGroup>();
        Group.RateManager = null;
        var stages = typeof(SimTickGroup).Assembly.GetTypes()
            .Where(t => t.Namespace == "WAR2D.Sim" && !t.IsAbstract
                        && (typeof(ComponentSystemBase).IsAssignableFrom(t) || typeof(ISystem).IsAssignableFrom(t))
                        && t.GetCustomAttributes(typeof(UpdateInGroupAttribute), false)
                            .Cast<UpdateInGroupAttribute>().Any(a => a.GroupType == typeof(SimTickGroup)));
        foreach (Type type in stages) Group.AddSystemToUpdateList(World.CreateSystem(type));
        Group.SortSystems();
        SimContext.RunningOverride = true;
        Context = SimContext.Create(World, map, Config);
        Context.WallsNeedSight = false;
    }

    /// <summary>A plain open map of the given size (with a border ring).</summary>
    public static MapStore OpenMap(int size)
    {
        var rows = new string[size];
        for (int r = 0; r < size; r++)
            rows[r] = r == 0 || r == size - 1 ? new string('B', size) : "B" + new string('.', size - 2) + "B";
        return MapStore.FromAscii(rows);
    }

    public EntityManager Em => World.EntityManager;

    /// <summary>Runs one tick.</summary>
    public void Tick()
    {
        float dt = Config.Simulation.TickSeconds;
        time += dt;
        World.SetTime(new TimeData(time, dt));
        Group.Update();
    }

    /// <summary>Runs ticks for the given number of simulated seconds.</summary>
    public void TickSeconds(float seconds)
    {
        int ticks = (int)math.ceil(seconds * Config.Simulation.TickRate);
        for (int i = 0; i < ticks; i++) Tick();
    }

    /// <summary>Queues a spawn and returns a box the new id lands in once applied.</summary>
    public int[] Spawn(int owner, float2 position, UnitType type = UnitType.Tank)
    {
        var id = new int[] { -1 };
        Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.SpawnUnit, OwnerId = owner, Position = position, UnitType = type, OnSpawned = v => id[0] = v });
        return id;
    }

    /// <summary>Queues a move order for the given unit ids.</summary>
    public void Move(int owner, int2 goal, params int[] ids) => Order(owner, OrderKind.Move, goal, ids);

    /// <summary>Queues an order of the given kind for the given unit ids.</summary>
    public void Order(int owner, OrderKind kind, int2 goal, params int[] ids) => Order(owner, kind, false, goal, ids);

    /// <summary>Queues an order of the given kind, Shift-queued when <paramref name="queue"/> is set.</summary>
    public void Order(int owner, OrderKind kind, bool queue, int2 goal, params int[] ids) =>
        Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.OrderUnits, Order = kind, Queue = queue, OwnerId = owner, Tile = goal, Ids = ids });

    /// <summary>Overwrites a unit's position (tests only; completes the tick's jobs first).</summary>
    public void Teleport(int id, float2 position)
    {
        Em.CompleteAllTrackedJobs();
        using var q = Em.CreateEntityQuery(typeof(Unit));
        using var entities = q.ToEntityArray(Allocator.Temp);
        foreach (Entity e in entities)
        {
            Unit u = Em.GetComponentData<Unit>(e);
            if (u.Id != id) continue;
            u.Position = position;
            Em.SetComponentData(e, u);
        }
    }

    /// <summary>Queues a building (placement and payment skipped) and returns its id.</summary>
    public int PlaceBuilding(int owner, BuildingType type, int2 anchor, bool complete = false)
    {
        int id = Context.Ids.Allocate();
        BuildingConfig config = Config.GetBuilding(type);
        Context.Commands.Enqueue(new SimCommand
        {
            Kind = SimCommandKind.CreateBuilding,
            OwnerId = owner,
            Building = new BuildingSpec { Data = new BuildingData { id = id, ownerId = owner, buildingType = type, position = (float2)anchor }, Config = config, Complete = complete },
        });
        if (type != BuildingType.Bomb) // a bomb does not mark its tile used: units walk over it
            foreach (int2 t in Footprint.Tiles(anchor, config.Size)) Map.QueueUsed(t, true);
        return id;
    }

    /// <summary>Queues a wall blueprint for the owner.</summary>
    public void PlaceWall(int owner, int2 tile) =>
        Context.Commands.Enqueue(new SimCommand { Kind = SimCommandKind.PlaceWall, OwnerId = owner, Tile = tile });

    /// <summary>Makes a placed wall blueprint a finished wall at full health, at once (completes jobs first).</summary>
    public void ForceBuiltWall(int2 tile)
    {
        Em.CompleteAllTrackedJobs();
        SimData data = Context.Data;
        int index = Map.Grid.Index(tile);
        Map.SetTile(tile, TileType.PlayerWall);
        data.RefreshTerrain(tile);
        data.WallState[index] = WallStates.Built;
        data.WallHealth[index] = data.WallMaxHealth;
        data.RebuildOpaqueSum();
    }

    /// <summary>The building entity with this id (completes the tick's jobs first).</summary>
    public Entity BuildingEntity(int id)
    {
        Em.CompleteAllTrackedJobs();
        using var q = Em.CreateEntityQuery(typeof(BuildingData));
        using var entities = q.ToEntityArray(Allocator.Temp);
        foreach (Entity e in entities) if (Em.GetComponentData<BuildingData>(e).id == id) return e;
        return Entity.Null;
    }

    /// <summary>Adds construction work to a building slot for the next boundary (what Builders do from Task 9).</summary>
    public void AddWork(int buildingId, float work)
    {
        Em.CompleteAllTrackedJobs();
        SimData data = Context.Data;
        int slot = data.BuildingSlotOfIndex[NetIdAllocator.IndexOf(buildingId)];
        data.BuildingWork[slot] += work;
    }

    /// <summary>Completes the tick's jobs (as a boundary would) and returns every unit.</summary>
    public Unit[] Units()
    {
        Em.CompleteAllTrackedJobs();
        using var q = Em.CreateEntityQuery(typeof(Unit));
        using NativeArray<Unit> units = q.ToComponentDataArray<Unit>(Allocator.Temp);
        return units.ToArray();
    }

    public Unit UnitById(int id) => Units().First(u => u.Id == id);

    public int UnitCount
    {
        get { using var q = Em.CreateEntityQuery(typeof(Unit)); return q.CalculateEntityCount(); }
    }

    public void Dispose()
    {
        Context.Dispose();
        World.Dispose();
        Map.Dispose();
        SimContext.RunningOverride = null;
        ConfigLoader.ResetForTests();
    }
}
