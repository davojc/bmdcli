using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bmd.Devices.Discovery;

/// <summary>The mDNS service names bmd queries for when discovering Blackmagic devices.</summary>
public static class MdnsServices
{
    public const string Blackmagic = "_blackmagic._tcp.local";
    public const string BmdBlockConfig = "_bmd_blockcfg._tcp.local";

    public static IReadOnlyList<string> All { get; } = [Blackmagic, BmdBlockConfig];
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
                    }

                    foreach (var service in MdnsServices.All)
                        await udp.SendAsync(DnsMessage.EncodeQuery(service), destination, ct);

                    clients.Add(udp);
                }
                catch (SocketException ex)
                {
                    // This one interface refuses multicast (or the send otherwise failed) —
                    // that must not abort discovery on the machine's other interfaces.
                    lastFailure = ex;
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

            return DeviceAssembler.FromRecords(records)
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
