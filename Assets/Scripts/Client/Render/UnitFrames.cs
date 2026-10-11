using System;
using Unity.Mathematics;
using WAR2D.Art;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// Picks a unit's atlas cell: its facing selects one of the rendered directions (quads never rotate), and
    /// its state the frame (style guide: move 2 frames, then the action 2, at 10 fps). A unit standing still
    /// holds frame 0 of its direction.
    /// </summary>
    public static class UnitFrames
    {
        /// <summary>Move frames come first, then the action (fire) frames.</summary>
        public const int MoveFrames = 2, ActionFrames = 2;

        /// <summary>How long an attack shows its action frames.</summary>
        public const float ActionSeconds = (float)ActionFrames / ArtStyle.FramesPerSecond;

        /// <summary>The direction index for a facing (0 = east, anticlockwise), rounding to the nearest.</summary>
        public static int Direction(float rotationRad, int directions)
        {
            float turns = rotationRad / (2f * math.PI) * directions;
            int d = (int)math.round(turns) % directions;
            return d < 0 ? d + directions : d;
        }

        /// <summary>The atlas cell for a unit of type <paramref name="t"/> (slow: looks the type up by name; see <see cref="Table"/>).</summary>
        public static uint Frame(ArtAtlas atlas, UnitType t, float rotationRad, bool moving, float time, float sinceAction = float.MaxValue, int phase = 0)
        {
            string id = t.ToString();
            int first = atlas.FirstFrame(id);
            AtlasSprite s = atlas.Data.sprites.Find(x => x.id == id);
            return Cell(first, s.frames, s.directions, rotationRad, moving, time, sinceAction, phase);
        }

        /// <summary>
        /// The cell from a sprite's first cell, frame and direction counts. Acting (within
        /// <see cref="ActionSeconds"/> of an attack) beats moving; <paramref name="phase"/> staggers the
        /// move cycle between units.
        /// </summary>
        public static uint Cell(int first, int frames, int directions, float rotationRad, bool moving, float time, float sinceAction, int phase)
        {
            int frame = 0;
            if (sinceAction >= 0 && sinceAction < ActionSeconds && frames >= MoveFrames + ActionFrames)
                frame = MoveFrames + math.min(ActionFrames - 1, (int)(sinceAction * ArtStyle.FramesPerSecond));
            else if (moving && frames >= MoveFrames)
                frame = (int)(((long)math.floor(time * ArtStyle.FramesPerSecond) + phase) & 1);
            return (uint)(first + Direction(rotationRad, directions) * frames + frame);
        }

        /// <summary>Each unit type's first cell, frame count and directions in an atlas, indexed by <see cref="UnitType"/>.</summary>
        public sealed class Table
        {
            public readonly int[] First, Frames, Directions;

            public Table(ArtAtlas atlas)
            {
                var types = (UnitType[])Enum.GetValues(typeof(UnitType));
                int n = 0;
                foreach (UnitType t in types) n = math.max(n, (int)t + 1);
                First = new int[n];
                Frames = new int[n];
                Directions = new int[n];
                // Fallback for a type the atlas lacks: the Tank.
                AtlasSprite tank = atlas.Data.sprites.Find(x => x.id == nameof(UnitType.Tank));
                for (int i = 0; i < n; i++)
                {
                    string id = ((UnitType)i).ToString();
                    AtlasSprite s = atlas.Data.sprites.Find(x => x.id == id) ?? tank;
                    First[i] = s.first;
                    Frames[i] = s.frames;
                    Directions[i] = s.directions;
                }
            }

            /// <summary>The cell for a unit (see <see cref="UnitFrames.Cell"/>).</summary>
            public uint Cell(int type, float rotationRad, bool moving, float time, float sinceAction, int phase)
            {
                int t = (uint)type < (uint)First.Length ? type : 0;
                return UnitFrames.Cell(First[t], Frames[t], Directions[t], rotationRad, moving, time, sinceAction, phase);
            }
        }
    }
}
