using UnityEngine;
using UnityEngine.Rendering.Universal;
using WAR2D.Art;

namespace Character
{
    /// <summary>
    /// RTS camera: pan with the arrow keys (Shift = faster), at the screen edges (Settings → Edge
    /// scrolling) or by dragging with the middle mouse button; zoom with the wheel towards the cursor in
    /// <see cref="CameraZoom"/> steps. It starts at 1:1, and the Pixel Perfect Camera (added here, not in the
    /// scene) is on only at whole-number scales.
    /// </summary>
    public class Character_Controler : MonoBehaviour
    {
        public Character_Settings settings;
        public Camera playerCamera;

        private const float ReferenceSize = 5f;
        private const float EdgePixels = 8f;
        private Vector3? dragOrigin;
        private PixelPerfectCamera pixelPerfect;
        private int lastScreenHeight;

        private void Start()
        {
            if (playerCamera == null || settings == null) return;
            if (!playerCamera.TryGetComponent(out pixelPerfect)) pixelPerfect = playerCamera.gameObject.AddComponent<PixelPerfectCamera>();
            pixelPerfect.assetsPPU = ArtStyle.PixelsPerUnit;
            pixelPerfect.gridSnapping = PixelPerfectCamera.GridSnapping.PixelSnapping;
            pixelPerfect.cropFrame = PixelPerfectCamera.CropFrame.None;
            SetSize(CameraZoom.Default(ArtStyle.PixelsPerUnit, Screen.height, settings.zoomScale.x, settings.zoomScale.y));
        }

        /// <summary>
        /// Sets the zoom; at a whole-number scale the Pixel Perfect Camera takes over with a reference
        /// resolution that gives exactly that scale, otherwise it is off and the size is used as is.
        /// </summary>
        private void SetSize(float size)
        {
            int ppu = ArtStyle.PixelsPerUnit;
            lastScreenHeight = Screen.height;
            bool snap = CameraZoom.PixelPerfect(size, ppu, Screen.height);
            if (pixelPerfect != null)
            {
                if (snap)
                {
                    int k = Mathf.RoundToInt(CameraZoom.Scale(size, ppu, Screen.height));
                    pixelPerfect.refResolutionX = Mathf.Max(1, Screen.width / k);
                    pixelPerfect.refResolutionY = Mathf.Max(1, Screen.height / k);
                }
                pixelPerfect.enabled = snap;
            }
            playerCamera.orthographicSize = size;
        }

        /// <summary>Panning from the pointer at a screen edge (when edge scrolling is on and the window has focus).</summary>
        private static Vector2 EdgePan()
        {
            if (!SettingsStore.Current.EdgeScroll || !Application.isFocused || Application.isBatchMode) return Vector2.zero;
            Vector2 p = GameInput.Point.ReadValue<Vector2>();
            if (p.x < 0 || p.y < 0 || p.x > Screen.width || p.y > Screen.height) return Vector2.zero;
            return new Vector2(p.x <= EdgePixels ? -1 : p.x >= Screen.width - EdgePixels ? 1 : 0,
                               p.y <= EdgePixels ? -1 : p.y >= Screen.height - EdgePixels ? 1 : 0);
        }

        /// <summary>Centres the camera on a world point, kept inside the map (the minimap's click).</summary>
        public void CentreOn(Vector2 point)
        {
            Vector3 next = new Vector3(point.x, point.y, transform.position.z);
            WorldStateManager world = WorldStateManager.Instance;
            if (world != null && world.Map != null)
            {
                var (min, max) = world.MapBounds;
                next.x = Mathf.Clamp(next.x, min.x, max.x + 1);
                next.y = Mathf.Clamp(next.y, min.y, max.y + 1);
            }
            transform.position = next;
        }

        private void Update()
        {
            if (playerCamera == null || settings == null) return;

            Vector3 next = transform.position;
            float scroll = GameInput.Zoom.ReadValue<float>();
            // A resized window keeps its whole-number scale.
            if (Screen.height != lastScreenHeight && pixelPerfect != null && pixelPerfect.enabled)
            {
                int k = Mathf.RoundToInt(CameraZoom.Scale(playerCamera.orthographicSize, ArtStyle.PixelsPerUnit, lastScreenHeight));
                SetSize(Mathf.Max(settings.zoomScale.x, CameraZoom.SizeAt(Mathf.Max(1, k), ArtStyle.PixelsPerUnit, Screen.height)));
            }
            if (scroll != 0f && !GameInput.PointerOverUI)
            {
                // Zoom towards the cursor: the world point under it stays under it.
                Vector3 before = GameInput.PointerWorld();
                SetSize(CameraZoom.Step(playerCamera.orthographicSize, scroll, ArtStyle.PixelsPerUnit, Screen.height, settings.zoomScale.x, settings.zoomScale.y));
                Vector3 after = GameInput.PointerWorld();
                next += new Vector3(before.x - after.x, before.y - after.y, 0f);
            }

            Vector2 pan = GameInput.Pan.ReadValue<Vector2>() + EdgePan();
            pan = Vector2.ClampMagnitude(pan, 1f);
            float multiplier = GameInput.FastPan.IsPressed() ? settings.shiftSpeedMultiplyer : 1f;
            float zoomFactor = playerCamera.orthographicSize / ReferenceSize;
            next += (Vector3)(pan * settings.speed * multiplier * zoomFactor * Time.unscaledDeltaTime);

            // Middle-drag: the world point grabbed stays under the cursor.
            if (GameInput.DragPan.WasPressedThisFrame() && !GameInput.PointerOverUI) dragOrigin = GameInput.PointerWorld();
            else if (!GameInput.DragPan.IsPressed()) dragOrigin = null;
            if (dragOrigin.HasValue)
            {
                Vector3 now = GameInput.PointerWorld();
                next += new Vector3(dragOrigin.Value.x - now.x, dragOrigin.Value.y - now.y, 0f);
            }
            WorldStateManager world = WorldStateManager.Instance;
            if (world != null && world.Map != null)
            {
                var (min, max) = world.MapBounds;
                next.x = Mathf.Clamp(next.x, min.x, max.x + 1);
                next.y = Mathf.Clamp(next.y, min.y, max.y + 1);
            }
            transform.position = next;
        }
    }
}
