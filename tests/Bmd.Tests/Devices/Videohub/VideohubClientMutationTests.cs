using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubClientMutationTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    static async Task<(FakeVideohub Fake, VideohubClient Client)> ConnectAsync()
    {
        var fake = FakeVideohub.Start();
        var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        return (fake, client);
    }

    [Fact]
    public async Task SetRoute_UsesOneBasedArguments()
    {
        var (fake, client) = await ConnectAsync();
        await using var _ = fake;
        await using var __ = client;
        await client.SetRouteAsync(output: 3, input: 1);   // 1-based
        Assert.Equal(0, fake.Routes()[2]);                 // wire: output index 2 ← input index 0
        Assert.Equal(1, client.State.GetRoute(3));
    }

    [Fact]
    public async Task RenameInput_And_RenameOutput()
    {
        var (fake, client) = await ConnectAsync();
        await using var _ = fake;
        await using var __ = client;
        await client.RenameInputAsync(2, "Camera Two");
        await client.RenameOutputAsync(4, "Aux Feed");
        Assert.Equal("Camera Two", fake.InputLabels()[1]);
        Assert.Equal("Aux Feed", fake.OutputLabels()[3]);
        Assert.Equal("Camera Two", client.State.GetInputLabel(2));
        Assert.Equal("Aux Feed", client.State.GetOutputLabel(4));
    }

    [Fact]
    public async Task LockAndUnlock_RoundTrip()
    {
        var (fake, client) = await ConnectAsync();
        await using var _ = fake;
        await using var __ = client;
        await client.LockOutputAsync(1);
        Assert.Equal('O', fake.Locks()[0]);
        await client.UnlockOutputAsync(1);
        Assert.Equal('U', fake.Locks()[0]);
    }

    [Fact]
    public async Task Unlock_Force_SendsF()
    {
        var (fake, client) = await ConnectAsync();
        await using var _ = fake;
        await using var __ = client;
        await client.LockOutputAsync(3);
        await client.UnlockOutputAsync(3, force: true);
        Assert.Equal('U', fake.Locks()[2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task OutOfRangeArguments_ThrowBeforeSending(int n)
    {
        var (fake, client) = await ConnectAsync();
        await using var _ = fake;
        await using var __ = client;
        var before = fake.Routes().ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetRouteAsync(n, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetRouteAsync(1, n));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.RenameOutputAsync(n, "x"));
        Assert.Equal(before, fake.Routes());
    }

    [Theory]
    [InlineData("bad\nlabel")]
    [InlineData("bad\rlabel")]
    public async Task LabelsWithNewlines_AreRejected(string label)
    {
        var (fake, client) = await ConnectAsync();
        await using var _ = fake;
        await using var __ = client;
        await Assert.ThrowsAsync<ArgumentException>(() => client.RenameInputAsync(1, label));
        Assert.Equal("Cam 1", fake.InputLabels()[0]);
    }

    [Fact]
    public async Task RenameInput_UnicodeEmojiLabel_RoundTrips()
    {
        // Minor 7: broadcast facilities are international — labels must round-trip
        // exactly, emoji included.
        var (fake, client) = await ConnectAsync();
        await using var _ = fake;
        await using var __ = client;
        await client.RenameInputAsync(1, "Kamera Zwei 🎥");
        Assert.Equal("Kamera Zwei 🎥", fake.InputLabels()[0]);
        Assert.Equal("Kamera Zwei 🎥", client.State.GetInputLabel(1));
    }
}
