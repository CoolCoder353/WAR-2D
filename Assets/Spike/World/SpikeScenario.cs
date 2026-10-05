using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace WAR2D.Spike
{
    /// <summary>Where the scenario starts each player's army.</summary>
    public enum SpikePlacement
    {
        /// <summary>A ring spiral out from the player's HQ (the default).</summary>
        Hqs,

        /// <summary>
        /// Both players of a front pair spiral out from their shared front, the midpoint between
        /// their two HQs, so each pair starts already engaged.
        /// </summary>
        Fronts,

        /// <summary>Every player spirals out from the map centre (the clump stress).</summary>
        Centre,
    }

    /// <summary>
    /// Inputs for <see cref="SpikeScenario"/>. It deliberately knows nothing about
    /// <see cref="SpikeArgs"/>: a bench maps the command-line options onto this.
    /// </summary>
    public struct SpikeScenarioConfig
    {
        /// <summary>Armies to place, 1 to <see cref="MapGenerator.HqCount"/>, one HQ each.</summary>
        public int Players;

        /// <summary>Units in each army.</summary>
        public int UnitsPerPlayer;

        /// <summary>1x1 vision-and-target markers per player, placed near the HQ. They do not block.</summary>
        public int BuildingsPerPlayer;

        /// <summary>Seed for placement; the order and terrain scripts derive from it too.</summary>
        public uint Seed;

        /// <summary>Percentage of each army in the large size class (radius 0.7).</summary>
        public int LargePercent;

        /// <summary>Where the armies start.</summary>
        public SpikePlacement Placement;

        /// <summary>Order every player at the map centre instead of at the opposing HQ.</summary>
        public bool Clump;

        /// <summary>Ticks between order pulses; 40 is every 2 s at the spike's 20 Hz tick. 0 disables orders.</summary>
        public int OrderIntervalTicks;

        /// <summary>Length of the scripted match in ticks; orders and terrain changes stop at it. 0 means unlimited.</summary>
        public int ScriptTicks;

        /// <summary>Starting health of a unit (today's Tank).</summary>
        public float UnitHealth;

        /// <summary>Starting health of a building marker (the plan's 300).</summary>
        public float BuildingHealth;

        /// <summary>The reference scenario table's defaults.</summary>
        public static SpikeScenarioConfig Defaults => new SpikeScenarioConfig
        {
            Players = 8,
            UnitsPerPlayer = 10000,
            BuildingsPerPlayer = 30,
            Seed = 1,
            LargePercent = 10,
            Placement = SpikePlacement.Hqs,
            Clump = false,
            OrderIntervalTicks = 40,
            ScriptTicks = 1200,
            UnitHealth = 100f,
            BuildingHealth = 300f,
        };
    }

    /// <summary>
    /// The deterministic battle scenario: unit and building placement in SoA arrays, a scripted
    /// order stream and a scripted stream of terrain changes. Every array is allocated with the
    /// allocator passed to <see cref="Create"/> and disposed by <see cref="Dispose"/>; the scenario
    /// keeps no reference to the map it was built from.
    /// </summary>
    public sealed class SpikeScenario : IDisposable
    {
        /// <summary>Radius of the small size class (today's Tank is about this wide).</summary>
        public const float SmallRadius = 0.35f;

        /// <summary>Radius of the large size class, about 1.4 tiles across.</summary>
        public const float LargeRadius = 0.7f;

        /// <summary>Share of an army each scripted order moves.</summary>
        public const float OrderFraction = 0.25f;

        /// <summary>The spike's tick rate; matches <see cref="SpikeClock"/>.</summary>
        public const int TicksPerSecond = 20;

        /// <summary>Terrain changes the script emits per second: one tile every fifth tick.</summary>
        public const int TerrainChangesPerSecond = 4;

        /// <summary>Units that fit on one floor tile, per player's army.</summary>
        public const int UnitsPerTile = 2;

        /// <summary>
        /// Terrain changes keep this Chebyshev radius around every HQ and around the map centre,
        /// so spawn points and the clump goal can never be sealed by the script.
        /// </summary>
        public const int TerrainClearRadius = 6;

        private const int BuildingStartRing = 2; // keep the markers off the HQ tile itself
        private const float Jitter = 0.5f;

        /// <summary>Armies placed (one HQ each).</summary>
        public readonly int Players;
        /// <summary>Units per army.</summary>
        public readonly int UnitsPerPlayer;
        /// <summary>Building markers per army.</summary>
        public readonly int BuildingsPerPlayer;
        /// <summary>Total units placed.</summary>
        public readonly int UnitCount;
        /// <summary>Total buildings placed.</summary>
        public readonly int BuildingCount;
        /// <summary>The scenario seed.</summary>
        public readonly uint Seed;
        /// <summary>Where the armies start.</summary>
        public readonly SpikePlacement Placement;
        /// <summary>True when orders send every army to the map centre.</summary>
        public readonly bool Clump;
        /// <summary>Ticks between order pulses.</summary>
        public readonly int OrderIntervalTicks;
        /// <summary>Ticks the script runs for.</summary>
        public readonly int ScriptTicks;
        /// <summary>The map's centre tile, the clump goal.</summary>
        public readonly int2 MapCentre;

        /// <summary>The eight HQ sites, in <see cref="MapGenerator.HqSites"/> order.</summary>
        public readonly NativeArray<int2> HqSites;

        /// <summary>Unit positions, one per unit. Unit i of player p is at p * UnitsPerPlayer + i.</summary>
        public NativeArray<float2> UnitPositions;
        /// <summary>Player index per unit.</summary>
        public NativeArray<byte> UnitOwners;
        /// <summary>Size class per unit: 0 small (<see cref="SmallRadius"/>), 1 large (<see cref="LargeRadius"/>).</summary>
        public NativeArray<byte> UnitSizeClass;
        /// <summary>Body radius per unit.</summary>
        public NativeArray<float> UnitRadius;
        /// <summary>Starting health per unit.</summary>
        public NativeArray<float> UnitHealth;

        /// <summary>Building marker positions, one per building. Building b of player p is at p * BuildingsPerPlayer + b.</summary>
        public NativeArray<float2> BuildingPositions;
        /// <summary>Player index per building marker.</summary>
        public NativeArray<byte> BuildingOwners;
        /// <summary>Starting health per building marker.</summary>
        public NativeArray<float> BuildingHealth;

        /// <summary>Tiles the terrain script may flip: floor or rock, next to a rock (or floor) tile, away from the clearings.</summary>
        private readonly NativeArray<int2> terrainCandidates;

        private bool disposed;

        private SpikeScenario(in SpikeScenarioConfig config, in SpikeMap map, Allocator allocator)
        {
            Validate(config, map);

            Players = config.Players;
            UnitsPerPlayer = config.UnitsPerPlayer;
            BuildingsPerPlayer = config.BuildingsPerPlayer;
            UnitCount = Players * UnitsPerPlayer;
            BuildingCount = Players * BuildingsPerPlayer;
            Seed = config.Seed;
            Placement = config.Placement;
            Clump = config.Clump;
            OrderIntervalTicks = config.OrderIntervalTicks;
            ScriptTicks = config.ScriptTicks;
            MapCentre = new int2(map.Width / 2, map.Height / 2);

            int2[] sites = MapGenerator.HqSites(map.Width);
            terrainCandidates = BuildTerrainCandidates(map, sites, MapCentre, allocator);
            if (terrainCandidates.Length < TerrainChangesPerSecond)
            {
                terrainCandidates.Dispose();
                throw new InvalidOperationException("the map has too little rock next to floor for the terrain-change script");
            }

            HqSites = new NativeArray<int2>(sites.Length, allocator);
            HqSites.CopyFrom(sites);

            UnitPositions = new NativeArray<float2>(UnitCount, allocator, NativeArrayOptions.UninitializedMemory);
            UnitOwners = new NativeArray<byte>(UnitCount, allocator, NativeArrayOptions.UninitializedMemory);
            UnitSizeClass = new NativeArray<byte>(UnitCount, allocator, NativeArrayOptions.UninitializedMemory);
            UnitRadius = new NativeArray<float>(UnitCount, allocator, NativeArrayOptions.UninitializedMemory);
            UnitHealth = new NativeArray<float>(UnitCount, allocator, NativeArrayOptions.UninitializedMemory);
            BuildingPositions = new NativeArray<float2>(BuildingCount, allocator, NativeArrayOptions.UninitializedMemory);
            BuildingOwners = new NativeArray<byte>(BuildingCount, allocator, NativeArrayOptions.UninitializedMemory);
            BuildingHealth = new NativeArray<float>(BuildingCount, allocator, NativeArrayOptions.UninitializedMemory);

            var rng = new Random(Mix(config.Seed, 1u)); // stream 1: placement
            for (int player = 0; player < Players; player++)
            {
                PlaceArmy(config, map, sites, player, ref rng);
                // The markers stay at the HQ even when the army starts somewhere else.
                PlaceBuildings(config, map, player, sites[player]);
            }
        }

        /// <summary>
        /// Places <see cref="SpikeScenarioConfig.Players"/> armies of
        /// <see cref="SpikeScenarioConfig.UnitsPerPlayer"/> units around their start points, into
        /// the scenario's SoA arrays. The map must be at least <see cref="MapGenerator.MinSize"/>
        /// across; generated maps are the intended input.
        /// </summary>
        public static SpikeScenario Create(in SpikeScenarioConfig config, in SpikeMap map, Allocator allocator) =>
            new SpikeScenario(config, map, allocator);

        /// <summary>
        /// The orders issued at <paramref name="tick"/>: at every <see cref="OrderIntervalTicks"/>
        /// tick, each player orders the fraction <see cref="OrderFraction"/> of its army at its
        /// opposing HQ (or at the map centre when <see cref="Clump"/> is set), until
        /// <see cref="ScriptTicks"/>. Empty on every other tick. A pure function of the tick.
        /// </summary>
        public IEnumerable<(int player, int2 goal, float fraction)> OrdersAt(int tick)
        {
            if (tick < 0 || OrderIntervalTicks <= 0) yield break;
            if (ScriptTicks > 0 && tick >= ScriptTicks) yield break;
            if (tick % OrderIntervalTicks != 0) yield break;
            for (int player = 0; player < Players; player++)
                yield return (player, GoalFor(player), OrderFraction);
        }

        /// <summary>
        /// Appends the tile whose rock/floor state flips at <paramref name="tick"/>: four tiles per
        /// second, one every fifth tick, until <see cref="ScriptTicks"/>. The caller toggles the tile
        /// (floor to rock, rock to floor) and invalidates the flow fields that cover it. A pure
        /// function of the tick: the same tick always returns the same tile, whatever the map looks
        /// like by then.
        /// </summary>
        public void TerrainChangesAt(int tick, NativeList<int2> into)
        {
            if (tick < 0 || tick % (TicksPerSecond / TerrainChangesPerSecond) != 0) return;
            if (ScriptTicks > 0 && tick >= ScriptTicks) return;

            var rng = new Random(Mix(Seed, (uint)tick + 2u)); // stream 2: terrain changes
            into.Add(terrainCandidates[rng.NextInt(terrainCandidates.Length)]);
        }

        /// <summary>Disposes every array the scenario allocated. Safe to call twice.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            terrainCandidates.Dispose();
            HqSites.Dispose();
            UnitPositions.Dispose();
            UnitOwners.Dispose();
            UnitSizeClass.Dispose();
            UnitRadius.Dispose();
            UnitHealth.Dispose();
            BuildingPositions.Dispose();
            BuildingOwners.Dispose();
            BuildingHealth.Dispose();
        }

        private int2 GoalFor(int player) => Clump ? MapCentre : HqSites[player ^ 1];

        /// <summary>
        /// Places one army in two steps. First it spirals out from the start point filling up to
        /// <see cref="UnitsPerTile"/> units per floor tile, so the army is dense. Then it promotes
        /// the large share: a large unit is due every <c>units / largeCount</c> indices, and each
        /// due takes the first unit at or after that index whose tile has clearance. Promotions
        /// therefore stay spread through the army, and an army's positions do not depend on the
        /// large percent, so the -large sweep compares identical armies. Throws when the map runs
        /// out of floor, or of clearance for the large share, rather than quietly placing a
        /// different army.
        /// </summary>
        private void PlaceArmy(in SpikeScenarioConfig config, in SpikeMap map, int2[] sites, int player, ref Random rng)
        {
            int2 origin = StartPoint(sites, player);
            int largeCount = (int)math.round(UnitsPerPlayer * config.LargePercent * 0.01f);
            int baseIndex = player * UnitsPerPlayer;
            int maxRing = math.max(map.Width, map.Height);
            int placed = 0;

            for (int ring = 0; placed < UnitsPerPlayer && ring <= maxRing; ring++)
            {
                int ringTiles = ring == 0 ? 1 : 8 * ring;
                for (int i = 0; i < ringTiles && placed < UnitsPerPlayer; i++)
                {
                    int2 tile = RingTile(origin, ring, i);
                    if (map.IsBlocked(tile)) continue;
                    for (int slot = 0; slot < UnitsPerTile && placed < UnitsPerPlayer; slot++)
                    {
                        WriteUnit(baseIndex + placed, tile, player, config, ref rng);
                        placed++;
                    }
                }
            }
            if (placed < UnitsPerPlayer)
                throw new InvalidOperationException($"the map holds only {placed} of {UnitsPerPlayer} units for player {player}");

            int promoted = 0, cursor = 0;
            for (int index = 0; index < UnitsPerPlayer && promoted < largeCount; index++)
            {
                int due = (index + 1) * largeCount / UnitsPerPlayer;
                int candidate;
                // Start each search at the due index (or past the previous promotion), so promotions
                // spread through the army instead of crowding its first eligible tiles.
                while (promoted < due && (candidate = FirstPromotable(map, baseIndex, math.max(cursor, index))) >= 0)
                {
                    cursor = candidate + 1;
                    PromoteToLarge(baseIndex + candidate);
                    promoted++;
                }
                // Once a search finds nothing, the rest of the army has no clearance either.
                if (promoted < due) break;
            }

            // The tail had no clearance left, so promote the eligible smalls it walked past.
            for (int j = 0; j < UnitsPerPlayer && promoted < largeCount; j++)
            {
                if (!IsPromotable(map, baseIndex + j)) continue;
                PromoteToLarge(baseIndex + j);
                promoted++;
            }
            if (promoted < largeCount)
                throw new InvalidOperationException($"the map has clearance for only {promoted} of {largeCount} large units for player {player}");
        }

        /// <summary>The first army index at or after <paramref name="from"/> that can take a large unit, or -1.</summary>
        private int FirstPromotable(in SpikeMap map, int baseIndex, int from)
        {
            for (int j = from; j < UnitsPerPlayer; j++)
                if (IsPromotable(map, baseIndex + j)) return j;
            return -1;
        }

        /// <summary>True when the unit is still small and its tile has the clearance a large unit needs.</summary>
        private bool IsPromotable(in SpikeMap map, int unit) =>
            UnitSizeClass[unit] == 0 && HasLargeClearance(map, (int2)math.floor(UnitPositions[unit]));

        private void PromoteToLarge(int unit)
        {
            UnitSizeClass[unit] = 1;
            UnitRadius[unit] = LargeRadius;
        }

        private void WriteUnit(int unit, int2 tile, int player, in SpikeScenarioConfig config, ref Random rng)
        {
            UnitPositions[unit] = (float2)tile + new float2(0.5f) + (rng.NextFloat2() - new float2(0.5f)) * Jitter;
            UnitOwners[unit] = (byte)player;
            UnitSizeClass[unit] = 0;
            UnitRadius[unit] = SmallRadius;
            UnitHealth[unit] = config.UnitHealth;
        }

        /// <summary>Places the player's building markers on the first free tiles of a spiral around its HQ, from ring 2 outwards.</summary>
        private void PlaceBuildings(in SpikeScenarioConfig config, in SpikeMap map, int player, int2 origin)
        {
            int baseIndex = player * BuildingsPerPlayer;
            int placed = 0;
            int maxRing = math.max(map.Width, map.Height);

            for (int ring = BuildingStartRing; placed < BuildingsPerPlayer && ring <= maxRing; ring++)
            {
                for (int i = 0; i < 8 * ring && placed < BuildingsPerPlayer; i++)
                {
                    int2 tile = RingTile(origin, ring, i);
                    if (map.IsBlocked(tile)) continue;
                    BuildingPositions[baseIndex + placed] = (float2)tile + new float2(0.5f);
                    BuildingOwners[baseIndex + placed] = (byte)player;
                    BuildingHealth[baseIndex + placed] = config.BuildingHealth;
                    placed++;
                }
            }

            if (placed < BuildingsPerPlayer)
                throw new InvalidOperationException($"the map holds only {placed} of {BuildingsPerPlayer} buildings for player {player}");
        }

        private int2 StartPoint(int2[] sites, int player)
        {
            switch (Placement)
            {
                case SpikePlacement.Fronts: return (sites[player & ~1] + sites[(player & ~1) + 1]) / 2;
                case SpikePlacement.Centre: return MapCentre;
                default: return sites[player];
            }
        }

        /// <summary>
        /// The i-th tile of the square ring at Chebyshev distance <paramref name="ring"/> from the
        /// origin, walking the top edge left to right and then clockwise. Gives every ring a fixed,
        /// seed-independent order, so placement is deterministic.
        /// </summary>
        private static int2 RingTile(int2 origin, int ring, int i)
        {
            if (ring == 0) return origin;
            int side = 2 * ring;
            if (i < side) return new int2(origin.x - ring + i, origin.y - ring);
            i -= side;
            if (i < side) return new int2(origin.x + ring, origin.y - ring + i);
            i -= side;
            if (i < side) return new int2(origin.x + ring - i, origin.y + ring);
            i -= side;
            return new int2(origin.x - ring, origin.y + ring - i);
        }

        /// <summary>
        /// Task 4's <c>ClearanceJob</c> does not exist yet, so the large size class's eligibility
        /// rule is inlined: a tile qualifies when every tile in its 3x3 Chebyshev neighbourhood is
        /// floor. That is exactly clearance >= 2 in Task 4's "distance in tiles to the nearest
        /// blocked tile" semantics (large units are filtered below clearance ceil(0.7 + 0.5) = 2).
        /// Reconcile the two when Task 4 lands.
        /// </summary>
        private static bool HasLargeClearance(in SpikeMap map, int2 tile)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (map.IsBlocked(tile + new int2(dx, dy))) return false;
            return true;
        }

        /// <summary>
        /// The tiles the terrain script may flip at creation time: floor with a rock neighbour, or
        /// rock with a floor neighbour, outside the clearings around the HQs and the map centre.
        /// A fixed set, so <see cref="TerrainChangesAt"/> stays a pure function of the tick.
        /// </summary>
        private static NativeArray<int2> BuildTerrainCandidates(in SpikeMap map, int2[] sites, int2 centre, Allocator allocator)
        {
            var candidates = new NativeList<int2>(1024, allocator);
            for (int y = 1; y < map.Height - 1; y++)
            for (int x = 1; x < map.Width - 1; x++)
            {
                int index = y * map.Width + x;
                byte value = map.Tiles[index];
                if (value != SpikeMap.Floor && value != SpikeMap.Rock) continue;

                var tile = new int2(x, y);
                if (math.cmax(math.abs(tile - centre)) <= TerrainClearRadius) continue;
                bool inClearing = false;
                foreach (int2 site in sites)
                    inClearing |= math.cmax(math.abs(tile - site)) <= TerrainClearRadius;
                if (inClearing) continue;

                bool rockNext = false, floorNext = false;
                for (int k = 0; k < 4; k++)
                {
                    byte neighbour = map.Tiles[(y + (k == 2 ? 1 : k == 3 ? -1 : 0)) * map.Width + x + (k == 0 ? 1 : k == 1 ? -1 : 0)];
                    rockNext |= neighbour == SpikeMap.Rock;
                    floorNext |= neighbour == SpikeMap.Floor;
                }
                if (value == SpikeMap.Floor ? rockNext : floorNext) candidates.Add(tile);
            }

            var result = new NativeArray<int2>(candidates.Length, allocator);
            NativeArray<int2>.Copy(candidates.AsArray(), result, result.Length);
            candidates.Dispose();
            return result;
        }

        private static void Validate(in SpikeScenarioConfig config, in SpikeMap map)
        {
            if (config.Players < 1 || config.Players > MapGenerator.HqCount)
                throw new ArgumentOutOfRangeException(nameof(config), config.Players, $"players must be 1 to {MapGenerator.HqCount}");
            if (config.UnitsPerPlayer < 0) throw new ArgumentOutOfRangeException(nameof(config), config.UnitsPerPlayer, "units per player cannot be negative");
            if (config.BuildingsPerPlayer < 0) throw new ArgumentOutOfRangeException(nameof(config), config.BuildingsPerPlayer, "buildings per player cannot be negative");
            if (config.LargePercent < 0 || config.LargePercent > 100) throw new ArgumentOutOfRangeException(nameof(config), config.LargePercent, "the large share is a percentage");
            if (config.OrderIntervalTicks < 0) throw new ArgumentOutOfRangeException(nameof(config), config.OrderIntervalTicks, "the order interval cannot be negative");
            if (config.ScriptTicks < 0) throw new ArgumentOutOfRangeException(nameof(config), config.ScriptTicks, "the script length cannot be negative");
            if (map.Width != map.Height) throw new ArgumentException("spike maps are square", nameof(map));
            if (map.Width < MapGenerator.MinSize) throw new ArgumentException($"spike maps are at least {MapGenerator.MinSize} tiles across", nameof(map));
            if (map.Tiles.Length < map.Width * map.Height) throw new ArgumentException("the map's tile array is too small", nameof(map));
        }

        /// <summary>
        /// A deterministic hash of a seed and a tick. Both scripts reseed from it, so each tick's
        /// output depends only on (seed, tick) and never on the calls before it.
        /// </summary>
        private static uint Mix(uint seed, uint value)
        {
            uint h = seed * 0x9E3779B9u + value * 0x85EBCA6Bu;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h | 1u;
        }
    }
}
