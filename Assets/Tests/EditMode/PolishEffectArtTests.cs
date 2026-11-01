using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;
using WAR2D.Art.Render;

/// <summary>The polish effects' generated frames (2026-10-30): palette pixel art, drawn, and ground marks that fade out.</summary>
public class PolishEffectArtTests
{
    private static readonly string[] Ids = { "Scorch", "ScorchLarge", "Rubble1", "Rubble2", "Rubble3", "Rubble5", "Tracks", "Smoke", "Fire", "GemSparkle" };
    private static readonly string[] Decals = { "Scorch", "ScorchLarge", "Rubble1", "Rubble2", "Rubble3", "Rubble5" };

    private static int Opaque(EffectFrame f)
    {
        int n = 0;
        foreach (Color32 c in f.Pixels) if (c.a > 0) n++;
        return n;
    }

    [Test]
    public void EveryFrameIsDrawnInTheEffectPalette()
    {
        var palette = new HashSet<Color32>(ArtStyle.EffectPalette);
        foreach (string id in Ids)
        {
            Assert.IsTrue(ArtManifest.TryGet(id, out ArtEntry e), id);
            for (int cell = 0; cell < e.Frames * e.Directions; cell++)
            {
                EffectFrame? made = EffectGen.For(id, cell, e.Frames);
                Assert.IsTrue(made.HasValue, $"{id} has a generator");
                EffectFrame f = made.Value;
                Assert.AreEqual(e.WidthTiles * ArtStyle.TileSize, f.Width, $"{id} cell {cell}");
                Assert.AreEqual(e.HeightTiles * ArtStyle.TileSize, f.Height, $"{id} cell {cell}");
                Assert.Greater(Opaque(f), 0, $"{id} cell {cell} draws something");
                foreach (Color32 c in f.Pixels)
                    if (c.a > 0) Assert.IsTrue(palette.Contains(c), $"{id} cell {cell}: {c} is not in the effect palette");
            }
        }
    }

    [Test]
    public void GroundMarksThinOutFrameByFrame()
    {
        foreach (string id in Decals)
        {
            ArtManifest.TryGet(id, out ArtEntry e);
            int last = int.MaxValue;
            for (int frame = 0; frame < e.Frames; frame++)
            {
                int n = Opaque(EffectGen.For(id, frame, e.Frames).Value);
                Assert.Less(n, last, $"{id} frame {frame} has fewer pixels than the one before");
                last = n;
            }
        }
        for (int dir = 0; dir < EffectGen.TrackDirections; dir++)
            Assert.Greater(Opaque(EffectGen.Tracks(dir, 0)), Opaque(EffectGen.Tracks(dir, EffectGen.DecalFrames - 1)), $"tracks {dir}");
    }
}
