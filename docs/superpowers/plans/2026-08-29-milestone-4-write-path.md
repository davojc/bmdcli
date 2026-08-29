# bmd Milestone 4: Write Path + Auto-Backup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `bmd videohub route set`, `input rename`, `output rename`, `output lock`, `output unlock` change a real Videohub — each snapshotting the pre-change state to the backup store first, aborting if that backup fails, and reporting the path that undoes it.

**Architecture:** `VideohubClient` gains a persistent reader and a `SendBlockAsync` primitive (send a block, await `ACK`/`NAK`) under typed mutation methods; `VideohubCommands` gains a `WithBackedUpClientAsync` seam that connects → snapshots → backs up → mutates → reports; the fake Videohub grows mutable state so every path is tested without hardware.

**Tech Stack:** .NET 10, ConsoleAppFramework v5, System.Text.Json source generators, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-bmd-cli-design.md` (see "Automatic backup before mutations", "Videohub protocol layer", "Agents and scripting")

## Global Constraints

- `PublishAot=true` stays green: no reflection, no `dynamic`, JSON ONLY via the source-generated contexts. No new package references.
- `Devices/` and `Config/` never reference ConsoleAppFramework or `Commands/`.
- **User-facing numbering is 1-based; the wire is 0-based.** Conversion stays inside `Devices/Videohub/`. Command arguments are 1-based and their XML docs must say so.
- `--json` on every command: one JSON document on stdout, camelCase, stable names; representation only. Errors are always one `error: ...` line on stderr + exit code (0 success / 1 operation failure / 2 usage or format error); stdout carries nothing on failure.
- **Every mutating command backs up first**: capture the state already read at connect → `BackupStore.Write` → only then mutate. A failed backup **aborts the mutation** (`error:` + exit 1, device untouched). Skipped when `backup.auto = false` or `--no-backup` is passed. The backup path appears in human output and in the `--json` result (`"backup"`; `null` when skipped).
- Mutations send the device a block and require `ACK`; a `NAK` is an operation failure (exit 1, clear message naming what was rejected).
- Help is the API: every command/argument/flag fully documented (1-based numbering, units, defaults, `--no-backup` semantics).
- Console-redirecting test classes carry `[Collection("console")]`.
- TDD: failing test → implementation → passing test → commit, every task.

**Carried-forward binding notes** (from milestone 2 and 3 reviews — these are requirements, not suggestions):
- `VideohubClient` must reuse **one** `StreamReader` for the connection's lifetime. The current per-call reader can buffer and silently drop bytes that arrive after the dump — fatal once we read `ACK`/`NAK` on the same socket (Task 2).
- `FakeVideohub` must track and await its per-client tasks in `DisposeAsync` before ACK-dependent tests rely on server-side state (Task 3).
- `WithClientAsync`'s catch must add `ConfigValueException` (`BackupStore.FromConfig` throws it) (Task 1).

---

### Task 1: Async client seam + ConfigValueException coverage

**Files:**
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs`
- Create: `tests/Bmd.Tests/Commands/VideohubConfigErrorTests.cs`

**Interfaces:**
- Consumes: existing `WithClientAsync`, `ConfigValueException` (from `Bmd.Config`).
- Produces: private `Task<int> RunWithClientAsync(string? host, int? port, int? timeout, Func<VideohubClient, Task<int>> action)` — the single implementation (connect, resolve, catch, exit codes). The existing `WithClientAsync(..., Func<VideohubClient,int>)` stays as a thin wrapper so no existing command changes. Its catch filter gains `ConfigValueException`.

- [ ] **Step 1: Write the failing test**

`tests/Bmd.Tests/Commands/VideohubConfigErrorTests.cs`:

```csharp
using Bmd.Commands.Videohub;
using Bmd.Config;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubConfigErrorTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");

    public VideohubConfigErrorTests()
    {
        Directory.CreateDirectory(WorkDir);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Directory.Delete(_root, recursive: true);
    }

    void SetConfig(string key, string value)
    {
        Assert.True(ConfigKey.TryParse(key, out var parsed));
        ConfigStore.Load(GlobalPath, WorkDir).Set(parsed, value, global: false);
    }

    VideohubCommands Commands() => new(() => ConfigStore.Load(GlobalPath, WorkDir));

    [Fact]
    public async Task InvalidBackupKeep_Exit1_CleanError()
    {
        SetConfig("videohub.host", "127.0.0.1");
        SetConfig("backup.keep", "lots");
        // Any command that constructs a BackupStore surfaces this; Info does not,
        // so this asserts the filter directly via a mutation-free path in Task 5+.
        // Until then, prove the exception type is caught by the shared seam:
        Assert.Equal(1, await Commands().ThrowingProbeAsync(new ConfigValueException("config backup.keep must be a positive number, not 'lots'")));
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("backup.keep", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }
}
```

`ThrowingProbeAsync` is an internal test seam added in Step 3 — a one-line method that runs the shared catch path with a supplied exception. It exists so the filter is provable before any command constructs a `BackupStore` (Task 5 wires the real path).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter VideohubConfigErrorTests`
Expected: compilation failure — `ThrowingProbeAsync` does not exist.

- [ ] **Step 3: Implement**

In `src/Bmd/Commands/Videohub/VideohubCommands.cs`, restructure the seam. Replace the body of the existing `WithClientAsync` with a delegation, and move the real work into `RunWithClientAsync`:

```csharp
    /// <summary>Synchronous-action convenience over <see cref="RunWithClientAsync"/>.</summary>
    Task<int> WithClientAsync(string? host, int? port, int? timeout, Func<VideohubClient, int> action)
        => RunWithClientAsync(host, port, timeout, client => Task.FromResult(action(client)));

    /// <summary>Resolves the connection from flags then config, connects, runs the action,
    /// and maps every expected failure to one stderr line plus an exit code.</summary>
    async Task<int> RunWithClientAsync(
        string? host, int? port, int? timeout, Func<VideohubClient, Task<int>> action)
    {
        try
        {
            var store = _loadConfig();
            var resolvedHost = host ?? GetConfig(store, "videohub.host");
            if (resolvedHost is null)
            {
                Console.Error.WriteLine("error: no host configured for videohub (run: bmd config set videohub.host <addr>)");
                return 1;
            }
            var resolvedPort = port ?? GetConfigInt(store, "videohub.port") ?? 9990;
            var resolvedTimeout = timeout ?? GetConfigInt(store, "videohub.timeout") ?? 5;
            if (resolvedTimeout <= 0)
            {
                Console.Error.WriteLine("error: timeout must be a positive number of seconds");
                return 2;
            }
            await using var client = await VideohubClient.ConnectAsync(
                resolvedHost, resolvedPort, TimeSpan.FromSeconds(resolvedTimeout));
            return await action(client);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException
                                       or TimeoutException or VideohubProtocolException
                                       or SnapshotFormatException or ConfigValueException
                                       or ConfigValueFormatException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Test seam: runs the shared failure path with a supplied exception.</summary>
    internal Task<int> ThrowingProbeAsync(Exception exception)
        => RunWithClientAsync(null, null, null, _ => throw exception);
```

Notes for the implementer:
- Keep the existing timeout and missing-host checks exactly as they are today — the snippet above shows where they live after the move; do not change their messages or exit codes.
- The old separate `catch (ConfigValueFormatException ...)` block collapses into the single filter above (that was a Minor finding from milestone 2's review).
- `ConfigValueFormatException` is currently a private nested class; keep it where it is and reference it in the filter.
- Add `using Bmd.Config;` if needed for `ConfigValueException`, plus `using Bmd.Devices.Videohub;` for `SnapshotFormatException` (already present).
- `internal` on `ThrowingProbeAsync` requires `InternalsVisibleTo` for the test assembly. Add to `src/Bmd/Bmd.csproj`:
  ```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Bmd.Tests" />
  </ItemGroup>
  ```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`
Expected: all 124 existing plus the new one pass.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "refactor: single async client seam covering all config failure types"
```

---

### Task 2: Persistent reader + SendBlockAsync (ACK/NAK)

**Files:**
- Modify: `src/Bmd/Devices/Videohub/VideohubClient.cs`
- Create: `tests/Bmd.Tests/Devices/Videohub/VideohubClientSendTests.cs`

**Interfaces:**
- Consumes: `BlockAccumulator`, `DumpParser`, `VideohubState`.
- Produces on `VideohubClient`:
  - one `StreamReader` held for the connection's lifetime (replacing the per-call reader — the binding milestone-2 note)
  - `Task SendBlockAsync(string header, IReadOnlyList<string> lines, CancellationToken ct = default)` — writes `HEADER:\n<line>\n…\n\n`, then reads until `ACK` (returns) or `NAK` (throws `VideohubCommandRejectedException`, defined in `VideohubState.cs` alongside the other protocol exceptions: `sealed class VideohubCommandRejectedException(string message) : Exception(message)`). Update blocks the device pushes before the ACK are consumed and applied to `State` where they are recognized; unknown blocks are ignored. Timeout is the client's configured timeout (store it at connect).
  - `void ApplyUpdate(ProtocolBlock block)` — internal; folds a pushed block into the current state so `State` stays current after mutations. (Milestone 6's `watch` will reuse it.)

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/VideohubClientSendTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubClientSendTests`
Expected: compilation failure — `SendBlockAsync` does not exist. (These tests also need Task 3's mutating fake; if the fake does not yet ACK, they fail at runtime instead — either failure is a valid RED. Implement Task 3 first if the ordering is awkward: the controller dispatches Task 3 next regardless.)

- [ ] **Step 3: Implement**

Restructure `VideohubClient` so the reader lives as long as the connection:

```csharp
public sealed class VideohubClient : IAsyncDisposable
{
    readonly TcpClient _tcp;
    readonly StreamReader _reader;
    readonly StreamWriter _writer;
    readonly TimeSpan _timeout;
    VideohubState _state;

    public string Host { get; }
    public int Port { get; }
    public VideohubState State => _state;

    VideohubClient(TcpClient tcp, StreamReader reader, StreamWriter writer,
                   TimeSpan timeout, string host, int port, VideohubState state)
    {
        _tcp = tcp; _reader = reader; _writer = writer;
        _timeout = timeout; Host = host; Port = port; _state = state;
    }

    public static async Task<VideohubClient> ConnectAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            var stream = tcp.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var state = await ReadDumpAsync(reader, cts.Token);
            return new VideohubClient(tcp, reader, writer, timeout, host, port, state);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"timed out talking to {host}:{port} after {timeout.TotalSeconds:0.#}s");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }
```

`ReadDumpAsync` becomes `static async Task<VideohubState> ReadDumpAsync(StreamReader reader, CancellationToken ct)` — same body as today, but taking the shared reader instead of creating one, and **without** the `using` that disposed it.

Then the send primitive:

```csharp
    /// <summary>Sends one protocol block and waits for the device's ACK.
    /// Update blocks that arrive first are folded into <see cref="State"/>.</summary>
    public async Task SendBlockAsync(string header, IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        try
        {
            await _writer.WriteAsync($"{header}:\n");
            foreach (var line in lines) await _writer.WriteAsync($"{line}\n");
            await _writer.WriteAsync("\n");

            var accumulator = new BlockAccumulator();
            while (await _reader.ReadLineAsync(cts.Token) is { } received)
            {
                if (accumulator.Add(received) is not { } block) continue;
                switch (block.Header)
                {
                    case "ACK": return;
                    case "NAK":
                        throw new VideohubCommandRejectedException($"device rejected {header} ({string.Join("; ", lines)})");
                    default:
                        ApplyUpdate(block);
                        break;
                }
            }
            throw new VideohubProtocolException($"connection closed while awaiting acknowledgement of {header}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"timed out awaiting acknowledgement from {Host}:{Port} after {_timeout.TotalSeconds:0.#}s");
        }
    }

    /// <summary>Folds a pushed update block into the current state. Unknown blocks are ignored.</summary>
    internal void ApplyUpdate(ProtocolBlock block) => _state = DumpParser.ApplyUpdate(_state, block);
```

Add to `DumpParser` a companion that rebuilds state from an update block (pure, testable):

```csharp
    /// <summary>Returns a new state with the update block's entries applied.
    /// Blocks that are not recognised return the state unchanged.</summary>
    public static VideohubState ApplyUpdate(VideohubState state, ProtocolBlock block)
    {
        var device = state.Device;
        switch (block.Header)
        {
            case "INPUT LABELS":
            {
                var labels = Enumerable.Range(1, device.VideoInputs).Select(state.GetInputLabel).ToArray();
                foreach (var (index, value) in ParsePairs(block, device.VideoInputs)) labels[index] = value;
                return state.WithInputLabels(labels);
            }
            case "OUTPUT LABELS":
            {
                var labels = Enumerable.Range(1, device.VideoOutputs).Select(state.GetOutputLabel).ToArray();
                foreach (var (index, value) in ParsePairs(block, device.VideoOutputs)) labels[index] = value;
                return state.WithOutputLabels(labels);
            }
            case "VIDEO OUTPUT ROUTING":
            {
                var routes = Enumerable.Range(1, device.VideoOutputs).Select(n => state.GetRoute(n) - 1).ToArray();
                foreach (var (index, value) in ParsePairs(block, device.VideoOutputs))
                {
                    var route = ParseInt(value, block.Header);
                    if (route < 0 || route >= device.VideoInputs)
                        throw new VideohubProtocolException(
                            $"route for output {index + 1} has invalid input {route} (valid range 0-{device.VideoInputs - 1})");
                    routes[index] = route;
                }
                return state.WithRoutes(routes);
            }
            case "VIDEO OUTPUT LOCKS":
            {
                var locks = Enumerable.Range(1, device.VideoOutputs).Select(state.GetLock).ToArray();
                foreach (var (index, value) in ParsePairs(block, device.VideoOutputs)) locks[index] = ParseLock(value);
                return state.WithLocks(locks);
            }
            default:
                return state;
        }
    }
```

Extract the existing lock-letter `switch` in `Parse` into `static LockState ParseLock(string value)` and call it from both places (do not duplicate it).

Add matching `With…` methods to `VideohubState` (each returns a new instance sharing the untouched arrays):

```csharp
    internal VideohubState WithInputLabels(string[] inputLabels) =>
        new(Device, inputLabels, _outputLabels, _routes, _locks);
    internal VideohubState WithOutputLabels(string[] outputLabels) =>
        new(Device, _inputLabels, outputLabels, _routes, _locks);
    internal VideohubState WithRoutes(int[] routes) =>
        new(Device, _inputLabels, _outputLabels, routes, _locks);
    internal VideohubState WithLocks(LockState[] locks) =>
        new(Device, _inputLabels, _outputLabels, _routes, locks);
```

Finally, `DisposeAsync` disposes the reader and writer alongside the socket.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter VideohubClientSendTests` (after Task 3 lands the mutating fake), then `dotnet test`.
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: persistent reader and ACK/NAK block sending on VideohubClient"
```

---

### Task 3: Mutating FakeVideohub + client-task tracking

**Files:**
- Modify: `tests/Bmd.Tests/Devices/Videohub/FakeVideohub.cs`
- Create: `tests/Bmd.Tests/Devices/Videohub/FakeVideohubMutationTests.cs`

**Interfaces:**
- Produces on `FakeVideohub`:
  - mutable server state parsed from the initial dump (input labels, output labels, routes, locks), so a connection sees the effects of earlier mutations
  - block handling: `VIDEO OUTPUT ROUTING`, `INPUT LABELS`, `OUTPUT LABELS`, `VIDEO OUTPUT LOCKS` → apply and reply `ACK\n\n`, echoing the changed block back first (as real devices do); an out-of-range index or unparseable value → `NAK\n\n` with no change; unknown headers → `NAK\n\n`
  - per-client task tracking, awaited in `DisposeAsync` (the binding milestone-2 note)
  - `IReadOnlyList<string> OutputLabels()`, `int[] Routes()`, `char[] Locks()` — server-side accessors so tests can assert what the device actually received
  - `static FakeVideohub StartRejecting()` — NAKs every mutation (routing included) but serves a normal dump, for testing the NAK path

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/FakeVideohubMutationTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FakeVideohubMutationTests`
Expected: compilation failure — the accessors and `StartRejecting` do not exist.

- [ ] **Step 3: Implement**

Rewrite `FakeVideohub` around mutable state. Keep `Start(string dump = Fixtures.Dump4x4)` and `StartChanging()` working (milestone 3's export tests depend on them — `StartChanging` keeps its own per-connection dump factory and does **not** need mutation support).

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bmd.Devices.Videohub;

namespace Bmd.Tests.Devices.Videohub;

/// <summary>In-process Videohub: serves its current state on connect, applies mutations,
/// and answers ACK/NAK. Doubles as executable protocol documentation.</summary>
public sealed class FakeVideohub : IAsyncDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly Task _acceptLoop;
    readonly List<Task> _clients = [];
    readonly Lock _gate = new();
    readonly Func<string>? _dumpFactory;      // set only by StartChanging
    readonly bool _rejectEverything;

    // mutable server state (0-based, wire order)
    string _preamble = "";
    string _deviceBlock = "";
    string[] _inputLabels = [];
    string[] _outputLabels = [];
    int[] _routes = [];
    char[] _locks = [];

    public int Port { get; }

    FakeVideohub(string dump, Func<string>? dumpFactory, bool rejectEverything)
    {
        _dumpFactory = dumpFactory;
        _rejectEverything = rejectEverything;
        LoadState(dump);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public static FakeVideohub Start(string dump = Fixtures.Dump4x4) => new(dump, null, false);
    public static FakeVideohub StartRejecting(string dump = Fixtures.Dump4x4) => new(dump, null, true);

    /// <summary>A hub whose output-1 route differs on every connection — used to prove
    /// export verification retries and then fails cleanly. Does not accept mutations.</summary>
    public static FakeVideohub StartChanging()
    {
        var connection = 0;
        return new FakeVideohub(Fixtures.Dump4x4, () =>
        {
            var input = Interlocked.Increment(ref connection) % 4;
            return Fixtures.Dump4x4.Replace("VIDEO OUTPUT ROUTING:\n0 3", $"VIDEO OUTPUT ROUTING:\n0 {input}");
        }, false);
    }

    public IReadOnlyList<string> OutputLabels() { lock (_gate) return _outputLabels.ToArray(); }
    public IReadOnlyList<string> InputLabels() { lock (_gate) return _inputLabels.ToArray(); }
    public int[] Routes() { lock (_gate) return _routes.ToArray(); }
    public char[] Locks() { lock (_gate) return _locks.ToArray(); }
    ...
}
```

The rest of the implementation, in order:

1. **`LoadState(string dump)`** — parse the dump text with `BlockReader.ReadBlocks`; keep `PROTOCOL PREAMBLE` and `VIDEOHUB DEVICE` blocks verbatim as strings (for re-rendering), and fill `_inputLabels`/`_outputLabels`/`_routes`/`_locks` from their blocks (wire indices, 0-based; `VIDEO OUTPUT LOCKS` values are the raw letters).
2. **`RenderDump()`** — rebuild the full dump text from current state in the fixture's block order, ending with `END PRELUDE:\n\n`. When `_dumpFactory` is set, return `_dumpFactory()` instead (StartChanging's behavior).
3. **`AcceptLoopAsync`** — as today, but each accepted client's handler task is added to `_clients` under `_gate` before being started.
4. **`HandleClientAsync`** — write `RenderDump()`, then loop reading lines through a `BlockAccumulator`; for each completed block call `ApplyBlock(block, writer)`.
5. **`ApplyBlock(ProtocolBlock block, StreamWriter writer)`** — under `_gate`: if `_rejectEverything`, write `NAK\n\n` and return. Otherwise switch on the header, validate every line's index is in range and its value parses (routes must be `0..VideoInputs-1`; locks must be one of `U`/`O`/`L`/`F`), apply all lines atomically (validate first, then mutate — a partially applied block must be impossible), echo the changed block back, then `ACK\n\n`. Any validation failure → `NAK\n\n` with no mutation. Unknown header → `NAK\n\n`. Lock letter `F` (force-unlock) stores `U`.
6. **`DisposeAsync`** — cancel, stop the listener, await `_acceptLoop`, then await all tracked client tasks (`await Task.WhenAll(snapshot)` inside a `try { } catch { }`), then dispose the CTS.

Use `System.Threading.Lock` (.NET 9+) for `_gate`; `lock (_gate)` works with it directly.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: everything passes, including Task 2's `VideohubClientSendTests` and milestone 3's export tests (which use `Start` and `StartChanging`).

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "test: fake Videohub applies mutations, ACKs, and tracks client tasks"
```

---

### Task 4: Typed mutation operations on VideohubClient

**Files:**
- Modify: `src/Bmd/Devices/Videohub/VideohubClient.cs`
- Create: `tests/Bmd.Tests/Devices/Videohub/VideohubClientMutationTests.cs`

**Interfaces:**
- Produces on `VideohubClient` (all arguments **1-based**, converted to wire indices inside):
  - `Task SetRouteAsync(int output, int input, CancellationToken ct = default)`
  - `Task RenameInputAsync(int input, string label, CancellationToken ct = default)`
  - `Task RenameOutputAsync(int output, string label, CancellationToken ct = default)`
  - `Task LockOutputAsync(int output, CancellationToken ct = default)`
  - `Task UnlockOutputAsync(int output, bool force = false, CancellationToken ct = default)`
  - Each validates its 1-based arguments against `State.Device` counts and throws `ArgumentOutOfRangeException` before touching the socket; labels are rejected with `ArgumentException` when they contain a newline or carriage return (they would corrupt the block framing).

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Devices/Videohub/VideohubClientMutationTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubClientMutationTests`
Expected: compilation failure — the methods do not exist.

- [ ] **Step 3: Implement**

```csharp
    /// <summary>Routes <paramref name="input"/> to <paramref name="output"/> (both 1-based).</summary>
    public Task SetRouteAsync(int output, int input, CancellationToken cancellationToken = default)
    {
        CheckOutput(output);
        CheckInput(input);
        return SendBlockAsync("VIDEO OUTPUT ROUTING", [$"{output - 1} {input - 1}"], cancellationToken);
    }

    /// <summary>Renames input <paramref name="input"/> (1-based).</summary>
    public Task RenameInputAsync(int input, string label, CancellationToken cancellationToken = default)
    {
        CheckInput(input);
        CheckLabel(label);
        return SendBlockAsync("INPUT LABELS", [$"{input - 1} {label}"], cancellationToken);
    }

    /// <summary>Renames output <paramref name="output"/> (1-based).</summary>
    public Task RenameOutputAsync(int output, string label, CancellationToken cancellationToken = default)
    {
        CheckOutput(output);
        CheckLabel(label);
        return SendBlockAsync("OUTPUT LABELS", [$"{output - 1} {label}"], cancellationToken);
    }

    /// <summary>Takes the lock on output <paramref name="output"/> (1-based).</summary>
    public Task LockOutputAsync(int output, CancellationToken cancellationToken = default)
    {
        CheckOutput(output);
        return SendBlockAsync("VIDEO OUTPUT LOCKS", [$"{output - 1} O"], cancellationToken);
    }

    /// <summary>Releases the lock on output <paramref name="output"/> (1-based).
    /// <paramref name="force"/> clears a lock owned by another controller.</summary>
    public Task UnlockOutputAsync(int output, bool force = false, CancellationToken cancellationToken = default)
    {
        CheckOutput(output);
        return SendBlockAsync("VIDEO OUTPUT LOCKS", [$"{output - 1} {(force ? 'F' : 'U')}"], cancellationToken);
    }

    void CheckInput(int input)
    {
        if (input < 1 || input > _state.Device.VideoInputs)
            throw new ArgumentOutOfRangeException(nameof(input), input,
                $"input must be between 1 and {_state.Device.VideoInputs}");
    }

    void CheckOutput(int output)
    {
        if (output < 1 || output > _state.Device.VideoOutputs)
            throw new ArgumentOutOfRangeException(nameof(output), output,
                $"output must be between 1 and {_state.Device.VideoOutputs}");
    }

    static void CheckLabel(string label)
    {
        if (label.Contains('\n') || label.Contains('\r'))
            throw new ArgumentException("label must not contain newlines", nameof(label));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: typed 1-based mutation operations on VideohubClient"
```

---

### Task 5: Auto-backup seam

**Files:**
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs`
- Create: `tests/Bmd.Tests/Commands/VideohubBackupTests.cs`

**Interfaces:**
- Consumes: `BackupStore`, `VideohubSnapshot`, `RunWithClientAsync` (Task 1), `VideohubClient.Host` (milestone 3).
- Produces: private `Task<int> WithBackedUpClientAsync(string? host, int? port, int? timeout, bool noBackup, Func<VideohubClient, string?, Task<int>> action)`:
  1. connect (via `RunWithClientAsync`)
  2. unless `noBackup` or `backup.auto = false`: `VideohubSnapshot.FromState(client.State, DateTimeOffset.UtcNow)` → `BackupStore.Write(BackupStore.DeviceKey(client.Host, client.State.Device.ModelName), snapshot)`; **any failure propagates** to the shared catch → `error:` + exit 1, device untouched
  3. run `action(client, backupPath)` — `backupPath` is `null` when the backup was skipped

Because `BackupStore.Write` throws `IOException` (milestone 3 funnels format failures into it) and `FromConfig` throws `ConfigValueException`, both are already in Task 1's catch filter — no new failure handling is needed, but the backup must happen **before** the action so a failure cannot leave a half-mutated device.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Commands/VideohubBackupTests.cs` — a test-only probe command exercises the seam without waiting for Task 6:

```csharp
using Bmd.Commands.Videohub;
using Bmd.Config;
using Bmd.Devices.Videohub;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubBackupTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("bmdtest").FullName;
    readonly StringWriter _stdout = new();
    readonly StringWriter _stderr = new();
    readonly TextWriter _origOut = Console.Out;
    readonly TextWriter _origErr = Console.Error;

    string GlobalPath => Path.Combine(_root, "global", "config");
    string WorkDir => Path.Combine(_root, "work");
    string BackupDir => Path.Combine(_root, "backups");

    public VideohubBackupTests()
    {
        Directory.CreateDirectory(WorkDir);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        SetConfig("backup.dir", BackupDir);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Directory.Delete(_root, recursive: true);
    }

    void SetConfig(string key, string value)
    {
        Assert.True(ConfigKey.TryParse(key, out var parsed));
        ConfigStore.Load(GlobalPath, WorkDir).Set(parsed, value, global: false);
    }

    VideohubCommands Commands() => new(() => ConfigStore.Load(GlobalPath, WorkDir));

    [Fact]
    public async Task Backup_WrittenBeforeAction_AndPathHandedToIt()
    {
        await using var fake = FakeVideohub.Start();
        string? seen = null;
        Assert.Equal(0, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: false,
            (_, path) => { seen = path; return Task.FromResult(0); }));
        Assert.NotNull(seen);
        Assert.True(File.Exists(seen));
        var snapshot = VideohubSnapshot.FromJson(File.ReadAllText(seen!));
        Assert.Equal(4, snapshot.Outputs.Length);
        Assert.Equal(4, snapshot.Outputs[0].Input);   // pre-change state
    }

    [Fact]
    public async Task NoBackupFlag_SkipsBackup_PathIsNull()
    {
        await using var fake = FakeVideohub.Start();
        string? seen = "not null";
        Assert.Equal(0, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: true,
            (_, path) => { seen = path; return Task.FromResult(0); }));
        Assert.Null(seen);
        Assert.False(Directory.Exists(BackupDir));
    }

    [Fact]
    public async Task BackupAutoFalse_SkipsBackup()
    {
        SetConfig("backup.auto", "false");
        await using var fake = FakeVideohub.Start();
        string? seen = "not null";
        Assert.Equal(0, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: false,
            (_, path) => { seen = path; return Task.FromResult(0); }));
        Assert.Null(seen);
    }

    [Fact]
    public async Task BackupFailure_AbortsBeforeAction_Exit1()
    {
        // a FILE where the backup directory must be → Write cannot create the device directory
        Directory.CreateDirectory(Path.GetDirectoryName(BackupDir)!);
        File.WriteAllText(BackupDir, "not a directory");
        await using var fake = FakeVideohub.Start();
        var ran = false;
        Assert.Equal(1, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: false,
            (_, _) => { ran = true; return Task.FromResult(0); }));
        Assert.False(ran, "the action must not run when the backup fails");
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }

    [Fact]
    public async Task BackupsAreDeviceKeyed()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().BackupProbeAsync("127.0.0.1", fake.Port, noBackup: false,
            (_, _) => Task.FromResult(0)));
        var deviceDirectory = Assert.Single(Directory.GetDirectories(BackupDir));
        Assert.Contains("127-0-0-1", Path.GetFileName(deviceDirectory));
        Assert.Contains("videohub", Path.GetFileName(deviceDirectory));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubBackupTests`
Expected: compilation failure — `BackupProbeAsync` does not exist.

- [ ] **Step 3: Implement**

```csharp
    /// <summary>Connects, backs up the pre-change state unless disabled, then runs the action.
    /// A failed backup aborts before the action runs.</summary>
    Task<int> WithBackedUpClientAsync(
        string? host, int? port, int? timeout, bool noBackup,
        Func<VideohubClient, string?, Task<int>> action)
        => RunWithClientAsync(host, port, timeout, async client =>
        {
            string? backupPath = null;
            if (!noBackup)
            {
                var store = BackupStore.FromConfig(_loadConfig());
                if (store.AutoBackupEnabled)
                {
                    var snapshot = VideohubSnapshot.FromState(client.State, DateTimeOffset.UtcNow);
                    backupPath = store.Write(
                        BackupStore.DeviceKey(client.Host, client.State.Device.ModelName), snapshot);
                }
            }
            return await action(client, backupPath);
        });

    /// <summary>Test seam: exercises the backup path with a supplied action.</summary>
    internal Task<int> BackupProbeAsync(
        string host, int port, bool noBackup, Func<VideohubClient, string?, Task<int>> action)
        => WithBackedUpClientAsync(host, port, null, noBackup, action);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: auto-backup seam that aborts mutations when the backup fails"
```

---

### Task 6: `videohub route set`

**Files:**
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs`, `src/Bmd/Commands/Videohub/VideohubResults.cs`, `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Program.cs`
- Create: `tests/Bmd.Tests/Commands/VideohubRouteSetTests.cs`

**Interfaces:**
- Produces:
  - `sealed record VideohubRouteSetResult(int Output, string OutputLabel, int Input, string InputLabel, int PreviousInput, string PreviousInputLabel, string? Backup)` registered in `BmdJsonContext`
  - `Task<int> RouteSet(int output, int input, string? host = null, int? port = null, int? timeout = null, bool noBackup = false, bool json = false)` registered as `videohub route set`
- Behavior: 1-based `output`/`input` positional arguments; out-of-range → `error: output must be between 1 and N`, **exit 2** (usage error, caught before the socket); `NAK` → exit 1; human output `output 3: Cam 2 → Cam 1` plus `Backup: <path>` (or `Backup: skipped`); `--json` emits the result object.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Bmd.Commands.Videohub;
using Bmd.Config;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Commands;

[Collection("console")]
public class VideohubRouteSetTests : IDisposable
{
    // same temp-dir/console harness as VideohubBackupTests (copy its constructor,
    // Dispose, SetConfig, Commands members verbatim, including SetConfig("backup.dir", BackupDir))

    [Fact]
    public async Task RouteSet_ChangesDevice_AndReportsBeforeAfter()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteSet(3, 1, host: "127.0.0.1", port: fake.Port));
        Assert.Equal(0, fake.Routes()[2]);
        var text = _stdout.ToString();
        Assert.Contains("output 3", text);
        Assert.Contains("Cam 1", text);
        Assert.Contains("Backup:", text);
    }

    [Fact]
    public async Task RouteSet_Json_ReportsChangeAndBackup()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteSet(3, 1, host: "127.0.0.1", port: fake.Port, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(3, root.GetProperty("output").GetInt32());
        Assert.Equal(1, root.GetProperty("input").GetInt32());
        Assert.Equal("Cam 1", root.GetProperty("inputLabel").GetString());
        Assert.Equal(1, root.GetProperty("previousInput").GetInt32());   // fixture: output 3 ← input 1
        Assert.False(string.IsNullOrEmpty(root.GetProperty("backup").GetString()));
        Assert.True(File.Exists(root.GetProperty("backup").GetString()));
    }

    [Fact]
    public async Task RouteSet_NoBackup_JsonBackupIsNull()
    {
        await using var fake = FakeVideohub.Start();
        Assert.Equal(0, await Commands().RouteSet(2, 4, host: "127.0.0.1", port: fake.Port,
            noBackup: true, json: true));
        var root = JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("backup").ValueKind);
        Assert.Equal(3, fake.Routes()[1]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 5)]
    public async Task RouteSet_OutOfRange_Exit2_DeviceUntouched(int output, int input)
    {
        await using var fake = FakeVideohub.Start();
        var before = fake.Routes().ToArray();
        Assert.Equal(2, await Commands().RouteSet(output, input, host: "127.0.0.1", port: fake.Port));
        Assert.Equal(before, fake.Routes());
        Assert.Equal("", _stdout.ToString());
        Assert.Contains("between 1 and", _stderr.ToString());
    }

    [Fact]
    public async Task RouteSet_Rejected_Exit1_CleanError()
    {
        await using var fake = FakeVideohub.StartRejecting();
        Assert.Equal(1, await Commands().RouteSet(1, 2, host: "127.0.0.1", port: fake.Port));
        Assert.Equal("", _stdout.ToString());
        Assert.StartsWith("error:", _stderr.ToString());
        Assert.DoesNotContain("   at ", _stderr.ToString());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubRouteSetTests`
Expected: compilation failure — `RouteSet` does not exist.

- [ ] **Step 3: Implement**

Add the result record, register it in `BmdJsonContext`, and implement (full XML docs required — summary states 1-based; params cover output, input, host, port, timeout, no-backup, json):

```csharp
    /// <summary>Route an input to an output (both 1-based, matching the device's front panel).</summary>
    /// <param name="output">Output to change (1-based).</param>
    /// <param name="input">Input to route to it (1-based).</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> RouteSet(
        [Argument] int output, [Argument] int input,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var device = client.State.Device;
            if (output < 1 || output > device.VideoOutputs)
            {
                Console.Error.WriteLine($"error: output must be between 1 and {device.VideoOutputs}");
                return 2;
            }
            if (input < 1 || input > device.VideoInputs)
            {
                Console.Error.WriteLine($"error: input must be between 1 and {device.VideoInputs}");
                return 2;
            }

            var previousInput = client.State.GetRoute(output);
            var previousLabel = client.State.GetInputLabel(previousInput);
            var outputLabel = client.State.GetOutputLabel(output);
            await client.SetRouteAsync(output, input);
            var inputLabel = client.State.GetInputLabel(input);

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(
                    new VideohubRouteSetResult(output, outputLabel, input, inputLabel,
                        previousInput, previousLabel, backup),
                    BmdJsonContext.Default.VideohubRouteSetResult));
            else
            {
                Console.WriteLine($"output {output} ({outputLabel}): {previousLabel} → {inputLabel}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });
```

Known ordering wrinkle to accept deliberately: range validation happens *after* the connect+backup (it needs the device's real counts). An out-of-range `route set` therefore still writes a backup. That is harmless (the backup is valid) and keeps validation honest against the actual device rather than guessing. Note it in the report.

Register in `Program.cs`: `app.Add("videohub route set", videohub.RouteSet);`

- [ ] **Step 4: Run all tests, then exercise help**

Run: `dotnet test`, then `dotnet run --project src/Bmd -- videohub route set --help`
Expected: pass; help documents both positional arguments as 1-based and explains `--no-backup`.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: bmd videohub route set with automatic pre-change backup"
```

---

### Task 7: Renames and locks

**Files:**
- Modify: `src/Bmd/Commands/Videohub/VideohubCommands.cs`, `src/Bmd/Commands/Videohub/VideohubResults.cs`, `src/Bmd/Output/BmdJsonContext.cs`, `src/Bmd/Program.cs`
- Create: `tests/Bmd.Tests/Commands/VideohubRenameLockTests.cs`

**Interfaces:**
- Produces:
  - `sealed record VideohubRenameResult(string Kind, int N, string PreviousLabel, string Label, string? Backup)` — `Kind` is `"input"` or `"output"`
  - `sealed record VideohubLockResult(int Output, string OutputLabel, string Lock, string PreviousLock, string? Backup)` — lock words `unlocked|owned|locked`
  - Commands (all with the shared `host/port/timeout/noBackup/json` options and full XML docs):
    - `Task<int> InputRename(int input, string label, …)` → `videohub input rename`
    - `Task<int> OutputRename(int output, string label, …)` → `videohub output rename`
    - `Task<int> OutputLock(int output, …)` → `videohub output lock`
    - `Task<int> OutputUnlock(int output, bool force = false, …)` → `videohub output unlock`
- Behavior mirrors Task 6 exactly: out-of-range → exit 2 before mutating; labels containing newlines → exit 2 (`error: label must not contain newlines`); NAK → exit 1; backup path reported in both modes. `output unlock --force` clears another controller's lock; without `--force`, unlocking an output whose lock is held elsewhere is whatever the device answers (a NAK becomes exit 1 with the device's rejection message — do not pre-empt it client-side).

- [ ] **Step 1: Write the failing tests**

Use the same harness as Task 6. Cover, at minimum:

```csharp
    [Fact] public async Task InputRename_ChangesLabel_ReportsBackup()          // fake.InputLabels()[1] == "Camera Two", stdout has Backup:
    [Fact] public async Task OutputRename_Json_ReportsPreviousAndNew()          // kind=="output", previousLabel=="Aux", label=="Aux Feed", backup non-null
    [Fact] public async Task OutputLock_ThenUnlock_RoundTrips()                 // fake.Locks()[0] 'O' then 'U'; json lock=="owned" then "unlocked"
    [Fact] public async Task OutputUnlock_Force_Succeeds()                      // lock via another path, then --force
    [Theory][InlineData(0)][InlineData(5)]
           public async Task OutOfRange_Exit2_DeviceUntouched(int n)            // rename + lock + unlock all exit 2
    [Theory][InlineData("bad\nlabel")][InlineData("bad\rlabel")]
           public async Task LabelWithNewline_Exit2(string label)               // device untouched
    [Fact] public async Task Rejected_Exit1_CleanError()                        // StartRejecting: rename and lock both exit 1, no stack trace
```

Write these out fully in the same style as Task 6's tests — assert server-side state via `fake.InputLabels()/OutputLabels()/Locks()`, assert stdout/stderr and exit codes, and parse `--json` with `JsonDocument`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter VideohubRenameLockTests`
Expected: compilation failure.

- [ ] **Step 3: Implement**

Follow Task 6's `RouteSet` shape exactly for all four commands — validate against `client.State.Device` counts inside the `WithBackedUpClientAsync` callback, capture the previous value, call the matching `VideohubClient` method, then render human or JSON output including the backup path. Reuse the existing `LockWord(LockState)` helper for lock words. Register all four in `Program.cs`.

Guard rails for the implementer:
- Do not duplicate the range-check bodies four times if a small private helper reads better — but keep the messages identical to Task 6's wording (`output must be between 1 and N`).
- `InputRename`/`OutputRename` validate the label before mutating: newline or carriage return → stderr `error: label must not contain newlines`, exit 2.

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test`, then spot-check `dotnet run --project src/Bmd -- videohub output unlock --help`.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: videohub input/output rename and output lock/unlock"
```

---

### Task 8: Group help

**Files:**
- Modify: `src/Bmd/Program.cs`
- Create: `tests/Bmd.Tests/Commands/GroupHelpTests.cs`

**Interfaces:**
- Produces: `bmd videohub --help` lists only videohub commands; `bmd config --help` lists only config commands; `bmd --help` still lists everything.

Background: commands are registered as flat delegate paths (`app.Add("videohub info", …)`) because ConsoleAppFramework's `Add<T>` cannot bind our two-constructor test seam. Flat paths give the framework no group node, so a partial path falls through to the root listing.

**Approach (in preference order — the implementer picks the first that works and records which):**
1. **Framework-native:** check whether ConsoleAppFramework 5.7.13 exposes sub-command builders (e.g. `app.Add("videohub", subCommands => …)`, a `ConsoleAppBuilder` group API, or `UseFilter`-style routing) that accept delegates rather than a type. If one exists, register groups with it.
2. **Pre-dispatch interception in `Program.cs`:** before `app.Run(args)`, detect `args` of the form `<group>` or `<group> --help`/`-h` where `<group>` is a known group with no exact command match, print a filtered listing built from a small static table of `(path, description)` pairs, and exit 0 without invoking the framework. Keep the table adjacent to the registrations so adding a command means adding one line, and add a test that fails if a registered command is missing from the table.

Whichever route is taken, `bmd --help`, every leaf `--help`, and all existing exit codes must be unchanged.

- [ ] **Step 1: Write the failing tests**

`tests/Bmd.Tests/Commands/GroupHelpTests.cs` — drive the published entry point the way a user does. Since `Program.cs` is top-level statements, expose a testable seam: extract the group-help logic into `internal static class GroupHelp` with `static bool TryWrite(string[] args, TextWriter writer)` returning true when it handled the invocation, and test that directly:

```csharp
using Bmd.Commands;

namespace Bmd.Tests.Commands;

public class GroupHelpTests
{
    static string Write(params string[] args)
    {
        var writer = new StringWriter();
        Assert.True(GroupHelp.TryWrite(args, writer));
        return writer.ToString();
    }

    [Theory]
    [InlineData(new[] { "videohub" })]
    [InlineData(new[] { "videohub", "--help" })]
    [InlineData(new[] { "videohub", "-h" })]
    public void VideohubGroup_ListsOnlyVideohubCommands(string[] args)
    {
        var text = Write(args);
        Assert.Contains("videohub route set", text);
        Assert.Contains("videohub info", text);
        Assert.DoesNotContain("config get", text);
    }

    [Fact]
    public void ConfigGroup_ListsOnlyConfigCommands()
    {
        var text = Write("config", "--help");
        Assert.Contains("config set", text);
        Assert.DoesNotContain("videohub", text);
    }

    [Theory]
    [InlineData(new[] { "videohub", "info" })]          // an exact command: not group help
    [InlineData(new[] { "videohub", "route", "set" })]
    [InlineData(new[] { "--help" })]                     // root help stays with the framework
    [InlineData(new string[0])]
    [InlineData(new[] { "nonsense" })]
    public void NonGroupInvocations_AreNotHandled(string[] args)
    {
        Assert.False(GroupHelp.TryWrite(args, new StringWriter()));
    }

    [Fact]
    public void EveryRegisteredCommandAppearsInTheTable()
    {
        // guards against adding a command and forgetting the listing
        Assert.Contains(GroupHelp.Commands, c => c.Path == "videohub output unlock");
        Assert.All(GroupHelp.Commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        Assert.Equal(GroupHelp.Commands.Length, GroupHelp.Commands.Select(c => c.Path).Distinct().Count());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter GroupHelpTests`
Expected: compilation failure — `GroupHelp` does not exist.

- [ ] **Step 3: Implement**

If approach 1 is available, implement it and still provide `GroupHelp` for the tests (or adapt the tests to the framework's real output — record the deviation). Otherwise implement approach 2:

`src/Bmd/Commands/GroupHelp.cs`:

```csharp
namespace Bmd.Commands;

/// <summary>Filtered listings for `bmd <group>` and `bmd <group> --help`, which the flat
/// command registration cannot delegate to the framework.</summary>
internal static class GroupHelp
{
    internal readonly record struct Entry(string Path, string Description);

    internal static readonly Entry[] Commands =
    [
        new("config get", "Print the effective value of a configuration key."),
        // … one line per registered command, matching Program.cs
    ];

    static readonly string[] Groups = ["config", "videohub"];

    internal static bool TryWrite(string[] args, TextWriter writer)
    {
        if (args.Length == 0) return false;
        var group = args[0];
        if (!Groups.Contains(group)) return false;
        if (args.Length > 1 && args[1] is not ("--help" or "-h")) return false;
        if (args.Length > 2) return false;

        var entries = Commands.Where(c => c.Path.StartsWith($"{group} ", StringComparison.Ordinal)).ToArray();
        if (entries.Length == 0) return false;

        var width = entries.Max(e => e.Path.Length);
        writer.WriteLine($"Usage: {group} [command] [-h|--help] [--version]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        foreach (var entry in entries)
            writer.WriteLine($"  {entry.Path.PadRight(width)}    {entry.Description}");
        return true;
    }
}
```

`src/Bmd/Program.cs` gains, before `app.Run(args)`:

```csharp
if (GroupHelp.TryWrite(args, Console.Out)) return 0;
```

(Top-level statements return an exit code via `return`; confirm the file's shape supports it and adjust if the framework's `Run` is the last statement.)

- [ ] **Step 4: Run all tests and verify the real CLI**

```powershell
dotnet test
dotnet run --project src/Bmd -- videohub --help
dotnet run --project src/Bmd -- config --help
dotnet run --project src/Bmd -- --help
dotnet run --project src/Bmd -- videohub info --help
```

Expected: group listings are filtered; root help and leaf help unchanged.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "feat: filtered group help for videohub and config"
```

---

### Task 9: Milestone proof — AOT publish, mutation smokes, help audit

**Files:**
- Modify: none expected

- [ ] **Step 1: Full suite + publish**

```powershell
dotnet test
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/Bmd -c Release -r win-x64
```

Expected: all tests pass; zero IL2xxx/IL3xxx warnings (fix causes, never suppress).

- [ ] **Step 2: Native smokes**

```powershell
$exe = "src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe"
& $exe --help                       # all commands incl. the five new ones
& $exe videohub --help              # ONLY videohub commands
& $exe config --help                # ONLY config commands
& $exe videohub route set --help    # 1-based args, --no-backup, --json documented
& $exe videohub output unlock --help
& $exe videohub route set 1 2 --host 127.0.0.1 --port 1 --timeout 2; $LASTEXITCODE   # expect error: + exit 1
& $exe videohub route set 0 1 --host 127.0.0.1 --port 1 --timeout 2; $LASTEXITCODE   # connection fails first → exit 1
```

Audit every help screen against the help contract; fix any doc-comment gap found and note it.

- [ ] **Step 3: Record size, verify no stray state, commit**

```powershell
(Get-Item src/Bmd/bin/Release/net10.0/win-x64/publish/bmd.exe).Length / 1MB
Test-Path .bmdconfig
Test-Path "$env:LOCALAPPDATA\bmd"     # smokes must not have written real backups (all failed to connect)
git status --short
git add -A; git commit -m "chore: prove milestone 4 (write path + auto-backup) on Native AOT" --allow-empty
```

---

## Self-Review Notes

- **Spec coverage (milestone 4):** ACK/NAK block sending with a persistent reader (Task 2); mutating fake with task tracking (Task 3) — both carried-forward binding notes discharged; typed 1-based mutations (Task 4); backup-before-mutate with abort-on-failure, `backup.auto`, `--no-backup`, path reported (Task 5); `route set` (Task 6); renames and locks (Task 7); group help (Task 8); AOT proof (Task 9). Restore is milestone 5 by design.
- **Deliberate ordering wrinkle:** Tasks 6-7 validate ranges *after* connecting (they need the device's real counts), so an out-of-range mutation still writes a backup before exiting 2. Harmless and honest; called out so a reviewer does not read it as a bug.
- **Task 2/3 interdependence:** Task 2's tests need Task 3's ACKing fake. The plan orders them 2-then-3 so the protocol primitive is designed first; the controller should expect Task 2's GREEN step to complete only once Task 3 lands, or dispatch them as one unit. Either is acceptable — record which was done.
- **Type consistency:** `WithBackedUpClientAsync(host, port, timeout, noBackup, action)` used identically in Tasks 6-7; `BackupStore.DeviceKey(client.Host, model)` matches milestone 3's signature; result records all carry `Backup` (nullable) and are registered in `BmdJsonContext`; lock words `unlocked|owned|locked` match milestone 2's `LockWord`.
- **Carried to milestone 5 (restore):** snapshot `FromJson` still validates array counts but not individual `n` values — restore must validate them before applying anything (binding).
