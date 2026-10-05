using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WAR2D.Spike
{
    /// <summary>
    /// The spike's team mapping, which it must state because the simulation's <c>Team</c> field is the
    /// player index: free-for-all has eight teams (team = player index), and the 4v4 mode has two
    /// (team = player / 4, so players 0-3 and 4-7). <c>-teams</c> selects the mode; the scenario always
    /// has eight players either way.
    /// </summary>
    public static class FogTeams
    {
        /// <summary>Teams in free-for-all: one per player.</summary>
        public const int FfaTeams = 8;

        /// <summary>Teams in the 4v4 mode: players 0-3 and players 4-7.</summary>
        public const int FourVFourTeams = 2;

        /// <summary>The team count a run's <c>-teams</c> value asks for: 2 is 4v4, anything else is FFA.</summary>
        public static int Count(int teamsArgument) => teamsArgument == FourVFourTeams ? FourVFourTeams : FfaTeams;

        /// <summary>The team a player index belongs to under <paramref name="teams"/> teams.</summary>
        public static byte Of(byte player, int teams) => teams == FourVFourTeams ? (byte)(player >> 2) : player;
    }

    /// <summary>One deduplicated vision source: the tile it stands on and the largest radius cast from it.</summary>
    public struct VisionSource
    {
        /// <summary>The source's tile.</summary>
        public int2 Tile;

        /// <summary>The largest radius of the sources deduplicated onto that tile, in tiles.</summary>
        public int Radius;
    }

    /// <summary>
    /// Collects one team's vision sources from the simulation's SoA arrays and deduplicates them by tile.
    /// One <see cref="IJob"/> per team, all teams scheduleable in parallel: each team has its own
    /// scratch grid and its own source list, so the jobs share nothing.
    ///
    /// <para>Per tile the largest radius wins, because a larger disc is a superset of a smaller one from
    /// the same tile. The first source on a tile is appended to <see cref="Sources"/> and the scratch
    /// grid carries the radius until the end of the job, when the radii are copied out and every touched
    /// tile is cleared - the grid must be all zero again for the next update.</para>
    /// </summary>
    [BurstCompile]
    public struct CollectVisionSourcesJob : IJob
    {
        /// <summary>Unit slots: positions, team (the sim's <c>Team</c>, i.e. the player index) and health.</summary>
        [ReadOnly] public NativeArray<float2> Positions;
        [ReadOnly] public NativeArray<byte> Team;
        [ReadOnly] public NativeArray<float> Health;

        /// <summary>
        /// Unit slots to walk. The SoA is dense over the living, so the tail at and past the live count
        /// keeps the previous tick's values: a slot whose unit died reads as dead and is skipped, but one
        /// whose unit merely moved to a lower slot can pass for a live unit for a tick - the sim bench's
        /// stale-slot note - so a handful of phantom sources can come from the tail.
        /// </summary>
        public int Capacity;

        /// <summary>Building markers: positions, owner (player index) and health. The scenario's arrays.</summary>
        [ReadOnly] public NativeArray<float2> BuildingPositions;
        [ReadOnly] public NativeArray<byte> BuildingOwners;
        [ReadOnly] public NativeArray<float> BuildingHealth;
        public int BuildingCount;

        /// <summary>Teams in the run: 8 for FFA, 2 for 4v4. See <see cref="FogTeams"/>.</summary>
        public int Teams;

        /// <summary>The team this job collects for.</summary>
        public int TeamIndex;

        /// <summary>Vision radius of a unit, in tiles.</summary>
        public int UnitVision;

        /// <summary>Vision radius of a building, in tiles (the plan's <c>-bvision</c>, default 10).</summary>
        public int BuildingVision;

        /// <summary>The map's dimensions, so a source outside the map is dropped.</summary>
        public int Width, Height;

        /// <summary>
        /// This team's dedup grid, one byte per tile: 0 means "no source on this tile", else the largest
        /// radius on it. It must be all zero when the job runs; the job clears every tile it set, so it is
        /// left all zero again (an exception thrown out of the job would leave it dirty).
        /// </summary>
        public NativeArray<byte> Scratch;

        /// <summary>This team's source list. The caller clears it before scheduling; first-seen tiles append.</summary>
        public NativeList<VisionSource> Sources;

        /// <summary>One int the job writes: how many individual sources (units and buildings) were examined.</summary>
        public NativeArray<int> RawCount;

        public void Execute()
        {
            int raw = 0;
            for (int i = 0; i < Capacity; i++)
            {
                if (Health[i] <= 0f || FogTeams.Of(Team[i], Teams) != TeamIndex) continue;
                raw++;
                Add(Positions[i], UnitVision);
            }
            for (int b = 0; b < BuildingCount; b++)
            {
                if (BuildingHealth[b] <= 0f || FogTeams.Of(BuildingOwners[b], Teams) != TeamIndex) continue;
                raw++;
                Add(BuildingPositions[b], BuildingVision);
            }
            RawCount[0] = raw;

            for (int s = 0; s < Sources.Length; s++)
            {
                VisionSource source = Sources[s];
                int index = source.Tile.y * Width + source.Tile.x;
                Sources[s] = new VisionSource { Tile = source.Tile, Radius = Scratch[index] };
                Scratch[index] = 0;
            }
        }

        /// <summary>
        /// Records a source on the tile the position floors to, keeping the larger radius. The radius is
        /// held in the scratch grid's byte, so it is clamped to 1..255 (0 means "no source on this tile").
        /// </summary>
        private void Add(float2 position, int radius)
        {
            int2 tile = (int2)math.floor(position);
            if ((uint)tile.x >= (uint)Width || (uint)tile.y >= (uint)Height) return;
            int index = tile.y * Width + tile.x;
            byte value = (byte)math.clamp(radius, 1, 255);
            if (Scratch[index] >= value) return;
            if (Scratch[index] == 0) Sources.Add(new VisionSource { Tile = tile });
            Scratch[index] = value;
        }
    }

    /// <summary>
    /// Casts every source of one team into that team's visibility grid, one source a thread.
    /// </summary>
    /// <remarks>
    /// <see cref="Visible"/> is shared by every iteration, which the parallel-for rules would normally
    /// forbid. It is safe here because <see cref="Shadowcast.Cast"/> only ever writes the value 1 - two
    /// sources marking the same tile write the same byte, so the races are benign. Nothing else may
    /// write that grid without giving up this invariant.
    /// </remarks>
    [BurstCompile]
    public struct FogCastJob : IJobParallelFor
    {
        /// <summary>The team's deduplicated sources.</summary>
        [ReadOnly] public NativeList<VisionSource> Sources;

        /// <summary>The map's tiles: <see cref="SpikeMap.Floor"/> (0) passes sight, anything else blocks.</summary>
        [ReadOnly] public NativeArray<byte> Tiles;

        /// <summary>The map's dimensions.</summary>
        public int Width, Height;

        /// <summary>The grid being filled; every write stores 1 (see the remarks).</summary>
        [NativeDisableParallelForRestriction] public NativeArray<byte> Visible;

        public void Execute(int index)
        {
            VisionSource source = Sources[index];
            Shadowcast.Cast(source.Tile, source.Radius, Width, Height, Tiles, ref Visible);
        }
    }

    /// <summary>
    /// The per-cell half of a team's fog update, parallel over blocks of cells: <c>Explored |= Visible</c>,
    /// and a flag per cell recording whether its visibility flipped since the team's last update. Each
    /// block also counts its changed cells, which is what the compaction needs.
    /// </summary>
    [BurstCompile]
    public struct FogDiffJob : IJobParallelFor
    {
        /// <summary>The visibility this update computed, and the one the previous update left.</summary>
        [ReadOnly] public NativeArray<byte> Visible;
        [ReadOnly] public NativeArray<byte> Previous;

        /// <summary>
        /// Every cell this team has ever seen: the union of all its updates. Cells are written at their
        /// own index, not the block's, so the parallel-for restriction is lifted on the per-cell arrays.
        /// </summary>
        [NativeDisableParallelForRestriction] public NativeArray<byte> Explored;

        /// <summary>One byte per cell: 1 where the visibility flipped this update.</summary>
        [NativeDisableParallelForRestriction] public NativeArray<byte> Flags;

        /// <summary>Cells in the grid, and the number of cells one block covers.</summary>
        public int Cells, BlockSize;

        /// <summary>Changed cells per block, for the compaction's prefix sum.</summary>
        public NativeArray<int> BlockCount;

        public void Execute(int block)
        {
            int start = block * BlockSize;
            int end = math.min(start + BlockSize, Cells);
            int count = 0;
            for (int cell = start; cell < end; cell++)
            {
                byte visible = Visible[cell];
                if (visible != 0) Explored[cell] = 1;
                byte changed = (byte)(visible != Previous[cell] ? 1 : 0);
                Flags[cell] = changed;
                count += changed;
            }
            BlockCount[block] = count;
        }
    }

    /// <summary>
    /// The compaction: every block copies the cells its <see cref="FogDiffJob"/> flagged into the team's
    /// changed list, at the offset its block's count placed it. Cell indices end up in ascending order.
    /// </summary>
    [BurstCompile]
    public struct FogScatterJob : IJobParallelFor
    {
        /// <summary>The flags the diff wrote, one byte per cell.</summary>
        [ReadOnly] public NativeArray<byte> Flags;

        /// <summary>Where each block's run starts in <see cref="Changed"/>.</summary>
        [ReadOnly] public NativeArray<int> BlockOffset;

        /// <summary>The changed cells, in ascending order; a block writes its run wherever its offset is.</summary>
        [NativeDisableParallelForRestriction] public NativeArray<int> Changed;

        /// <summary>Cells in the grid, and the number of cells one block covers.</summary>
        public int Cells, BlockSize;

        public void Execute(int block)
        {
            int start = block * BlockSize;
            int end = math.min(start + BlockSize, Cells);
            int write = BlockOffset[block];
            for (int cell = start; cell < end; cell++)
                if (Flags[cell] != 0) Changed[write++] = cell;
        }
    }

    /// <summary>Inputs for <see cref="FogSystem"/>.</summary>
    public struct FogConfig
    {
        /// <summary>Teams in the run: 8 (FFA) or 2 (4v4). See <see cref="FogTeams"/>.</summary>
        public int Teams;

        /// <summary>Times a second every team is refreshed; the plan's 5 Hz.</summary>
        public int Hz;

        /// <summary>Vision radius of a unit, in tiles.</summary>
        public int UnitVision;

        /// <summary>Vision radius of a building, in tiles.</summary>
        public int BuildingVision;

        /// <summary>The plan's defaults: 8 teams at 5 Hz, units seeing 8 tiles, buildings 10.</summary>
        public static FogConfig Defaults => new FogConfig
        {
            Teams = FogTeams.FfaTeams,
            Hz = 5,
            UnitVision = 8,
            BuildingVision = 10,
        };
    }

    /// <summary>
    /// The spike's line-of-sight fog: per team a <see cref="Visible"/> grid (what the team sees now), an
    /// <see cref="Explored"/> grid (everything it has ever seen) and a <see cref="Changed"/> list (the
    /// cells whose visibility flipped in that team's last update, in ascending cell order).
    ///
    /// <para><b>Cadence.</b> <see cref="Tick"/> is one 20 Hz tick and refreshes <c>Teams / 4</c> teams,
    /// round-robin, so every team refreshes every four ticks - 5 Hz. Eight teams therefore take two teams
    /// a tick, and two teams take one team every other tick.</para>
    ///
    /// <para><b>A team's update</b> clears the grid the cast will write into, collects the team's
    /// deduplicated sources (units of the team plus its buildings, largest radius per tile), casts them
    /// in parallel, diffs the result against the team's current visibility (<c>Explored |= Visible</c>,
    /// changed cells) and then trades the two grids' roles: the grid just computed becomes the team's
    /// visibility (what <see cref="Visible"/> returns) and the comparison for the next update, and the
    /// grid it was compared against becomes the spare the next update clears and writes into. The
    /// per-team update is synchronous: when it returns, its jobs have run.</para>
    ///
    /// <para><b>Ownership.</b> The tile grid, the unit SoA arrays and the building arrays belong to the
    /// caller and are shared, not copied; the caller must keep them alive while the system is used. The
    /// grids and lists are allocated with the constructor's allocator and freed by <see cref="Dispose"/>.
    /// A team update is synchronous: when <see cref="Tick"/> returns, its jobs are complete.</para>
    /// </summary>
    public sealed class FogSystem : IDisposable
    {
        /// <summary>Cells one diff/compaction block covers.</summary>
        private const int BlockSize = 1024;

        /// <summary>Sources one parallel-for batch of the cast covers.</summary>
        private const int CastBatch = 32;

        /// <summary>The spike's tick rate: a team's refresh budget is counted in twentieths of a second.</summary>
        private const int TickRate = 20;

        private readonly FogConfig config;
        private readonly NativeArray<byte> tiles;
        private readonly int width, height, cells, teams, blocks;
        private readonly NativeArray<byte>[] current, spare, explored, scratch;
        private readonly NativeList<VisionSource>[] sources;
        private readonly NativeArray<int>[] rawCount;
        private readonly NativeList<int>[] changed;
        private readonly NativeArray<byte> flags;
        private readonly NativeArray<int> blockCount;
        private NativeArray<int> blockOffset; // not readonly: the main thread writes the prefix sums into it
        private readonly NativeArray<byte> empty;
        private readonly double[] teamMilliseconds;
        private readonly int[] lastTeams;
        private readonly Stopwatch stopwatch = new Stopwatch();

        private NativeArray<float2> unitPositions;
        private NativeArray<byte> unitTeam;
        private NativeArray<float> unitHealth;
        private int unitCapacity;
        private NativeArray<float2> buildingPositions;
        private NativeArray<byte> buildingOwners;
        private NativeArray<float> buildingHealth;
        private int buildingCount;

        /// <summary>True while the building arrays are this system's own zero-length placeholders.</summary>
        private bool buildingsArePlaceholders;

        private int budget, cursor, tick;
        private bool disposed;

        /// <summary>Teams the last <see cref="Tick"/> refreshed; 0 on a tick that refreshed none.</summary>
        public int LastTickTeams { get; private set; }

        /// <summary>The <paramref name="index"/>-th team the last <see cref="Tick"/> refreshed, in order.</summary>
        public int LastTickTeam(int index) => lastTeams[index];

        /// <summary>Ticks run so far.</summary>
        public int Ticks => tick;

        /// <summary>Teams in this run.</summary>
        public int Teams => teams;

        /// <summary>
        /// Allocates one visibility, one explored and one scratch grid per team, plus the shared diff and
        /// compaction scratch. <paramref name="tiles"/> is the map's grid, <c>y * width + x</c>, the same
        /// array <see cref="Shadowcast.Cast"/> reads; the caller keeps owning it.
        /// </summary>
        public FogSystem(in FogConfig config, int width, int height, NativeArray<byte> tiles, Allocator allocator)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            this.config = config;
            this.tiles = tiles;
            this.width = width;
            this.height = height;
            cells = width * height;
            teams = math.max(1, config.Teams);
            blocks = (cells + BlockSize - 1) / BlockSize;

            int sourceCapacity = 64; // grown by Reserve when the unit and building arrays are bound
            current = new NativeArray<byte>[teams];
            spare = new NativeArray<byte>[teams];
            explored = new NativeArray<byte>[teams];
            scratch = new NativeArray<byte>[teams];
            sources = new NativeList<VisionSource>[teams];
            rawCount = new NativeArray<int>[teams];
            changed = new NativeList<int>[teams];
            teamMilliseconds = new double[teams];
            lastTeams = new int[teams];
            for (int team = 0; team < teams; team++)
            {
                current[team] = new NativeArray<byte>(cells, allocator);
                spare[team] = new NativeArray<byte>(cells, allocator);
                explored[team] = new NativeArray<byte>(cells, allocator);
                scratch[team] = new NativeArray<byte>(cells, allocator);
                sources[team] = new NativeList<VisionSource>(sourceCapacity, allocator);
                rawCount[team] = new NativeArray<int>(1, allocator);
                changed[team] = new NativeList<int>(1024, allocator);
            }
            flags = new NativeArray<byte>(cells, allocator);
            blockCount = new NativeArray<int>(blocks, allocator);
            blockOffset = new NativeArray<int>(blocks, allocator);
            empty = new NativeArray<byte>(cells, allocator);

            // A run may have no buildings at all, but a job still needs valid containers, so start with
            // empty ones and drop them when SetBuildings binds the caller's.
            buildingPositions = new NativeArray<float2>(0, allocator);
            buildingOwners = new NativeArray<byte>(0, allocator);
            buildingHealth = new NativeArray<float>(0, allocator);
            buildingsArePlaceholders = true;
        }

        /// <summary>
        /// Binds the simulation's unit SoA arrays: the first <paramref name="capacity"/> slots are walked,
        /// dead slots (health 0) skipped and the stale tail's phantoms accepted, as
        /// <see cref="CollectVisionSourcesJob.Capacity"/> describes. The sim's <c>Team</c> is the player
        /// index (see <see cref="FogTeams"/>), so the arrays must stay valid for as long as this system
        /// is used.
        /// </summary>
        public void SetUnits(NativeArray<float2> positions, NativeArray<byte> team, NativeArray<float> health, int capacity)
        {
            unitPositions = positions;
            unitTeam = team;
            unitHealth = health;
            unitCapacity = math.max(0, capacity);
            Reserve();
        }

        /// <summary>Binds the scenario's building markers: position, owner (player index) and health.</summary>
        public void SetBuildings(NativeArray<float2> positions, NativeArray<byte> owners, NativeArray<float> health, int count)
        {
            if (buildingsArePlaceholders)
            {
                buildingPositions.Dispose();
                buildingOwners.Dispose();
                buildingHealth.Dispose();
                buildingsArePlaceholders = false;
            }
            buildingPositions = positions;
            buildingOwners = owners;
            buildingHealth = health;
            buildingCount = math.max(0, count);
            Reserve();
        }

        /// <summary>
        /// Sizes every team's source list for the most sources a run can produce - one per unit slot plus
        /// one per building - so the collector never has to grow a list while it runs.
        /// </summary>
        private void Reserve()
        {
            int needed = unitCapacity + buildingCount;
            if (needed <= 0) return;
            for (int team = 0; team < teams; team++)
                if (sources[team].Capacity < needed) sources[team].Capacity = needed;
        }

        /// <summary>A team's current visibility: 1 where the team sees the tile.</summary>
        public NativeArray<byte> Visible(int team) => current[team];

        /// <summary>Everything <paramref name="team"/> has ever seen: 1 where the tile is explored.</summary>
        public NativeArray<byte> Explored(int team) => explored[team];

        /// <summary>
        /// The cells whose visibility flipped in <paramref name="team"/>'s last update, ascending. It is
        /// replaced on that team's next update, so a consumer must read it before then.
        /// </summary>
        public NativeList<int> Changed(int team) => changed[team];

        /// <summary>Deduplicated vision sources in <paramref name="team"/>'s last update.</summary>
        public int SourceCount(int team) => sources[team].Length;

        /// <summary>Individual sources (units and buildings) examined in <paramref name="team"/>'s last update.</summary>
        public int RawSourceCount(int team) => rawCount[team][0];

        /// <summary>Milliseconds <paramref name="team"/>'s last full update took.</summary>
        public double TeamMilliseconds(int team) => teamMilliseconds[team];

        /// <summary>
        /// One 20 Hz tick: refreshes <c>Teams / 4</c> teams round-robin, so each team is refreshed every
        /// four ticks. The budget is accumulated in ticks so a team count that does not divide evenly
        /// still refreshes at the configured rate.
        /// </summary>
        public void Tick()
        {
            ThrowIfDisposed();
            LastTickTeams = 0;
            tick++;
            budget += teams * math.max(1, config.Hz);
            while (budget >= TickRate)
            {
                budget -= TickRate;
                UpdateTeam(cursor);
                lastTeams[LastTickTeams] = cursor;
                cursor++;
                if (cursor == teams) cursor = 0;
                LastTickTeams++;
            }
        }

        /// <summary>
        /// One team's full update: collect its sources, cast them, diff against the team's current
        /// visibility and rebuild its changed list, then trade the two grids' roles. Synchronous - the
        /// jobs are complete when this returns - and the unit of work a tick's slice is made of.
        /// </summary>
        public void UpdateTeam(int team)
        {
            ThrowIfDisposed();
            if ((uint)team >= (uint)teams) throw new ArgumentOutOfRangeException(nameof(team));
            if (!unitHealth.IsCreated) throw new InvalidOperationException("the fog system has no unit arrays: bind them with SetUnits");

            stopwatch.Restart();
            // This update writes into the team's spare grid and compares against the current one, then
            // the two trade roles: the grid just computed is the team's visibility, and the grid it was
            // compared against becomes the spare the next update writes into.
            NativeArray<byte> writeGrid = spare[team];
            NativeArray<byte> previousGrid = current[team];
            NativeList<VisionSource> teamSources = sources[team];

            // The cast fills a grid, so it starts empty. (Only the tiles the last update touched would
            // need clearing, but a copy of the zero grid is a single memmove and keeps the cast simple.)
            NativeArray<byte>.Copy(empty, writeGrid);
            teamSources.Clear();
            rawCount[team][0] = 0;

            JobHandle collect = new CollectVisionSourcesJob
            {
                Positions = unitPositions, Team = unitTeam, Health = unitHealth, Capacity = unitCapacity,
                BuildingPositions = buildingPositions, BuildingOwners = buildingOwners,
                BuildingHealth = buildingHealth, BuildingCount = buildingCount,
                Teams = teams, TeamIndex = team,
                UnitVision = config.UnitVision, BuildingVision = config.BuildingVision,
                Width = width, Height = height,
                Scratch = scratch[team], Sources = teamSources, RawCount = rawCount[team],
            }.Schedule();
            collect.Complete();

            JobHandle cast = new FogCastJob
            {
                Sources = teamSources, Tiles = tiles, Width = width, Height = height, Visible = writeGrid,
            }.Schedule(teamSources.Length, CastBatch, collect);
            cast.Complete();

            JobHandle diff = new FogDiffJob
            {
                Visible = writeGrid, Previous = previousGrid, Explored = explored[team],
                Flags = flags, Cells = cells, BlockSize = BlockSize, BlockCount = blockCount,
            }.Schedule(blocks, 1, cast);
            diff.Complete();

            int total = 0;
            for (int block = 0; block < blocks; block++)
            {
                blockOffset[block] = total;
                total += blockCount[block];
            }
            NativeList<int> changedList = changed[team];
            changedList.ResizeUninitialized(total);
            new FogScatterJob
            {
                Flags = flags, BlockOffset = blockOffset, Changed = changedList.AsArray(),
                Cells = cells, BlockSize = BlockSize,
            }.Schedule(blocks, 1).Complete();

            current[team] = writeGrid;
            spare[team] = previousGrid;

            stopwatch.Stop();
            teamMilliseconds[team] = stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Disposes every grid and list this system allocated. Safe to call twice.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int team = 0; team < teams; team++)
            {
                current[team].Dispose();
                spare[team].Dispose();
                explored[team].Dispose();
                scratch[team].Dispose();
                sources[team].Dispose();
                rawCount[team].Dispose();
                changed[team].Dispose();
            }
            flags.Dispose();
            blockCount.Dispose();
            blockOffset.Dispose();
            empty.Dispose();
            if (buildingsArePlaceholders)
            {
                buildingPositions.Dispose();
                buildingOwners.Dispose();
                buildingHealth.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(FogSystem));
        }
    }
}
