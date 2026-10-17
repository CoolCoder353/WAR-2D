using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// An immediate-mode world overlay: <see cref="Clear"/>, add shapes in world units, then <see cref="Apply"/>.
    /// One mesh with per-vertex colours on a shared material, drawn at a sorting order. Client-only.
    /// </summary>
    public sealed class OverlayMesh : IDisposable
    {
        private static Material material; // shared by every overlay (colours are per vertex), so none leaks one
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Color32> colours = new List<Color32>();
        private readonly List<int> triangles = new List<int>();
        private readonly GameObject go;
        private readonly MeshFilter filter;
        private readonly Mesh mesh;

        public OverlayMesh(string name, int sortingOrder)
        {
            go = new GameObject(name);
            filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            if (material == null) material = new Material(Shader.Find("Sprites/Default")) { name = "OverlayMesh" };
            renderer.sharedMaterial = material;
            renderer.sortingOrder = sortingOrder;
            mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
        }

        /// <summary>Drops every shape.</summary>
        public void Clear()
        {
            vertices.Clear();
            colours.Clear();
            triangles.Clear();
        }

        /// <summary>A quad from four corners in order.</summary>
        public void Quad(float2 a, float2 b, float2 c, float2 d, Color32 colour)
        {
            int v = vertices.Count;
            vertices.Add(new Vector3(a.x, a.y)); vertices.Add(new Vector3(b.x, b.y));
            vertices.Add(new Vector3(c.x, c.y)); vertices.Add(new Vector3(d.x, d.y));
            for (int i = 0; i < 4; i++) colours.Add(colour);
            triangles.Add(v); triangles.Add(v + 1); triangles.Add(v + 2);
            triangles.Add(v); triangles.Add(v + 2); triangles.Add(v + 3);
        }

        /// <summary>A filled rectangle.</summary>
        public void Rect(float2 min, float2 max, Color32 colour) =>
            Quad(min, new float2(min.x, max.y), max, new float2(max.x, min.y), colour);

        /// <summary>A rectangle's outline of the given width, inside its edges; dashed with <paramref name="dash"/> &gt; 0.</summary>
        public void RectOutline(float2 min, float2 max, float width, Color32 colour, float dash = 0f)
        {
            float2 a = min, b = new float2(min.x, max.y), c = max, d = new float2(max.x, min.y);
            float h = width * 0.5f;
            Segment(a + new float2(0, h), d + new float2(0, h), width, colour, dash);
            Segment(b - new float2(0, h), c - new float2(0, h), width, colour, dash);
            Segment(a + new float2(h, 0), b + new float2(h, 0), width, colour, dash);
            Segment(d - new float2(h, 0), c - new float2(h, 0), width, colour, dash);
        }

        /// <summary>A straight line; dashed with <paramref name="dash"/> &gt; 0 (dash and gap the same length).</summary>
        public void Segment(float2 a, float2 b, float width, Color32 colour, float dash = 0f)
        {
            float length = math.distance(a, b);
            if (length < 1e-5f) return;
            float2 dir = (b - a) / length, n = new float2(-dir.y, dir.x) * (width * 0.5f);
            if (dash <= 0f)
            {
                Quad(a - n, a + n, b + n, b - n, colour);
                return;
            }
            for (float s = 0f; s < length; s += dash * 2f)
            {
                float2 p = a + dir * s, q = a + dir * math.min(s + dash, length);
                Quad(p - n, p + n, q + n, q - n, colour);
            }
        }

        /// <summary>A ring (circle outline).</summary>
        public void Ring(float2 centre, float radius, float width, Color32 colour, int segments = 28)
        {
            float inner = radius - width * 0.5f, outer = radius + width * 0.5f;
            for (int i = 0; i < segments; i++)
            {
                float a0 = 2f * math.PI * i / segments, a1 = 2f * math.PI * (i + 1) / segments;
                float2 d0 = new float2(math.cos(a0), math.sin(a0)), d1 = new float2(math.cos(a1), math.sin(a1));
                Quad(centre + d0 * inner, centre + d0 * outer, centre + d1 * outer, centre + d1 * inner, colour);
            }
        }

        /// <summary>An X of the given half-size.</summary>
        public void Cross(float2 centre, float half, float width, Color32 colour)
        {
            Segment(centre - half, centre + half, width, colour);
            Segment(centre + new float2(-half, half), centre + new float2(half, -half), width, colour);
        }

        /// <summary>Uploads the shapes added since <see cref="Clear"/>.</summary>
        public void Apply()
        {
            mesh.Clear();
            if (vertices.Count == 0) return;
            mesh.SetVertices(vertices);
            mesh.SetColors(colours);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
        }

        public void Dispose()
        {
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (go != null) UnityEngine.Object.Destroy(go);
        }
    }
}
