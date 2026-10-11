using UnityEngine;
using WAR2D.Art;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// Loops a building's idle frames from an <see cref="ArtAtlas"/> on the <see cref="ArtStyle.FramesPerSecond"/>
    /// clock, holding each frame for a whole number of ticks.
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public sealed class SpriteFrameAnimator : MonoBehaviour
    {
        private SpriteRenderer sprite;
        private ArtAtlas atlas;
        private string id;
        private int frames, hold = 1, shown = -1;
        private float phase;

        /// <summary>
        /// Starts looping <paramref name="spriteId"/>'s frames, each held <paramref name="holdTicks"/> ticks; a
        /// sprite with one frame just shows it.
        /// </summary>
        public void Play(ArtAtlas source, string spriteId, int holdTicks = 1)
        {
            hold = Mathf.Max(1, holdTicks);
            sprite = GetComponent<SpriteRenderer>();
            atlas = source;
            id = spriteId;
            frames = atlas != null ? atlas.FrameCount(id) : 0;
            // Stagger neighbours so a row of drills doesn't move in lockstep.
            phase = (transform.position.x * 0.37f + transform.position.y * 0.61f) % 1f;
            shown = -1;
            enabled = frames > 1;
            Show(0);
        }

        private void Update() => Show((int)((Time.time + phase) * ArtStyle.FramesPerSecond) / hold % frames);

        private void Show(int frame)
        {
            if (atlas == null || frame == shown) return;
            shown = frame;
            sprite.sprite = atlas.Sprite(id, frame);
        }
    }
}
