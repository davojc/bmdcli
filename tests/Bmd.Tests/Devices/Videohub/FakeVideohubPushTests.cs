using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

public class FakeVideohubPushTests
{
    static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PushedRoute_ReachesAConnectedClient()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);

        await fake.PushRouteAsync(output: 0, input: 1);   // wire indices

        // the client only folds updates in while reading; read one update through the watch loop
        var seen = new List<VideohubUpdate>();
        using var cts = new CancellationTokenSource(Timeout5);
        await foreach (var update in client.WatchAsync(cts.Token))
        {
            seen.Add(update);
            break;
        }

        Assert.Single(seen);
        Assert.Equal(VideohubUpdateKind.Route, seen[0].Kind);
        Assert.Equal(1, seen[0].N);          // 1-based
        Assert.Equal("Cam 2", seen[0].To);   // wire input 1 → 1-based input 2
        Assert.Equal(1, fake.Routes()[0]);
    }

    [Fact]
    public async Task PushedLabelsAndLocks_ReachAConnectedClient()
    {
        await using var fake = FakeVideohub.Start();
        await using var client = await VideohubClient.ConnectAsync("127.0.0.1", fake.Port, Timeout5);

        await fake.PushInputLabelAsync(0, "Camera One");
        await fake.PushLockAsync(0, 'O');

        var seen = new List<VideohubUpdate>();
        using var cts = new CancellationTokenSource(Timeout5);
        await foreach (var update in client.WatchAsync(cts.Token))
        {
            seen.Add(update);
            if (seen.Count == 2) break;
        }

        Assert.Contains(seen, u => u.Kind == VideohubUpdateKind.InputLabel && u.To == "Camera One");
        Assert.Contains(seen, u => u.Kind == VideohubUpdateKind.Lock && u.To == "owned");
    }

    [Fact]
    public async Task Push_WithNoClientConnected_DoesNotThrow()
    {
        await using var fake = FakeVideohub.Start();
        await fake.PushRouteAsync(0, 1);
        Assert.Equal(1, fake.Routes()[0]);
    }
}
