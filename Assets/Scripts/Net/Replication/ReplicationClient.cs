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

        public static void Receive(ReplicationBatch batch)
        {
            BatchesReceived++;
            Received?.Invoke(batch);
        }
    }
}
