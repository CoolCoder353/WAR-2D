using Unity.Mathematics;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Render;

namespace WAR2D.Client.Render
{
    /// <summary>
    /// Corner brackets around the selected building's footprint: the <c>SelectionRingLarge</c> effect frame,
    /// 9-sliced so the corners keep their size on any footprint.
    /// </summary>
    public sealed class BuildingSelectionMarker : MonoBehaviour
    {
        private SpriteRenderer sprite;
        private int shownId;

        /// <summary>Shows the marker on <paramref name="building"/> (footprint <paramref name="size"/> tiles), or hides it for null.</summary>
        public void Show(int id, GameObject building, int2 size)
        {
            if (sprite == null && !Init()) return;
            if (building == null)
            {
                sprite.enabled = false;
                shownId = 0;
                return;
            }
            transform.position = new Vector3(building.transform.position.x, building.transform.position.y, 0f);
            if (shownId == id && sprite.enabled) return;
            shownId = id;
            sprite.size = new Vector2(size.x, size.y);
            sprite.enabled = true;
        }

        private bool Init()
        {
            ArtAtlas atlas = ArtAtlas.Load(ArtAtlas.Effects);
            int cell = atlas != null ? atlas.FirstFrame("SelectionRingLarge") : -1;
            if (cell < 0) return false;
            AtlasFrame f = atlas.Data.frames[cell];
            float b = EffectGen.RingBorder;
            Sprite sliced = Sprite.Create(atlas.Texture, new Rect(f.x, f.y, f.w, f.h), new Vector2(0.5f, 0.5f), atlas.Data.pixelsPerUnit, 0,
                SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            sliced.name = "SelectionRingLarge (sliced)";
            sprite = gameObject.AddComponent<SpriteRenderer>();
            sprite.sprite = sliced;
            sprite.drawMode = SpriteDrawMode.Sliced;
            sprite.sortingOrder = SortingOrders.BuildingSelection;
            sprite.enabled = false;
            return true;
        }
    }
}
