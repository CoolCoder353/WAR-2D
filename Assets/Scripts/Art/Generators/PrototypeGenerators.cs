using UnityEngine;

namespace WAR2D.Art.Generators
{
    /// <summary>Draws one manifest sprite: <c>Frames × Directions</c> canvases, direction-major (all of direction 0's frames first).</summary>
    public interface ISpriteGenerator
    {
        /// <summary>The <see cref="ArtManifest"/> id this generator draws.</summary>
        string Id { get; }

        /// <summary>Same seed, same pixels.</summary>
        PixelCanvas[] Generate(int seed);
    }

    /// <summary>
    /// The method prototype (a)'s provisional size and palette (plan Task 2), and its generators. The style
    /// guide (Task 5) replaces these with <c>ArtStyle</c>.
    /// </summary>
    public static class PrototypeGenerators
    {
        /// <summary>Provisional pixels per tile.</summary>
        public const int TileSize = 16;

        // Palette indices, named for the drawing code.
        public const byte Clear = 0, Outline = 1, Steel0 = 2, Steel1 = 3, Steel2 = 4, Steel3 = 5, White = 6;
        public const byte Rock0 = 7, Rock1 = 8, Rock2 = 9, Rock3 = 10, Floor0 = 11, Floor1 = 12, Floor2 = 13;
        public const byte Gem0 = 14, Gem1 = 15, Gem2 = 16, Gem3 = 17, Yellow = 18, Orange = 19, Red = 20, Green0 = 21, Green1 = 22, Brown = 23;
        /// <summary>The team key ramp, darkest first: pure magentas the importer turns into the team mask.</summary>
        public const byte Team0 = 24, Team1 = 25, Team2 = 26, Team3 = 27;

        /// <summary>24 colours plus transparent, then the 4-step team key ramp.</summary>
        public static readonly Color32[] Palette =
        {
            new Color32(0, 0, 0, 0),
            Hex(0x14161C), Hex(0x2B2F38), Hex(0x4A505C), Hex(0x7A8290), Hex(0xB8C0CC), Hex(0xEEF2F6),
            Hex(0x3A3430), Hex(0x5A5048), Hex(0x7D7064), Hex(0xA39282), Hex(0x1E242B), Hex(0x262E37), Hex(0x303944),
            Hex(0x0F5A5E), Hex(0x1E9C9A), Hex(0x5FE0D0), Hex(0xD8FFF6), Hex(0xF2C14E), Hex(0xE07B39), Hex(0xB8413A),
            Hex(0x4F8A4B), Hex(0x8CC084), Hex(0x6B4A2F),
            Hex(0x400040), Hex(0x800080), Hex(0xC000C0), Hex(0xFF00FF),
        };

        /// <summary>Every prototype generator.</summary>
        public static readonly ISpriteGenerator[] All =
        {
            new TankGen(), new MinerGen(), new TerrainGen.Ground(), new TerrainGen.Wall(), new TerrainGen.Gem(), new TerrainGen.Border(),
        };

        public static PixelCanvas Canvas(ArtEntry entry) =>
            new PixelCanvas(entry.WidthTiles * TileSize, entry.HeightTiles * TileSize, Palette);

        public static ArtEntry Entry(string id) =>
            ArtManifest.TryGet(id, out ArtEntry e) ? e : throw new System.ArgumentException($"{id} is not in the art manifest");

        private static Color32 Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
    }

    /// <summary>A small deterministic RNG (xorshift32), so generated art never depends on the runtime's Random.</summary>
    public struct ArtRandom
    {
        private uint state;

        public ArtRandom(int seed, string salt)
        {
            uint h = 2166136261u; // FNV-1a over the salt, mixed with the seed
            foreach (char c in salt) h = (h ^ c) * 16777619u;
            state = h ^ (uint)seed * 2654435761u;
            if (state == 0) state = 0x9E3779B9u;
        }

        public uint Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        /// <summary>0 ≤ value &lt; <paramref name="max"/>.</summary>
        public int Range(int max) => (int)(Next() % (uint)max);

        /// <summary>True with probability <paramref name="p"/>.</summary>
        public bool Chance(float p) => Next() % 10000 < p * 10000f;
    }
}
