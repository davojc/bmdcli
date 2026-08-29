using System.Net;
using System.Net.Sockets;
using Bmd.Devices.Discovery;

namespace Bmd.Tests.Devices.Discovery;

public class MdnsClientTests
{
    /// <summary>Replies to any datagram with a canned mDNS response.</summary>
    sealed class FakeResponder : IDisposable
    {
        readonly UdpClient _udp;
        readonly CancellationTokenSource _cts = new();
        public int Port { get; }

        public FakeResponder(byte[] response)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var received = await _udp.ReceiveAsync(_cts.Token);
                        await _udp.SendAsync(response, response.Length, received.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
            });
        }

        public void Dispose() { _cts.Cancel(); _udp.Dispose(); _cts.Dispose(); }
    }

    [Fact]
    public async Task Discover_FindsADeviceFromAResponse()
    {
        using var responder = new FakeResponder(GoldenResponse());
        var devices = await MdnsClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, responder.Port), IPAddress.Loopback,
            TimeSpan.FromSeconds(2));

        var device = Assert.Single(devices);
        Assert.Equal("Studio Hub", device.Name);
        Assert.Equal("videohub", device.DeviceType);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), device.Address);
    }

    [Fact]
    public async Task Discover_NoResponders_ReturnsEmptyWithoutThrowing()
    {
        var devices = await MdnsClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, 59999), IPAddress.Loopback,
            TimeSpan.FromMilliseconds(400));
        Assert.Empty(devices);
    }

    [Fact]
    public async Task Discover_GarbageResponse_IsIgnoredNotFatal()
    {
        using var responder = new FakeResponder([1, 2, 3]);
        var devices = await MdnsClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, responder.Port), IPAddress.Loopback,
            TimeSpan.FromMilliseconds(600));
        Assert.Empty(devices);
    }

    [Fact]
    public async Task Discover_Cancellation_ReturnsPromptly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var started = DateTime.UtcNow;
        var devices = await MdnsClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, 59999), IPAddress.Loopback,
            TimeSpan.FromSeconds(30), cts.Token);
        Assert.Empty(devices);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "cancellation must cut the window short");
    }

    [Fact]
    public async Task Discover_AlreadyCancelledToken_ReturnsWithoutThrowing()
    {
        // An already-cancelled token guarantees cancellation lands during the send phase
        // itself (SendAsync observes it immediately), not 200ms later during the receive
        // loop — the gap the socket-leak bug lived in: that UdpClient must still be disposed
        // even though it never reaches `clients`.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var devices = await MdnsClient.DiscoverAsync(
            new IPEndPoint(IPAddress.Loopback, 59999), IPAddress.Loopback,
            TimeSpan.FromSeconds(30), cts.Token);

        Assert.Empty(devices);
    }

    static byte[] GoldenResponse() => Convert.FromHexString(DiscoveryFixtures.ResponseHex);
}
