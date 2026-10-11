using System;
using NUnit.Framework;
using UnityEngine;
using WAR2D.Art;
using WAR2D.UI;

/// <summary>The approved v0.8 pixel icons and cursors.</summary>
public class CommandIconTests
{
    [Test]
    public void EveryCommandIconHasABitmap()
    {
        foreach (IconKind kind in Enum.GetValues(typeof(IconKind)))
            Assert.IsTrue(PixelIcons.Contains(CommandIcon.IdOf(kind)), kind.ToString());
    }

    [Test]
    public void EveryCursorTheGameShowsHasABitmap()
    {
        foreach (OrderKind? armed in new OrderKind?[] { null, OrderKind.Move, OrderKind.AttackMove, OrderKind.Stop })
        foreach (bool placing in new[] { false, true })
        foreach (bool? valid in new bool?[] { null, true, false })
            Assert.IsTrue(PixelIcons.Contains(GameCursor.Choose(armed, placing, valid)));
        Assert.AreEqual("CursorInvalid", GameCursor.Choose(OrderKind.AttackMove, true, false), "a refused spot wins");
        Assert.AreEqual("CursorAttack", GameCursor.Choose(OrderKind.AttackMove, false, null));
        Assert.AreEqual("CursorDefault", GameCursor.Choose(null, true, true));
    }

    [Test]
    public void BitmapsAreSixteenSquareWithKnownCharacters()
    {
        foreach (string id in PixelIcons.Ids)
        {
            string[] rows = PixelIcons.Bitmap(id);
            Assert.AreEqual(PixelIcons.Size, rows.Length, id);
            foreach (string row in rows)
            {
                Assert.AreEqual(PixelIcons.Size, row.Length, id);
                Assert.That(row.Trim('.', '#', '+'), Is.Empty, $"{id}: {row}");
            }
        }
    }

    [Test]
    public void TexturesArePointFilteredWithAnOutline()
    {
        Texture2D t = PixelIcons.Texture("IconStop", 2);
        Assert.AreEqual(32, t.width);
        Assert.AreEqual(FilterMode.Point, t.filterMode);
        Assert.AreEqual(1, t.mipmapCount);
        Color32[] px = PixelIcons.Pixels("IconStop");
        // Stop's square spans columns 4–11, rows 4–11 (top-down): pixel (3, 4) is its outline, (0, 0) is empty.
        Assert.AreEqual(PixelIcons.Outline, px[(15 - 4) * 16 + 3]);
        Assert.AreEqual(0, px[15 * 16].a);
        Assert.AreEqual(255, px[(15 - 6) * 16 + 6].r, "the body is white so USS tints it");
        Assert.AreSame(t, PixelIcons.Texture("IconStop", 2), "cached");
    }
}
