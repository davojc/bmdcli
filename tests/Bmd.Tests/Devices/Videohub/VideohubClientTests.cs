using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
        Assert.Equal("127.0.0.1", client.Host);
        Assert.Equal(fake.Port, client.Port);
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

    [Fact]
    public async Task Connect_BlockAfterRequiredButBeforeEndPrelude_IsRetained()
    {
        // The regression this settle window exists for: a real MultiView 4's dump completes the
        // five required blocks at VIDEO OUTPUT ROUTING, then sends CONFIGURATION, then finally
        // END PRELUDE — exactly Fixtures.DumpMultiView4's order. Against the pre-fix client this
        // fails: ReadDumpAsync returned the instant VIDEO OUTPUT ROUTING completed the required
        // set, never reading CONFIGURATION (or END PRELUDE) off the wire at all.
        await using var fake = FakeVideohub.Start(Fixtures.DumpMultiView4);
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        Assert.True(client.State.ExtraBlocks.ContainsKey("CONFIGURATION"));
        Assert.Contains("Layout: 2x2", client.State.ExtraBlocks["CONFIGURATION"]);
    }

    [Fact]
    public async Task Connect_RequiredBlocksThenSilence_SettlesAndCompletesPromptly()
    {
        // A device that sends the required blocks, no END PRELUDE, and then nothing further at
        // all (unlike Connect_WithoutEndPrelude_CompletesOnRequiredBlocks below, which goes
        // through FakeVideohub — whose RenderDump() always synthesises its own trailing END
        // PRELUDE regardless of the raw dump it was given, so that test never actually exercises
        // a wire with no terminator). This drives a raw socket directly so the client truly never
        // receives one, and proves the settle window ends the read rather than hanging until the
        // full connect timeout.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await using var stream = tcp.GetStream();
            var dump = Fixtures.Dump4x4.Replace("END PRELUDE:\n\n", "");
            await stream.WriteAsync(Encoding.UTF8.GetBytes(dump));
            await Task.Delay(TimeSpan.FromMilliseconds(500)); // keep the connection open, say nothing more
        });

        try
        {
            var stopwatch = Stopwatch.StartNew();
            await using var client = await VideohubClient.ConnectAsync("127.0.0.1", port, Timeout5);
            stopwatch.Stop();

            Assert.Equal(LockState.Owned, client.State.GetLock(2));
            // Well under the 5s connect timeout: proves the settle window, not the timeout, ended
            // the read.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"expected the {250}ms settle window to end the read; took {stopwatch.Elapsed}");
        }
        finally
        {
            listener.Stop();
            try { await serverTask; } catch { /* listener.Stop() can fault the accept */ }
        }
    }

    [Fact]
    public async Task Connect_ClosedBeforeRequiredBlocksComplete_ThrowsProtocolException()
    {
        // The connection closes (not times out) partway through the required blocks — distinct
        // from Connect_IncompleteDump_TimesOut, where the fake stays open and never sends the
        // rest. A closed socket must still be reported as a protocol failure, not treated as a
        // valid (if early) end to the dump.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await using var stream = tcp.GetStream();
            // VIDEOHUB DEVICE only — well short of the five required blocks — then close.
            await stream.WriteAsync(Encoding.UTF8.GetBytes(
                "VIDEOHUB DEVICE:\nDevice present: true\nModel name: Test\nVideo inputs: 1\nVideo outputs: 1\n\n"));
        });

        try
        {
            var ex = await Assert.ThrowsAsync<VideohubProtocolException>(
                () => VideohubClient.ConnectAsync("127.0.0.1", port, Timeout5));
            Assert.Equal("connection closed before the device dump completed", ex.Message);
        }
        finally
        {
            listener.Stop();
            try { await serverTask; } catch { }
        }
    }
}
