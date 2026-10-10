using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;
using WAR2D.Client;
using WAR2D.Net.Replication;

namespace WAR2D.UI
{
    /// <summary>
    /// The minimap (<c>Assets/UI/Hud/Minimap.uxml</c>): a <see cref="MinimapTexture"/> redrawn at 5 Hz from
    /// the client's fog, units and building records, plus the camera's box. Left-click or drag moves the
    /// camera; right-click sends the selection there (the armed order, else Move; Shift queues).
    /// </summary>
    public sealed class MinimapController : System.IDisposable
    {
        private const float RedrawSeconds = 0.2f;

        private readonly VisualElement image, cameraBox;
        private MinimapTexture texture;
        private float redrawTimer;
        private bool dragging;

        /// <summary>The element pings are drawn into (alerts, Task 11).</summary>
        public VisualElement Pings { get; }

        /// <summary>The texture being shown (null before the first fog).</summary>
        public MinimapTexture Texture => texture;

        public MinimapController(VisualElement root)
        {
            image = root.Q("minimap-image");
            cameraBox = root.Q("minimap-camera");
            Pings = root.Q("minimap-pings");
            image.RegisterCallback<PointerDownEvent>(OnPointerDown);
            image.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            image.RegisterCallback<PointerUpEvent>(OnPointerUp);
        }

        /// <summary>The world size the minimap covers (fog grid × cell size), or zero before the first fog.</summary>
        public float2 WorldSize => texture == null ? float2.zero : new float2(texture.Width, texture.Height) * texture.CellSize;

        /// <summary>Handles a click at a position on the image (from its top-left): 0 moves the camera, 1 orders the selection.</summary>
        public void Click(Vector2 local, int button, bool queue)
        {
            if (texture == null) return;
            float2 world = MinimapTexture.ToWorld(local, image.layout.size, WorldSize);
            if (button == 0)
            {
                Camera cam = Camera.main;
                if (cam != null && cam.TryGetComponent(out Character.Character_Controler controller)) controller.CentreOn(world);
                else if (cam != null) cam.transform.position = new Vector3(world.x, world.y, cam.transform.position.z);
            }
            else if (button == 1)
            {
                UnitCommander.Instance?.OrderAt((int2)math.floor(world), queue);
            }
        }

        private const long PingMilliseconds = 3000;

        /// <summary>Shows a ring at a world tile for a few seconds (an UnderAttack alert).</summary>
        public void Ping(int2 tile)
        {
            if (texture == null) return;
            Vector2 at = MinimapTexture.ToLocal((float2)tile + 0.5f, image.layout.size, WorldSize);
            var ring = new VisualElement { name = "minimap-ping", pickingMode = PickingMode.Ignore };
            ring.AddToClassList("minimap-ping");
            ring.style.left = at.x;
            ring.style.top = at.y;
            Pings.Add(ring);
            ring.schedule.Execute(() => ring.RemoveFromHierarchy()).ExecuteLater(PingMilliseconds);
        }

        /// <summary>Redraws at 5 Hz and moves the camera box every frame.</summary>
        public void Update(float deltaTime)
        {
            ClientFog fog = ClientFog.Current;
            WorldStateManager wsm = WorldStateManager.Instance;
            if (fog.State == null || wsm == null || wsm.Map == null) return;
            if (texture == null || texture.Width != fog.Width || texture.Height != fog.Height || texture.CellSize != fog.CellSize)
            {
                texture?.Dispose();
                var grid = wsm.Map.Grid;
                texture = new MinimapTexture(fog.Width, fog.Height, fog.CellSize, grid.TileAt);
                image.style.backgroundImage = new StyleBackground(texture.Texture);
                float aspect = (float)fog.Width / fog.Height;
                image.style.width = aspect >= 1f ? Length.Percent(100) : Length.Percent(100 * aspect);
                image.style.height = aspect >= 1f ? Length.Percent(100 / aspect) : Length.Percent(100);
                redrawTimer = 0f;
            }

            redrawTimer -= deltaTime;
            if (redrawTimer <= 0f)
            {
                redrawTimer = RedrawSeconds;
                Redraw(fog);
            }
            ShowCamera();
        }

        /// <summary>Redraws the texture now: terrain under fog, buildings and ghosts, then units.</summary>
        public void Redraw(ClientFog fog)
        {
            texture.Begin(fog.State);
            foreach (ClientBuildings.Entry entry in ClientBuildings.Current.Entries.Values)
            {
                texture.PlotBuilding(entry.Data.position, PlayerPalette.OfOwner(entry.Data.ownerId), entry.Ghost);
            }
            ClientWorld.Instance?.PlotOn(texture);
            texture.End();
        }

        private void ShowCamera()
        {
            Camera cam = Camera.main;
            Vector2 size = image.layout.size;
            if (cam == null || float.IsNaN(size.x) || size.x <= 0f) return;
            float halfH = cam.orthographicSize, halfW = halfH * cam.aspect;
            Vector3 c = cam.transform.position;
            Vector2 topLeft = MinimapTexture.ToLocal(new float2(c.x - halfW, c.y + halfH), size, WorldSize);
            Vector2 bottomRight = MinimapTexture.ToLocal(new float2(c.x + halfW, c.y - halfH), size, WorldSize);
            cameraBox.style.left = topLeft.x;
            cameraBox.style.top = topLeft.y;
            cameraBox.style.width = bottomRight.x - topLeft.x;
            cameraBox.style.height = bottomRight.y - topLeft.y;
        }

        private void OnPointerDown(PointerDownEvent e)
        {
            Click(e.localPosition, e.button, e.shiftKey);
            if (e.button != 0) return;
            dragging = true;
            image.CapturePointer(e.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (dragging) Click(e.localPosition, 0, false);
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (!dragging) return;
            dragging = false;
            image.ReleasePointer(e.pointerId);
        }

        public void Dispose() => texture?.Dispose();
    }
}
