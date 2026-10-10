using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.World;

namespace WAR2D.Client
{
    /// <summary>
    /// The lobby's map preview: runs <see cref="MapGenerator.Generate"/> for the host's size and seed in a
    /// job on a worker thread (jobs may use <c>Allocator.Temp</c>, which the generator does), then downsamples it to 256² on the main thread. A newer request supersedes an older
    /// one. The preview reveals nothing secret: every client regenerates the full map at match start.
    /// </summary>
    public sealed class MapPreview : IDisposable
    {
        /// <summary>Preview texels per side.</summary>
        public const int Resolution = 256;

        private static readonly Color32 FloorColour = new Color32(0x40, 0x50, 0x5F, 255); // surface/raised
        private static readonly Color32 RockColour = new Color32(0x26, 0x30, 0x3A, 255);  // surface/base
        private static readonly Color32 GemColour = new Color32(0x6E, 0x5A, 0x9E, 255);

        private (int size, uint seed) wanted = (-1, 0), pendingKey;
        private JobHandle pendingHandle;
        private NativeArray<byte> pendingTiles;
        private NativeArray<long> pendingMs;
        private bool pending;

        /// <summary>Generates one map into <see cref="Tiles"/> (not Burst: the generator is managed code).</summary>
        private struct GenerateJob : IJob
        {
            public int Size;
            public uint Seed;
            public float GemChance;
            public NativeArray<byte> Tiles;
            public NativeArray<long> Ms;

            public void Execute()
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                (NativeArray<byte> tiles, int2[] _) = MapGenerator.Generate(Size, Seed, GemChance, Allocator.Temp);
                Tiles.CopyFrom(tiles);
                tiles.Dispose();
                Ms[0] = watch.ElapsedMilliseconds;
            }
        }

        /// <summary>The preview (null until the first map is ready).</summary>
        public Texture2D Texture { get; private set; }

        /// <summary>The HQ sites of the shown map, in preview texels (y up).</summary>
        public IReadOnlyList<int2> Spawns { get; private set; } = Array.Empty<int2>();

        /// <summary>Milliseconds the last generation took (worker thread).</summary>
        public long LastGenerateMs { get; private set; }

        /// <summary>True while a requested map is still generating.</summary>
        public bool Busy => pending;

        /// <summary>Raised on the main thread when a new preview is shown.</summary>
        public event Action Ready;

        /// <summary>
        /// Generates the preview for a map unless it is already shown or generating. A newer request
        /// replaces an older one (whose result is dropped). A size below the generator's minimum (the
        /// scene's own tilemaps) shows nothing new.
        /// </summary>
        public void Request(int size, uint seed, float gemChance)
        {
            if ((size, seed) == wanted) return;
            wanted = (size, seed);
            DropPending();
            if (size < MapGenerator.MinSize) return;
            pendingKey = wanted;
            pendingTiles = new NativeArray<byte>(size * size, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            pendingMs = new NativeArray<long>(1, Allocator.Persistent);
            pendingHandle = new GenerateJob { Size = size, Seed = seed, GemChance = gemChance, Tiles = pendingTiles, Ms = pendingMs }.Schedule();
            pending = true;
        }

        /// <summary>Call every frame on the main thread: shows the latest preview once it is generated.</summary>
        public void Update()
        {
            if (!pending || !pendingHandle.IsCompleted) return;
            pendingHandle.Complete();
            LastGenerateMs = pendingMs[0];
            Show(pendingTiles, MapGenerator.HqSites(pendingKey.size), pendingKey.size);
            DropPending();
            Ready?.Invoke();
        }

        /// <summary>Abandons the map being generated (waits for its job, which can't be interrupted).</summary>
        private void DropPending()
        {
            if (!pending) return;
            pendingHandle.Complete();
            pendingTiles.Dispose();
            pendingMs.Dispose();
            pending = false;
        }

        private void Show(NativeArray<byte> tiles, int2[] sites, int size)
        {
            if (Texture == null)
                Texture = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "MapPreview" };
            NativeArray<Color32> pixels = Texture.GetPixelData<Color32>(0);
            // Each texel averages its block of tiles, so caves read as shapes rather than noise.
            float step = (float)size / Resolution;
            int block = math.max(1, (int)step);
            for (int y = 0; y < Resolution; y++)
            for (int x = 0; x < Resolution; x++)
            {
                int x0 = (int)(x * step), y0 = (int)(y * step);
                int floor = 0, gem = 0, n = 0;
                for (int dy = 0; dy < block && y0 + dy < size; dy++)
                for (int dx = 0; dx < block && x0 + dx < size; dx++)
                {
                    byte tile = tiles[(y0 + dy) * size + x0 + dx];
                    if (tile == (byte)TileType.Ground) floor++;
                    else if (tile == (byte)TileType.Gem) gem++;
                    n++;
                }
                Color32 c = Color32.Lerp(RockColour, FloorColour, (float)floor / n);
                pixels[y * Resolution + x] = gem * 4 >= n ? Color32.Lerp(c, GemColour, 0.6f) : c;
            }
            Texture.Apply(false);
            var spawns = new int2[sites.Length];
            for (int i = 0; i < sites.Length; i++) spawns[i] = (int2)((float2)sites[i] / step);
            Spawns = spawns;
        }

        public void Dispose()
        {
            DropPending();
            if (Texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(Texture);
            else UnityEngine.Object.DestroyImmediate(Texture);
            Texture = null;
        }
    }
}
