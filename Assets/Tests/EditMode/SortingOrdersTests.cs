using NUnit.Framework;
using WAR2D.Client.Render;

/// <summary>World layers keep their order on every map size (buildings never sink under the terrain).</summary>
public class SortingOrdersTests
{
    [Test]
    public void BuildingsStayBetweenTheClearingRingAndTheEffects()
    {
        foreach (float y in new[] { 0f, 1f, 255.5f, 512f, 1023.5f, 1024f })
        {
            int b = SortingOrders.Building(y);
            Assert.Greater(b, SortingOrders.ClearingRing, $"y {y}");
            Assert.Less(b, SortingOrders.Effects, $"y {y}");
        }
        Assert.Greater(SortingOrders.Building(10f), SortingOrders.Building(11f), "nearer (lower) buildings draw in front");
    }

    [Test]
    public void LayersAreInOrder()
    {
        int[] order = { SortingOrders.Terrain, SortingOrders.ClearingRing, SortingOrders.Building(1024f), SortingOrders.Effects, SortingOrders.Fog, SortingOrders.BuildingSelection, SortingOrders.PlacementPreview };
        for (int i = 1; i < order.Length; i++) Assert.Less(order[i - 1], order[i], $"layer {i}");
        Assert.LessOrEqual(SortingOrders.PlacementPreview, short.MaxValue);
        Assert.GreaterOrEqual(SortingOrders.Terrain, short.MinValue);
    }
}
