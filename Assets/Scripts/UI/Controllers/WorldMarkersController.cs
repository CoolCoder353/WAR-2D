using System.Collections.Generic;
using Mirror;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace WAR2D.UI
{
    /// <summary>
    /// What the HUD draws over the world (playtest 2026-11-04): pings (Figma K3), and the middle click that sends one.
    /// </summary>
    public sealed class WorldMarkersController
    {
        private const float PingRingTiles = 3f;

        private struct Ping { public float2 At; public float Start; public VisualElement Root; public VisualElement[] Rings; }

        private readonly VisualElement layer;
        private readonly MinimapController minimap;
        private readonly float pingSeconds;
        private readonly List<Ping> pings = new List<Ping>();
        private Vector2 pressedAt;
        private float pressedTime;
        private bool armed;

        public WorldMarkersController(VisualElement root, MinimapController minimap)
        {
            this.minimap = minimap;
            pingSeconds = Config.ConfigLoader.LoadConfig().Pings.ShowSeconds;
            layer = new VisualElement { name = "world-markers", pickingMode = PickingMode.Ignore };
            layer.AddToClassList("world-markers");
            root.Insert(0, layer);
        }

        /// <summary>Called every frame. <paramref name="mayPing"/> is false while spectating, on the end screen or with a menu open.</summary>
        public void Update(bool mayPing)
        {
            ReadPingClick(mayPing);
            PlacePings(Camera.main);
        }

        /// <summary>Shows a ping: rings and a name on the map, a ring on the minimap.</summary>
        public void AddPing(int2 tile, Color32 colour, string name)
        {
            var root = new VisualElement { name = "world-ping", pickingMode = PickingMode.Ignore };
            root.AddToClassList("world-ping");
            var rings = new VisualElement[3];
            for (int i = 0; i < rings.Length; i++)
            {
                var ring = new VisualElement { pickingMode = PickingMode.Ignore };
                ring.AddToClassList("world-ping__ring");
                ring.style.borderTopColor = ring.style.borderBottomColor = ring.style.borderLeftColor = ring.style.borderRightColor = (Color)colour;
                root.Add(ring);
                rings[i] = ring;
            }
            var centre = new VisualElement { pickingMode = PickingMode.Ignore };
            centre.AddToClassList("world-ping__centre");
            centre.style.backgroundColor = (Color)colour;
            root.Add(centre);
            var label = new Label(name) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("world-ping__name");
            root.Add(label);
            layer.Add(root);
            pings.Add(new Ping { At = (float2)tile + 0.5f, Start = Time.unscaledTime, Root = root, Rings = rings });
            minimap.PingColoured(tile, colour, pingSeconds);
        }

        private void ReadPingClick(bool mayPing)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;
            if (GameInput.DragPan.WasPressedThisFrame())
            {
                armed = !GameInput.PointerOverUI;
                pressedAt = mouse.position.ReadValue();
                pressedTime = Time.unscaledTime;
            }
            if (!GameInput.DragPan.WasReleasedThisFrame() || !armed) return;
            armed = false;
            GameCore core = GameCore.Instance;
            if (!mayPing || core == null || core.CurrentState != GameState.Playing || !NetworkClient.active) return;
            if (!PingRules.IsClick(pressedAt, mouse.position.ReadValue(), Time.unscaledTime - pressedTime)) return;
            Vector3 world = GameInput.PointerWorld();
            core.Cmd_Ping(new int2(Mathf.FloorToInt(world.x), Mathf.FloorToInt(world.y)));
        }

        private void PlacePings(Camera camera)
        {
            for (int i = pings.Count - 1; i >= 0; i--)
            {
                Ping ping = pings[i];
                float age = (Time.unscaledTime - ping.Start) / pingSeconds;
                if (age >= 1f || camera == null || layer.panel == null)
                {
                    ping.Root.RemoveFromHierarchy();
                    pings.RemoveAt(i);
                    continue;
                }
                Vector2 p = ToPanel(camera, ping.At);
                ping.Root.style.left = p.x;
                ping.Root.style.top = p.y;
                // Three rings grow from the tile in turn, in tiles, so they stay on the ground as the camera zooms.
                float tilePixels = Mathf.Abs(ToPanel(camera, ping.At + new float2(1f, 0f)).x - p.x);
                for (int r = 0; r < ping.Rings.Length; r++)
                {
                    float t = Mathf.Repeat(age * 3f + r / 3f, 1f);
                    float size = Mathf.Max(12f, t * PingRingTiles * 2f * tilePixels);
                    VisualElement ring = ping.Rings[r];
                    ring.style.width = ring.style.height = size;
                    ring.style.marginLeft = ring.style.marginTop = -size / 2f;
                    ring.style.opacity = 1f - t;
                }
            }
        }

        private Vector2 ToPanel(Camera camera, float2 world)
        {
            Vector3 screen = camera.WorldToScreenPoint(new Vector3(world.x, world.y, 0f));
            return RuntimePanelUtils.ScreenToPanel(layer.panel, new Vector2(screen.x, Screen.height - screen.y));
        }
    }
}
