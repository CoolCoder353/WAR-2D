using NUnit.Framework;
using WAR2D.Net;

public class JoinAddressTests
{
    [TestCase("192.168.1.10", "192.168.1.10", 7778)]
    [TestCase("  203.0.113.5:27015 ", "203.0.113.5", 27015)]
    [TestCase("example.com", "example.com", 7778)]
    [TestCase("play.example.com:40123", "play.example.com", 40123)]
    [TestCase("::1", "::1", 7778)]
    [TestCase("2001:db8::7", "2001:db8::7", 7778)]
    [TestCase("[2001:db8::7]", "2001:db8::7", 7778)]
    [TestCase("[2001:db8::7]:9000", "2001:db8::7", 9000)]
    public void ParsesHostAndPort(string text, string host, int port)
    {
        Assert.IsTrue(JoinAddress.TryParse(text, 7778, out string h, out ushort p));
        Assert.AreEqual(host, h);
        Assert.AreEqual(port, p);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("1.2.3")]
    [TestCase("1.2.3.4:")]
    [TestCase("1.2.3.4:0")]
    [TestCase("1.2.3.4:65536")]
    [TestCase("1.2.3.4:-5")]
    [TestCase("1.2.3.4:+5")]
    [TestCase("host:port")]
    [TestCase(":7778")]
    [TestCase("[2001:db8::7")]
    [TestCase("[2001:db8::7]9000")]
    [TestCase("[example.com]:9000")]
    [TestCase(".example.com")]
    public void RejectsMalformed(string text)
    {
        Assert.IsFalse(JoinAddress.TryParse(text, 7778, out _, out _));
    }
}
