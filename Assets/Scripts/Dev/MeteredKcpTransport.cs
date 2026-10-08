using System;
using System.Threading;
using kcp2k;
using Mirror;

/// <summary>
/// The KCP transport, counting every raw UDP datagram the client receives (KCP headers, acks and
/// retransmits included) plus the IPv4 and UDP headers. The perf gate's real clients use it to measure
/// the bandwidth a player actually receives.
/// </summary>
public sealed class MeteredKcpTransport : KcpTransport
{
    /// <summary>IPv4 (20) plus UDP (8) header bytes per datagram.</summary>
    public const int DatagramOverhead = 28;

    /// <summary>Bytes received by the client so far, datagram headers included.</summary>
    public static long ClientBytesReceived => Interlocked.Read(ref bytes);
    private static long bytes;

    protected override void Awake()
    {
        base.Awake();
        client = new MeteredClient(
            () => OnClientConnected.Invoke(),
            (message, channel) => OnClientDataReceived.Invoke(message, FromKcpChannel(channel)),
            () => OnClientDisconnected?.Invoke(),
            (error, reason) => OnClientError?.Invoke(ToTransportError(error), reason),
            config);
    }

    /// <summary>Copies the tuning of the transport this one replaces.</summary>
    public void CopyFrom(KcpTransport other)
    {
        Port = other.Port;
        DualMode = other.DualMode;
        NoDelay = other.NoDelay;
        Interval = other.Interval;
        Timeout = other.Timeout;
        RecvBufferSize = other.RecvBufferSize;
        SendBufferSize = other.SendBufferSize;
        FastResend = other.FastResend;
        ReceiveWindowSize = other.ReceiveWindowSize;
        SendWindowSize = other.SendWindowSize;
        MaxRetransmit = other.MaxRetransmit;
        MaximizeSocketBuffers = other.MaximizeSocketBuffers;
    }

    private sealed class MeteredClient : KcpClient
    {
        public MeteredClient(Action onConnected, Action<ArraySegment<byte>, KcpChannel> onData, Action onDisconnected,
            Action<ErrorCode, string> onError, KcpConfig config) : base(onConnected, onData, onDisconnected, onError, config) { }

        protected override bool RawReceive(out ArraySegment<byte> segment)
        {
            bool received = base.RawReceive(out segment);
            if (received) Interlocked.Add(ref bytes, segment.Count + DatagramOverhead);
            return received;
        }
    }
}
