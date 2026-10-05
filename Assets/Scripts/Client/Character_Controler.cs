using UnityEngine;

namespace Character
{
    /// <summary>RTS camera: pan with WASD/arrows (Shift = faster), zoom with the mouse wheel.</summary>
    public class Character_Controler : MonoBehaviour
    {
        public Character_Settings settings;
        public Camera playerCamera;

        private const float ReferenceSize = 5f;

        private void Update()
        {
            if (playerCamera == null || settings == null) return;

            float scroll = GameInput.Zoom.ReadValue<float>();
            if (scroll != 0f && !GameInput.PointerOverUI)
            {
                float size = playerCamera.orthographicSize - Mathf.Sign(scroll) * settings.zoomStep;
                playerCamera.orthographicSize = Mathf.Clamp(size, settings.zoomScale.x, settings.zoomScale.y);
            }

            Vector2 pan = GameInput.Pan.ReadValue<Vector2>();
            float multiplier = GameInput.FastPan.IsPressed() ? settings.shiftSpeedMultiplyer : 1f;
            float zoomFactor = playerCamera.orthographicSize / ReferenceSize;
            transform.position += (Vector3)(pan * settings.speed * multiplier * zoomFactor * Time.unscaledDeltaTime);
        }
    }
}
