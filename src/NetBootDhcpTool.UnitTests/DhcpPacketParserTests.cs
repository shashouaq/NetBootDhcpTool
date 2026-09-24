using System.Net;
using NetBootDhcpTool.Dhcp;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class DhcpPacketParserTests
{
    [TestMethod]
    public void ParsesSelectingRequestIdentityAndServerFields()
    {
        var parsed = DhcpPacketParser.Parse(Request(
            53, 1, (byte)DhcpMessageType.Request,
            50, 4, 192, 168, 50, 100,
            54, 4, 192, 168, 50, 1,
            61, 7, 1, 0x02, 0x11, 0x22, 0x33, 0x44, 0x55,
            12, 6, (byte)'d', (byte)'e', (byte)'v', (byte)'i', (byte)'c', (byte)'e',
            255));

        Assert.AreEqual(0x10203040u, parsed.Xid);
        Assert.AreEqual(DhcpMessageType.Request, parsed.MessageType);
        Assert.AreEqual("192.168.50.100", parsed.RequestedIp?.ToString());
        Assert.AreEqual("192.168.50.1", parsed.ServerIdentifier?.ToString());
        Assert.AreEqual("ID:01021122334455", parsed.ClientKey);
        Assert.AreEqual("device", parsed.Hostname);
    }

    [TestMethod]
    public void RejectsMalformedHeadersOptionsAndMissingEndMarker()
    {
        var valid = Request(53, 1, (byte)DhcpMessageType.Discover, 255);
        var shortPacket = new byte[239];
        var badCookie = (byte[])valid.Clone();
        badCookie[239] = 0;
        var badOp = (byte[])valid.Clone();
        badOp[0] = 3;
        var badHlen = (byte[])valid.Clone();
        badHlen[2] = 5;
        var multicastMac = (byte[])valid.Clone();
        multicastMac[28] = 0x01;
        var missingEnd = Request(53, 1, (byte)DhcpMessageType.Discover);
        var truncated = Request(53, 1, (byte)DhcpMessageType.Discover, 12, 4, 1, 2, 255);
        var missingType = Request(255);
        var duplicateType = Request(53, 1, (byte)DhcpMessageType.Discover, 53, 1, (byte)DhcpMessageType.Discover, 255);
        var unsupportedOverload = Request(53, 1, (byte)DhcpMessageType.Discover, 52, 1, 1, 255);
        var serverTypeInRequest = Request(53, 1, (byte)DhcpMessageType.Offer, 255);

        foreach (var packet in new[] { shortPacket, badCookie, badOp, badHlen, multicastMac, missingEnd, truncated, missingType, duplicateType, unsupportedOverload, serverTypeInRequest })
            Assert.Throws<InvalidDataException>(() => DhcpPacketParser.Parse(packet));
    }

    [TestMethod]
    public void BuildsParseableOfferAndAddresslessNak()
    {
        var request = DhcpPacketParser.Parse(Request(53, 1, (byte)DhcpMessageType.Discover, 255));
        var settings = new DhcpServerSettings
        {
            ServerIp = IPAddress.Parse("192.168.50.1"),
            SubnetMask = IPAddress.Parse("255.255.255.0"),
            PoolStart = IPAddress.Parse("192.168.50.100"),
            PoolEnd = IPAddress.Parse("192.168.50.110"),
            LeaseSeconds = 120
        };

        var offer = DhcpPacketParser.Parse(DhcpPacketParser.BuildReply(request, settings, IPAddress.Parse("192.168.50.100"), DhcpMessageType.Offer));
        Assert.AreEqual(DhcpMessageType.Offer, offer.MessageType);
        Assert.AreEqual("192.168.50.1", offer.ServerIdentifier?.ToString());
        Assert.AreEqual("192.168.50.100", offer.YiAddr.ToString());

        var nak = DhcpPacketParser.Parse(DhcpPacketParser.BuildReply(request, settings, IPAddress.Any, DhcpMessageType.Nak));
        Assert.AreEqual(DhcpMessageType.Nak, nak.MessageType);
        Assert.AreEqual(IPAddress.Any, nak.YiAddr);
        Assert.AreEqual("192.168.50.1", nak.ServerIdentifier?.ToString());
    }

    [TestMethod]
    public void ExistingDhcpDetectorMatchesOnlyTheOutstandingClientOffer()
    {
        var packet = new DhcpPacket
        {
            Op = 2,
            HType = 1,
            HLen = 6,
            Xid = 0x10203040,
            ChAddr = new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 }.Concat(new byte[10]).ToArray(),
            MessageType = DhcpMessageType.Offer,
            YiAddr = IPAddress.Parse("192.168.50.100"),
            ServerIdentifier = IPAddress.Parse("192.168.50.1")
        };
        var mac = packet.ChAddr.AsSpan(0, 6).ToArray();
        Assert.IsTrue(NetBootDhcpTool.Network.ExistingDhcpDetector.IsMatchingOffer(packet, packet.Xid, mac));
        Assert.IsFalse(NetBootDhcpTool.Network.ExistingDhcpDetector.IsMatchingOffer(packet, packet.Xid + 1, mac));
        Assert.IsFalse(NetBootDhcpTool.Network.ExistingDhcpDetector.IsMatchingOffer(packet, packet.Xid, new byte[] { 0x02, 1, 2, 3, 4, 6 }));
        packet.MessageType = DhcpMessageType.Ack;
        Assert.IsFalse(NetBootDhcpTool.Network.ExistingDhcpDetector.IsMatchingOffer(packet, packet.Xid, mac));
    }

    private static byte[] Request(params byte[] options)
    {
        var data = new byte[240 + options.Length];
        data[0] = 1;
        data[1] = 1;
        data[2] = 6;
        data[4] = 0x10;
        data[5] = 0x20;
        data[6] = 0x30;
        data[7] = 0x40;
        data[28] = 0x02;
        data[29] = 0x11;
        data[30] = 0x22;
        data[31] = 0x33;
        data[32] = 0x44;
        data[33] = 0x55;
        data[236] = 99;
        data[237] = 130;
        data[238] = 83;
        data[239] = 99;
        options.CopyTo(data, 240);
        return data;
    }
}
