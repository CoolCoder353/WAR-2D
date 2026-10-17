// OverlayLayers.cs
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;
using WAR2D.Net.Replication;
using WAR2D.Sim;

namespace WAR2D.Dev.Overlays
{
    /// <summary>What one layer draws: an optional grid texture (a texel per tile or fog cell) and lines in world space.</summary>
    public sealed class OverlayCanvas
    {
        public int GridWidth, GridHeight;
        public float CellSize = 1f;
        public Color32[] Texels;
        public readonly List<(float2 a, float2 b, Color32 c)> Lines = new List<(float2, float2, Color32)>();

        public void Grid(int width, int height, float cellSize)
        {
            if (Texels == null || Texels.Length != width * height) Texels = new Color32[width * height];
            else System.Array.Clear(Texels, 0, Texels.Length);
            GridWidth = width; GridHeight = height; CellSize = cellSize;
        }

        public void Line(float2 a, float2 b, Color32 c) => Lines.Add((a, b, c));

        public void Clear() { Lines.Clear(); if (Texels != null) System.Array.Clear(Texels, 0, Texels.Length); }
    }

    /// <summary>One debug overlay. Server layers copy what they need in <see cref="OnSettled"/>; <see cref="Draw"/> runs on the main thread.</summary>
    public abstract class OverlayLayer
    {
        public abstract string Name { get; }
        /// <summary>True when the layer reads server data (only the host process can draw it).</summary>
        public virtual bool ServerOnly => true;
        public abstract KeyCode Hotkey { get; }
        /// <summary>The <c>overlay</c> command's extra arguments (e.g. <c>owner</c>).</summary>
        public JObject Args = new JObject();
        public virtual void OnSettled(SimData data, int tick, int count) { }
        public abstract void Draw(OverlayCanvas canvas);
    }

    /// <summary>Tiles under building footprints (<c>MapGrid.Used</c>).</summary>
    public sealed class FootprintsLayer : OverlayLayer
    {
        public override string Name => "Footprints";
        public override KeyCode Hotkey => KeyCode.F7;
        private byte[] used = new byte[0];
        private int w, h;

        public override void OnSettled(SimData data, int tick, int count)
        {
            w = data.Width; h = data.Height;
            if (used.Length != w * h) used = new byte[w * h];
            data.Used.CopyTo(used);
        }

        public override void Draw(OverlayCanvas c)
        {
            c.Grid(w, h, 1f);
            for (int i = 0; i < used.Length; i++) if (used[i] != 0) c.Texels[i] = new Color32(255, 160, 0, 110);
        }
    }

    /// <summary>One owner's fog grid: visible bright, explored dim. Server: <c>owner</c> argument; client: its own fog.</summary>
    public sealed class FogLayer : OverlayLayer
    {
        public override string Name => "Fog";
        public override bool ServerOnly => false;
        public override KeyCode Hotkey => KeyCode.F4;
        private byte[] visible = new byte[0], explored = new byte[0];
        private int fw, fh, cell = 2;

        public override void OnSettled(SimData data, int tick, int count)
        {
            int owner = Args?["owner"]?.Type == JTokenType.Integer ? (int)Args["owner"] : 0;
            int grid = SimContext.Current != null ? SimContext.Current.VisionOf(owner) : -1;
            if (grid < 0) return;
            fw = data.FogW; fh = data.FogH; cell = data.FogCellSize;
            int n = data.FogCells;
            if (visible.Length != n) { visible = new byte[n]; explored = new byte[n]; }
            for (int i = 0; i < n; i++) { visible[i] = data.Visible[grid * n + i]; explored[i] = data.Explored[grid * n + i]; }
        }

        public override void Draw(OverlayCanvas c)
        {
            if (!Mirror.NetworkServer.active && ClientFog.Current?.State != null) // a client: its own fog
            {
                ClientFog fog = ClientFog.Current;
                c.Grid(fog.Width, fog.Height, fog.CellSize);
                for (int i = 0; i < fog.State.Length; i++)
                    c.Texels[i] = fog.State[i] == (byte)FogState.Visible ? new Color32(0, 255, 120, 70) : fog.State[i] == (byte)FogState.Explored ? new Color32(0, 120, 255, 50) : default;
                return;
            }
            c.Grid(fw, fh, cell);
            for (int i = 0; i < visible.Length; i++)
                c.Texels[i] = visible[i] != 0 ? new Color32(0, 255, 120, 70) : explored[i] != 0 ? new Color32(0, 120, 255, 50) : default;
        }
    }

    /// <summary>Units per spatial-hash cell as a heat map.</summary>
    public sealed class HashLayer : OverlayLayer
    {
        public override string Name => "Hash";
        public override KeyCode Hotkey => KeyCode.F6;
        private int[] counts = new int[0];
        private int hw, hh, size;

        public override void OnSettled(SimData data, int tick, int count)
        {
            size = data.CellSize;
            hw = (data.Width + size - 1) / size; hh = (data.Height + size - 1) / size;
            if (counts.Length != hw * hh) counts = new int[hw * hh];
            System.Array.Clear(counts, 0, counts.Length);
            for (int i = 0; i < count; i++)
            {
                float2 p = data.Positions[i];
                int x = math.clamp((int)(p.x / size), 0, hw - 1), y = math.clamp((int)(p.y / size), 0, hh - 1);
                counts[y * hw + x]++;
            }
        }

        public override void Draw(OverlayCanvas c)
        {
            c.Grid(hw, hh, size);
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                float t = math.saturate(math.log2(counts[i] + 1) / 8f);
                c.Texels[i] = (Color32)Color.Lerp(new Color(0f, 0.4f, 1f, 0.35f), new Color(1f, 0f, 0f, 0.6f), t);
            }
        }
    }

    /// <summary>Flow arrows of one order (<c>order</c> argument, else the first unit's order slot with an order).</summary>
    public sealed class FlowLayer : OverlayLayer
    {
        public override string Name => "Flow";
        public override KeyCode Hotkey => KeyCode.F1;
        private readonly List<(float2, float2)> arrows = new List<(float2, float2)>();

        public override void OnSettled(SimData data, int tick, int count)
        {
            arrows.Clear();
            int handle = Args?["order"]?.Type == JTokenType.Integer ? (int)Args["order"] : -1;
            if (handle < 0)
                for (int i = 0; i < count && handle < 0; i++) if (data.OrderSlot[i] >= 0) handle = data.OrderSlot[i];
            if (handle < 0) return;
            var t = data.Orders;
            if (!t.BlockOf.IsCreated) return;
            for (int y = 0; y < data.Height; y += t.CellSize)
                for (int x = 0; x < data.Width; x += t.CellSize)
                {
                    float2 c = new float2(x + t.CellSize * 0.5f, y + t.CellSize * 0.5f);
                    byte d = t.DirectionAt(handle, c, data.Width);
                    if (!WAR2D.Pathing.FlowDirections.IsStep(d)) continue;
                    arrows.Add((c, c + (float2)WAR2D.Pathing.FlowDirections.Step(d) * (t.CellSize * 0.4f)));
                }
        }

        public override void Draw(OverlayCanvas c)
        {
            foreach (var (a, b) in arrows) c.Line(a, b, new Color32(80, 200, 255, 220));
        }
    }

    /// <summary>The selected units' replicated routes (a client layer).</summary>
    public sealed class RoutesLayer : OverlayLayer
    {
        public override string Name => "Routes";
        public override bool ServerOnly => false;
        public override KeyCode Hotkey => KeyCode.F2;
        private readonly List<float2> route = new List<float2>();

        public override void Draw(OverlayCanvas c)
        {
            var world = WAR2D.Client.ClientWorld.Instance;
            var commander = UnitCommander.Instance;
            if (world == null || commander == null) return;
            foreach (int id in commander.Selection.Selected)
            {
                if (!world.CopyRoute(id, route) || !world.TryGet(id, out var view)) continue;
                float2 prev = view.Position;
                foreach (float2 p in route) { c.Line(prev, p, new Color32(255, 230, 0, 230)); prev = p; }
            }
        }
    }

    /// <summary>A line from each unit to its target.</summary>
    public sealed class TargetsLayer : OverlayLayer
    {
        public override string Name => "Targets";
        public override KeyCode Hotkey => KeyCode.F3;
        private readonly List<(float2, float2, Color32)> lines = new List<(float2, float2, Color32)>();

        public override void OnSettled(SimData data, int tick, int count)
        {
            lines.Clear();
            for (int i = 0; i < count; i++)
            {
                int t = data.Target[i];
                if (t < 0 || data.Health[i] <= 0f) continue;
                if (data.TargetKind[i] == 0)
                {
                    if (t >= count) continue;
                    lines.Add((data.Positions[i], data.Positions[t], new Color32(255, 60, 60, 220)));
                }
            }
        }

        public override void Draw(OverlayCanvas c)
        {
            foreach (var (a, b, col) in lines) c.Line(a, b, col);
        }
    }

    /// <summary>Pathing sector borders (32-tile sectors).</summary>
    public sealed class SectorsLayer : OverlayLayer
    {
        public override string Name => "Sectors";
        public override KeyCode Hotkey => KeyCode.F5;
        private int w, h;

        public override void OnSettled(SimData data, int tick, int count) { w = data.Width; h = data.Height; }

        public override void Draw(OverlayCanvas c)
        {
            int s = WAR2D.Pathing.SectorGraph.SectorSize;
            var col = new Color32(255, 255, 255, 150);
            for (int x = 0; x <= w; x += s) c.Line(new float2(x, 0), new float2(x, h), col);
            for (int y = 0; y <= h; y += s) c.Line(new float2(0, y), new float2(w, y), col);
        }
    }

    /// <summary>
    /// Units one owner's client knows (<c>owner</c> argument): the replication service's real interest set after
    /// the last boundary, snapshot pacing included, so it shows what that client was actually sent. Draws nothing
    /// when no client is replicated for the owner.
    /// </summary>
    public sealed class InterestLayer : OverlayLayer
    {
        public override string Name => "Interest";
        public override KeyCode Hotkey => KeyCode.F8;
        /// <summary>The service to read (tests); null reads <see cref="ReplicationService.Instance"/>.</summary>
        public ReplicationService Source;
        private readonly List<float2> inside = new List<float2>();

        public override void OnSettled(SimData data, int tick, int count)
        {
            inside.Clear();
            int owner = Args?["owner"]?.Type == JTokenType.Integer ? (int)Args["owner"] : 0;
            ReplicationService replication = Source ?? ReplicationService.Instance;
            if (replication == null || !replication.TryGetKnown(owner, out var known)) return;
            for (int i = 0; i < count; i++)
            {
                int index = NetIdAllocator.IndexOf(data.IdOf[i]);
                if ((uint)(index >> 6) < (uint)known.Length && BitSet.Get(known, index)) inside.Add(data.Positions[i]);
            }
        }

        public override void Draw(OverlayCanvas c)
        {
            foreach (float2 p in inside) { c.Line(p - new float2(0.3f, 0f), p + new float2(0.3f, 0f), new Color32(0, 255, 0, 255)); c.Line(p - new float2(0f, 0.3f), p + new float2(0f, 0.3f), new Color32(0, 255, 0, 255)); }
        }
    }

    /// <summary>Tiles changed since the match started (dug, built or destroyed terrain).</summary>
    public sealed class TerrainChangesLayer : OverlayLayer
    {
        public override string Name => "Terrain";
        public override KeyCode Hotkey => KeyCode.F9;
        private readonly List<int> changed = new List<int>();
        private int w, h;

        public override void OnSettled(SimData data, int tick, int count)
        {
            w = data.Width; h = data.Height;
            changed.Clear();
            if (SimContext.Current != null) changed.AddRange(SimContext.Current.Map.ChangedSinceStart);
        }

        public override void Draw(OverlayCanvas c)
        {
            if (w == 0) return;
            c.Grid(w, h, 1f);
            foreach (int i in changed) if ((uint)i < (uint)c.Texels.Length) c.Texels[i] = new Color32(255, 0, 255, 120);
        }
    }
}
