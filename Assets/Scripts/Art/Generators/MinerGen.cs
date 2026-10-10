using static WAR2D.Art.Generators.PrototypeGenerators;

namespace WAR2D.Art.Generators
{
    /// <summary>
    /// The Miner (1×1), facing east (the game rotates the sprite toward its gem in 90° steps): a team-coloured
    /// housing with a steel drill head whose teeth step down one pixel per frame (4-frame loop).
    /// </summary>
    public sealed class MinerGen : ISpriteGenerator
    {
        public string Id => nameof(BuildingType.Miner);

        public PixelCanvas[] Generate(int seed)
        {
            ArtEntry entry = Entry(Id);
            var frames = new PixelCanvas[entry.Frames * entry.Directions];
            for (int f = 0; f < frames.Length; f++) frames[f] = Draw(entry, f);
            return frames;
        }

        private static PixelCanvas Draw(ArtEntry entry, int frame)
        {
            PixelCanvas c = Canvas(entry);
            // Housing: a squat block with a lit top edge and a dark base.
            c.Rect(2, 2, 9, 12, Team2);
            c.Rect(2, 12, 9, 2, Team3);
            c.Rect(2, 2, 9, 1, Team0);
            c.Rect(3, 4, 7, 2, Steel1); // vent
            c.Dither(new UnityEngine.RectInt(3, 4, 7, 2), Steel1, Steel0);
            c.Rect(4, 8, 3, 3, Steel2); // hatch
            c.Set(5, 9, Yellow);       // status light
            // Drill shaft and head.
            c.Rect(11, 6, 1, 4, Steel1);
            for (int x = 12; x < 16; x++)
            {
                int half = 15 - x; // tapers toward the tip
                for (int y = 8 - half - 1; y <= 7 + half + 1; y++)
                {
                    if (y < 4 || y > 11) continue;
                    bool tooth = ((y + x + frame) & 3) == 0;
                    c.Set(x, y, tooth ? Steel3 : Steel2);
                }
            }
            c.Outline(Outline);
            return c;
        }
    }
}
