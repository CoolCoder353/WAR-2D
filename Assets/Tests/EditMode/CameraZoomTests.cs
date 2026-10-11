using NUnit.Framework;

/// <summary>Zoom steps: whole-number art scales when zoomed in, smooth steps when zoomed out.</summary>
public class CameraZoomTests
{
    private const int Ppu = 64, H = 1080;
    private const float Min = 3f, Max = 30f;
    private static readonly float One = H / 128f; // 8.4375

    [Test]
    public void ZoomingInFromFarSnapsOntoWholeScales()
    {
        float size = Max;
        int steps = 0;
        while (size > One + 1e-3f && steps++ < 50)
        {
            float next = CameraZoom.Step(size, 1f, Ppu, H, Min, Max);
            Assert.Less(next, size);
            size = next;
        }
        Assert.AreEqual(One, size, 1e-4, "lands exactly on 1:1");
        Assert.IsTrue(CameraZoom.PixelPerfect(size, Ppu, H));
        size = CameraZoom.Step(size, 1f, Ppu, H, Min, Max);
        Assert.AreEqual(One / 2f, size, 1e-4, "then 2:1");
        Assert.IsTrue(CameraZoom.PixelPerfect(size, Ppu, H));
        Assert.AreEqual(size, CameraZoom.Step(size, 1f, Ppu, H, Min, Max), "3:1 (2.81) is past the minimum, so it stays");
    }

    [Test]
    public void ZoomingOutPastOneToOneIsSmoothUpToTheMaximum()
    {
        float size = CameraZoom.Step(One / 2f, -1f, Ppu, H, Min, Max);
        Assert.AreEqual(One, size, 1e-4, "2:1 back to 1:1");
        float before = size;
        size = CameraZoom.Step(size, -1f, Ppu, H, Min, Max);
        Assert.AreEqual(before * CameraZoom.SmoothFactor, size, 1e-4);
        Assert.IsFalse(CameraZoom.PixelPerfect(size, Ppu, H));
        for (int i = 0; i < 40; i++) size = CameraZoom.Step(size, -1f, Ppu, H, Min, Max);
        Assert.AreEqual(Max, size, 1e-4, "clamped at the maximum");
    }

    [Test]
    public void PixelPerfectOnlyAtWholeScalesOfOneOrMore()
    {
        Assert.IsTrue(CameraZoom.PixelPerfect(One, Ppu, H));
        Assert.IsTrue(CameraZoom.PixelPerfect(One / 3f, Ppu, H));
        Assert.IsFalse(CameraZoom.PixelPerfect(10f, Ppu, H), "today's default 10 is 0.84:1");
        Assert.IsFalse(CameraZoom.PixelPerfect(One * 2f, Ppu, H), "1:2 is a whole ratio but downscaled");
        Assert.IsFalse(CameraZoom.PixelPerfect(One / 1.5f, Ppu, H));
    }

    [Test]
    public void DefaultIsOneToOneWithinTheLimits()
    {
        Assert.AreEqual(One, CameraZoom.Default(Ppu, H, Min, Max), 1e-4);
        Assert.AreEqual(2160f / 128f, CameraZoom.Default(Ppu, 2160, Min, Max), 1e-4);
        Assert.AreEqual(Max, CameraZoom.Default(Ppu, 4320, Min, Max), 1e-4, "an 8K screen's 1:1 is past the maximum");
        Assert.AreEqual(5f, CameraZoom.Step(5f, 0f, Ppu, H, Min, Max));
    }
}
