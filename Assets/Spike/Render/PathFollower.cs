using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace WAR2D.Spike
{
    /// <summary>
    /// The client half of the v0.5 hybrid movement model: a unit's position is its travel
    /// <c>speed * (now - startTime)</c> along its waypoint polyline, clamped at the ends, and its
    /// facing is the segment it is on.
    ///
    /// <para><b>The position function is the contract.</b> Task 9's server prediction has to produce
    /// the same position from the same inputs, bit for bit, so it must call
    /// <see cref="Evaluate(NativeArray{float2}, int, int, float, float, float, out float2, out float)"/>
    /// rather than reimplement it. It is deliberately a small static over a flat waypoint array with a
    /// slice (first, count), so a server job with its own packing can call it directly.</para>
    /// </summary>
    public static class PathFollower
    {
        /// <summary>
        /// The unit's position and facing at time <paramref name="now"/>. Its route is the
        /// <paramref name="count"/> waypoints <c>[first, first + count)</c> of
        /// <paramref name="waypoints"/> (a unit's slice is <c>WaypointStart[i] .. WaypointStart[i + 1]</c>).
        /// Travel starts at the first waypoint at <paramref name="startTime"/>, runs at
        /// <paramref name="speed"/> world units per second, and stops at the last waypoint. Facing is
        /// the direction of the segment the unit is on (the way it was heading when it stops).
        /// </summary>
        public static void Evaluate(NativeArray<float2> waypoints, int first, int count,
            float speed, float startTime, float now, out float2 position, out float rotation)
        {
            float2 from = waypoints[first];
            float remaining = math.max(0f, speed * (now - startTime));
            float2 direction = new float2(1f, 0f);
            for (int i = 1; i < count; i++)
            {
                float2 to = waypoints[first + i];
                float2 segment = to - from;
                float length = math.length(segment);
                if (length > 0f) direction = segment / length;
                if (remaining <= length)
                {
                    position = length > 0f ? from + segment * (remaining / length) : from;
                    rotation = math.atan2(direction.y, direction.x);
                    return;
                }
                remaining -= length;
                from = to;
            }
            position = from;
            rotation = math.atan2(direction.y, direction.x);
        }
    }

    /// <summary>
    /// Evaluates every unit of one client's army for the current render time. This is the client CPU
    /// work the render benchmark times: it is the same work a v0.5 client does for each of its units
    /// every frame, independent of how the result is drawn.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Deterministic)]
    public struct PathFollowerJob : IJobParallelFor
    {
        /// <summary>Every unit's waypoints, back to back.</summary>
        [ReadOnly] public NativeArray<float2> Waypoints;

        /// <summary>First waypoint of each unit; entry <c>i + 1</c> ends unit <c>i</c>'s slice.</summary>
        [ReadOnly] public NativeArray<int> WaypointStart;

        /// <summary>Tiles per second.</summary>
        [ReadOnly] public NativeArray<float> Speed;

        /// <summary>When the unit left its first waypoint; usually negative, so units are mid-path.</summary>
        [ReadOnly] public NativeArray<float> StartTime;

        /// <summary>Render time in seconds, the same clock both sides of the hybrid model use.</summary>
        public float Now;

        /// <summary>Where each unit is at <see cref="Now"/>.</summary>
        [WriteOnly] public NativeArray<float2> Positions;

        /// <summary>Radians, each unit's facing at <see cref="Now"/>.</summary>
        [WriteOnly] public NativeArray<float> Rotations;

        public void Execute(int i)
        {
            PathFollower.Evaluate(Waypoints, WaypointStart[i], WaypointStart[i + 1] - WaypointStart[i],
                Speed[i], StartTime[i], Now, out float2 position, out float rotation);
            Positions[i] = position;
            Rotations[i] = rotation;
        }
    }
}
