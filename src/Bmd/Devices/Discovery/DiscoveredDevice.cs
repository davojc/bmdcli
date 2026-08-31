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
/// <summary>One thing a device announced it can do: the mDNS service it answered on, a plain
/// word for what that service is, and the port it listens on.
///
/// Modern Blackmagic hardware is rarely one function. An ATEM Television Studio HD8 ISO is a
/// switcher and a recorder in the same box and says so, on two services at two ports; a HyperDeck
/// is a recorder that also exposes a setup service. Reporting only the one bmd happens to drive
/// would answer a narrower question than the one `discover` is for.</summary>
public sealed record DeviceService(string Service, string Capability, int Port);

public sealed record DiscoveredDevice(
    string Name, string DeviceClass, string? DeviceType, IPAddress Address, int Port,
    IReadOnlyList<string> TxtEntries)
{
    /// <summary>Everything this device announced, including the service bmd drives it through.
    /// Ordered as heard, so the primary service is first.</summary>
    public IReadOnlyList<DeviceService> Services { get; init; } = [];

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

    /// <summary>The bmd device group implied by the mDNS service a device answered on.
    ///
    /// The newer, device-specific service names are themselves an identification: a responder on
    /// <c>_switcher_ctrl._udp</c> is a switcher whether or not it bothers to say so in TXT, and an
    /// HD8 ISO does not — its TXT carries a unique id and nothing else. This is the second
    /// identification path that HyperDeck and the WebPresenter family were previously said to
    /// need, and it turned out to be the service name rather than the <c>product id</c>.
    ///
    /// <para>Only services bmd can actually drive are mapped. <c>_hyperdeck_ctrl._tcp</c> is
    /// queried and its answers are reported, but it stays unmapped for the same reason
    /// AtemSwitcher once did: offering to configure a device bmd cannot talk to is worse than
    /// leaving it unclassified.</para></summary>
    /// <summary>A plain word for what a service does, for people reading a device listing rather
    /// than an mDNS trace. Unknown services fall back to their own name, which is more useful than
    /// hiding them: an unexpected service is exactly the thing worth noticing.</summary>
    public static string CapabilityForService(string service)
    {
        if (Is(service, MdnsServices.SwitcherControl)) return "switcher";
        if (Is(service, MdnsServices.HyperDeckControl)) return "recorder";
        if (Is(service, MdnsServices.Videohub)) return "routing";
        if (Is(service, MdnsServices.BmdStreaming)) return "streaming";
        if (Is(service, MdnsServices.BmdBlockConfig)) return "setup";
        if (Is(service, MdnsServices.Blackmagic)) return "control";
        return service.TrimEnd('.').Replace(".local", "", StringComparison.OrdinalIgnoreCase);
    }

    public static string? DeviceTypeForService(string service) => service switch
    {
        _ when Is(service, MdnsServices.SwitcherControl) => "atem",
        _ when Is(service, MdnsServices.Videohub) => "videohub",
        _ => null,
    };

    static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
        // Which service each instance was announced under. A device that advertises no class in
        // TXT is identified by this instead, so the mapping has to survive assembly rather than
        // being collapsed into a yes/no admission test.
        var serviceBySrvName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (record is PtrRecord p) serviceBySrvName.TryAdd(p.Target, p.Name);
        }

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

        // Paired with the SRV target — the device's own hostname — because that, not the
        // instance name, is what identifies the box. One device advertising two services gives
        // them two different instance names (a HyperDeck answers as "HyperDeck" on its setup
        // service and "HyperDeck Studio 4K Pro" on its control service) but only ever one host.
        var devices = new List<(string Host, DiscoveredDevice Device)>(srvRecords.Count);
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

            // TXT first: a device that names its own class is the better authority. The service
            // it answered on is the fallback, for the newer firmware that says nothing at all.
            var deviceType = DeviceClasses.DeviceTypeFor(deviceClass);
            if (deviceType is null && serviceBySrvName.TryGetValue(srv.Name, out var service))
                deviceType = DeviceClasses.DeviceTypeForService(service);

            var announced = serviceBySrvName.TryGetValue(srv.Name, out var announcedOn)
                ? new[] { new DeviceService(announcedOn, DeviceClasses.CapabilityForService(announcedOn), srv.Port) }
                : [];

            devices.Add((srv.Target, new DiscoveredDevice(name, deviceClass, deviceType, address, srv.Port, txtEntries)
            {
                Services = announced,
            }));
        }

        return Collapse(devices);
    }

    /// <summary>One physical box, one entry, listing everything it said it can do.
    ///
    /// Modern hardware is several things at once and announces each separately. An HD8 ISO is a
    /// switcher and a recorder, on two services at two ports; a HyperDeck answers on its control
    /// service and again on a setup service, under a different instance name each time. Listing
    /// those separately answers "what did the network say" when the question is "what is out
    /// there".
    ///
    /// <para>Keyed on the SRV target — the device's own hostname — because the instance name
    /// varies between a device's own services and the address can be shared by a NAT or a host
    /// running several responders. A hostname cannot be two devices on one link.</para>
    ///
    /// <para>Which entry represents the device: the one bmd can drive, else the one that named
    /// its own class, else the first heard. That order matters — a HyperDeck's setup service
    /// carries a richer TXT record than its control service but no <c>class=</c>, so picking on
    /// TXT size alone would report it as an unknown thing on the wrong port.</para></summary>
    static IReadOnlyList<DiscoveredDevice> Collapse(List<(string Host, DiscoveredDevice Device)> devices)
    {
        var best = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var (host, device) in devices)
        {
            if (!best.TryGetValue(host, out var existing))
            {
                best[host] = device;
                order.Add(host);
                continue;
            }

            // Merge rather than discard: the entry that loses is still a real thing the device
            // does, and it is the answer to "what else is in this box".
            var merged = existing.Services.Concat(device.Services)
                .GroupBy(s => s.Service, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();

            best[host] = (Rank(device) > Rank(existing) ? device : existing) with { Services = merged };
        }

        return [.. order.Select(k => best[k])];
    }

    static int Rank(DiscoveredDevice device) =>
        device.DeviceType is not null ? 2 : device.DeviceClass.Length > 0 ? 1 : 0;

    static string InstanceLabel(string srvName)
    {
        int dot = srvName.IndexOf('.');
        return dot < 0 ? srvName : srvName[..dot];
    }
}
