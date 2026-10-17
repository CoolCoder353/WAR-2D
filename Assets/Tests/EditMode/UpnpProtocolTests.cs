using System;
using System.Net;
using NUnit.Framework;
using WAR2D.Net.Upnp;

public class UpnpProtocolTests
{
    private const string Description = @"<?xml version=""1.0""?>
<root xmlns=""urn:schemas-upnp-org:device-1-0"">
  <device>
    <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
    <serviceList><service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType><controlURL>/l3f</controlURL></service></serviceList>
    <deviceList><device><deviceList><device>
      <serviceList>
        <service><serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType><controlURL>/ppp</controlURL></service>
        <service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/upnp/control/WANIPConn1</controlURL></service>
      </serviceList>
    </device></deviceList></device></deviceList>
  </device>
</root>";

    [Test]
    public void ReadsLocationHeaderCaseInsensitively()
    {
        string reply = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nLocation: http://192.168.1.1:5000/rootDesc.xml\r\nST: x\r\n\r\n";
        Assert.AreEqual(new Uri("http://192.168.1.1:5000/rootDesc.xml"), UpnpProtocol.ParseLocation(reply));
        Assert.IsNull(UpnpProtocol.ParseLocation("HTTP/1.1 200 OK\r\nST: x\r\n\r\n"));
        Assert.IsNull(UpnpProtocol.ParseLocation("HTTP/1.1 200 OK\r\nLOCATION: file:///etc/passwd\r\n\r\n"));
    }

    [Test]
    public void PrefersWanIpConnectionAndResolvesControlUrl()
    {
        Assert.IsTrue(UpnpProtocol.TryFindWanService(Description, new Uri("http://192.168.1.1:5000/rootDesc.xml"), out WanService s));
        Assert.AreEqual("urn:schemas-upnp-org:service:WANIPConnection:1", s.ServiceType);
        Assert.AreEqual(new Uri("http://192.168.1.1:5000/upnp/control/WANIPConn1"), s.ControlUrl);
    }

    [Test]
    public void UsesUrlBaseWhenGiven()
    {
        string xml = Description.Replace("<device>\n    <deviceType>", "<URLBase>http://10.0.0.1:49000/</URLBase><device>\n    <deviceType>");
        StringAssert.Contains("URLBase", xml);
        Assert.IsTrue(UpnpProtocol.TryFindWanService(xml, new Uri("http://192.168.1.1:5000/rootDesc.xml"), out WanService s));
        Assert.AreEqual(new Uri("http://10.0.0.1:49000/upnp/control/WANIPConn1"), s.ControlUrl);
    }

    [Test]
    public void NoWanServiceOrBadXmlFails()
    {
        Assert.IsFalse(UpnpProtocol.TryFindWanService("<root><device/></root>", new Uri("http://r/"), out _));
        Assert.IsFalse(UpnpProtocol.TryFindWanService("not xml", new Uri("http://r/"), out _));
    }

    [Test]
    public void EnvelopeEscapesArguments()
    {
        string body = UpnpProtocol.SoapEnvelope("urn:x:WANIPConnection:1", "AddPortMapping", ("NewPortMappingDescription", "a<b&c"), ("NewExternalPort", "7778"));
        StringAssert.Contains("<u:AddPortMapping xmlns:u=\"urn:x:WANIPConnection:1\">", body);
        StringAssert.Contains("<NewPortMappingDescription>a&lt;b&amp;c</NewPortMappingDescription><NewExternalPort>7778</NewExternalPort>", body);
    }

    [Test]
    public void ReadsValuesAndFaults()
    {
        string ok = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"urn:x\"><NewExternalIPAddress>203.0.113.5</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>";
        Assert.AreEqual("203.0.113.5", UpnpProtocol.ParseValue(ok, "NewExternalIPAddress"));
        string fault = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>725</errorCode><errorDescription>OnlyPermanentLeasesSupported</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        Assert.AreEqual(UpnpProtocol.OnlyPermanentLeases, UpnpProtocol.ParseErrorCode(fault));
        Assert.AreEqual(0, UpnpProtocol.ParseErrorCode("garbage"));
    }

    [TestCase("203.0.114.5", true)]
    [TestCase("8.8.8.8", true)]
    [TestCase("100.63.255.255", true)]
    [TestCase("172.32.0.1", true)]
    [TestCase("10.1.2.3", false)]
    [TestCase("172.16.0.1", false)]
    [TestCase("172.31.255.255", false)]
    [TestCase("192.168.0.1", false)]
    [TestCase("100.64.0.1", false)]
    [TestCase("100.127.255.255", false)]
    [TestCase("169.254.1.1", false)]
    [TestCase("127.0.0.1", false)]
    [TestCase("0.0.0.0", false)]
    [TestCase("224.0.0.1", false)]
    [TestCase("2001:db8::1", false)]
    [TestCase("198.18.0.1", false)]
    [TestCase("192.0.2.1", false)]
    [TestCase("198.51.100.1", false)]
    [TestCase("203.0.113.5", false)]
    public void ClassifiesPublicAddresses(string address, bool isPublic)
    {
        Assert.AreEqual(isPublic, UpnpProtocol.IsPublic(IPAddress.Parse(address)));
    }

    [Test]
    public void OnlyTheRespondersOwnAddressCountsAsItsHost()
    {
        var router = IPAddress.Parse("192.168.1.1");
        Assert.IsTrue(UpnpProtocol.IsSameHost(new Uri("http://192.168.1.1:5000/desc.xml"), router));
        Assert.IsFalse(UpnpProtocol.IsSameHost(new Uri("http://192.168.1.2:5000/desc.xml"), router));
        Assert.IsFalse(UpnpProtocol.IsSameHost(new Uri("http://evil.example/desc.xml"), router));
    }

    [Test]
    public void DoctypesInADescriptionAreRefused()
    {
        string xml = "<!DOCTYPE r [<!ENTITY a \"x\">]><root><URLBase>http://192.168.1.1/</URLBase></root>";
        Assert.IsFalse(UpnpProtocol.TryFindWanService(xml, new Uri("http://192.168.1.1/d.xml"), out _));
        Assert.IsNull(UpnpProtocol.ParseValue(xml, "URLBase"));
    }
}
