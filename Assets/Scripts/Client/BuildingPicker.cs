using System;
using System.Collections.Generic;
using Unity.Mathematics;
using WAR2D.Net.Replication;

namespace WAR2D.Client
{
    /// <summary>
    /// Which known building (seen or ghost) covers a tile, kept in step with <see cref="ClientBuildings"/>.
    /// Clicks and box selection ask it by tile instead of hitting per-building colliders.
    /// </summary>
    public sealed class BuildingPicker
    {
        private readonly Func<BuildingType, int2> sizeOf;
        private readonly Dictionary<int2, int> byTile = new Dictionary<int2, int>();
        private readonly Dictionary<int, List<int2>> tilesOf = new Dictionary<int, List<int2>>();
        private ClientBuildings source;

        public BuildingPicker(Func<BuildingType, int2> sizeOf) => this.sizeOf = sizeOf;

        /// <summary>Starts following a building set, including the buildings it already holds.</summary>
        public void Attach(ClientBuildings buildings)
        {
            Detach();
            source = buildings;
            foreach (ClientBuildings.Entry e in buildings.Entries.Values) Add(e.Data, e.Health);
            buildings.Entered += Add;
            buildings.Removed += Remove;
        }

        /// <summary>Stops following and forgets every building.</summary>
        public void Detach()
        {
            if (source != null)
            {
                source.Entered -= Add;
                source.Removed -= Remove;
            }
            source = null;
            byTile.Clear();
            tilesOf.Clear();
        }

        /// <summary>The building covering a tile.</summary>
        public bool TryPick(int2 tile, out int id) => byTile.TryGetValue(tile, out id);

        /// <summary>Adds each building with a footprint tile inside the inclusive box, once.</summary>
        public void InBox(int2 min, int2 max, List<int> into)
        {
            foreach (KeyValuePair<int, List<int2>> pair in tilesOf)
                foreach (int2 t in pair.Value)
                    if (t.x >= min.x && t.y >= min.y && t.x <= max.x && t.y <= max.y) { into.Add(pair.Key); break; }
        }

        private void Add(BuildingData data, HealthComponent _)
        {
            Remove(data.id);
            List<int2> tiles = Footprint.Tiles((int2)math.round(data.position), sizeOf(data.buildingType));
            tilesOf[data.id] = tiles;
            foreach (int2 t in tiles) byTile[t] = data.id;
        }

        private void Remove(int id)
        {
            if (!tilesOf.Remove(id, out List<int2> tiles)) return;
            foreach (int2 t in tiles)
                if (byTile.TryGetValue(t, out int at) && at == id) byTile.Remove(t);
        }
    }
}
