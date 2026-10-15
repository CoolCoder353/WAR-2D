using System.IO;
using Mirror;
using UnityEngine;

/// <summary>
/// Writes the bytes this process receives and sends through Mirror each second (relay bandwidth check):
/// <c>-relayMeter &lt;outfile&gt; -relayMeterAllowed</c> (or <c>-perf</c>). Counts Mirror payload, so it understates wire bytes.
/// </summary>
public sealed class RelayMeter : MonoBehaviour
{
    private long inBytes, outBytes;
    private float next;
    private StreamWriter file;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        int i = System.Array.IndexOf(args, "-relayMeter");
        if (i < 0 || i + 1 >= args.Length) return;
        if (!DevApi.Allowed && System.Array.IndexOf(args, "-relayMeterAllowed") < 0) return;
        new GameObject("RelayMeter").AddComponent<RelayMeter>().Begin(args[i + 1]);
    }

    private void Begin(string path)
    {
        DontDestroyOnLoad(gameObject);
        file = new StreamWriter(path) { AutoFlush = true };
        file.WriteLine("t,inBytes,outBytes");
        NetworkDiagnostics.InMessageEvent += m => System.Threading.Interlocked.Add(ref inBytes, (long)m.bytes * m.count);
        NetworkDiagnostics.OutMessageEvent += m => System.Threading.Interlocked.Add(ref outBytes, (long)m.bytes * m.count);
        next = Time.unscaledTime + 1f;
    }

    private void Update()
    {
        if (file == null || Time.unscaledTime < next) return;
        next += 1f;
        file.WriteLine($"{Time.unscaledTime:F0},{System.Threading.Interlocked.Exchange(ref inBytes, 0)},{System.Threading.Interlocked.Exchange(ref outBytes, 0)}");
    }

    private void OnDestroy() => file?.Dispose();
}
