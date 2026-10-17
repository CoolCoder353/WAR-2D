using System;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml;

namespace WAR2D.Net.Upnp
{
    /// <summary>A router's WAN connection service: where to send its SOAP actions.</summary>
    public readonly struct WanService
    {
        public readonly string ServiceType;
        public readonly Uri ControlUrl;

        public WanService(string serviceType, Uri controlUrl)
        {
            ServiceType = serviceType;
            ControlUrl = controlUrl;
        }
    }

    /// <summary>
    /// The text side of UPnP IGD port mapping (no I/O, so EditMode tests cover it): the SSDP search, the
    /// device description, SOAP envelopes and their replies.
    /// </summary>
    public static class UpnpProtocol
    {
        public const string MulticastAddress = "239.255.255.250";
        public const int MulticastPort = 1900;

        /// <summary>SSDP search targets, gateway devices first. Routers answer only the ones they implement.</summary>
        public static readonly string[] SearchTargets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        };

        /// <summary>The router's "only permanent leases" error: retry with lease 0.</summary>
        public const int OnlyPermanentLeases = 725;
        /// <summary>The router's "port already mapped to another machine" error.</summary>
        public const int ConflictInMapping = 718;

        /// <summary>An SSDP M-SEARCH datagram for <paramref name="searchTarget"/>.</summary>
        public static string SearchRequest(string searchTarget) =>
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {searchTarget}\r\n\r\n";

        /// <summary>The LOCATION header of an SSDP reply (the device description URL), or null.</summary>
        public static Uri ParseLocation(string response)
        {
            if (response == null) return null;
            foreach (string line in response.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 || !line.Substring(0, colon).Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) continue;
                string value = line.Substring(colon + 1).Trim();
                return Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && (uri.Scheme == "http" || uri.Scheme == "https") ? uri : null;
            }
            return null;
        }

        /// <summary>
        /// Finds the WAN connection service in a device description (WANIPConnection:2, then :1, then
        /// WANPPPConnection), resolving its control URL against URLBase or <paramref name="location"/>.
        /// </summary>
        public static bool TryFindWanService(string descriptionXml, Uri location, out WanService service)
        {
            service = default;
            XmlDocument doc = Load(descriptionXml);
            if (doc == null) return false;

            Uri baseUri = location;
            string urlBase = FirstText(doc.DocumentElement, "URLBase");
            if (!string.IsNullOrWhiteSpace(urlBase) && Uri.TryCreate(urlBase.Trim(), UriKind.Absolute, out Uri b)) baseUri = b;

            int bestRank = int.MaxValue;
            foreach (XmlElement e in doc.GetElementsByTagName("*"))
            {
                if (e.LocalName != "service") continue;
                string type = FirstText(e, "serviceType")?.Trim();
                string control = FirstText(e, "controlURL")?.Trim();
                int rank = Rank(type);
                if (rank >= bestRank || string.IsNullOrEmpty(control)) continue;
                if (!Uri.TryCreate(baseUri, control, out Uri url)) continue;
                service = new WanService(type, url);
                bestRank = rank;
            }
            return bestRank != int.MaxValue;
        }

        private static int Rank(string serviceType)
        {
            switch (serviceType)
            {
                case "urn:schemas-upnp-org:service:WANIPConnection:2": return 0;
                case "urn:schemas-upnp-org:service:WANIPConnection:1": return 1;
                case "urn:schemas-upnp-org:service:WANPPPConnection:1": return 2;
                default: return int.MaxValue;
            }
        }

        /// <summary>A SOAP request body for <paramref name="action"/> with its arguments in order (values escaped).</summary>
        public static string SoapEnvelope(string serviceType, string action, params (string name, string value)[] args)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>")
              .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">")
              .Append("<s:Body><u:").Append(action).Append(" xmlns:u=\"").Append(SecurityElement.Escape(serviceType)).Append("\">");
            foreach ((string name, string value) in args)
                sb.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(value ?? "")).Append("</").Append(name).Append('>');
            sb.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");
            return sb.ToString();
        }

        /// <summary>The text of the first element named <paramref name="name"/> in a SOAP reply, or null.</summary>
        public static string ParseValue(string soapXml, string name)
        {
            XmlDocument doc = Load(soapXml);
            return doc == null ? null : FirstText(doc.DocumentElement, name)?.Trim();
        }

        /// <summary>The UPnPError errorCode of a SOAP fault, or 0.</summary>
        public static int ParseErrorCode(string soapXml) =>
            int.TryParse(ParseValue(soapXml, "errorCode"), out int code) ? code : 0;

        /// <summary>
        /// True for an IPv4 address reachable from the internet: not private, carrier-grade NAT (100.64/10),
        /// loopback, link-local or unspecified. A router whose own external address is not public sits behind
        /// another NAT, so a port mapped on it is still unreachable from outside.
        /// </summary>
        public static bool IsPublic(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = address.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && (b[1] & 0xF0) == 16) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return false; // IETF protocol and documentation
            if (b[0] == 198 && (b[1] & 0xFE) == 18) return false; // benchmarking
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return false; // TEST-NET-2
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false; // TEST-NET-3
            return b[0] < 224; // multicast and reserved
        }

        /// <summary>True when <paramref name="url"/> names exactly <paramref name="address"/> (a literal IP, as routers give).</summary>
        public static bool IsSameHost(Uri url, IPAddress address) =>
            url != null && address != null && IPAddress.TryParse(url.Host, out IPAddress host) && host.Equals(address);

        private static XmlDocument Load(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using (var reader = XmlReader.Create(new System.IO.StringReader(xml), settings)) doc.Load(reader);
                return doc;
            }
            catch (XmlException)
            {
                return null;
            }
        }

        private static string FirstText(XmlElement root, string localName)
        {
            if (root == null) return null;
            if (root.LocalName == localName) return root.InnerText;
            foreach (XmlElement e in root.GetElementsByTagName("*"))
                if (e.LocalName == localName) return e.InnerText;
            return null;
        }
    }
}
