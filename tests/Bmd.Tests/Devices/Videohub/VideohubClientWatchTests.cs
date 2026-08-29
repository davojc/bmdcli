using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class VideohubClientWatchTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Watch_YieldsUpdatesAsTheyArrive()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        using var cts = new CancellationTokenSource(Timeout5);

        var updates = new List<VideohubUpdate>();
        var watching = Task.Run(async () =>
        {
            await foreach (var update in client.WatchAsync(cts.Token))
            {
                updates.Add(update);
                if (updates.Count == 2) break;
            }
        }, cts.Token);

        await fake.PushRouteAsync(0, 0);
        await fake.PushOutputLabelAsync(1, "Preview B");
        await watching;

        Assert.Equal(2, updates.Count);
        Assert.Contains(updates, u => u.Kind == VideohubUpdateKind.Route && u.N == 1);
        Assert.Contains(updates, u => u.Kind == VideohubUpdateKind.OutputLabel && u.To == "Preview B");
    }

    [Fact]
    public async Task Watch_UpdatesClientState()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        using var cts = new CancellationTokenSource(Timeout5);

        await fake.PushRouteAsync(2, 3);
        await foreach (var _ in client.WatchAsync(cts.Token)) break;

        Assert.Equal(4, client.State.GetRoute(3));   // wire (2,3) → 1-based output 3 ← input 4
    }

    [Fact]
    public async Task Watch_DoesNotTimeOutWhileIdle()
    {
        // a client whose configured timeout is short must still watch quietly past it
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync(
            "127.0.0.1", fake.Port, TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var watching = Task.Run(async () =>
        {
            await foreach (var update in client.WatchAsync(cts.Token)) return update;
            return null;
        }, cts.Token);

        await Task.Delay(900, cts.Token);            // three times the client's timeout
        Assert.False(watching.IsCompleted, "watch must not end while merely idle");

        await fake.PushRouteAsync(0, 1);
        var seen = await watching;
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task Watch_Cancellation_EndsTheSequenceCleanly()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        using var cts = new CancellationTokenSource();

        var watching = Task.Run(async () =>
        {
            await foreach (var _ in client.WatchAsync(cts.Token)) { }
        });

        await Task.Delay(100);
        await cts.CancelAsync();
        await watching;   // must complete without throwing
    }

    [Fact]
    public async Task Watch_ConnectionClosed_ThrowsProtocolException()
    {
        var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);
        using var cts = new CancellationTokenSource(Timeout5);

        var watching = Task.Run(async () =>
        {
            await foreach (var _ in client.WatchAsync(cts.Token)) { }
        }, cts.Token);

        await Task.Delay(100);
        await fake.DisposeAsync();   // server goes away

        await Assert.ThrowsAsync<VideohubProtocolException>(() => watching);
    }
}
