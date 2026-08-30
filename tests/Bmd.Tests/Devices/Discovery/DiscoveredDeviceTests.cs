using System.Net;
using Bmd.Devices.Discovery;

namespace Bmd.Tests.Devices.Discovery;

public class DiscoveredDeviceTests
{
    static readonly IPAddress Address = IPAddress.Parse("10.0.0.5");

    static List<DnsRecord> Records(string instance = "Studio Hub._blackmagic._tcp.local",
                                   string target = "studio-hub.local",
                                   string? deviceClass = "Videohub",
                                   string? name = "Studio Hub",
                                   bool withAddress = true)
    {
        var records = new List<DnsRecord>
        {
            new PtrRecord("_blackmagic._tcp.local", instance),
            new SrvRecord(instance, target, 9990),
        };
        var entries = new List<string>();
        if (deviceClass is not null) entries.Add($"class={deviceClass}");
        if (name is not null) entries.Add($"name={name}");
        if (entries.Count > 0) records.Add(new TxtRecord(instance, entries));
        if (withAddress) records.Add(new ARecord(target, Address));
        return records;
    }

    [Fact]
    public void FromRecords_AssemblesASupportedDevice()
    {
        var device = Assert.Single(DeviceAssembler.FromRecords(Records()));
        Assert.Equal("Studio Hub", device.Name);
        Assert.Equal("Videohub", device.DeviceClass);
        Assert.Equal("videohub", device.DeviceType);
        Assert.Equal(Address, device.Address);
        Assert.Equal(9990, device.Port);
    }

    [Fact]
    public void FromRecords_UnknownClass_HasNullDeviceType_ButIsStillReturned()
    {
        // HyperDeck is genuinely unmapped: it advertises no class= at all, so discovery finds it
        // and declines to guess. AtemSwitcher used to stand here, before bmd could drive one.
        var device = Assert.Single(DeviceAssembler.FromRecords(Records(deviceClass: "HyperDeck")));
        Assert.Equal("HyperDeck", device.DeviceClass);
        Assert.Null(device.DeviceType);
    }

    [Fact]
    public void FromRecords_MissingTxt_StillYieldsADeviceWithUnknownClass()
    {
        var device = Assert.Single(DeviceAssembler.FromRecords(Records(deviceClass: null, name: null)));
        Assert.Equal("Studio Hub", device.Name);      // falls back to the instance label
        Assert.Equal("", device.DeviceClass);
        Assert.Null(device.DeviceType);
    }

    [Fact]
    public void FromRecords_NoAddress_IsSkipped()
    {
        Assert.Empty(DeviceAssembler.FromRecords(Records(withAddress: false)));
    }

    [Fact]
    public void FromRecords_NoSrv_IsSkipped()
    {
        var records = new List<DnsRecord> { new PtrRecord("_blackmagic._tcp.local", "Ghost._blackmagic._tcp.local") };
        Assert.Empty(DeviceAssembler.FromRecords(records));
    }

    [Fact]
    public void FromRecords_MultipleDevices_AreAllReturned()
    {
        var records = Records();
        records.AddRange(Records("Second Hub._blackmagic._tcp.local", "second-hub.local", "Videohub", "Second Hub"));
        records.Add(new ARecord("second-hub.local", IPAddress.Parse("10.0.0.6")));
        var devices = DeviceAssembler.FromRecords(records);
        Assert.Equal(2, devices.Count);
        Assert.Contains(devices, d => d.Name == "Second Hub");
    }

    [Fact]
    public void FromRecords_ARecordNameDiffersOnlyInCase_StillResolvesAddress()
    {
        // RFC 1035 §3.1: DNS name comparison is case-insensitive. Real firmware is not
        // guaranteed to emit an SRV target with byte-identical casing to the A record name.
        var records = new List<DnsRecord>
        {
            new PtrRecord("_blackmagic._tcp.local", "Studio Hub._blackmagic._tcp.local"),
            new SrvRecord("Studio Hub._blackmagic._tcp.local", "studio-hub.local", 9990),
            new TxtRecord("Studio Hub._blackmagic._tcp.local", ["class=Videohub", "name=Studio Hub"]),
            new ARecord("Studio-Hub.LOCAL", Address),
        };

        var device = Assert.Single(DeviceAssembler.FromRecords(records));
        Assert.Equal(Address, device.Address);
        Assert.Equal("Videohub", device.DeviceClass);
    }

    [Fact]
    public void FromRecords_TxtRecordNameDiffersOnlyInCase_StillResolvesClass()
    {
        var records = new List<DnsRecord>
        {
            new PtrRecord("_blackmagic._tcp.local", "Studio Hub._blackmagic._tcp.local"),
            new SrvRecord("Studio Hub._blackmagic._tcp.local", "studio-hub.local", 9990),
            new TxtRecord("STUDIO HUB._BLACKMAGIC._TCP.LOCAL", ["class=Videohub", "name=Studio Hub"]),
            new ARecord("studio-hub.local", Address),
        };

        var device = Assert.Single(DeviceAssembler.FromRecords(records));
        Assert.Equal("Videohub", device.DeviceClass);
        Assert.Equal("videohub", device.DeviceType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromRecords_EmptyOrWhitespaceName_FallsBackToInstanceLabel(string blankName)
    {
        var device = Assert.Single(DeviceAssembler.FromRecords(Records(name: blankName)));
        Assert.Equal("Studio Hub", device.Name);
    }

    [Fact]
    public void FromRecords_TrimsWhitespaceAroundTxtValues()
    {
        var device = Assert.Single(DeviceAssembler.FromRecords(Records(deviceClass: " Videohub ", name: " Studio Hub ")));
        Assert.Equal("Videohub", device.DeviceClass);
        Assert.Equal("videohub", device.DeviceType);
        Assert.Equal("Studio Hub", device.Name);
    }

    [Theory]
    [InlineData("Videohub", "videohub")]
    [InlineData("videohub", "videohub")]
    [InlineData("SmartVideohub", "videohub")]
    [InlineData("", null)]
    public void DeviceTypeFor_MapsKnownClassesCaseInsensitively(string deviceClass, string? expected)
    {
        Assert.Equal(expected, DeviceClasses.DeviceTypeFor(deviceClass));
    }

    [Theory]
    [InlineData("MultiView", "multiview")]
    [InlineData("multiview", "multiview")]
    [InlineData("MULTIVIEW", "multiview")]
    public void DeviceTypeFor_RecognisesTheMultiViewClass(string advertised, string expected)
    {
        // Verified against a real MultiView 4, which advertises class=MultiView.
        Assert.Equal(expected, DeviceClasses.DeviceTypeFor(advertised));
    }

    [Fact]
    public void DeviceTypeFor_StillRecognisesVideohub()
    {
        Assert.Equal("videohub", DeviceClasses.DeviceTypeFor("Videohub"));
    }

    [Theory]
    [InlineData("AtemSwitcher", "atem")]
    [InlineData("atemswitcher", "atem")]
    [InlineData("ATEMSWITCHER", "atem")]
    public void DeviceTypeFor_RecognisesTheAtemClass(string advertised, string expected)
    {
        // Reverses a considered decision rather than fixing an oversight: this used to assert
        // null, because offering to configure a device bmd could not drive would have been
        // worse than not finding it. Confirmed against two real ATEMs of different models.
        Assert.Equal(expected, DeviceClasses.DeviceTypeFor(advertised));
    }

    [Fact]
    public void DeviceTypeFor_StillDoesNotGuessAtAnUnmappedClass()
    {
        // A HyperDeck advertises no class= at all; identifying it needs a different path.
        Assert.Null(DeviceClasses.DeviceTypeFor("HyperDeck"));
    }

    [Fact]
    public void FromRecords_TxtEntries_AreSurfacedVerbatimInAdvertisedOrder()
    {
        // Includes an entry with no '=' at all and one whose value itself contains '=' — both
        // must survive untouched, since this property carries the raw wire content, not a
        // reparsed/reinterpreted view of it.
        var instance = "Studio Hub._blackmagic._tcp.local";
        var target = "studio-hub.local";
        var records = new List<DnsRecord>
        {
            new PtrRecord("_blackmagic._tcp.local", instance),
            new SrvRecord(instance, target, 9990),
            new TxtRecord(instance, ["class=Videohub", "name=Studio Hub", "nokey", "key=a=b=c"]),
            new ARecord(target, Address),
        };

        var device = Assert.Single(DeviceAssembler.FromRecords(records));
        Assert.Equal(["class=Videohub", "name=Studio Hub", "nokey", "key=a=b=c"], device.TxtEntries);
    }

    [Fact]
    public void FromRecords_NoTxtRecord_TxtEntriesIsEmpty_NotNull()
    {
        var device = Assert.Single(DeviceAssembler.FromRecords(Records(deviceClass: null, name: null)));
        Assert.NotNull(device.TxtEntries);
        Assert.Empty(device.TxtEntries);
    }

    [Fact]
    public void FromRecords_ServiceFilter_ExcludesSrvNotAdmittedByAnyQueriedPtr()
    {
        // No PTR record at all admits this SRV — as would happen for an SRV/A pair pooled
        // from unrelated mDNS traffic on the segment rather than from a service bmd queried for.
        var records = new List<DnsRecord>
        {
            new SrvRecord("Studio Hub._blackmagic._tcp.local", "studio-hub.local", 9990),
            new ARecord("studio-hub.local", Address),
        };
        Assert.Empty(DeviceAssembler.FromRecords(records, ["_blackmagic._tcp.local"]));
    }

    [Fact]
    public void FromRecords_ServiceFilter_IncludesSrvAdmittedByQueriedPtr()
    {
        var device = Assert.Single(DeviceAssembler.FromRecords(Records(), ["_blackmagic._tcp.local"]));
        Assert.Equal("Studio Hub", device.Name);
        Assert.Equal(Address, device.Address);
    }

    [Fact]
    public void FromRecords_ServiceFilter_PtrUnderUnrelatedService_DoesNotAdmitSrv()
    {
        // The PTR exists, but under a service bmd never queried for — a Chromecast or similar
        // answering on the same multicast group must not be admitted just because bmd's socket
        // happened to receive its announcement too.
        var records = new List<DnsRecord>
        {
            new PtrRecord("_googlecast._tcp.local", "Studio Hub._blackmagic._tcp.local"),
            new SrvRecord("Studio Hub._blackmagic._tcp.local", "studio-hub.local", 9990),
            new ARecord("studio-hub.local", Address),
        };
        Assert.Empty(DeviceAssembler.FromRecords(records, ["_blackmagic._tcp.local"]));
    }

    [Fact]
    public void FromRecords_ServiceFilter_PtrNameMatchIsCaseInsensitive()
    {
        var records = new List<DnsRecord>
        {
            new PtrRecord("_BLACKMAGIC._TCP.LOCAL", "Studio Hub._blackmagic._tcp.local"),
            new SrvRecord("Studio Hub._blackmagic._tcp.local", "studio-hub.local", 9990),
            new ARecord("studio-hub.local", Address),
        };
        var device = Assert.Single(DeviceAssembler.FromRecords(records, ["_blackmagic._tcp.local"]));
        Assert.Equal(Address, device.Address);
    }

    [Fact]
    public void FromRecords_NoServiceFilter_BehavesExactlyAsBeforeFiltering()
    {
        // services omitted entirely: every SRV+A pair is admitted regardless of any PTR,
        // matching every pre-filter test above and below unchanged.
        var records = new List<DnsRecord>
        {
            new SrvRecord("Studio Hub._blackmagic._tcp.local", "studio-hub.local", 9990),
            new ARecord("studio-hub.local", Address),
        };
        Assert.Single(DeviceAssembler.FromRecords(records));
    }
}
