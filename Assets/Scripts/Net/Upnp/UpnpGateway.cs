using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WAR2D.Net.Upnp
{
    /// <summary>A router refused a UPnP action (its UPnPError code, 0 when it gave none).</summary>
    public sealed class UpnpException : Exception
    {
        public readonly int Code;

        public UpnpException(string action, int code, int status)
            : base($"{action} failed: HTTP {status}, UPnP error {code}")
        {
            Code = code;
        }
    }

    /// <summary>
    /// Talks to the local network's UPnP internet gateway: finds it over SSDP, reads its external address and
    /// adds or removes port mappings. Everything runs off the main thread (<c>ConfigureAwait(false)</c>), so a
    /// caller may block on it without deadlocking Unity's synchronisation context.
    /// </summary>
    public sealed class UpnpGateway
    {
        /// <summary>No redirects and a small body cap: a device description comes from whatever answered on the LAN.</summary>
        private const long MaxDescriptionBytes = 256 * 1024;

        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(3),
            MaxResponseContentBufferSize = MaxDescriptionBytes,
        };

        /// <summary>The router's WAN connection service.</summary>
        public WanService Service { get; }

        /// <summary>This machine's address on the interface facing the router (what a mapping points at).</summary>
        public IPAddress LocalAddress { get; }

        private UpnpGateway(WanService service, IPAddress localAddress)
        {
            Service = service;
            LocalAddress = localAddress;
        }

        /// <summary>Searches for a gateway for up to <paramref name="timeout"/>; null when none answers.</summary>
        public static async Task<UpnpGateway> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
        {
            var seen = new HashSet<string>();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var group = new IPEndPoint(IPAddress.Parse(UpnpProtocol.MulticastAddress), UpnpProtocol.MulticastPort);
            foreach (string target in UpnpProtocol.SearchTargets)
            {
                byte[] request = Encoding.ASCII.GetBytes(UpnpProtocol.SearchRequest(target));
                await udp.SendAsync(request, request.Length, group).ConfigureAwait(false);
            }

            DateTime end = DateTime.UtcNow + timeout;
            Task<UdpReceiveResult> receive = udp.ReceiveAsync();
            // Disposing the socket faults the receive still pending when this returns; observe it.
            using var observe = new Disposer(() => receive.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted));
            while (true)
            {
                TimeSpan left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                if (await Task.WhenAny(receive, Task.Delay(left, ct)).ConfigureAwait(false) != receive) break;
                ct.ThrowIfCancellationRequested();

                UdpReceiveResult reply = receive.Result;
                Uri location = UpnpProtocol.ParseLocation(Encoding.ASCII.GetString(reply.Buffer));
                receive = udp.ReceiveAsync();
                // A gateway describes itself at its own address; anything else is another device pointing us elsewhere.
                if (location == null || !UpnpProtocol.IsSameHost(location, reply.RemoteEndPoint.Address) || !seen.Add(location.AbsoluteUri)) continue;

                try
                {
                    using var get = new HttpRequestMessage(HttpMethod.Get, location);
                    using HttpResponseMessage page = await Http.SendAsync(get, ct).ConfigureAwait(false);
                    if (!page.IsSuccessStatusCode) continue;
                    string description = Encoding.UTF8.GetString(await page.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                    if (UpnpProtocol.TryFindWanService(description, location, out WanService service) && UpnpProtocol.IsSameHost(service.ControlUrl, reply.RemoteEndPoint.Address))
                        return new UpnpGateway(service, LocalAddressFacing(location));
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    // One bad responder (unreachable, malformed, IPv6) must not stop the search for the real router.
                    UnityEngine.Debug.Log($"[UPnP] could not use {location}: {e.Message}");
                }
            }
            ct.ThrowIfCancellationRequested();
            return null;
        }

        /// <summary>
        /// The router's external IPv4 address, or null when it does not say. An empty answer usually means the
        /// router has no IPv4 of its own on the internet (DS-Lite, or behind another router).
        /// </summary>
        public async Task<IPAddress> GetExternalAddressAsync(CancellationToken ct)
        {
            string reply = await SoapAsync("GetExternalIPAddress", ct).ConfigureAwait(false);
            return IPAddress.TryParse(UpnpProtocol.ParseValue(reply, "NewExternalIPAddress") ?? "", out IPAddress ip) ? ip : null;
        }

        /// <summary>Maps external <paramref name="port"/> to the same port on this machine. Throws <see cref="UpnpException"/>.</summary>
        public Task AddMappingAsync(string protocol, ushort port, int leaseSeconds, string description, CancellationToken ct) =>
            SoapAsync("AddPortMapping", ct,
                ("NewRemoteHost", ""),
                ("NewExternalPort", Port(port)),
                ("NewProtocol", protocol),
                ("NewInternalPort", Port(port)),
                ("NewInternalClient", LocalAddress.ToString()),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", description),
                ("NewLeaseDuration", leaseSeconds.ToString(CultureInfo.InvariantCulture)));

        /// <summary>Removes the mapping of external <paramref name="port"/>.</summary>
        public Task DeleteMappingAsync(string protocol, ushort port, CancellationToken ct) =>
            SoapAsync("DeletePortMapping", ct,
                ("NewRemoteHost", ""),
                ("NewExternalPort", Port(port)),
                ("NewProtocol", protocol));

        private async Task<string> SoapAsync(string action, CancellationToken ct, params (string, string)[] args)
        {
            string body = UpnpProtocol.SoapEnvelope(Service.ServiceType, action, args);
            using var request = new HttpRequestMessage(HttpMethod.Post, Service.ControlUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{Service.ServiceType}#{action}\"");
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            // Decoded by hand: routers send charsets HttpClient refuses (e.g. quoted, "utf-8").
            string reply = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
            if (!response.IsSuccessStatusCode) throw new UpnpException(action, UpnpProtocol.ParseErrorCode(reply), (int)response.StatusCode);
            return reply;
        }

        private sealed class Disposer : IDisposable
        {
            private readonly Action action;
            public Disposer(Action action) => this.action = action;
            public void Dispose() => action();
        }

        private static string Port(ushort port) => port.ToString(CultureInfo.InvariantCulture);

        /// <summary>The local address the OS would use to reach the router (no packet is sent).</summary>
        private static IPAddress LocalAddressFacing(Uri router)
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(router.Host, router.Port);
            return ((IPEndPoint)probe.LocalEndPoint).Address;
        }
    }
}
