using System.Net;

namespace Bmd.Devices.Discovery;

/// <summary>A Blackmagic device found via mDNS. <see cref="DeviceType"/> is the bmd device
/// group (currently only <c>"videohub"</c>) when <see cref="DeviceClass"/> is recognized,
/// else <c>null</c> for an unsupported or unknown class. An unrecognized class is never an
/// error and is never guessed into a supported type — the device still appears, just
/// untyped.</summary>
public sealed record DiscoveredDevice(string Name, string DeviceClass, string? DeviceType, IPAddress Address, int Port);

/// <summary>Maps a device's mDNS TXT <c>class=</c> value to the bmd device group it belongs to.</summary>
public static class DeviceClasses
{
    /// <summary>The mDNS TXT <c>class=</c> values we believe Blackmagic Videohub models report.
    /// <para><b>This list is unverified against real hardware.</b> Blackmagic does not publish
    /// the set of <c>class=</c> values its devices advertise; these are seed values based on
    /// observation, not a specification. Run <c>bmd discover --all</c> against real devices on
    /// the network to see the actual classes present and refine this table.</para></summary>
    public static IReadOnlyList<string> KnownVideohubClasses { get; } = ["Videohub", "SmartVideohub", "VideoHub"];

    /// <summary>Case-insensitive lookup of the bmd device group for a device class.
    /// Returns <c>"videohub"</c> when <paramref name="deviceClass"/> matches one of
    /// <see cref="KnownVideohubClasses"/>, otherwise <c>null</c> — never a guess.</summary>
    public static string? DeviceTypeFor(string deviceClass)
    {
        foreach (var known in KnownVideohubClasses)
        {
            if (string.Equals(known, deviceClass, StringComparison.OrdinalIgnoreCase))
                return "videohub";
        }
        return null;
    }
}

/// <summary>Joins the flat list of records from an mDNS response into <see cref="DiscoveredDevice"/> entries.</summary>
public static class DeviceAssembler
{
    /// <summary>Builds one device per SRV record that has a resolvable A record. A SRV entry
    /// without a matching address is skipped — an incomplete announcement is not a device.
    /// The device name comes from the TXT <c>name=</c> entry if present, else the instance
    /// label of the SRV name (the part before the first '.').</summary>
    public static IReadOnlyList<DiscoveredDevice> FromRecords(IReadOnlyList<DnsRecord> records)
    {
        var srvRecords = new List<SrvRecord>();
        // RFC 1035 §3.1: DNS name comparison is case-insensitive. A device's SRV target and
        // its A record's name (or its SRV name and TXT name) are not guaranteed to share
        // byte-identical casing on the wire, so an ordinal dictionary here would silently
        // drop real devices whose firmware capitalizes the two records differently.
        var txtByName = new Dictionary<string, TxtRecord>(StringComparer.OrdinalIgnoreCase);
        var addressByName = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            switch (record)
            {
                case SrvRecord srv:
                    srvRecords.Add(srv);
                    break;
                case TxtRecord txt:
                    txtByName[txt.Name] = txt; // last wins on duplicates
                    break;
                case ARecord a:
                    addressByName[a.Name] = a.Address; // last wins on duplicates
                    break;
            }
        }

        var devices = new List<DiscoveredDevice>(srvRecords.Count);
        foreach (var srv in srvRecords)
        {
            if (!addressByName.TryGetValue(srv.Target, out var address))
                continue; // incomplete announcement: no resolvable address, not a device

            string deviceClass = "";
            string? name = null;
            if (txtByName.TryGetValue(srv.Name, out var txt))
            {
                foreach (var entry in txt.Entries)
                {
                    int eq = entry.IndexOf('=');
                    if (eq < 0) continue;
                    var key = entry[..eq];
                    var value = entry[(eq + 1)..].Trim();
                    if (string.Equals(key, "class", StringComparison.OrdinalIgnoreCase)) deviceClass = value;
                    else if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) && value.Length > 0) name = value;
                }
            }

            name ??= InstanceLabel(srv.Name);
            devices.Add(new DiscoveredDevice(name, deviceClass, DeviceClasses.DeviceTypeFor(deviceClass), address, srv.Port));
        }

        return devices;
    }

    static string InstanceLabel(string srvName)
    {
        int dot = srvName.IndexOf('.');
        return dot < 0 ? srvName : srvName[..dot];
    }
}
