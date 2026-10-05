using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using WAR2D.Spike;

public class RenderTests
{
    [Test]
    public void UnitInstanceIsTheShadersThirtyTwoBytes()
    {
        Assert.AreEqual(32, Marshal.SizeOf<UnitInstance>());
        Assert.AreEqual(32, InstancedUnitRenderer.InstanceStride);
    }

    [Test]
    public void ColourPackingMatchesTheShadersUnpacking()
    {
        // The shader reads r = color & 255, g = (color >> 8) & 255, b = (color >> 16) & 255, a = color >> 24.
        uint packed = InstancedUnitRenderer.Pack(new UnityEngine.Color32(1, 2, 3, 4));
        Assert.AreEqual(1u, packed & 255);
        Assert.AreEqual(2u, (packed >> 8) & 255);
        Assert.AreEqual(3u, (packed >> 16) & 255);
        Assert.AreEqual(4u, packed >> 24);
    }

    [Test]
    public void BarColorIsGreenAtFullHealthAndRedAtNone()
    {
        UnityEngine.Color32 full = Unpack(InstancedUnitRenderer.BarColor(1f));
        UnityEngine.Color32 empty = Unpack(InstancedUnitRenderer.BarColor(0f));
        Assert.Greater(full.g, full.r);
        Assert.Greater(empty.r, empty.g);
    }

    [Test]
    public void PathFollowsThePolylineAndClampsAtTheEnd()
    {
        var waypoints = new NativeArray<float2>(new[]
        {
            new float2(0f, 0f), new float2(10f, 0f), new float2(10f, 10f),
        }, Allocator.Temp);
        PathFollower.Evaluate(waypoints, 0, 3, 2f, 0f, 3f, out float2 position, out float rotation);
        Assert.AreEqual(6f, position.x, 1e-4f);
        Assert.AreEqual(0f, position.y, 1e-4f);
        Assert.AreEqual(0f, rotation, 1e-4f);

        // Past the last waypoint the unit sits on it, still facing its last segment.
        PathFollower.Evaluate(waypoints, 0, 3, 2f, 0f, 1000f, out position, out rotation);
        Assert.AreEqual(10f, position.x, 1e-4f);
        Assert.AreEqual(10f, position.y, 1e-4f);
        Assert.AreEqual(math.PI / 2f, rotation, 1e-4f);

        // Before the start time it waits at the first waypoint.
        PathFollower.Evaluate(waypoints, 0, 3, 2f, 5f, 1f, out position, out rotation);
        Assert.AreEqual(0f, position.x, 1e-4f);
        Assert.AreEqual(0f, position.y, 1e-4f);
        waypoints.Dispose();
    }

    [Test]
    public void PathFollowerJobMatchesTheFunction()
    {
        var waypoints = new NativeArray<float2>(new[]
        {
            new float2(1f, 1f), new float2(1f, 21f), new float2(31f, 21f),
            new float2(50f, 50f), new float2(50f, 60f), new float2(70f, 60f),
        }, Allocator.TempJob);
        var starts = new NativeArray<int>(new[] { 0, 3, 6 }, Allocator.TempJob);
        var speed = new NativeArray<float>(new[] { 4f, 3f }, Allocator.TempJob);
        var startTime = new NativeArray<float>(new[] { 0f, -2f }, Allocator.TempJob);
        var positions = new NativeArray<float2>(2, Allocator.TempJob);
        var rotations = new NativeArray<float>(2, Allocator.TempJob);

        new PathFollowerJob
        {
            Waypoints = waypoints,
            WaypointStart = starts,
            Speed = speed,
            StartTime = startTime,
            Now = 3.5f,
            Positions = positions,
            Rotations = rotations,
        }.Schedule(2, 1).Complete();

        for (int i = 0; i < 2; i++)
        {
            PathFollower.Evaluate(waypoints, starts[i], starts[i + 1] - starts[i], speed[i], startTime[i], 3.5f,
                out float2 position, out float rotation);
            Assert.AreEqual(position.x, positions[i].x, 1e-6f);
            Assert.AreEqual(position.y, positions[i].y, 1e-6f);
            Assert.AreEqual(rotation, rotations[i], 1e-6f);
        }

        waypoints.Dispose();
        starts.Dispose();
        speed.Dispose();
        startTime.Dispose();
        positions.Dispose();
        rotations.Dispose();
    }

    private static UnityEngine.Color32 Unpack(uint color) => new UnityEngine.Color32(
        (byte)(color & 255), (byte)((color >> 8) & 255), (byte)((color >> 16) & 255), (byte)(color >> 24));
}
