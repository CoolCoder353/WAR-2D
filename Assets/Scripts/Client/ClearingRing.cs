using Unity.Mathematics;
using UnityEngine;

namespace WAR2D.Client
{
    /// <summary>
    /// The local player's HQ clearing during HQ placement (Figma: HUD / HQ placement – own clearing): a faint
    /// disc with a dashed rim in the player's colour. Client-only; the clearing is the player's own public
    /// lobby claim, so it shows nothing hidden.
    /// </summary>
    public sealed class ClearingRing : MonoBehaviour
    {
        private const int Dashes = 48, DiscSegments = 64;
        private const float DashWidth = 0.15f, FillAlpha = 0.06f;

        private static ClearingRing instance;
        private MeshFilter filter;
        private Mesh mesh;
        private (float2 centre, float radius, Color32 colour) shown;

        /// <summary>Shows (or moves) the ring.</summary>
        public static void Show(float2 centre, float radius, Color32 colour)
        {
            if (instance == null)
            {
                var go = new GameObject("ClearingRing");
                instance = go.AddComponent<ClearingRing>();
                instance.filter = go.AddComponent<MeshFilter>();
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
                renderer.sortingOrder = -500; // above the map, below units, buildings and fog
            }
            if (instance.mesh != null && instance.shown.Equals((centre, radius, colour))) return;
            instance.shown = (centre, radius, colour);
            instance.Build(centre, radius, colour);
        }

        /// <summary>Removes the ring.</summary>
        public static void Hide()
        {
            if (instance != null) Destroy(instance.gameObject);
            instance = null;
        }

        private void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
            if (instance == this) instance = null;
        }

        private void Build(float2 centre, float radius, Color32 colour)
        {
            if (mesh == null) mesh = new Mesh { name = "ClearingRing" };
            var vertices = new Vector3[DiscSegments + 1 + Dashes * 4];
            var colours = new Color32[vertices.Length];
            var triangles = new int[DiscSegments * 3 + Dashes * 6];
            Color32 fill = colour;
            fill.a = (byte)(255 * FillAlpha);

            // The disc: a fan around the centre.
            vertices[0] = new Vector3(centre.x, centre.y, 0f);
            colours[0] = fill;
            for (int i = 0; i < DiscSegments; i++)
            {
                float a = 2f * math.PI * i / DiscSegments;
                vertices[1 + i] = new Vector3(centre.x + math.cos(a) * radius, centre.y + math.sin(a) * radius, 0f);
                colours[1 + i] = fill;
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = 1 + (i + 1) % DiscSegments;
                triangles[i * 3 + 2] = 1 + i;
            }

            // The rim: one quad per dash, each covering half its arc.
            int v = DiscSegments + 1, t = DiscSegments * 3;
            for (int d = 0; d < Dashes; d++)
            {
                float a0 = 2f * math.PI * d / Dashes, a1 = a0 + math.PI / Dashes;
                float inner = radius - DashWidth * 0.5f, outer = radius + DashWidth * 0.5f;
                vertices[v] = Point(centre, a0, inner);
                vertices[v + 1] = Point(centre, a0, outer);
                vertices[v + 2] = Point(centre, a1, outer);
                vertices[v + 3] = Point(centre, a1, inner);
                for (int k = 0; k < 4; k++) colours[v + k] = colour;
                triangles[t] = v; triangles[t + 1] = v + 2; triangles[t + 2] = v + 1;
                triangles[t + 3] = v; triangles[t + 4] = v + 3; triangles[t + 5] = v + 2;
                v += 4;
                t += 6;
            }

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.colors32 = colours;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            filter.sharedMesh = mesh;
        }

        private static Vector3 Point(float2 centre, float angle, float r) =>
            new Vector3(centre.x + math.cos(angle) * r, centre.y + math.sin(angle) * r, 0f);
    }
}
