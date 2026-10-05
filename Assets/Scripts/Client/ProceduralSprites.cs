using UnityEngine;

/// <summary>Tiny generated sprites for placeholder effects (replaced by real art in v0.8).</summary>
public static class ProceduralSprites
{
    private static Sprite circle;
    private static Sprite pixel;

    public static Sprite Circle => circle != null ? circle : (circle = MakeCircle(32));
    public static Sprite Pixel => pixel != null ? pixel : (pixel = MakePixel());

    private static Sprite MakeCircle(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        float r = size / 2f;
        for (int x = 0; x < size; x++)
            for (int y = 0; y < size; y++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                tex.SetPixel(x, y, d <= r ? Color.white : Color.clear);
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private static Sprite MakePixel()
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1);
    }
}
