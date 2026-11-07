using System;

namespace WAR2D.Net.Replication
{
    /// <summary>Client entry point for <see cref="ReplicationBatch"/>es; the client world subscribes.</summary>
    public static class ReplicationClient
    {
        /// <summary>Raised for each batch received. The payload is only valid during the call.</summary>
        public static event Action<ReplicationBatch> Received;

        /// <summary>Batches received so far (diagnostics and tests).</summary>
        public static long BatchesReceived;

        private static readonly Unity.Profiling.ProfilerMarker ReceiveMarker = new Unity.Profiling.ProfilerMarker("Receive.Units");

        public static void Receive(ReplicationBatch batch)
        {
            BatchesReceived++;
            using var marker = ReceiveMarker.Auto();
            Received?.Invoke(batch);
        }
    }
}
