using System;
using System.Collections.Generic;
using Config;
using UnityEngine;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>The small diagrams on the manual's rules pages (v0.18), as approved on the mock, drawn to scale from the config.</summary>
    public static class ManualDiagrams
    {
        private static readonly Color Good = new Color(0.357f, 0.89f, 0.49f), Warn = new Color(1f, 0.604f, 0.239f), Bad = new Color(1f, 0.322f, 0.388f), Blue = new Color(0.302f, 0.639f, 1f), Green = new Color(0.275f, 0.769f, 0.42f), Red = new Color(0.898f, 0.282f, 0.302f), Line = new Color(0.322f, 0.384f, 0.478f), Panel = new Color(0.204f, 0.251f, 0.298f), Muted = new Color(0.682f, 0.733f, 0.796f);

        /// <summary>The diagram with this id, or null.</summary>
        public static VisualElement Build(string id, GameConfigData config)
        {
            VisualElement d = id switch
            {
                "economy" => Economy(config),
                "fog" => Fog(config),
                "diplomacy" => Diplomacy(),
                "siege" => Siege(config),
                "winning" => Winning(),
                "combat" => Spotting(config),
                _ => null,
            };
            d?.AddToClassList("manual__diagram");
            return d;
        }

        private static VisualElement Economy(GameConfigData c)
        {
            float passive = c.Resources.PassiveGenerationRate, mining = c.Resources.MiningRate;
            var rows = new List<(string name, float value, bool income)>
            {
                ("Passive income", passive, true), ("One Miner", mining, true), ("Miner on a rich gem", mining * c.Resources.RichGemMultiplier, true),
                ("10 Tanks, upkeep", 10f * c.GetUnit(UnitType.Tank).RunningCost, false),
                ("One Barracks, upkeep", c.GetBuilding(BuildingType.Barracks).RunningCost, false),
                ("One Heavy Factory, upkeep", c.GetBuilding(BuildingType.HeavyFactory).RunningCost, false),
            };
            float max = 1f;
            foreach (var r in rows) max = Mathf.Max(max, r.value);
            var root = new VisualElement();
            foreach ((string name, float value, bool income) in rows)
            {
                var row = new VisualElement();
                row.AddToClassList("manual__bar-row");
                var label = new Label(name);
                label.AddToClassList("manual__bar-label");
                var bar = new VisualElement();
                bar.AddToClassList("manual__bar");
                bar.style.backgroundColor = income ? Good : Warn;
                bar.style.width = value / max * 400f;
                var number = new Label((income ? "+" : "−") + ManualModel.Number(value) + " /s");
                number.AddToClassList("manual__bar-value");
                row.Add(label);
                row.Add(bar);
                row.Add(number);
                root.Add(row);
            }
            return root;
        }

        private static Label At(string text, float x, float y, bool muted = true)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("manual__diagram-text");
            label.style.position = Position.Absolute;
            label.style.left = x;
            label.style.top = y;
            label.style.color = muted ? Muted : Color.white;
            return label;
        }

        private static void Rect(Painter2D p, float x, float y, float w, float h, Color c)
        {
            p.fillColor = c;
            p.BeginPath();
            p.MoveTo(new Vector2(x, y));
            p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h));
            p.LineTo(new Vector2(x, y + h));
            p.ClosePath();
            p.Fill();
        }

        private static VisualElement Canvas(float w, float h, Action<Painter2D> draw)
        {
            var e = new VisualElement();
            e.style.width = w;
            e.style.height = h;
            e.generateVisualContent += m => draw(m.painter2D);
            return e;
        }

        /// <summary>
        /// v0.20 spotting (the Combat page): the Artillery's sight and reach as bars to scale, a Scout ahead of it whose sight covers an
        /// enemy behind a wall, which the shell flies over. The same layout as the mock's.
        /// </summary>
        private static VisualElement Spotting(GameConfigData c)
        {
            const float Tile = 16f;
            const int Span = 44;
            UnitConfig art = c.GetUnit(UnitType.Artillery), scout = c.GetUnit(UnitType.Scout);
            float scoutAt = Mathf.Min(Span - 4, art.Sight + 4), enemyAt = Mathf.Min(art.Range - 2, scoutAt + scout.Sight - 2), wallAt = Mathf.Round((scoutAt + enemyAt) / 2f);
            var e = Canvas(Span * Tile, 150f, p =>
            {
                void Bar(float y, float from, float to, Color colour) => Rect(p, from * Tile, y, Mathf.Max(0f, (Mathf.Min(Span, to) - from) * Tile), 14f, new Color(colour.r, colour.g, colour.b, 0.5f));
                Bar(22f, 0f, art.Sight, Good);
                Bar(58f, art.MinRange, art.Range, Bad);
                Bar(94f, Mathf.Max(0f, scoutAt - scout.Sight), scoutAt + scout.Sight, Blue);
                Rect(p, 0f, 124f, Tile, Tile, Blue);
                Rect(p, scoutAt * Tile, 124f, Tile * 0.8f, Tile * 0.8f, Blue);
                Rect(p, wallAt * Tile, 112f, Tile * 0.6f, Tile * 2f, new Color(0.42f, 0.478f, 0.549f));
                Rect(p, enemyAt * Tile, 124f, Tile, Tile, Red);
            });
            e.Add(At($"Artillery sight {ManualModel.Number(art.Sight)}", 4f, 2f));
            e.Add(At($"Artillery range {ManualModel.Number(art.MinRange)} to {ManualModel.Number(art.Range)}", art.MinRange * Tile + 4f, 38f));
            e.Add(At($"Scout sight {ManualModel.Number(scout.Sight)}", Mathf.Max(0f, scoutAt - scout.Sight) * Tile + 4f, 74f));
            e.Add(At("Artillery", 0f, 132f));
            e.Add(At("Scout", scoutAt * Tile, 132f));
            e.Add(At("Enemy", enemyAt * Tile, 132f));
            return e;
        }

        private static VisualElement Fog(GameConfigData c)
        {
            const int Cols = 34, Rows = 14;
            const float Px = 20f;
            float sight = c.GetUnit(UnitType.Tank).Sight, size = c.Simulation.FogCellSize;
            int ux = 12, uy = 7;
            var e = Canvas(Cols * Px, Rows * Px + 32f, p =>
            {
                for (int y = 0; y < Rows; y++)
                    for (int x = 0; x < Cols; x++)
                    {
                        float d = Mathf.Sqrt((x - ux) * (x - ux) + (y - uy) * (y - uy)) * size;
                        bool wall = x == 18 && y >= 4 && y <= 10;
                        bool shadow = x > 18 && Mathf.Abs((y - uy) / (float)(x - ux)) < 3.5f / 6f + 0.02f;
                        Color colour = wall ? new Color(0.42f, 0.478f, 0.549f) : d <= sight && !shadow ? new Color(0.353f, 0.498f, 0.353f) : x < 18 ? new Color(0.184f, 0.231f, 0.212f) : new Color(0.043f, 0.059f, 0.075f);
                        Rect(p, x * Px, y * Px, Px - 1f, Px - 1f, colour);
                    }
                Rect(p, ux * Px + 3f, uy * Px + 3f, 14f, 14f, Blue);
            });
            e.Add(At("Tank", ux * Px - 22f, uy * Px - 22f, false));
            e.Add(At("Explored", 20f, Rows * Px + 6f));
            e.Add(At($"Visible (sight {ManualModel.Number(sight)})", ux * Px - 8f, Rows * Px + 6f));
            e.Add(At("Wall", 18 * Px - 14f, Rows * Px + 6f));
            e.Add(At("Unexplored", 24 * Px, Rows * Px + 6f));
            return e;
        }

        private static void Arrow(Painter2D p, Vector2 from, Vector2 to, Color c)
        {
            p.strokeColor = c;
            p.lineWidth = 3f;
            p.BeginPath();
            p.MoveTo(from);
            p.LineTo(to);
            p.Stroke();
            Vector2 dir = (to - from).normalized, side = new Vector2(-dir.y, dir.x);
            p.fillColor = c;
            p.BeginPath();
            p.MoveTo(to);
            p.LineTo(to - dir * 10f + side * 5f);
            p.LineTo(to - dir * 10f - side * 5f);
            p.ClosePath();
            p.Fill();
        }

        private static void Box(Painter2D p, float cx, float cy, Color c)
        {
            p.fillColor = Panel;
            p.strokeColor = c;
            p.lineWidth = 3f;
            p.BeginPath();
            p.MoveTo(new Vector2(cx - 56f, cy - 20f));
            p.LineTo(new Vector2(cx + 56f, cy - 20f));
            p.LineTo(new Vector2(cx + 56f, cy + 20f));
            p.LineTo(new Vector2(cx - 56f, cy + 20f));
            p.ClosePath();
            p.Fill();
            p.Stroke();
        }

        private static VisualElement Diplomacy()
        {
            var e = Canvas(700f, 170f, p =>
            {
                Arrow(p, new Vector2(544f, 30f), new Vector2(158f, 66f), Bad);
                Arrow(p, new Vector2(158f, 90f), new Vector2(542f, 130f), Good);
                Box(p, 100f, 80f, Blue);
                Box(p, 600f, 30f, Red);
                Box(p, 600f, 140f, Green);
            });
            e.Add(At("You", 86f, 68f, false));
            e.Add(At("IronFox", 570f, 18f, false));
            e.Add(At("bluemoss", 566f, 128f, false));
            e.Add(At("attacks you", 310f, 22f));
            e.Add(At("you share vision", 290f, 128f));
            e.Add(At("You don't have to", 40f, 114f));
            e.Add(At("attack back", 60f, 136f));
            return e;
        }

        private static void Disc(Painter2D p, float cx, float cy, float r, Color c)
        {
            p.fillColor = new Color(c.r, c.g, c.b, 0.22f);
            p.strokeColor = c;
            p.lineWidth = 2f;
            p.BeginPath();
            p.Arc(new Vector2(cx, cy), r, 0f, 360f);
            p.ClosePath();
            p.Fill();
            p.Stroke();
        }

        private static VisualElement Siege(GameConfigData c)
        {
            const float T = 22f;
            const int W = 30, H = 8;
            float bomb = c.Siege.BlastRadius, bomber = c.GetUnit(UnitType.Bomber).BlastRadius;
            var e = Canvas(W * T, H * T + 34f, p =>
            {
                for (int x = 0; x <= W; x++) Rect(p, x * T, 0f, 1f, H * T, Panel);
                for (int y = 0; y <= H; y++) Rect(p, 0f, y * T, W * T, 1f, Panel);
                Disc(p, 5 * T, 4 * T, bomb * T, Warn);
                Rect(p, 5 * T - T / 2f, 4 * T - T / 2f, T, T, Warn);
                Disc(p, 14 * T, 4 * T, bomber * T, Bad);
                Rect(p, 14 * T - T / 2f, 4 * T - T / 2f, T, T, Bad);
                Rect(p, 21 * T, 3 * T, 2 * T, 2 * T, Blue);
            });
            e.Add(At($"Mine, radius {ManualModel.Number(bomb)}", 3 * T - 10f, H * T + 6f));
            e.Add(At($"Bomber, radius {ManualModel.Number(bomber)}", 12 * T - 10f, H * T + 6f));
            e.Add(At("Barracks (2 × 2)", 20 * T - 10f, H * T + 6f));
            e.Add(At("1 square = 1 tile", (W - 6) * T, 2f));
            return e;
        }

        private static VisualElement Winning()
        {
            string[] steps = { "HQ destroyed", "Eliminated: all you own is lost", "You can watch the rest", "Survivors at peace: they win" };
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            foreach (string s in steps)
            {
                var box = new Label(s) { pickingMode = PickingMode.Ignore };
                box.AddToClassList("manual__step");
                row.Add(box);
            }
            return row;
        }
    }
}
