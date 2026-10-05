using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace WAR2D.Spike
{
    /// <summary>
    /// Today's way, one <see cref="GameObject"/> and <see cref="SpriteRenderer"/> per unit, moved by
    /// setting <c>transform.position</c> each frame (no DOTween). Its only purpose is to price the
    /// gain the instanced renderer buys: same units, same client path following, same camera.
    ///
    /// <para>Damaged units get a child bar sprite, the way the game would spend another GameObject on
    /// it; the children carry their own rotation-free SpriteRenderer and are scaled by the health
    /// fraction at creation (health does not change in this benchmark). Sprites are not rotated: the
    /// brief's baseline moves them by position only.</para>
    /// </summary>
    public sealed class SpriteBaseline : IDisposable
    {
        private readonly GameObject root;
        private readonly Transform[] transforms;
        private readonly Sprite unitSprite, barSprite;
        private readonly Texture2D barTexture;

        /// <summary>Unit GameObjects created.</summary>
        public int UnitObjects { get; }

        /// <summary>Health-bar child GameObjects created (damaged units only).</summary>
        public int BarObjects { get; }

        private SpriteBaseline(GameObject root, Transform[] transforms, Sprite unitSprite, Sprite barSprite,
            Texture2D barTexture, int unitObjects, int barObjects)
        {
            this.root = root;
            this.transforms = transforms;
            this.unitSprite = unitSprite;
            this.barSprite = barSprite;
            this.barTexture = barTexture;
            UnitObjects = unitObjects;
            BarObjects = barObjects;
        }

        /// <summary>
        /// Builds one GameObject per unit plus a bar child for each damaged unit. <paramref name="unitSize"/>
        /// is the whole sprite's world size for a small unit, exactly the shader's <c>_UnitSize</c>, so
        /// the two scenes draw the same picture.
        /// </summary>
        internal static SpriteBaseline Create(in SceneData data, Texture2D atlas, float unitSize)
        {
            var root = new GameObject("[Spike] Sprite baseline");
            var unitSprite = Sprite.Create(atlas, new Rect(0f, 0f, atlas.width, atlas.height),
                new Vector2(0.5f, 0.5f), atlas.height);
            var barTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "Spike bar pixel", filterMode = FilterMode.Point };
            barTexture.SetPixel(0, 0, Color.white);
            barTexture.Apply(false, false);
            var barSprite = Sprite.Create(barTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);

            var transforms = new Transform[data.Units];
            int bars = 0;
            for (int i = 0; i < data.Units; i++)
            {
                float size = unitSize * data.Scale[i];
                var go = new GameObject("u" + i, typeof(SpriteRenderer));
                Transform t = go.transform;
                t.SetParent(root.transform, false);
                t.localPosition = new Vector3(data.Positions[i].x, data.Positions[i].y, 0f);
                t.localScale = new Vector3(size, size, 1f);
                SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
                sr.sprite = unitSprite;
                sr.color = Unpack(data.Colors[i]);
                transforms[i] = t;

                if (data.Damaged[i] == 0) continue;
                float health = math.saturate(data.Health[i]);
                var bar = new GameObject("bar", typeof(SpriteRenderer));
                Transform bt = bar.transform;
                bt.SetParent(t, false);
                // The child inherits the unit's scale, so undo it to keep the bar the same world size
                // for small and large units.
                bt.localPosition = new Vector3(0f, (size * 0.5f + 0.175f) / size, 0f);
                bt.localScale = new Vector3(0.7f * health / size, 0.07f / size, 1f);
                SpriteRenderer bsr = bar.GetComponent<SpriteRenderer>();
                bsr.sprite = barSprite;
                bsr.color = Unpack(InstancedUnitRenderer.BarColor(health));
                bars++;
            }
            return new SpriteBaseline(root, transforms, unitSprite, barSprite, barTexture, data.Units, bars);
        }

        /// <summary>The baseline's per-frame CPU work: one position write per unit.</summary>
        public void SetPositions(NativeArray<float2> positions)
        {
            for (int i = 0; i < transforms.Length; i++)
                transforms[i].position = new Vector3(positions[i].x, positions[i].y, 0f);
        }

        /// <summary>Turns the whole hierarchy on or off; the benchmark's background-only frame needs it off.</summary>
        public void SetVisible(bool visible) => root.SetActive(visible);

        private static Color32 Unpack(uint color) => new Color32(
            (byte)(color & 255), (byte)((color >> 8) & 255), (byte)((color >> 16) & 255), (byte)((color >> 24) & 255));

        public void Dispose()
        {
            UnityEngine.Object.Destroy(root);
            UnityEngine.Object.Destroy(unitSprite);
            UnityEngine.Object.Destroy(barSprite);
            UnityEngine.Object.Destroy(barTexture);
        }
    }
}
