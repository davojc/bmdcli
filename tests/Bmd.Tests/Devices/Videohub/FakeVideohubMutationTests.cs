using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class FakeVideohubMutationTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Routing_AppliedServerSide()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        await client.SendBlockAsync("VIDEO OUTPUT ROUTING", ["2 0"]);
        Assert.Equal(0, fake.Routes()[2]);
    }

    [Fact]
    public async Task Labels_AppliedServerSide_AndVisibleToNextConnection()
    {
        await using var fake = FakeVideohub.Start();
        await using (var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5))
            await client.SendBlockAsync("OUTPUT LABELS", ["0 Main Program"]);

        Assert.Equal("Main Program", fake.OutputLabels()[0]);
        await using var reconnected = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        Assert.Equal("Main Program", reconnected.State.GetOutputLabel(1));
    }

    [Fact]
    public async Task Locks_AppliedServerSide()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        await client.SendBlockAsync("VIDEO OUTPUT LOCKS", ["0 O"]);
        Assert.Equal('O', fake.Locks()[0]);
    }

    [Fact]
    public async Task OutOfRangeIndex_IsRejected_AndChangesNothing()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        var before = fake.Routes().ToArray();
        await Assert.ThrowsAsync<VideohubCommandRejectedException>(
            () => client.SendBlockAsync("VIDEO OUTPUT ROUTING", ["9 0"]));
        Assert.Equal(before, fake.Routes());
    }

    [Fact]
    public async Task RejectingHub_NaksEveryMutation()
    {
        await using var fake = FakeVideohub.StartRejecting();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        await Assert.ThrowsAsync<VideohubCommandRejectedException>(
            () => client.SendBlockAsync("VIDEO OUTPUT ROUTING", ["0 1"]));
    }

    [Fact]
    public async Task DisposeAsync_WaitsForClientHandlers()
    {
        var fake = FakeVideohub.Start();
        await using (var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5))
            await client.SendBlockAsync("VIDEO OUTPUT ROUTING", ["0 1"]);
        await fake.DisposeAsync();          // must not hang, must not throw
        Assert.Equal(1, fake.Routes()[0]);  // state observable after shutdown
    }
}
