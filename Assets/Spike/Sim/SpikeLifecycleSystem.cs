using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// Stage 5: lifecycle. Destroys the units combat left at zero health and spawns replacements at
    /// each player's HQ, up to <see cref="SpikeSimRules.SpawnPerTick"/> a tick while the army is below
    /// its target, so every army stays around its starting size through the fight. Ids come from the
    /// monotonic counter the spawn job owns, so a destroyed unit's id is never handed out again.
    ///
    /// <para>The commands are recorded by jobs but played back on the main thread: at the end of the
    /// tick in sync mode (so nothing structural is pending when <see cref="SpikeSim.Tick"/> returns),
    /// or at the start of the next tick in async mode, where <see cref="SpikeSim"/> calls
    /// <see cref="PlaybackPending"/> after completing the previous tick, so a tick never waits on its
    /// own stages. When the group is timing its stages (sync bench runs), the group completes the
    /// tick's jobs stage by stage and then calls <see cref="PlaybackPending"/>, so this update only
    /// records. Playback is the reason this OnUpdate is not Burst compiled; the recording jobs
    /// are.</para>
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpikeTickGroup))]
    [UpdateAfter(typeof(SpikeMovementSystem))]
    public partial struct SpikeLifecycleSystem : ISystem
    {
        private EntityCommandBuffer pendingDeaths;
        private EntityCommandBuffer pendingSpawns;
        private bool hasPending;

        /// <summary>True while a tick's commands are recorded and waiting for playback.</summary>
        public bool HasPendingPlayback => hasPending;

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            hasPending = false;
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            if (!hasPending) return;
            pendingDeaths.Dispose();
            pendingSpawns.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            SpikeWorldData world = SystemAPI.GetSingleton<SpikeWorldData>();

            // Async mode's boundary is SpikeSim's: it completes the previous tick and calls
            // PlaybackPending before this update. If it did not, the commands are played back here so
            // the world stays consistent - which costs this tick a sync point.
            if (hasPending)
            {
                state.Dependency.Complete();
                Playback(state.EntityManager);
            }

            var deaths = new EntityCommandBuffer(Allocator.Persistent);
            var spawns = new EntityCommandBuffer(Allocator.Persistent);

            state.Dependency = new RecordDeathsJob { Deaths = deaths.AsParallelWriter() }.ScheduleParallel(state.Dependency);
            state.Dependency = new RecordSpawnsJob
            {
                Spawns = spawns,
                Archetype = world.UnitArchetype,
                NextId = world.NextId,
                IdCapacity = world.IdCapacity,
                Capacity = world.Capacity,
                Health = world.Health,
                Team = world.Team,
                Players = world.Players,
                SpawnPerTick = world.SpawnPerTick,
                SpawnTarget = world.SpawnTarget,
                SpawnTiles = world.SpawnTiles,
                SpawnBase = world.SpawnBase,
                SpawnCount = world.SpawnCount,
                SpawnCursor = world.SpawnCursor,
                SpawnHealth = world.SpawnHealth,
                SmallRadius = SpikeScenario.SmallRadius,
            }.Schedule(state.Dependency);

            pendingDeaths = deaths;
            pendingSpawns = spawns;
            hasPending = true;

            if (!world.Async && !world.Breakdown)
            {
                state.Dependency.Complete();
                Playback(state.EntityManager);
            }
        }

        /// <summary>
        /// Plays the recorded commands back and releases their buffers. The caller must have completed
        /// the jobs that recorded them first: <see cref="SpikeSim"/> does it at the tick boundary in
        /// async mode, this system at the end of the tick in sync mode.
        /// </summary>
        public void PlaybackPending(EntityManager entityManager) => Playback(entityManager);

        private void Playback(EntityManager entityManager)
        {
            if (!hasPending) return;
            pendingDeaths.Playback(entityManager);
            pendingSpawns.Playback(entityManager);
            pendingDeaths.Dispose();
            pendingSpawns.Dispose();
            hasPending = false;
        }
    }

    /// <summary>Records a destroy for every unit at or below zero health, keyed by its query index.</summary>
    [BurstCompile]
    public partial struct RecordDeathsJob : IJobEntity
    {
        public EntityCommandBuffer.ParallelWriter Deaths;

        private void Execute(Entity entity, in SpikeUnit unit, [EntityIndexInQuery] int index)
        {
            if (unit.Health <= 0f) Deaths.DestroyEntity(index, entity);
        }
    }

    /// <summary>
    /// Records the tick's spawns: for each player, up to the per-tick cap while the army is below its
    /// target, on the next free tile of that player's spawn spiral, with the next id.
    /// </summary>
    [BurstCompile]
    public struct RecordSpawnsJob : IJob
    {
        public EntityCommandBuffer Spawns;
        public EntityArchetype Archetype;
        public NativeArray<int> NextId;
        public int IdCapacity;
        [ReadOnly] public NativeArray<float> Health;
        [ReadOnly] public NativeArray<byte> Team;
        public int Capacity, Players, SpawnPerTick, SpawnTarget;
        [ReadOnly] public NativeArray<int2> SpawnTiles;
        [ReadOnly] public NativeArray<int> SpawnBase;
        [ReadOnly] public NativeArray<int> SpawnCount;
        public NativeArray<int> SpawnCursor;
        public float SpawnHealth;
        public float SmallRadius;

        public void Execute()
        {
            if (SpawnPerTick <= 0 || SpawnTarget <= 0) return;

            // One pass over the SoA counts the living; it is cheaper than counting in the parallel
            // gather, which would need an atomic per unit on one shared counter per player.
            var alive = new NativeArray<int>(Players, Allocator.Temp);
            for (int i = 0; i < Capacity; i++)
            {
                byte team = Team[i];
                if (Health[i] > 0f && team < Players) alive[team]++;
            }

            for (int player = 0; player < Players; player++)
            {
                int count = SpawnCount[player];
                int baseIndex = SpawnBase[player];
                if (count <= 0 || baseIndex + count > SpawnTiles.Length) continue;

                int spawns = math.min(SpawnPerTick, SpawnTarget - alive[player]);
                for (int k = 0; k < spawns; k++)
                {
                    int id = NextId[0];
                    if (id >= IdCapacity) break;
                    NextId[0] = id + 1;

                    int cursor = SpawnCursor[player];
                    SpawnCursor[player] = cursor + 1;
                    int2 tile = SpawnTiles[baseIndex + cursor % count];

                    Entity entity = Spawns.CreateEntity(Archetype);
                    Spawns.SetComponent(entity, new SpikeUnit
                    {
                        Id = id,
                        Owner = (byte)player,
                        Team = (byte)player,
                        SizeClass = 0,
                        Radius = SmallRadius,
                        Health = SpawnHealth,
                        Position = (float2)tile + new float2(0.5f),
                        Velocity = float2.zero,
                        TargetId = -1,
                        Cooldown = 0f,
                        FieldGoal = -1,
                    });
                }
            }

            alive.Dispose();
        }
    }
}
