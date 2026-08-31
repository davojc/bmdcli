using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bmd.Devices.Discovery;

/// <summary>The mDNS service names bmd queries for when discovering Blackmagic devices.
///
/// <para>Newer firmware advertises a service named after what the device <i>does</i> rather than
/// the generic <c>_blackmagic._tcp</c>, and does not necessarily advertise the generic one at all.
/// An ATEM Television Studio HD8 ISO announces only <c>_switcher_ctrl._udp</c> and
/// <c>_hyperdeck_ctrl._tcp</c>, so querying the two original names found everything on a test
/// network except the newest switcher on it — which bmd could drive perfectly well once told its
/// address by hand.</para>
///
/// <para>Each name here was observed on real hardware by enumerating
/// <c>_services._dns-sd._udp.local</c>, the DNS-SD meta-query that asks a network which service
/// types exist on it. That is the way to extend this list: ask, rather than guess.</para></summary>
public static class MdnsServices
{
    /// <summary>The generic name. Older Videohubs, MultiViews and ATEMs answer on this.</summary>
    public const string Blackmagic = "_blackmagic._tcp.local";

    public const string BmdBlockConfig = "_bmd_blockcfg._tcp.local";

    /// <summary>ATEM switchers, on UDP 9910. The HD8 ISO advertises this and nothing generic.</summary>
    public const string SwitcherControl = "_switcher_ctrl._udp.local";

    /// <summary>Videohub routers, on TCP 9990.</summary>
    public const string Videohub = "_videohub._tcp.local";

    /// <summary>HyperDeck control, on TCP 9993. Advertised by HyperDecks and also by switchers
    /// with a recorder built in — an HD8 ISO answers on both this and SwitcherControl, which is
    /// why <see cref="DeviceAssembler"/> has to collapse one device announcing several
    /// protocols into a single entry.</summary>
    public const string HyperDeckControl = "_hyperdeck_ctrl._tcp.local";

    /// <summary>Streaming hardware — the ATEM Streaming Bridge and the Web Presenter family.</summary>
    public const string BmdStreaming = "_bmd_streaming._tcp.local";

    public static IReadOnlyList<string> All { get; } =
        [Blackmagic, BmdBlockConfig, SwitcherControl, Videohub, HyperDeckControl, BmdStreaming];
}

/// <summary>Minimal mDNS client: sends a PTR query for each <see cref="MdnsServices.All"/> entry,
/// collects whatever answers arrive within a fixed window, and assembles them into
/// <see cref="DiscoveredDevice"/> entries via <see cref="DeviceAssembler"/>.
/// <para>Real IP multicast is not exercised by the unit tests in this repo — it depends on OS
/// routing and network hardware that CI and locked-down machines can't be relied on to provide.
/// The tests instead drive <see cref="DiscoverAsync(IPEndPoint,IPAddress,TimeSpan,CancellationToken)"/>
/// against a loopback UDP responder, which exercises send → receive → parse → assemble end to
/// end. The real multicast path (<see cref="DiscoverAsync(TimeSpan,CancellationToken)"/>) is
/// exercised only by this milestone's manual network smoke test.</para></summary>
public sealed class MdnsClient
{
    static readonly IPEndPoint MulticastGroup = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>Discovers devices over real IP multicast: sends the queries from every up,
    /// multicast-capable, non-loopback IPv4 interface, then collects answers on all of them
    /// for <paramref name="window"/>. An interface that can't send (its <see cref="SocketException"/>
    /// is swallowed) is skipped rather than aborting the whole discovery; if every interface fails
    /// to send, the last such exception is (re)thrown so the caller can report it.</summary>
    public static Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        TimeSpan window, CancellationToken ct = default)
        => DiscoverAcrossAsync(GetMulticastCapableAddresses(), MulticastGroup, window, ct, joinMulticastGroup: true);

    /// <summary>Discovers devices by sending to and receiving from a specific endpoint, without
    /// joining any multicast group. Exists so tests can drive the client against a loopback UDP
    /// responder instead of relying on real multicast routing.</summary>
    public static Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        IPEndPoint group, IPAddress localAddress, TimeSpan window, CancellationToken ct = default)
        => DiscoverAcrossAsync([localAddress], group, window, ct, joinMulticastGroup: false);

    static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAcrossAsync(
        IReadOnlyList<IPAddress> localAddresses, IPEndPoint destination, TimeSpan window,
        CancellationToken ct, bool joinMulticastGroup)
    {
        var clients = new List<UdpClient>(localAddresses.Count);
        SocketException? lastFailure = null;

        try
        {
            foreach (var localAddress in localAddresses)
            {
                UdpClient? udp = null;
                try
                {
                    udp = new UdpClient(new IPEndPoint(localAddress, 0));
                    if (joinMulticastGroup)
                    {
                        udp.JoinMulticastGroup(destination.Address, localAddress);
                        udp.MulticastLoopback = false;
                        udp.Ttl = 255;
                        // `udp.Ttl` above sets the unicast TTL only; multicast sends still leave
                        // with the OS default IP_MULTICAST_TTL of 1 unless this is also set
                        // explicitly. RFC 6762 §11 recommends 255 for mDNS. Set inside this same
                        // try so a platform that rejects the option doesn't abort discovery on
                        // this interface.
                        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    }

                    foreach (var service in MdnsServices.All)
                        await udp.SendAsync(DnsMessage.EncodeQuery(service), destination, ct);

                    clients.Add(udp);
                    udp = null; // ownership transferred to `clients` — the finally below is a no-op
                }
                catch (SocketException ex)
                {
                    // This one interface refuses multicast (or the send otherwise failed) —
                    // that must not abort discovery on the machine's other interfaces.
                    lastFailure = ex;
                }
                finally
                {
                    // Reached on every failure path — including an OperationCanceledException
                    // from a `ct` that fires mid-send, which isn't a SocketException and so
                    // isn't caught above. Without this, a socket created just before cancellation
                    // lands would never make it into `clients` and would leak: the outer
                    // `finally` only disposes what's already in that list.
                    udp?.Dispose();
                }
            }

            if (clients.Count == 0)
            {
                if (lastFailure is not null) throw lastFailure; // every interface failed to send
                return []; // no suitable interfaces at all — not an error
            }

            var perClientRecords = await Task.WhenAll(
                clients.Select(udp => ReceiveUntilAsync(udp, window, ct)));

            var records = new List<DnsRecord>();
            foreach (var list in perClientRecords) records.AddRange(list);

            return DeviceAssembler.FromRecords(records, MdnsServices.All)
                .GroupBy(d => (d.Address, d.Port))
                .Select(g => g.First())
                .ToList();
        }
        catch (OperationCanceledException)
        {
            // Cancelled before/while sending (before any receive loop even started running):
            // nothing was collected yet, so there is nothing to return but empty — this is
            // the same "return what we have" contract the receive loop below honors itself.
            return [];
        }
        finally
        {
            foreach (var udp in clients) udp.Dispose();
        }
    }

    /// <summary>Receives datagrams on <paramref name="udp"/> until <paramref name="window"/>
    /// elapses or <paramref name="ct"/> is cancelled, parsing each into DNS records. A datagram
    /// that fails to parse is ignored, not fatal — a hostile or merely unrelated packet on the
    /// wire must not abort discovery. Likewise a <see cref="SocketException"/> on an individual
    /// receive (for example a stale "ICMP port unreachable" surfacing from an earlier send to a
    /// closed port) is ignored and the loop keeps waiting for the remainder of the window.</summary>
    static async Task<List<DnsRecord>> ReceiveUntilAsync(UdpClient udp, TimeSpan window, CancellationToken ct)
    {
        var records = new List<DnsRecord>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);

        try
        {
            while (true)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(cts.Token);
                }
                catch (SocketException)
                {
                    // A socket that keeps failing synchronously (e.g. a stream of stale ICMP
                    // errors) would otherwise spin hot for whatever remains of the window; this
                    // still terminates on schedule via `cts.Token` either way, but the short
                    // delay keeps a misbehaving socket from burning CPU while it does. If `cts`
                    // is already cancelled, this throws immediately and exits the loop below.
                    await Task.Delay(20, cts.Token);
                    continue;
                }

                try { records.AddRange(DnsMessage.ParseRecords(result.Buffer)); }
                catch (DnsFormatException) { }
            }
        }
        catch (OperationCanceledException)
        {
            // window elapsed, or the caller's token was cancelled — return what we have.
        }

        return records;
    }

    static IReadOnlyList<IPAddress> GetMulticastCapableAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    addresses.Add(unicast.Address);
            }
        }
        return addresses;
    }
}
