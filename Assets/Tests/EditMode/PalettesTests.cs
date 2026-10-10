using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class PalettesTests
{
    [Test]
    public void EachPaletteHasEightDistinctColours()
    {
        foreach (string palette in Palettes.Names)
        {
            var seen = new HashSet<Color32>();
            for (int i = 0; i < 8; i++) Assert.IsTrue(seen.Add(Palettes.Player(palette, i)), $"{palette} colour {i + 1} repeats");
        }
    }

    [Test]
    public void ColourblindPairsKeepDeltaE2000OfAtLeast20()
    {
        for (int a = 0; a < 8; a++)
        for (int b = a + 1; b < 8; b++)
        {
            double de = DeltaE2000(Lab(Palettes.Player(Palettes.Colourblind, a)), Lab(Palettes.Player(Palettes.Colourblind, b)));
            Assert.GreaterOrEqual(de, 20.0, $"colourblind colours {a + 1} and {b + 1}");
        }
    }

    [Test]
    public void HighContrastColoursStandOutFromTheMapFloor()
    {
        Color32 floor = MapView.TileColor(TileType.Ground);
        for (int i = 0; i < 8; i++)
        {
            double ratio = Contrast(Palettes.Player(Palettes.HighContrast, i), floor);
            Assert.GreaterOrEqual(ratio, 4.5, $"high-contrast colour {i + 1}");
        }
    }

    [Test]
    public void SwitchingRaisesChangedOnce()
    {
        string before = Palettes.Current;
        int raised = 0;
        void Count() => raised++;
        Palettes.Changed += Count;
        try
        {
            Palettes.Current = Palettes.HighContrast;
            Palettes.Current = Palettes.HighContrast;
            Assert.AreEqual(1, raised);
            Assert.AreEqual(Palettes.Player(Palettes.HighContrast, 2), PlayerPalette.Of(2));
            Palettes.Current = "nonsense";
            Assert.AreEqual(Palettes.Standard, Palettes.Current);
        }
        finally
        {
            Palettes.Changed -= Count;
            Palettes.Current = before;
        }
    }

    private static double Linear(byte c)
    {
        double v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color32 c) => 0.2126 * Linear(c.r) + 0.7152 * Linear(c.g) + 0.0722 * Linear(c.b);

    private static double Contrast(Color32 a, Color32 b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static (double L, double a, double b) Lab(Color32 c)
    {
        double r = Linear(c.r), g = Linear(c.g), bl = Linear(c.b);
        double x = (r * 0.4124 + g * 0.3576 + bl * 0.1805) / 0.95047, y = r * 0.2126 + g * 0.7152 + bl * 0.0722, z = (r * 0.0193 + g * 0.1192 + bl * 0.9505) / 1.08883;
        double F(double t) => t > 0.008856 ? Math.Pow(t, 1.0 / 3) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    private static double Rad(double d) => d * Math.PI / 180;

    /// <summary>CIEDE2000 colour difference.</summary>
    private static double DeltaE2000((double L, double a, double b) c1, (double L, double a, double b) c2)
    {
        double C1 = Math.Sqrt(c1.a * c1.a + c1.b * c1.b), C2 = Math.Sqrt(c2.a * c2.a + c2.b * c2.b), Cb = (C1 + C2) / 2;
        double G = 0.5 * (1 - Math.Sqrt(Math.Pow(Cb, 7) / (Math.Pow(Cb, 7) + Math.Pow(25, 7))));
        double a1 = (1 + G) * c1.a, a2 = (1 + G) * c2.a;
        double C1p = Math.Sqrt(a1 * a1 + c1.b * c1.b), C2p = Math.Sqrt(a2 * a2 + c2.b * c2.b);
        double h1 = (Math.Atan2(c1.b, a1) * 180 / Math.PI + 360) % 360, h2 = (Math.Atan2(c2.b, a2) * 180 / Math.PI + 360) % 360;
        double dL = c2.L - c1.L, dC = C2p - C1p, dh = h2 - h1;
        if (C1p * C2p == 0) dh = 0;
        else if (dh > 180) dh -= 360;
        else if (dh < -180) dh += 360;
        double dH = 2 * Math.Sqrt(C1p * C2p) * Math.Sin(Rad(dh / 2));
        double Lb = (c1.L + c2.L) / 2, Cbp = (C1p + C2p) / 2;
        double hb = C1p * C2p == 0 ? h1 + h2 : Math.Abs(h1 - h2) <= 180 ? (h1 + h2) / 2 : (h1 + h2 < 360 ? (h1 + h2 + 360) / 2 : (h1 + h2 - 360) / 2);
        double T = 1 - 0.17 * Math.Cos(Rad(hb - 30)) + 0.24 * Math.Cos(Rad(2 * hb)) + 0.32 * Math.Cos(Rad(3 * hb + 6)) - 0.20 * Math.Cos(Rad(4 * hb - 63));
        double dTheta = 30 * Math.Exp(-Math.Pow((hb - 275) / 25, 2));
        double Rc = 2 * Math.Sqrt(Math.Pow(Cbp, 7) / (Math.Pow(Cbp, 7) + Math.Pow(25, 7)));
        double Sl = 1 + 0.015 * Math.Pow(Lb - 50, 2) / Math.Sqrt(20 + Math.Pow(Lb - 50, 2)), Sc = 1 + 0.045 * Cbp, Sh = 1 + 0.015 * Cbp * T;
        double Rt = -Math.Sin(Rad(2 * dTheta)) * Rc;
        return Math.Sqrt(Math.Pow(dL / Sl, 2) + Math.Pow(dC / Sc, 2) + Math.Pow(dH / Sh, 2) + Rt * (dC / Sc) * (dH / Sh));
    }
}
