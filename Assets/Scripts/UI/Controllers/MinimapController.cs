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
    /// camera; right-click sends the selection there (the armed order, else Move; Shift queues). v0.15 (Figma v0.15 QoL
    /// redesign, frame 13): the title bar holds an eye button that opens a popover of layer switches (Gems, Enemy ghosts,
    /// Own army, Own buildings; the eye outlines gold while any is off) and an S/M/L size control, both saved with the settings.
    /// </summary>
    public sealed class MinimapController : System.IDisposable
    {
        private const float RedrawSeconds = 0.2f;

        /// <summary>The image size at each <see cref="MinimapSize"/> (px at 1080p; the panel adds its padding and border).</summary>
        public static readonly float[] SizePixels = { 160f, 224f, 304f };

        /// <summary>The panel's width beyond the image: content padding and border.</summary>
        private const float PanelExtra = 20f;

        private static readonly (MinimapLayers layer, string name, string label)[] Layers =
        {
            (MinimapLayers.Gems, "gems", "Gems"),
            (MinimapLayers.Ghosts, "ghosts", "Enemy ghosts"),
            (MinimapLayers.Army, "army", "Own army"),
            (MinimapLayers.Buildings, "buildings", "Own buildings"),
        };

        private readonly VisualElement image, cameraBox, panel, frame, popover, filterRows;
        private readonly Button eye;
        private readonly Button[] sizes = new Button[3];
        private int shownSize = -1, shownFilters = -1;
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

            panel = root.Q("minimap");
            frame = root.Q("minimap-frame");
            popover = root.Q("minimap-filter-popover");
            filterRows = root.Q("minimap-filter-rows");
            eye = root.Q<Button>("minimap-filters");
            if (eye != null) eye.clicked += () => SetFiltersOpen(!FiltersOpen);
            string[] names = { "minimap-scale-s", "minimap-scale-m", "minimap-scale-l" };
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = i;
                sizes[i] = root.Q<Button>(names[i]);
                if (sizes[i] != null) sizes[i].clicked += () => SetSize((MinimapSize)size);
            }
            panel?.RegisterCallback<GeometryChangedEvent>(_ => PlacePopover());
            ShowSettings();
        }

        /// <summary>True while the filter popover is open.</summary>
        public bool FiltersOpen => popover != null && popover.ClassListContains("minimap-popover--open");

        /// <summary>Opens or closes the layer switches above the minimap.</summary>
        public void SetFiltersOpen(bool open)
        {
            if (popover == null) return;
            popover.EnableInClassList("minimap-popover--open", open);
            eye?.EnableInClassList("minimap__eye--open", open);
            if (open) { ShowFilters(); PlacePopover(); }
        }

        /// <summary>Saves the minimap's size (S, M or L) and resizes it.</summary>
        public void SetSize(MinimapSize size)
        {
            WAR2D.Audio.AudioService.Ui("ui_click");
            SettingsStore.Current.MinimapScale = (int)size;
            SettingsStore.Save(SettingsStore.Current);
            ShowSettings();
        }

        /// <summary>Turns a layer on or off, saves it and redraws the minimap at once (the texture reads the filters at each redraw).</summary>
        public void ToggleLayer(MinimapLayers layer)
        {
            WAR2D.Audio.AudioService.Ui("ui_click");
            SettingsStore.Current.MinimapFilters ^= (int)layer;
            SettingsStore.Save(SettingsStore.Current);
            redrawTimer = 0f;
            ShowSettings();
        }

        /// <summary>Applies the saved size and filters when they changed (here or in Settings): sizes, the eye's outline, the switches.</summary>
        private void ShowSettings()
        {
            GameSettings settings = SettingsStore.Current;
            int size = Mathf.Clamp(settings.MinimapScale, 0, 2);
            if (size != shownSize && panel != null && frame != null)
            {
                shownSize = size;
                frame.style.width = SizePixels[size];
                frame.style.height = SizePixels[size];
                panel.style.width = SizePixels[size] + PanelExtra;
                for (int i = 0; i < sizes.Length; i++) sizes[i]?.EnableInClassList("minimap__size--on", i == size);
            }
            if (settings.MinimapFilters != shownFilters)
            {
                shownFilters = settings.MinimapFilters;
                redrawTimer = 0f;
                eye?.EnableInClassList("minimap__eye--filtered", (shownFilters & (int)MinimapLayers.All) != (int)MinimapLayers.All);
                if (FiltersOpen) ShowFilters();
            }
        }

        /// <summary>One switch per layer, each with the swatch the minimap draws for it.</summary>
        private void ShowFilters()
        {
            if (filterRows == null) return;
            filterRows.Clear();
            int own = Mirror.NetworkClient.localPlayer != null ? BuildingData.UIntToInt(Mirror.NetworkClient.localPlayer.netId) : 0;
            foreach ((MinimapLayers layer, string name, string label) in Layers)
            {
                var row = new VisualElement { name = "minimap-filter-" + name };
                row.AddToClassList("minimap-popover__row");
                var swatch = new VisualElement();
                swatch.AddToClassList("minimap-popover__swatch");
                if (layer == MinimapLayers.Army) swatch.AddToClassList("minimap-popover__swatch--small");
                swatch.style.backgroundColor = (Color)(layer == MinimapLayers.Gems ? MapView.TileColor(TileType.Gem)
                    : layer == MinimapLayers.Ghosts ? new Color32(0x6B, 0x7A, 0x8C, 255) : PlayerPalette.OfOwner(own));
                var text = new Label(label);
                text.AddToClassList("minimap-popover__label");
                bool on = SettingsStore.Current.ShowsLayer(layer);
                text.EnableInClassList("minimap-popover__label--off", !on);
                row.Add(swatch);
                row.Add(text);
                row.Add(Widgets.Switch("minimap-filter-" + name + "-switch", on, true, () => ToggleLayer(layer), ""));
                filterRows.Add(row);
            }
        }

        /// <summary>Puts the popover just above the minimap panel, left-aligned with it.</summary>
        private void PlacePopover()
        {
            if (popover == null || panel == null || popover.parent == null || float.IsNaN(panel.layout.height)) return;
            popover.style.left = panel.layout.xMin;
            popover.style.bottom = popover.parent.layout.height - panel.layout.yMin + 8f;
        }

        private System.Collections.Generic.List<(float2 centre, Color32 colour)> starts;

        /// <summary>
        /// While choosing an HQ site nothing sees anything, so the minimap shows the whole terrain dimmed with the start
        /// options marked; null (or none) goes back to the fog. Call every frame.
        /// </summary>
        public void ShowStarts(System.Collections.Generic.List<(float2 centre, Color32 colour)> options)
        {
            bool before = starts != null;
            starts = options != null && options.Count > 0 ? options : null;
            if (before != (starts != null)) redrawTimer = 0f;
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
        public void Ping(int2 tile, AlertKind kind = AlertKind.UnderAttack)
        {
            if (texture == null) return;
            Vector2 at = MinimapTexture.ToLocal((float2)tile + 0.5f, image.layout.size, WorldSize);
            var ring = new VisualElement { name = "minimap-ping", pickingMode = PickingMode.Ignore };
            ring.AddToClassList("minimap-ping");
            if (kind == AlertKind.BombTriggered) ring.AddToClassList("minimap-ping--warning");
            ring.style.left = at.x;
            ring.style.top = at.y;
            Pings.Add(ring);
            ring.schedule.Execute(() => ring.RemoveFromHierarchy()).ExecuteLater(PingMilliseconds);
        }

        /// <summary>Redraws at 5 Hz and moves the camera box every frame.</summary>
        public void Update(float deltaTime)
        {
            ShowSettings();
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
            texture.Begin(fog.State, starts != null);
            foreach (ClientBuildings.Entry entry in ClientBuildings.Current.Entries.Values)
            {
                texture.PlotBuilding(entry.Data.position, PlayerPalette.OfOwner(entry.Data.ownerId), entry.Ghost);
            }
            ClientWorld.Instance?.PlotOn(texture);
            if (starts != null)
                foreach ((float2 centre, Color32 colour) in starts) texture.PlotMarker(centre, colour, 2);
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
