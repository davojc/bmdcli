using System.Net.Sockets;
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class FakeVideohubTests
{
    [Fact]
    public async Task ServesDumpOnConnect()
    {
        await using var fake = FakeVideohub.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", fake.Port);
        using var reader = new StreamReader(tcp.GetStream());

        var acc = new BlockAccumulator();
        var blocks = new List<ProtocolBlock>();
        while (blocks.Count < 7 && await reader.ReadLineAsync() is { } line)
            if (acc.Add(line) is { } block) blocks.Add(block);

        Assert.Contains(blocks, b => b.Header == "VIDEOHUB DEVICE");
        Assert.Contains(blocks, b => b.Header == "END PRELUDE");
    }

    [Fact]
    public async Task SupportsSequentialConnections()
    {
        await using var fake = FakeVideohub.Start();
        for (var i = 0; i < 2; i++)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", fake.Port);
            using var reader = new StreamReader(tcp.GetStream());
            var first = await reader.ReadLineAsync();
            Assert.Equal("PROTOCOL PREAMBLE:", first);
        }
    }
}
