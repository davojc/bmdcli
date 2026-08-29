using System.Text.Json;
using Bmd.Commands.Videohub;
using Bmd.Config;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubWatchTests : IDisposable
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;

    // Watch runs on a background task while the test thread polls the captured output, so
    // (unlike every other command's tests, which write and read on the same thread) reads and
    // writes here are genuinely concurrent. A plain StringWriter's underlying StringBuilder is
    // not thread-safe for that — read Stdout()/Stderr() below.
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _stdoutSync;
    readonly TextWriter _stderrSync;
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    public VideohubWatchTests()
    {
        Directory.CreateDirectory(WorkDir);
        // TextWriter.Synchronized locks on itself around every write; taking that same lock
        // here around each read makes Stdout()/Stderr() mutually exclusive with the writes
        // Console.WriteLine performs from the background watch task.
        _stdoutSync = TextWriter.Synchronized(_stdout);
        _stderrSync = TextWriter.Synchronized(_stderr);
        Console.SetOut(_stdoutSync);
        Console.SetError(_stderrSync);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Directory.Delete(_root, recursive: true);
    }

    string Stdout() { lock (_stdoutSync) return _stdout.ToString(); }
    string Stderr() { lock (_stderrSync) return _stderr.ToString(); }

    VideohubCommands Commands() => new(() => ConfigStore.Load(GlobalPath, WorkDir));

    static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("condition not met in time");
    }

    /// <summary>Repeatedly invokes <paramref name="push"/> (passing an increasing counter, so
    /// each call can use a value distinct from the one before it) until <paramref name="condition"/>
    /// holds. The watch connects to the fake asynchronously in the background: a push issued only
    /// once, immediately after starting the watch task, can land before that connection completes
    /// — in which case the fake folds it straight into the initial dump the client reads on
    /// connect, and it is never observed as a subsequent update at all. Retrying with a value that
    /// keeps changing guarantees the first retry to land *after* connection produces a real diff.</summary>
    static async Task PushUntilSeen(Func<int, Task> push, Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var i = 0;
        while (DateTime.UtcNow < deadline)
        {
            await push(i++);
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met in time");
    }

    [Fact]
    public async Task Watch_PrintsUpdatesAsTheyArrive()
    {
        await using var fake = FakeVideohub.Start();
        using var cts = new CancellationTokenSource();
        var watching = Task.Run(() => Commands().Watch(host: "127.0.0.1", port: fake.Port, cancellationToken: cts.Token));

        // wire output0 → 1-based "route 1: ...";  wire output1 → 1-based output 2
        await PushUntilSeen(i => fake.PushRouteAsync(0, i % 2), () => Stdout().Contains("route 1:"), Bound);
        await PushUntilSeen(i => fake.PushOutputLabelAsync(1, $"Preview B{i}"),
            () => Stdout().Contains("Preview B"), Bound);

        await cts.CancelAsync();
        Assert.Equal(0, await watching.WaitAsync(Bound));
    }

    [Fact]
    public async Task Watch_Json_EmitsOneObjectPerLine()
    {
        await using var fake = FakeVideohub.Start();
        using var cts = new CancellationTokenSource();
        var watching = Task.Run(() => Commands().Watch(
            host: "127.0.0.1", port: fake.Port, json: true, cancellationToken: cts.Token));

        await PushUntilSeen(i => fake.PushRouteAsync(0, i % 2),
            () => Stdout().Contains("\"kind\":\"route\""), Bound);
        await PushUntilSeen(i => fake.PushOutputLabelAsync(1, $"Preview B{i}"),
            () => Stdout().Contains("Preview B"), Bound);

        await cts.CancelAsync();
        Assert.Equal(0, await watching.WaitAsync(Bound));

        var lines = Stdout().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2);
        var kinds = new HashSet<string>();
        foreach (var line in lines)
        {
            var root = JsonDocument.Parse(line).RootElement;
            kinds.Add(root.GetProperty("kind").GetString()!);
            Assert.True(root.TryGetProperty("n", out _));
            Assert.True(root.TryGetProperty("from", out _));
            Assert.True(root.TryGetProperty("to", out _));
        }
        Assert.Contains("route", kinds);
        Assert.Contains("outputLabel", kinds);
        Assert.Equal("", Stderr());   // header suppressed in --json mode
    }

    [Fact]
    public async Task Watch_Human_WritesHeaderToStderrNotStdout()
    {
        await using var fake = FakeVideohub.Start();
        using var cts = new CancellationTokenSource();
        var watching = Task.Run(() => Commands().Watch(host: "127.0.0.1", port: fake.Port, cancellationToken: cts.Token));

        await WaitUntil(() => Stderr().Contains("Watching"), Bound);

        await cts.CancelAsync();
        Assert.Equal(0, await watching.WaitAsync(Bound));

        Assert.Contains("Watching", Stderr());
        Assert.Contains("127.0.0.1", Stderr());
        Assert.DoesNotContain("Watching", Stdout());
    }

    [Fact]
    public async Task Watch_Cancelled_Exit0()
    {
        await using var fake = FakeVideohub.Start();
        using var cts = new CancellationTokenSource();
        var watching = Task.Run(() => Commands().Watch(host: "127.0.0.1", port: fake.Port, cancellationToken: cts.Token));

        await PushUntilSeen(i => fake.PushRouteAsync(0, i % 2), () => Stdout().Contains("route 1:"), Bound);

        await cts.CancelAsync();
        Assert.Equal(0, await watching.WaitAsync(Bound));
    }

    [Fact]
    public async Task Watch_ServerDisappears_Exit1_CleanError()
    {
        var fake = FakeVideohub.Start();
        using var cts = new CancellationTokenSource(Bound);
        var watching = Task.Run(() => Commands().Watch(host: "127.0.0.1", port: fake.Port, cancellationToken: cts.Token));

        // Let the watch connect and start reading before pulling the rug out.
        await Task.Delay(200);
        await fake.DisposeAsync();

        var completed = await Task.WhenAny(watching, Task.Delay(Bound));
        Assert.Same(watching, completed);
        Assert.Equal(1, await watching);
        // The human-mode header ("Watching host:port — ...") is written to stderr first, so the
        // error line is not necessarily the very first line of stderr — only the last one.
        var stderrLines = Stderr().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("error:", stderrLines[^1]);
        Assert.DoesNotContain("   at ", Stderr());
    }

    [Fact]
    public async Task Watch_NoHostConfigured_Exit1_WithHint()
    {
        Assert.Equal(1, await Commands().Watch());
        Assert.Equal("", Stdout());
        Assert.Contains("no host configured", Stderr());
        Assert.Contains("bmd config set videohub.host", Stderr());
    }
}
