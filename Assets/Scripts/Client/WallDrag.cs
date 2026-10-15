using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Client.Render;

namespace WAR2D.Client
{
    /// <summary>
    /// The wall drag (Figma <c>World look – v0.7</c>, Wall drag preview): armed by the Wall button, a press on the
    /// map sets the start tile, the 4-connected line follows the pointer (at most <c>Siege.MaxWallLine</c> tiles),
    /// each tile tinted in team colour where a wall can go and red where it can't, and the release places it.
    /// What can go where is the client's own knowledge (its terrain and seen buildings); the server checks again.
    /// </summary>
    public sealed class WallDrag
    {
        private readonly List<int2> line = new List<int2>();
        private readonly List<bool> valid = new List<bool>();
        private readonly List<SpriteRenderer> tiles = new List<SpriteRenderer>();
        private readonly Func<int2, TileType> tileAt;
        private readonly Func<int2, bool> taken;
        private readonly int maxLine;
        private Transform root;
        private static Sprite square;

        /// <summary>True from the Wall button until the drag is placed or cancelled.</summary>
        public bool Armed { get; private set; }

        /// <summary>True while the button is held after the press that started the line.</summary>
        public bool Dragging { get; private set; }

        /// <summary>The line's tiles, start first.</summary>
        public IReadOnlyList<int2> Line => line;

        /// <summary>Tiles in the line a wall can go on.</summary>
        public int ValidCount { get; private set; }

        /// <summary>Tiles in the line a wall can't go on.</summary>
        public int BlockedCount => line.Count - ValidCount;

        /// <summary>True when a wall can go on the line's tile <paramref name="i"/>.</summary>
        public bool IsValid(int i) => valid[i];

        /// <param name="taken">True where a seen building (or a bomb the player knows) stands.</param>
        public WallDrag(Func<int2, TileType> tileAt, Func<int2, bool> taken, int maxLine)
        {
            this.tileAt = tileAt;
            this.taken = taken;
            this.maxLine = math.max(1, maxLine);
        }

        public void Arm() => Armed = true;

        /// <summary>Drops the drag and its preview. False when nothing was armed.</summary>
        public bool Cancel()
        {
            if (!Armed) return false;
            Armed = Dragging = false;
            line.Clear();
            valid.Clear();
            ValidCount = 0;
            Draw(default);
            return true;
        }

        /// <summary>Starts the line at a tile.</summary>
        public void Press(int2 tile)
        {
            if (!Armed) return;
            Dragging = true;
            start = tile;
            Move(tile, default);
        }

        private int2 start;

        /// <summary>Re-routes the line to end at <paramref name="tile"/> (cut at the line limit) and redraws it.</summary>
        public void Move(int2 tile, Color team)
        {
            if (!Dragging) return;
            line.Clear();
            WallRules.Line(start, tile, line);
            if (line.Count > maxLine) line.RemoveRange(maxLine, line.Count - maxLine);
            valid.Clear();
            ValidCount = 0;
            foreach (int2 t in line)
            {
                bool ok = WallRules.CanPlace(t, tileAt, taken);
                valid.Add(ok);
                if (ok) ValidCount++;
            }
            Draw(team);
        }

        /// <summary>Ends the drag: the line's ends to send, or false when there was none.</summary>
        public bool Release(out int2 from, out int2 to)
        {
            from = to = default;
            if (!Dragging || line.Count == 0) { Cancel(); return false; }
            from = line[0];
            to = line[line.Count - 1];
            Dragging = false;
            return true;
        }

        /// <summary>Clears the drawn line after a release (the drag stays armed when Shift keeps it).</summary>
        public void ClearLine()
        {
            line.Clear();
            valid.Clear();
            ValidCount = 0;
            Draw(default);
        }

        private void Draw(Color team)
        {
            if (line.Count > 0 && root == null) root = new GameObject("WallDragPreview").transform;
            if (square == null && line.Count > 0)
                square = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height),
                    new Vector2(0.5f, 0.5f), Texture2D.whiteTexture.width);
            while (tiles.Count < line.Count)
            {
                var go = new GameObject("WallDragTile");
                go.transform.SetParent(root, false);
                SpriteRenderer sprite = go.AddComponent<SpriteRenderer>();
                sprite.sprite = square;
                sprite.sortingOrder = SortingOrders.PlacementPreview;
                tiles.Add(sprite);
            }
            Color good = new Color(team.r, team.g, team.b, 0.45f), bad = new Color(1f, 0.32f, 0.39f, 0.55f);
            for (int i = 0; i < tiles.Count; i++)
            {
                bool shown = i < line.Count;
                tiles[i].enabled = shown;
                if (!shown) continue;
                tiles[i].transform.position = new Vector3(line[i].x + 0.5f, line[i].y + 0.5f, 0f);
                tiles[i].color = valid[i] ? good : bad;
            }
        }

        /// <summary>Destroys the preview objects (the match ended).</summary>
        public void Dispose()
        {
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            root = null;
            tiles.Clear();
        }
    }
}
