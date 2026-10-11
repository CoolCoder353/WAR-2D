using UnityEngine;

/// <summary>
/// Zoom steps (style guide, item 4): zooming in snaps to whole-number scales of the art (1:1, 2:1, …), where
/// the Pixel Perfect Camera is on; zooming out past 1:1 moves in smooth steps up to the maximum size. Sizes
/// are orthographic sizes; scale is screen pixels per art pixel.
/// </summary>
public static class CameraZoom
{
    /// <summary>Size change per wheel notch beyond 1:1 (multiplied or divided).</summary>
    public const float SmoothFactor = 1.25f;

    private const float Epsilon = 1e-3f;

    /// <summary>Screen pixels per art pixel at an orthographic size.</summary>
    public static float Scale(float orthoSize, int ppu, int screenHeight) => screenHeight / (2f * orthoSize * ppu);

    /// <summary>The orthographic size that draws the art at a whole-number <paramref name="scale"/>.</summary>
    public static float SizeAt(int scale, int ppu, int screenHeight) => screenHeight / (2f * scale * ppu);

    /// <summary>The default zoom: 1:1, inside the limits.</summary>
    public static float Default(int ppu, int screenHeight, float minSize, float maxSize) =>
        Mathf.Clamp(SizeAt(1, ppu, screenHeight), minSize, maxSize);

    /// <summary>True when the art draws at a whole-number scale of at least 1:1.</summary>
    public static bool PixelPerfect(float orthoSize, int ppu, int screenHeight)
    {
        float s = Scale(orthoSize, ppu, screenHeight);
        return s >= 1f - Epsilon && Mathf.Abs(s - Mathf.Round(s)) < Epsilon;
    }

    /// <summary>The size after one wheel step (<paramref name="scroll"/> &gt; 0 zooms in), within the limits.</summary>
    public static float Step(float current, float scroll, int ppu, int screenHeight, float minSize, float maxSize)
    {
        if (scroll == 0f) return current;
        float one = SizeAt(1, ppu, screenHeight);
        float next;
        if (scroll > 0f)
        {
            if (current > one * (1f + Epsilon)) next = Mathf.Max(current / SmoothFactor, one);
            else
            {
                int k = Mathf.Max(1, Mathf.RoundToInt(Scale(current, ppu, screenHeight)));
                next = SizeAt(k + 1, ppu, screenHeight);
                if (next < minSize - Epsilon) return current; // the next whole scale is past the limit
            }
        }
        else
        {
            if (current < one * (1f - Epsilon))
            {
                int k = Mathf.Max(2, Mathf.RoundToInt(Scale(current, ppu, screenHeight)));
                next = SizeAt(k - 1, ppu, screenHeight);
            }
            else next = current * SmoothFactor;
        }
        return Mathf.Clamp(next, minSize, maxSize);
    }
}
