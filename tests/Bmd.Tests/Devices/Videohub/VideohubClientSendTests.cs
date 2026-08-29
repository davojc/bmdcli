using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubClientSendTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SendBlock_AckedCommand_Returns()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        await client.SendBlockAsync("VIDEO OUTPUT ROUTING", ["0 1"]);   // wire indices
        Assert.Equal(2, client.State.GetRoute(1));                      // 1-based view updated
    }

    [Fact]
    public async Task SendBlock_NakedCommand_Throws()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        var ex = await Assert.ThrowsAsync<VideohubCommandRejectedException>(
            () => client.SendBlockAsync("VIDEO OUTPUT ROUTING", ["99 0"]));   // out-of-range → NAK
        Assert.Contains("VIDEO OUTPUT ROUTING", ex.Message);
    }

    [Fact]
    public async Task SendBlock_TwiceOnSameConnection_BothSucceed()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        await client.SendBlockAsync("VIDEO OUTPUT ROUTING", ["0 1"]);
        await client.SendBlockAsync("VIDEO OUTPUT ROUTING", ["1 2"]);
        Assert.Equal(2, client.State.GetRoute(1));
        Assert.Equal(3, client.State.GetRoute(2));
    }

    [Fact]
    public async Task SendBlock_LabelWithSpaces_RoundTripsThroughState()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        await client.SendBlockAsync("INPUT LABELS", ["0 Studio Camera A"]);
        Assert.Equal("Studio Camera A", client.State.GetInputLabel(1));
    }
}
