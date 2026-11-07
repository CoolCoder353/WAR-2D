using System.Collections.Generic;
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
    /// <summary>A start option on the HQ-placement minimap (v0.16, Figma P): the player's own (numbered) or a teammate's.</summary>
    public struct MinimapStart
    {
        public float2 Centre;
        public Color32 Colour;
        /// <summary>The number matching its Option button (own options only).</summary>
        public string Label;
        public bool Own;
    }

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
            (MinimapLayers.Hqs, "hqs", "HQs (flags)"), // Figma "Playtest fixes" item 10
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
            SettingsStore.Current.ToggleLayer(layer);
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
            int filters = (int)settings.ShownLayers;
            if (filters != shownFilters)
            {
                shownFilters = filters;
                redrawTimer = 0f;
                const int every = (int)(MinimapLayers.All | MinimapLayers.Hqs);
                eye?.EnableInClassList("minimap__eye--filtered", (shownFilters & every) != every);
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
                if (layer == MinimapLayers.Gems)
                {
                    // v0.17 (Figma H1): the Gems layer covers rich gems too, in their own colour.
                    var rich = new VisualElement { name = "minimap-filter-rich-swatch" };
                    rich.AddToClassList("minimap-popover__swatch");
                    rich.style.backgroundColor = (Color)MapView.TileColor(TileType.RichGem);
                    row.Add(rich);
                }
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

        private System.Collections.Generic.List<MinimapStart> starts;
        private readonly System.Collections.Generic.List<MinimapStart> shownStarts = new System.Collections.Generic.List<MinimapStart>();
        private readonly System.Collections.Generic.List<VisualElement> startRings = new System.Collections.Generic.List<VisualElement>();
        private readonly System.Collections.Generic.List<Vector2> startLocal = new System.Collections.Generic.List<Vector2>();
        private int markerPixels = -1;

        /// <summary>
        /// While choosing an HQ site nothing sees anything, so the minimap shows the whole (public) terrain at full brightness
        /// with the start options as rings over it (v0.16, Figma P): the player's own numbered like their Option buttons, a
        /// teammate's smaller in their colour. Null (or none) goes back to the fog. Call every frame.
        /// </summary>
        public void ShowStarts(System.Collections.Generic.List<MinimapStart> options)
        {
            bool before = starts != null;
            starts = options != null && options.Count > 0 ? options : null;
            if (before != (starts != null)) redrawTimer = 0f;
            if (!SameStarts()) BuildStartRings();
        }

        /// <summary>The on-screen size of an own option's ring: the configured minimum, never under 12 px.</summary>
        public static float OwnRingPixels(int markerPixels) => Mathf.Max(markerPixels, 12);

        /// <summary>The option whose ring a click at <paramref name="click"/> falls in (nearest first), or -1.</summary>
        public static int SnapToStart(System.Collections.Generic.IReadOnlyList<Vector2> markers, Vector2 click, float radius)
        {
            int best = -1;
            float bestSq = radius * radius;
            for (int i = 0; i < markers.Count; i++)
            {
                float d = (markers[i] - click).sqrMagnitude;
                if (d <= bestSq) { bestSq = d; best = i; }
            }
            return best;
        }

        private bool SameStarts()
        {
            int count = starts?.Count ?? 0;
            if (count != shownStarts.Count) return false;
            for (int i = 0; i < count; i++)
            {
                MinimapStart a = starts[i], b = shownStarts[i];
                if (!a.Centre.Equals(b.Centre) || a.Own != b.Own || a.Label != b.Label || !a.Colour.Equals(b.Colour)) return false;
            }
            return true;
        }

        private void BuildStartRings()
        {
            foreach (VisualElement ring in startRings) ring.RemoveFromHierarchy();
            startRings.Clear();
            shownStarts.Clear();
            if (starts == null) return;
            if (markerPixels < 0) markerPixels = Config.ConfigLoader.LoadConfig().PlacementMinimap.MarkerPixels;
            float own = OwnRingPixels(markerPixels);
            foreach (MinimapStart start in starts)
            {
                shownStarts.Add(start);
                var ring = new VisualElement { name = "minimap-start", pickingMode = PickingMode.Ignore };
                ring.AddToClassList("minimap-start");
                ring.style.borderTopColor = ring.style.borderBottomColor = ring.style.borderLeftColor = ring.style.borderRightColor = (Color)start.Colour;
                if (start.Own)
                {
                    ring.style.width = ring.style.height = own;
                    ring.style.marginLeft = ring.style.marginTop = -own / 2f;
                    var label = new Label(start.Label) { name = "minimap-start-label", pickingMode = PickingMode.Ignore };
                    label.AddToClassList("minimap-start__label");
                    label.style.backgroundColor = (Color)start.Colour;
                    label.style.left = own + 1f;
                    label.style.top = -2f;
                    ring.Add(label);
                }
                else ring.AddToClassList("minimap-start--teammate");
                Pings.Add(ring);
                startRings.Add(ring);
            }
        }

        /// <summary>Places the option rings on the image (every frame: the panel can resize).</summary>
        private void PlaceStartRings()
        {
            if (startRings.Count == 0 || texture == null) return;
            Vector2 size = image.layout.size;
            for (int i = 0; i < startRings.Count; i++)
            {
                Vector2 at = MinimapTexture.ToLocal(shownStarts[i].Centre, size, WorldSize);
                startRings[i].style.left = at.x;
                startRings[i].style.top = at.y;
            }
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
                if (starts != null) world = SnapToOwnStart(local, world);
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

        /// <summary>While placing the HQ, a click inside an own option's ring centres on that option (v0.16, Figma P).</summary>
        private float2 SnapToOwnStart(Vector2 local, float2 world)
        {
            startLocal.Clear();
            Vector2 size = image.layout.size;
            int ownCount = 0;
            foreach (MinimapStart start in starts)
            {
                if (!start.Own) continue;
                startLocal.Add(MinimapTexture.ToLocal(start.Centre, size, WorldSize));
                ownCount++;
            }
            int pick = SnapToStart(startLocal, local, OwnRingPixels(markerPixels < 0 ? 8 : markerPixels));
            if (pick < 0) return world;
            foreach (MinimapStart start in starts)
                if (start.Own && pick-- == 0) return start.Centre;
            return world;
        }

        private readonly System.Collections.Generic.Dictionary<int2, VisualElement> pulseRings = new System.Collections.Generic.Dictionary<int2, VisualElement>();
        private readonly System.Collections.Generic.List<int2> pulseGone = new System.Collections.Generic.List<int2>();
        private readonly System.Collections.Generic.HashSet<int2> pulseLive = new System.Collections.Generic.HashSet<int2>();

        /// <summary>
        /// Fights on the minimap (v0.16, Figma C): one ring per <see cref="MinimapCombat"/> pulse, growing from 30 % to 120 %
        /// and fading over the pulse; the alert colour when the player's own things were hit.
        /// </summary>
        private void ShowPulses()
        {
            MinimapCombat combat = MinimapCombat.Current;
            float now = Time.time;
            combat?.Update(now);
            pulseLive.Clear();
            if (combat != null && texture != null)
            {
                Vector2 size = image.layout.size;
                foreach (MinimapCombat.Pulse pulse in combat.Pulses)
                {
                    pulseLive.Add(pulse.Cell);
                    if (!pulseRings.TryGetValue(pulse.Cell, out VisualElement ring))
                    {
                        ring = new VisualElement { name = "minimap-pulse", pickingMode = PickingMode.Ignore };
                        ring.AddToClassList("minimap-pulse");
                        Pings.Add(ring);
                        pulseRings[pulse.Cell] = ring;
                    }
                    ring.EnableInClassList("minimap-pulse--own", pulse.Own);
                    Vector2 at = MinimapTexture.ToLocal(pulse.Centre, size, WorldSize);
                    float t = math.saturate((now - pulse.Start) / math.max(0.01f, combat.PulseSeconds));
                    float scale = math.lerp(0.3f, 1.2f, t);
                    ring.style.left = at.x;
                    ring.style.top = at.y;
                    ring.style.scale = new Scale(new Vector3(scale, scale, 1f));
                    ring.style.opacity = math.lerp(1f, 0.2f, t);
                }
            }
            if (pulseRings.Count == pulseLive.Count) return;
            pulseGone.Clear();
            foreach (int2 cell in pulseRings.Keys) if (!pulseLive.Contains(cell)) pulseGone.Add(cell);
            foreach (int2 cell in pulseGone) { pulseRings[cell].RemoveFromHierarchy(); pulseRings.Remove(cell); }
        }

        /// <summary>Shows a ring at a world tile for a few seconds (an UnderAttack alert).</summary>
        public void Ping(int2 tile, AlertKind kind = AlertKind.UnderAttack)
        {
            if (texture == null) return;
            Vector2 at = MinimapTexture.ToLocal((float2)tile + 0.5f, image.layout.size, WorldSize);
            var ring = new VisualElement { name = "minimap-ping", pickingMode = PickingMode.Ignore };
            ring.AddToClassList("minimap-ping");
            if (kind == AlertKind.MineTriggered) ring.AddToClassList("minimap-ping--warning");
            ring.style.left = at.x;
            ring.style.top = at.y;
            Pings.Add(ring);
            ring.schedule.Execute(() => ring.RemoveFromHierarchy()).ExecuteLater(PingMilliseconds);
        }

        /// <summary>A ring in a player's colour at a world tile (Figma K3).</summary>
        public void PingColoured(int2 tile, Color32 colour, float seconds)
        {
            if (texture == null) return;
            Vector2 at = MinimapTexture.ToLocal((float2)tile + 0.5f, image.layout.size, WorldSize);
            var ring = new VisualElement { name = "minimap-ping", pickingMode = PickingMode.Ignore };
            ring.AddToClassList("minimap-ping");
            ring.style.borderTopColor = ring.style.borderBottomColor = ring.style.borderLeftColor = ring.style.borderRightColor = (Color)colour;
            ring.style.left = at.x;
            ring.style.top = at.y;
            Pings.Add(ring);
            ring.schedule.Execute(() => ring.RemoveFromHierarchy()).ExecuteLater((long)(seconds * 1000f));
        }

        /// <summary>Redraws at 5 Hz and moves the camera box every frame.</summary>
        public void Update(float deltaTime)
        {
            ShowSettings();
            ClientFog fog = ClientFog.Current;
            WorldStateManager wsm = WorldStateManager.Instance;
            if (wsm == null || wsm.Map == null) return;
            // While placing HQs the sim isn't ticking, so no fog has arrived yet (v0.16, Figma P): size the texture from the
            // map and the fog grid's cell size, and draw the public terrain revealed.
            int width, height, cellSize;
            if (fog.State != null) { width = fog.Width; height = fog.Height; cellSize = fog.CellSize; }
            else if (starts != null)
            {
                cellSize = Mathf.Max(1, Config.ConfigLoader.LoadConfig().Simulation.FogCellSize);
                width = (wsm.Map.Grid.Width + cellSize - 1) / cellSize;
                height = (wsm.Map.Grid.Height + cellSize - 1) / cellSize;
            }
            else return;
            if (texture == null || texture.Width != width || texture.Height != height || texture.CellSize != cellSize)
            {
                texture?.Dispose();
                var grid = wsm.Map.Grid;
                texture = new MinimapTexture(width, height, cellSize, grid.TileAt);
                image.style.backgroundImage = new StyleBackground(texture.Texture);
                float aspect = (float)width / height;
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
            ShowPulses();
            PlaceStartRings();
        }

        /// <summary>Redraws the texture now: terrain under fog, buildings and ghosts, then units, then flags over the known HQs.</summary>
        public void Redraw(ClientFog fog)
        {
            using (BeginMarker.Auto()) texture.Begin(fog.State, starts != null);
            ownerColours.Clear(); // one lookup per owner, not per building (each a component lookup: 1 to 2 ms a redraw with thousands of walls)
            using (BuildingsMarker.Auto())
                foreach (ClientBuildings.Entry entry in ClientBuildings.Current.Entries.Values)
                    texture.PlotBuilding(entry.Data.position, ColourOf(entry.Data.ownerId), entry.Ghost);
            using (UnitsMarker.Auto()) ClientWorld.Instance?.PlotOn(texture);
            // Flags over every HQ the player has found (their own included), on top of the units.
            foreach (ClientBuildings.Entry entry in ClientBuildings.Current.Entries.Values)
                if (entry.Data.buildingType == BuildingType.Base)
                    texture.PlotFlag(entry.Data.position + new float2(2.5f, 2.5f), ColourOf(entry.Data.ownerId), entry.Ghost);
            using (UploadMarker.Auto()) texture.End();
        }

        private readonly Dictionary<int, Color32> ownerColours = new Dictionary<int, Color32>();

        private Color32 ColourOf(int owner)
        {
            if (!ownerColours.TryGetValue(owner, out Color32 colour)) ownerColours[owner] = colour = PlayerPalette.OfOwner(owner);
            return colour;
        }

        private static readonly Unity.Profiling.ProfilerMarker BeginMarker = new Unity.Profiling.ProfilerMarker("Minimap.Begin"),
            BuildingsMarker = new Unity.Profiling.ProfilerMarker("Minimap.Buildings"), UnitsMarker = new Unity.Profiling.ProfilerMarker("Minimap.Units"),
            UploadMarker = new Unity.Profiling.ProfilerMarker("Minimap.Upload");

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
