using System.Net;
using System.Net.Sockets;
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubClientTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Connect_ReadsDumpIntoState()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        Assert.Equal("Blackmagic Smart Videohub", client.State.Device.ModelName);
        Assert.Equal(4, client.State.GetRoute(1));
        Assert.Equal("Program", client.State.GetOutputLabel(1));
    }

    [Fact]
    public async Task Connect_WithoutEndPrelude_CompletesOnRequiredBlocks()
    {
        var dump = Fixtures.Dump4x4.Replace("END PRELUDE:\n\n", "");
        await using var fake = FakeVideohub.Start(dump);
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        Assert.Equal(LockState.Owned, client.State.GetLock(2));
    }

    [Fact]
    public async Task Connect_Refused_ThrowsSocketException()
    {
        // bind an ephemeral port then close it, so nothing is listening and the
        // connection is genuinely refused (port 1 is filtered rather than refused
        // on some machines, which would time out instead)
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var deadPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        // this sandbox's network stack takes ~2s to actively refuse a loopback
        // connection to a dead port, so the client timeout must clear that with margin
        await Assert.ThrowsAsync<SocketException>(
            () => VideohubClient.ConnectAsync("127.0.0.1", deadPort, Timeout5));
    }

    [Fact]
    public async Task Connect_IncompleteDump_TimesOut()
    {
        // fake serves only the preamble: required blocks never arrive → dump read must time out
        await using var fake = FakeVideohub.Start("PROTOCOL PREAMBLE:\nVersion: 2.8\n\n");
        await Assert.ThrowsAsync<TimeoutException>(
            () => VideohubClient.ConnectAsync("127.0.0.1", fake.Port, TimeSpan.FromMilliseconds(500)));
    }
}
