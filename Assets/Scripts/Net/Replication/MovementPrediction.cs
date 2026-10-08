using Unity.Collections;
using Unity.Mathematics;

namespace WAR2D.Net.Replication
{
    /// <summary>What one client believes about one unit: its route leg, anchor and speed.</summary>
    public struct PredictState
    {
        /// <summary>The full id the client knows at this index (0 when unknown).</summary>
        public int Id;
        /// <summary>The corrected point while <see cref="Anchored"/>, else where the route leg began.</summary>
        public float2 Anchor;
        /// <summary>The position the prediction produced at the last check.</summary>
        public float2 LastPredicted;
        /// <summary>When an anchored prediction started (the correction's tick time).</summary>
        public float AnchorTime;
        /// <summary>When the current route leg started.</summary>
        public float StartTime;
        /// <summary>Route waypoint the current leg departs from.</summary>
        public int Index;
        /// <summary>1 while predicting from a corrected point rather than from a waypoint.</summary>
        public byte Anchored;
        /// <summary>1 for the tick the unit entered (the server skips its correction check).</summary>
        public byte Fresh;
        /// <summary>The health byte the client was last sent.</summary>
        public byte LastHealth;
        /// <summary>The route generation the client was last sent.</summary>
        public int RouteGeneration;
        /// <summary>Tiles a second the client predicts at.</summary>
        public float Speed;
        /// <summary>The unit type's full speed, the scale of the speed byte.</summary>
        public float FullSpeed;
        /// <summary>The real position and time at the last check, for the measured speed (server only).</summary>
        public float2 LastActual;
        public float LastCheck;
    }

    /// <summary>
    /// The prediction both sides run: the server to decide corrections, the client to draw. It is the
    /// same code over the same inputs, in the same (deterministic) float mode, so the server knows the
    /// client's position bit for bit.
    /// </summary>
    public static class MovementPrediction
    {
        /// <summary>
        /// The position along a polyline at <paramref name="now"/>: travel <c>speed * (now - startTime)</c>
        /// from the first waypoint, clamped at the last.
        /// </summary>
        public static float2 Evaluate(NativeArray<float2> waypoints, int first, int count, float speed, float startTime, float now)
        {
            float2 from = waypoints[first];
            float remaining = math.max(0f, speed * (now - startTime));
            for (int i = 1; i < count; i++)
            {
                float2 to = waypoints[first + i];
                float2 segment = to - from;
                float length = math.length(segment);
                if (remaining <= length) return length > 0f ? from + segment * (remaining / length) : from;
                remaining -= length;
                from = to;
            }
            return from;
        }

        /// <summary>
        /// The predicted position: a straight anchored leg out of a corrected point toward the resumed
        /// waypoint, then the route itself. Advancing past the anchored leg updates the state, at a time
        /// fixed by the state alone, so it does not matter when (or how often) this is called.
        /// </summary>
        public static float2 Predict(ref PredictState s, NativeArray<float2> waypoints, int first, int count, float now)
        {
            if (count <= 0) return s.Anchor;
            if (s.Anchored != 0)
            {
                int target = s.Index + 1;
                if (target >= count) return s.Anchor; // the route ended: hold the corrected point
                float2 to = waypoints[first + target];
                float2 segment = to - s.Anchor;
                float length = math.length(segment);
                float remaining = math.max(0f, s.Speed * (now - s.AnchorTime));
                if (length <= 0f || remaining <= length || s.Speed <= 0f)
                    return length > 0f ? s.Anchor + segment * (math.min(remaining, length) / length) : s.Anchor;
                s.Anchored = 0;
                s.Index = target;
                s.StartTime = s.AnchorTime + length / s.Speed;
            }
            int index = math.clamp(s.Index, 0, count - 1);
            return Evaluate(waypoints, first + index, count - index, s.Speed, s.StartTime, now);
        }

        /// <summary>
        /// The waypoint a correction resumes at: the end of the route segment nearest
        /// <paramref name="actual"/>, searched a few legs either side of the current one.
        /// </summary>
        public static int ProjectedResume(NativeArray<float2> waypoints, int first, int count, int index, float2 actual)
        {
            if (count <= 1) return 0;
            int from = math.max(0, index - 2), to = math.min(count - 2, index + 4);
            int best = math.clamp(index, 0, count - 2);
            float bestDistance = float.MaxValue;
            for (int i = from; i <= to; i++)
            {
                float2 a = waypoints[first + i], b = waypoints[first + i + 1];
                float2 ab = b - a;
                float t = math.saturate(math.dot(actual - a, ab) / math.max(1e-6f, math.lengthsq(ab)));
                float d = math.distancesq(actual, a + ab * t);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best + 1;
        }

        /// <summary>Applies a correction exactly as both sides must: anchor at prediction + delta.</summary>
        public static void ApplyCorrection(ref PredictState s, NativeArray<float2> waypoints, int first, int count,
            float now, in CorrectionUnit c, byte deltaScale)
        {
            float2 predicted = Predict(ref s, waypoints, first, count, now);
            float2 anchor = deltaScale == 0 ? predicted : predicted + new float2(c.DX, c.DY) / deltaScale;
            s.Anchored = 1;
            s.Anchor = anchor;
            s.AnchorTime = now;
            s.Index = math.max(0, c.Resume - 1);
            s.LastPredicted = anchor;
            s.Speed = c.Speed / 64f * s.FullSpeed;
        }

        /// <summary>Restarts the prediction on a new route at its first waypoint.</summary>
        public static void StartRoute(ref PredictState s, float2 position, float now)
        {
            s.Index = 0;
            s.Anchored = 0;
            s.StartTime = now;
            s.Anchor = position;
            s.Speed = s.FullSpeed;
        }
    }
}
