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
        var device = Assert.Single(DeviceAssembler.FromRecords(Records(deviceClass: "AtemSwitcher")));
        Assert.Equal("AtemSwitcher", device.DeviceClass);
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

    [Theory]
    [InlineData("Videohub", "videohub")]
    [InlineData("videohub", "videohub")]
    [InlineData("SmartVideohub", "videohub")]
    [InlineData("AtemSwitcher", null)]
    [InlineData("", null)]
    public void DeviceTypeFor_MapsKnownClassesCaseInsensitively(string deviceClass, string? expected)
    {
        Assert.Equal(expected, DeviceClasses.DeviceTypeFor(deviceClass));
    }
}
