using System.Net;

namespace Bmd.Devices.Discovery;

/// <summary>A Blackmagic device found via mDNS. <see cref="DeviceType"/> is the bmd device
/// group (currently only <c>"videohub"</c>) when <see cref="DeviceClass"/> is recognized,
/// else <c>null</c> for an unsupported or unknown class. An unrecognized class is never an
/// error and is never guessed into a supported type — the device still appears, just
/// untyped. <see cref="TxtEntries"/> carries the device's TXT record verbatim, in the order
/// advertised — every <c>key=value</c> string as received, not just the <c>class=</c>/<c>name=</c>
/// pair this type otherwise derives; it is empty (never <c>null</c>) when the device sent no
/// TXT record at all. This is what lets <c>--all</c> show what a device with no recognized
/// <c>class=</c> reports, and is the raw material any future device-type support would key off
/// of.</summary>
public sealed record DiscoveredDevice(
    string Name, string DeviceClass, string? DeviceType, IPAddress Address, int Port,
    IReadOnlyList<string> TxtEntries)
{
    /// <summary>Convenience constructor for the common case of no TXT entries (or when a
    /// caller already extracted <see cref="DeviceClass"/>/<see cref="Name"/> and has nothing
    /// further to carry) — equivalent to passing an empty collection for
    /// <see cref="TxtEntries"/>.</summary>
    public DiscoveredDevice(string Name, string DeviceClass, string? DeviceType, IPAddress Address, int Port)
        : this(Name, DeviceClass, DeviceType, Address, Port, []) { }
}

/// <summary>Maps a device's mDNS TXT <c>class=</c> value to the bmd device group it belongs to.</summary>
public static class DeviceClasses
{
    /// <summary>mDNS TXT <c>class=</c> values mapped to the bmd device group that handles them.
    /// <para><c>Videohub</c>, <c>MultiView</c> and <c>AtemSwitcher</c> are <b>confirmed against
    /// real hardware</b> (a Smart Videohub 40x40, a MultiView 4, and two ATEMs — a Television
    /// Studio HD III and a 1 M/E Production Studio 4K, both advertising on UDP 9910). The
    /// remaining Videohub spellings are seed values based on observation, not a specification —
    /// Blackmagic does not publish this set.</para>
    /// <para><c>AtemSwitcher</c> was deliberately absent here until bmd could drive an ATEM,
    /// because mapping it would have offered to configure a device it could not talk to. That
    /// reasoning expired when the `atem` group landed.</para>
    /// <para>The HyperDeck and WebPresenter family are still unmapped, and cannot be added the
    /// same way: they advertise on port 9977 with <b>no <c>class=</c> at all</b>, identifying
    /// themselves by a <c>product id=</c> (BE73, BE74, BE8B, BE8C observed) instead. Supporting
    /// them needs a second identification path, not another row in this table.</para>
    /// Run <c>bmd discover --all</c> against real devices to refine this.</summary>
    static readonly KeyValuePair<string, string>[] ClassMap =
    [
        new("Videohub", "videohub"),
        new("SmartVideohub", "videohub"),
        new("VideoHub", "videohub"),
        new("MultiView", "multiview"),
        new("AtemSwitcher", "atem"),
    ];

    /// <summary>Case-insensitive lookup of the bmd device group for a device class.
    /// Returns null for anything not in the table — never a guess.</summary>
    public static string? DeviceTypeFor(string deviceClass)
    {
        foreach (var (advertised, group) in ClassMap)
            if (string.Equals(advertised, deviceClass, StringComparison.OrdinalIgnoreCase))
                return group;
        return null;
    }
}

/// <summary>Joins the flat list of records from an mDNS response into <see cref="DiscoveredDevice"/> entries.</summary>
public static class DeviceAssembler
{
    /// <summary>Builds one device per SRV record that has a resolvable A record. A SRV entry
    /// without a matching address is skipped — an incomplete announcement is not a device.
    /// The device name comes from the TXT <c>name=</c> entry if present, else the instance
    /// label of the SRV name (the part before the first '.').
    /// <para>When <paramref name="services"/> is supplied, an SRV record is only admitted if
    /// some PTR record in <paramref name="records"/> both has a <c>Name</c> matching one of
    /// <paramref name="services"/> and a <c>Target</c> matching the SRV's <c>Name</c>. This
    /// defends the pooled response set against stray unicast traffic and any future change to
    /// how records are collected: <see cref="MdnsClient"/>'s socket binds an ephemeral port, so
    /// it cannot actually receive multicast traffic addressed to 224.0.0.251:5353 — it only
    /// hears responders that honor RFC 6762 §6.7's legacy-unicast reply rule (Blackmagic gear
    /// does; the hardware run proves it). So the pool is not "every device replying on the
    /// segment" in practice, but the filter still scopes admission to devices actually
    /// advertised under a service bmd asked about, rather than admitting every SRV+A pair
    /// regardless of what service it belongs to. When <paramref name="services"/> is
    /// <c>null</c>, every SRV record is admitted, matching prior (unfiltered) behavior.</para></summary>
    public static IReadOnlyList<DiscoveredDevice> FromRecords(
        IReadOnlyList<DnsRecord> records, IReadOnlyCollection<string>? services = null)
    {
        // RFC 1035 §3.1: DNS name comparison is case-insensitive throughout this method — a
        // device's SRV target and its A record's name (or its SRV name and TXT/PTR names) are
        // not guaranteed to share byte-identical casing on the wire, so ordinal comparison
        // anywhere here would silently drop real devices whose firmware capitalizes records
        // differently.
        HashSet<string>? admittedSrvNames = null;
        if (services is not null)
        {
            var queriedServices = new HashSet<string>(services, StringComparer.OrdinalIgnoreCase);
            admittedSrvNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (record is PtrRecord ptr && queriedServices.Contains(ptr.Name))
                    admittedSrvNames.Add(ptr.Target);
            }
        }

        var srvRecords = new List<SrvRecord>();
        var txtByName = new Dictionary<string, TxtRecord>(StringComparer.OrdinalIgnoreCase);
        var addressByName = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            switch (record)
            {
                case SrvRecord srv:
                    if (admittedSrvNames is null || admittedSrvNames.Contains(srv.Name))
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
            IReadOnlyList<string> txtEntries = [];
            if (txtByName.TryGetValue(srv.Name, out var txt))
            {
                txtEntries = txt.Entries;
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
            devices.Add(new DiscoveredDevice(name, deviceClass, DeviceClasses.DeviceTypeFor(deviceClass), address, srv.Port, txtEntries));
        }

        return devices;
    }

    static string InstanceLabel(string srvName)
    {
        int dot = srvName.IndexOf('.');
        return dot < 0 ? srvName : srvName[..dot];
    }
}
