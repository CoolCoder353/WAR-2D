using WAR2D.Dev.Logging;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WAR2D.Net.Upnp;

namespace WAR2D.Net
{
    /// <summary>How a hosted match can be reached from the internet.</summary>
    public enum Reachability
    {
        /// <summary>Not hosting, or UPnP is off (<c>-noUpnp</c>).</summary>
        Off,
        /// <summary>Looking for the router and asking it to open the port.</summary>
        Opening,
        /// <summary>The router forwards the port to this machine.</summary>
        Open,
        /// <summary>No UPnP router, or it refused: the host forwards the port by hand or uses a tunnel.</summary>
        Failed,
        /// <summary>The router's own external address is not public (carrier-grade or double NAT): only a tunnel works.</summary>
        SharedAddress,
    }

    /// <summary>
    /// Opens the host's game port on the router through UPnP so players on other networks can join for external
    /// playtests, and describes how to reach the host. A stopgap until v0.9's Steam relay replaces join-by-IP.
    /// The mapping is leased for <see cref="LeaseSeconds"/> and renewed while hosting, so a crashed host's port
    /// closes by itself; leaving the lobby removes it. Only the menu's Host button starts it (tests, the perf
    /// harness and QA host through <see cref="GameManager.HostServer"/> and never touch the router).
    /// </summary>
    public static class HostReachability
    {
        /// <summary>The mapping's lease; renewed every half lease.</summary>
        public const int LeaseSeconds = 7200;
        private const string Protocol = "UDP", Description = "WAR-2D";
        private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(3);

        private static readonly object gate = new object();
        private static CancellationTokenSource cts;
        private static Task run;
        private static volatile int state;
        private static volatile string externalAddress, localAddress, reason;
        private static int version;

        /// <summary>Where the mapping stands.</summary>
        public static Reachability State => (Reachability)state;

        /// <summary>The router's external address once known, else null.</summary>
        public static string ExternalAddress => externalAddress;

        /// <summary>The port being mapped.</summary>
        public static ushort Port { get; private set; }

        /// <summary>Bumped on every change, so the UI can redraw only when needed.</summary>
        public static int Version => Volatile.Read(ref version);

        /// <summary>False with <c>-noUpnp</c> on the command line.</summary>
        public static bool Enabled => Array.IndexOf(Environment.GetCommandLineArgs(), "-noUpnp") < 0;

        /// <summary>Starts opening <paramref name="port"/> (UDP) on the router. Main thread.</summary>
        public static void Start(ushort port)
        {
            if (!Enabled) return;
            lock (gate)
            {
                Stop(false);
                Port = port;
                externalAddress = null;
                localAddress = null;
                reason = null;
                Set(Reachability.Opening);
                var source = new CancellationTokenSource();
                cts = source;
                Task previous = run ?? Task.CompletedTask;
                // The previous run removes its mapping first, so it never deletes this one.
                run = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    await RunAsync(port, source.Token).ConfigureAwait(false);
                });
            }
        }

        /// <summary>
        /// Stops renewing and removes the mapping. With <paramref name="wait"/> (quitting), blocks up to two
        /// seconds for the router to confirm; otherwise the removal finishes in the background.
        /// </summary>
        public static void Stop(bool wait)
        {
            Task pending;
            lock (gate)
            {
                if (cts == null) return;
                cts.Cancel();
                cts = null;
                pending = run;
                Set(Reachability.Off);
            }
            if (wait && pending != null)
            {
                try { pending.Wait(TimeSpan.FromSeconds(2)); }
                catch (AggregateException) { }
            }
        }

        /// <summary>
        /// The lobby's line for the host: the LAN address (<paramref name="lanFallback"/> until the router says
        /// which interface faces it) and how internet players join.
        /// </summary>
        public static string Describe(string lanFallback)
        {
            string lan = localAddress ?? lanFallback;
            switch (State)
            {
                case Reachability.Opening: return $"LAN: {lan} · opening port {Port} on your router…";
                case Reachability.Open: return externalAddress != null
                    ? $"LAN: {lan} · internet: {externalAddress}:{Port}"
                    : $"LAN: {lan} · internet: port {Port} opened, but the router reports no public IP (a tunnel may be needed)";
                case Reachability.Failed: return $"LAN: {lan} · internet: forward UDP {Port} to {lan} on your router ({reason})";
                case Reachability.SharedAddress: return $"LAN: {lan} · internet: your ISP shares your address, use a tunnel (docs/playtesting.md)";
                default: return $"others join at {lan}";
            }
        }

        private static async Task RunAsync(ushort port, CancellationToken ct)
        {
            UpnpGateway router = null;
            bool mapped = false;
            try
            {
                router = await UpnpGateway.DiscoverAsync(DiscoverTimeout, ct).ConfigureAwait(false);
                if (router == null)
                {
                    Fail("no UPnP router answered", ct);
                    return;
                }
                if (ct.IsCancellationRequested) return;
                localAddress = router.LocalAddress.ToString();

                IPAddress external = null;
                try { external = await router.GetExternalAddressAsync(ct).ConfigureAwait(false); }
                catch (Exception e) when (!(e is OperationCanceledException)) { WarLog.Info(LogCategory.Net, $"no external address: {e.Message}"); }
                if (external != null && !UpnpProtocol.IsPublic(external))
                {
                    WarLog.Info(LogCategory.Net, $"the router's external address {external} is not public: carrier-grade or double NAT.");
                    Report(Reachability.SharedAddress, ct);
                    return;
                }
                if (ct.IsCancellationRequested) return;
                externalAddress = external?.ToString();

                int lease = await MapAsync(router, port, LeaseSeconds, ct).ConfigureAwait(false);
                mapped = true;
                WarLog.Info(LogCategory.Net, $"mapped UDP {port} to {router.LocalAddress} (lease {lease} s); internet players join {externalAddress ?? "<public IP>"}:{port}");
                Report(Reachability.Open, ct);

                while (lease > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(lease / 2), ct).ConfigureAwait(false);
                    try { await MapAsync(router, port, lease, ct).ConfigureAwait(false); }
                    catch (Exception e) when (!(e is OperationCanceledException)) { WarLog.Warn(LogCategory.Net, $"could not renew the mapping: {e.Message}"); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (UpnpException e)
            {
                WarLog.Warn(LogCategory.Net, $"{e.Message}");
                Fail(e.Code == UpnpProtocol.ConflictInMapping ? "the port is already forwarded to another machine" : "the router refused UPnP", ct);
            }
            catch (Exception e)
            {
                WarLog.Warn(LogCategory.Net, $"{e.GetType().Name}: {e.Message}");
                Fail("UPnP failed", ct);
            }
            finally
            {
                if (mapped)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await router.DeleteMappingAsync(Protocol, port, timeout.Token).ConfigureAwait(false);
                        WarLog.Info(LogCategory.Net, $"removed the UDP {port} mapping");
                    }
                    catch (Exception e) { WarLog.Warn(LogCategory.Net, $"could not remove the UDP {port} mapping (it lapses with its lease): {e.Message}"); }
                }
            }
        }

        /// <summary>Adds the mapping; returns the lease used (0 when the router only takes permanent ones).</summary>
        private static async Task<int> MapAsync(UpnpGateway router, ushort port, int lease, CancellationToken ct)
        {
            try
            {
                await router.AddMappingAsync(Protocol, port, lease, Description, ct).ConfigureAwait(false);
                return lease;
            }
            catch (UpnpException e) when (e.Code == UpnpProtocol.OnlyPermanentLeases && lease != 0)
            {
                await router.AddMappingAsync(Protocol, port, 0, Description, ct).ConfigureAwait(false);
                return 0;
            }
        }

        private static void Fail(string why, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            reason = why;
            Report(Reachability.Failed, ct);
        }

        /// <summary>Publishes a run's result, unless the run was stopped (a newer state already stands).</summary>
        private static void Report(Reachability value, CancellationToken ct)
        {
            lock (gate)
                if (!ct.IsCancellationRequested) Set(value);
        }

        private static void Set(Reachability value)
        {
            state = (int)value;
            Interlocked.Increment(ref version);
        }
    }
}
