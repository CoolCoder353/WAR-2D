using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Net.Replication;

namespace WAR2D.Client
{
    /// <summary>
    /// The minimap's texture: one texel per fog cell, drawn only from what the client already knows.
    /// Unexplored cells are black, explored ones dim terrain, visible ones terrain. Units are plotted
    /// only on cells the client sees now, buildings on any explored cell (ghosts dimmed). Redraw with
    /// <see cref="Begin"/>, the Plot calls, then <see cref="End"/>.
    /// </summary>
    public sealed class MinimapTexture : IDisposable
    {
        /// <summary>The colour of an unexplored cell.</summary>
        public static readonly Color32 Unexplored = new Color32(0, 0, 0, 255);

        private NativeArray<Color32> visible, explored, visibleNoGems, exploredNoGems;

        /// <summary>
        /// The layers drawn over the terrain (Settings → Gameplay, or the minimap's filter toggles): Gems are the gem tiles' colour,
        /// Army the units, Buildings the seen buildings, Ghosts the last-seen ones. They change only what is drawn, never what is known.
        /// Read from the settings at every <see cref="Begin"/>.
        /// </summary>
        public MinimapLayers Layers { get; private set; } = MinimapLayers.All;
        private NativeArray<byte> fogCopy;
        private byte[] fog;
        private NativeArray<Color32> pixels;

        /// <summary>The texture to show (point-filtered, one texel per fog cell).</summary>
        public Texture2D Texture { get; }
        public int Width { get; }
        public int Height { get; }
        /// <summary>Tiles per fog cell side.</summary>
        public int CellSize { get; }

        /// <param name="tileAt">The map's tiles; each cell takes the colour of the tile at its centre.</param>
        public MinimapTexture(int width, int height, int cellSize, Func<int2, TileType> tileAt)
        {
            Width = width;
            Height = height;
            CellSize = cellSize;
            visible = new NativeArray<Color32>(width * height, Allocator.Persistent);
            explored = new NativeArray<Color32>(width * height, Allocator.Persistent);
            visibleNoGems = new NativeArray<Color32>(width * height, Allocator.Persistent);
            exploredNoGems = new NativeArray<Color32>(width * height, Allocator.Persistent);
            fogCopy = new NativeArray<byte>(width * height, Allocator.Persistent);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                TileType type = tileAt(new int2(x * cellSize + cellSize / 2, y * cellSize + cellSize / 2));
                Color32 tile = MapView.TileColor(type);
                visible[y * width + x] = Scale(tile, 1.6f);
                explored[y * width + x] = Scale(tile, 0.75f);
                // With the Gems layer off a gem cell reads as plain ground.
                Color32 plain = type == TileType.Gem ? MapView.TileColor(TileType.Ground) : tile;
                visibleNoGems[y * width + x] = Scale(plain, 1.6f);
                exploredNoGems[y * width + x] = Scale(plain, 0.75f);
            }
            Texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "Minimap",
            };
        }

        /// <summary>
        /// Starts a redraw: terrain by fog state (<see cref="FogState"/> per cell; null draws everything unexplored).
        /// <paramref name="revealTerrain"/> draws unexplored cells as explored terrain (choosing an HQ site, when no
        /// unit or building sees anything yet; the client already holds the whole map).
        /// </summary>
        public void Begin(byte[] fogState, bool revealTerrain = false)
        {
            fog = fogState != null && fogState.Length == visible.Length ? fogState : null;
            pixels = Texture.GetPixelData<Color32>(0);
            Layers = (MinimapLayers)SettingsStore.Current.MinimapFilters;
            NativeArray<Color32> terrainVisible = (Layers & MinimapLayers.Gems) != 0 ? visible : visibleNoGems;
            NativeArray<Color32> terrainExplored = (Layers & MinimapLayers.Gems) != 0 ? explored : exploredNoGems;
            if (fog == null)
            {
                for (int i = 0; i < pixels.Length; i++) pixels[i] = revealTerrain ? terrainExplored[i] : Unexplored;
                return;
            }
            fogCopy.CopyFrom(fog);
            new FillJob { Fog = fogCopy, Visible = terrainVisible, Explored = terrainExplored, Pixels = pixels, Reveal = revealTerrain }.Run();
        }

        /// <summary>Plots a square of cells around a world position whatever the fog (an HQ start option), <paramref name="radius"/> cells out.</summary>
        public void PlotMarker(float2 position, Color32 colour, int radius)
        {
            int cx = (int)math.floor(position.x / CellSize), cy = (int)math.floor(position.y / CellSize);
            for (int y = cy - radius; y <= cy + radius; y++)
            for (int x = cx - radius; x <= cx + radius; x++)
                if ((uint)x < (uint)Width && (uint)y < (uint)Height) pixels[y * Width + x] = colour;
        }

        /// <summary>Terrain under fog for every cell (Burst: the default map has 262,144 cells).</summary>
        [BurstCompile]
        private struct FillJob : IJob
        {
            [ReadOnly] public NativeArray<byte> Fog;
            [ReadOnly] public NativeArray<Color32> Visible, Explored;
            public NativeArray<Color32> Pixels;
            public bool Reveal;

            public void Execute()
            {
                var black = new Color32(0, 0, 0, 255);
                for (int i = 0; i < Pixels.Length; i++)
                {
                    byte f = Fog[i];
                    Pixels[i] = f == (byte)FogState.Visible ? Visible[i] : f == (byte)FogState.Explored || Reveal ? Explored[i] : black;
                }
            }
        }

        /// <summary>Plots a unit at a world position, only if its cell is visible now.</summary>
        public void PlotUnit(float2 position, Color32 colour)
        {
            if ((Layers & MinimapLayers.Army) == 0) return;
            int cell = CellOf(position);
            if (fog != null && cell >= 0 && fog[cell] == (byte)FogState.Visible) pixels[cell] = colour;
        }

        /// <summary>Plots a building at a world position on any explored cell; a ghost (last seen) is dimmed.</summary>
        public void PlotBuilding(float2 position, Color32 colour, bool ghost)
        {
            if ((Layers & (ghost ? MinimapLayers.Ghosts : MinimapLayers.Buildings)) == 0) return;
            int cell = CellOf(position);
            if (fog == null || cell < 0 || fog[cell] == (byte)FogState.Unexplored) return;
            pixels[cell] = ghost ? Scale(colour, 0.5f) : colour;
        }

        /// <summary>Uploads the redraw.</summary>
        public void End() => Texture.Apply(false);

        /// <summary>A texel after <see cref="End"/> (tests).</summary>
        public Color32 Pixel(int x, int y) => Texture.GetPixelData<Color32>(0)[y * Width + x];

        /// <summary>
        /// The world point under a position on the shown minimap: <paramref name="local"/> is measured from
        /// the image's top-left corner, in an image of <paramref name="size"/>.
        /// </summary>
        public static float2 ToWorld(Vector2 local, Vector2 size, float2 worldSize) =>
            new float2(local.x / size.x * worldSize.x, (1f - local.y / size.y) * worldSize.y);

        /// <summary>The position on the shown minimap (from its top-left corner) of a world point; the inverse of <see cref="ToWorld"/>.</summary>
        public static Vector2 ToLocal(float2 world, Vector2 size, float2 worldSize) =>
            new Vector2(world.x / worldSize.x * size.x, (1f - world.y / worldSize.y) * size.y);

        private int CellOf(float2 position)
        {
            if (fog == null) return -1;
            int x = (int)math.floor(position.x / CellSize), y = (int)math.floor(position.y / CellSize);
            return (uint)x < (uint)Width && (uint)y < (uint)Height ? y * Width + x : -1;
        }

        private static Color32 Scale(Color32 c, float k) =>
            new Color32((byte)math.min(255f, c.r * k), (byte)math.min(255f, c.g * k), (byte)math.min(255f, c.b * k), 255);

        public void Dispose()
        {
            if (visible.IsCreated) visible.Dispose();
            if (explored.IsCreated) explored.Dispose();
            if (visibleNoGems.IsCreated) visibleNoGems.Dispose();
            if (exploredNoGems.IsCreated) exploredNoGems.Dispose();
            if (fogCopy.IsCreated) fogCopy.Dispose();
            if (Texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(Texture);
            else UnityEngine.Object.DestroyImmediate(Texture);
        }
    }
}
