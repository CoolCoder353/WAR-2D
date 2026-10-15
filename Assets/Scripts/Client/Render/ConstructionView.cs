using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// How a building under construction looks (Figma <c>World look – v0.7</c>): the sprite ghosted at 25 % that fills
    /// in with progress, the <c>ConstructionOverlay</c> scaffold frames over the footprint, and a gold progress bar
    /// under it. At 100 % everything goes and the sprite is whole. Lives on the building's GameObject.
    /// </summary>
    public sealed class ConstructionView : MonoBehaviour
    {
        private const float BarHeight = 0.12f, BarGap = 0.08f, StartAlpha = 0.25f, FramesPerSecond = 4f;
        private static Sprite pixel;
        private SpriteRenderer building, overlay, barBack, barFill;
        private float2 size;
        private int frames;
        private ArtAtlas effects;

        /// <summary>The construction progress shown, in percent.</summary>
        public int Progress { get; private set; } = 100;

        /// <summary>Shows <paramref name="progress"/> on a building drawn by <paramref name="sprite"/> over a footprint of <paramref name="footprint"/> tiles.</summary>
        public void Set(SpriteRenderer sprite, int2 footprint, int progress)
        {
            building = sprite;
            size = footprint;
            Progress = math.clamp(progress, 0, 100);
            bool rising = Progress < 100;
            if (rising && overlay == null) Create();
            if (overlay != null)
            {
                overlay.enabled = barBack.enabled = barFill.enabled = rising;
                float width = size.x - 0.2f;
                barFill.transform.localScale = new Vector3(math.max(0.02f, width * Progress / 100f), BarHeight * 0.6f, 1f);
                barFill.transform.localPosition = new Vector3(-width * 0.5f + width * Progress / 200f, -size.y * 0.5f - BarGap - BarHeight * 0.5f, 0f);
            }
            if (building != null)
            {
                Color c = building.color;
                c.a = rising ? math.lerp(StartAlpha, 0.85f, Progress / 100f) : 1f;
                building.color = c;
            }
        }

        private void Create()
        {
            if (pixel == null)
                pixel = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height), new Vector2(0.5f, 0.5f), Texture2D.whiteTexture.width);
            effects = ArtAtlas.Load(ArtAtlas.Effects);
            frames = effects != null ? math.max(1, effects.FrameCount("ConstructionOverlay")) : 0;
            int order = building != null ? building.sortingOrder : SortingOrders.Effects;
            overlay = Child("ConstructionOverlay", order + 1);
            overlay.drawMode = SpriteDrawMode.Simple;
            float width = size.x - 0.2f;
            barBack = Child("ProgressBack", order + 2);
            barBack.sprite = pixel;
            barBack.color = new Color(0f, 0f, 0f, 0.6f);
            barBack.transform.localScale = new Vector3(width, BarHeight, 1f);
            barBack.transform.localPosition = new Vector3(0f, -size.y * 0.5f - BarGap - BarHeight * 0.5f, 0f);
            barFill = Child("Progress", order + 3);
            barFill.sprite = pixel;
            barFill.color = new Color32(0xFF, 0xC8, 0x57, 0xFF); // the HUD's readout gold
        }

        private SpriteRenderer Child(string name, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            SpriteRenderer sprite = go.AddComponent<SpriteRenderer>();
            sprite.sortingOrder = order;
            return sprite;
        }

        private void Update()
        {
            if (overlay == null || !overlay.enabled || frames == 0) return;
            overlay.sprite = effects.Sprite("ConstructionOverlay", (int)(Time.time * FramesPerSecond) % frames);
            Vector2 native = overlay.sprite != null ? (Vector2)overlay.sprite.bounds.size : Vector2.one;
            overlay.transform.localScale = new Vector3(size.x / math.max(native.x, 1e-3f), size.y / math.max(native.y, 1e-3f), 1f);
        }
    }
}
