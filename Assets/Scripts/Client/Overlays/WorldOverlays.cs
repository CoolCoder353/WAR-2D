using Unity.Mathematics;
using UnityEngine;
using WAR2D.Client.Render;

namespace WAR2D.Client.Overlays
{
    /// <summary>What every overlay part needs for one frame: the camera, one screen pixel in tiles, colours, the mesh and tags.</summary>
    public sealed class OverlayFrame
    {
        public Camera Cam;
        /// <summary>One screen pixel in tiles (the overlays are drawn in screen pixels, as <see cref="OrderFeedback"/>).</summary>
        public float Px;
        public float Now;
        public string Palette;
        public Color32 Team, Accent, Warning, Danger, Readout, Success;
        public OverlayMesh Mesh;
        public WorldLabels Labels;
        public UnitCommander Commander;
        public ClientWorld World;
        public float2 Pointer;
        public Vector2 PointerScreen;
        public bool PointerOverUI;
        public int Own;
    }

    /// <summary>
    /// The v0.15 world overlays (Figma <c>v0.15 QoL – redesign</c>): group order feedback (item 10), range rings for
    /// Sniper and Artillery (14), rally spokes and the blueprint rally (8), and the building hover card (12). One
    /// <see cref="OverlayMesh"/> holds every shape, rebuilt each frame; the text goes into the HUD's tag layer through
    /// <see cref="WorldLabels"/>. Everything drawn is the local player's own data or what the client already holds.
    /// Added next to <see cref="UnitCommander"/> on clients.
    /// </summary>
    public sealed class WorldOverlays : MonoBehaviour
    {
        private readonly OverlayFrame frame = new OverlayFrame();
        private readonly WorldLabels labels = new WorldLabels();
        private OverlayMesh mesh;
        private GroupOrderOverlay orders;
        private RangeRingOverlay ranges;
        private RallyOverlay rally;
        private BuildingHoverCard hover;
        private UnitCommander commander;

        /// <summary>The group order feedback (tests and the HUD may read it).</summary>
        public GroupOrderOverlay Orders => orders;

        private void Awake()
        {
            commander = GetComponent<UnitCommander>();
            orders = new GroupOrderOverlay();
            ranges = new RangeRingOverlay();
            rally = new RallyOverlay();
            hover = new BuildingHoverCard();
            if (commander != null) commander.Selection.Ordered += orders.OnOrdered;
        }

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (commander == null || cam == null || ClientWorld.Instance == null)
            {
                labels.HideAll();
                hover.Hide();
                mesh?.Clear();
                mesh?.Apply();
                return;
            }
            mesh ??= new OverlayMesh("WorldOverlays", SortingOrders.DebugOverlay - 2);
            mesh.Clear();
            labels.Begin();

            string palette = Palettes.Current;
            frame.Cam = cam;
            frame.Px = 2f * cam.orthographicSize / Mathf.Max(1, Screen.height);
            frame.Now = Time.unscaledTime;
            frame.Palette = palette;
            frame.Own = commander.LocalOwnerId;
            frame.Team = PlayerPalette.OfOwner(frame.Own);
            frame.Accent = Palettes.Accent(palette);
            frame.Warning = Palettes.Warning(palette);
            frame.Danger = OverlayColours.Danger(palette);
            frame.Readout = OverlayColours.Readout(palette);
            frame.Success = OverlayColours.Success(palette);
            frame.Mesh = mesh;
            frame.Labels = labels;
            frame.Commander = commander;
            frame.World = ClientWorld.Instance;
            Vector3 world = GameInput.PointerWorld();
            frame.Pointer = new float2(world.x, world.y);
            frame.PointerScreen = GameInput.Point.ReadValue<Vector2>();
            frame.PointerOverUI = GameInput.PointerOverUI;

            ranges.Draw(frame);
            rally.Draw(frame);
            orders.Draw(frame);
            hover.Update(frame);

            labels.End();
            mesh.Apply();
        }

        private void OnDestroy()
        {
            if (commander != null) commander.Selection.Ordered -= orders.OnOrdered;
            labels.HideAll();
            hover?.Dispose();
            mesh?.Dispose();
            mesh = null;
        }
    }
}
