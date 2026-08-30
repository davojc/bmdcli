using System.Net.Sockets;
using System.Text.Json;
using Bmd.Config;
using Bmd.Devices.Videohub;
using Bmd.Output;
using ConsoleAppFramework;

namespace Bmd.Commands.Videohub;

/// <summary>bmd videohub — control a Blackmagic Videohub over the network.</summary>
public class VideohubCommands
{
    readonly DeviceSession _session;

    public VideohubCommands() : this(ConfigStore.LoadDefault) { }

    public VideohubCommands(Func<ConfigStore> loadConfig)
    {
        _session = new DeviceSession("videohub", loadConfig);
    }

    /// <summary>Show device information (model, protocol version, input/output counts).</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Info(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            if (json)
            {
                var result = new VideohubInfoResult(
                    device.ModelName, device.FriendlyName, device.ProtocolVersion,
                    device.VideoInputs, device.VideoOutputs);
                Console.WriteLine(JsonSerializer.Serialize(result, BmdJsonContext.Default.VideohubInfoResult));
            }
            else
            {
                Console.WriteLine($"Model:          {device.ModelName}");
                if (device.FriendlyName is not null)
                    Console.WriteLine($"Friendly name:  {device.FriendlyName}");
                Console.WriteLine($"Protocol:       {device.ProtocolVersion}");
                Console.WriteLine($"Video inputs:   {device.VideoInputs}");
                Console.WriteLine($"Video outputs:  {device.VideoOutputs}");
            }
            return 0;
        });

    /// <summary>List inputs (1-based) with their labels.</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> InputList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var device = client.State.Device;
            var entries = Enumerable.Range(1, device.VideoInputs)
                .Select(n => new VideohubInputEntry(n, client.State.GetInputLabel(n))).ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.VideohubInputEntryArray));
            else
                Table.Write(["N", "LABEL"], entries.Select(e => (IReadOnlyList<string>)[e.N.ToString(), e.Label]).ToArray());
            return 0;
        });

    /// <summary>List outputs (1-based) with label, routed input, and lock state.</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> OutputList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var entries = Enumerable.Range(1, state.Device.VideoOutputs)
                .Select(n => new VideohubOutputEntry(
                    n, state.GetOutputLabel(n), state.GetRoute(n),
                    state.GetInputLabel(state.GetRoute(n)), LockWord(state.GetLock(n))))
                .ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.VideohubOutputEntryArray));
            else
                Table.Write(["N", "LABEL", "INPUT", "INPUT LABEL", "LOCK"],
                    entries.Select(e => (IReadOnlyList<string>)
                        [e.N.ToString(), e.Label, e.Input.ToString(), e.InputLabel, e.Lock]).ToArray());
            return 0;
        });

    /// <summary>List the current routing (1-based): which input feeds each output.</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> RouteList(string? host = null, int? port = null, int? timeout = null, bool json = false)
        => _session.WithClientAsync(host, port, timeout, client =>
        {
            var state = client.State;
            var entries = Enumerable.Range(1, state.Device.VideoOutputs)
                .Select(n => new VideohubRouteEntry(
                    n, state.GetOutputLabel(n), state.GetRoute(n), state.GetInputLabel(state.GetRoute(n))))
                .ToArray();
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(entries, BmdJsonContext.Default.VideohubRouteEntryArray));
            else
                Table.Write(["OUT", "OUTPUT LABEL", "IN", "INPUT LABEL"],
                    entries.Select(e => (IReadOnlyList<string>)
                        [e.Output.ToString(), e.OutputLabel, e.Input.ToString(), e.InputLabel]).ToArray());
            return 0;
        });

    /// <summary>Stream device changes as they happen, including changes made by other controllers. Numbering is 1-based.</summary>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection timeout in seconds; defaults to config videohub.timeout, else 5. Watching itself never times out.</param>
    /// <param name="json">Emit one JSON object per line as updates arrive.</param>
    /// <param name="cancellationToken">Cancelled by Ctrl+C.</param>
    public Task<int> Watch(
        string? host = null, int? port = null, int? timeout = null, bool json = false,
        CancellationToken cancellationToken = default)
        => _session.RunWithClientAsync(host, port, timeout, async client =>
        {
            // The header is diagnostic chatter about the watch itself, not part of the data
            // stream — it goes to stderr so stdout stays pure for piping (`bmd videohub watch |
            // head`), and is suppressed entirely in --json mode, which has no room for it since
            // every stdout line must parse as one of the JSON Lines objects.
            if (!json)
                Console.Error.WriteLine($"Watching {client.Host}:{client.Port} — press Ctrl+C to stop");

            await foreach (var update in client.WatchAsync(cancellationToken))
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(
                        new VideohubUpdateResult(KindWord(update.Kind), update.N, update.From, update.To),
                        BmdJsonContext.Default.VideohubUpdateResult));
                else
                    Console.WriteLine(update.Describe());
            }
            // Cancellation (Ctrl+C) is how a watch normally ends — WatchAsync ends the sequence
            // cleanly on cancellation rather than throwing, and this returns 0 rather than
            // treating the interruption as a failure. A dropped connection is a different path:
            // WatchAsync throws VideohubProtocolException, which the shared catch filter in
            // RunCatchingAsync maps to an "error:" line and exit 1.
            return 0;
        });

    static string KindWord(VideohubUpdateKind kind) => kind switch
    {
        VideohubUpdateKind.InputLabel => "inputLabel",
        VideohubUpdateKind.OutputLabel => "outputLabel",
        VideohubUpdateKind.Route => "route",
        _ => "lock",
    };

    /// <summary>Export a verified snapshot of labels and routing (1-based). Locks are not captured.</summary>
    /// <param name="file">Destination file; omit to write the snapshot JSON to stdout.</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="json">Emit a summary object as JSON on stdout; requires a file.</param>
    public async Task<int> Export(
        [Argument] string? file = null, string? host = null, int? port = null, int? timeout = null, bool json = false)
    {
        if (json && file is null)
        {
            Console.Error.WriteLine("error: --json requires a file argument (the snapshot itself goes to stdout without it)");
            return 2;
        }

        try
        {
            const int attempts = 3;
            IReadOnlyList<string> differences = [];
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                VideohubSnapshot? captured = null;
                var capture = await _session.WithClientAsync(host, port, timeout, client =>
                {
                    captured = VideohubSnapshot.FromState(client.State, DateTimeOffset.UtcNow);
                    return 0;
                });
                if (capture != 0) return capture;

                var snapshot = captured!;
                var text = snapshot.ToJson();
                if (file is not null) File.WriteAllText(file, text);

                var written = VideohubSnapshot.FromJson(file is not null ? File.ReadAllText(file) : text);
                var verify = await _session.WithClientAsync(host, port, timeout, client =>
                {
                    differences = written.DifferencesFrom(client.State);
                    return 0;
                });
                if (verify != 0) return verify;
                if (differences.Count > 0) continue;   // device changed mid-export: recapture

                if (file is null) Console.Write(text);
                else if (json)
                    Console.WriteLine(JsonSerializer.Serialize(
                        new VideohubExportResult(snapshot.Device, snapshot.VideoInputs, snapshot.VideoOutputs,
                            snapshot.Outputs.Length, file, true),
                        BmdJsonContext.Default.VideohubExportResult));
                else
                    Console.WriteLine(
                        $"Exported and verified: {snapshot.VideoInputs} inputs, {snapshot.VideoOutputs} outputs, " +
                        $"{snapshot.Outputs.Length} routes → {file}");
                return 0;
            }

            Console.Error.WriteLine(
                $"error: device kept changing during export; snapshot not verified after {attempts} attempts");
            foreach (var difference in differences) Console.Error.WriteLine($"  {difference}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SnapshotFormatException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Apply a snapshot to the device, changing only what differs. Numbering is 1-based.</summary>
    /// <param name="file">Snapshot file to apply; use - to read from stdin.</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="dryRun">Show what would change without touching the device.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> Restore(
        [Argument] string file,
        string? host = null, int? port = null, int? timeout = null,
        bool dryRun = false, bool noBackup = false, bool json = false)
    {
        VideohubSnapshot snapshot;
        try
        {
            var text = file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file);
            snapshot = VideohubSnapshot.FromJson(text);
        }
        catch (SnapshotFormatException ex)
        {
            // Bad file CONTENT (malformed JSON, invalid indices) is a usage/format error, same
            // family as the incompatible-device check below — exit 2. Only a file that could
            // not be READ (missing, permission denied) is exit 1; see the catch below.
            Console.Error.WriteLine($"error: {ex.Message}");
            return Task.FromResult(2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return Task.FromResult(1);
        }

        // --dry-run disables the backup thunk outright: nothing on the device is ever going to
        // change on this run, so there is nothing to protect against.
        return _session.WithDeferredBackupClientAsync(host, port, timeout, noBackup || dryRun, async (client, ensureBackup) =>
        {
            var problems = snapshot.IncompatibilityWith(client.State.Device);
            if (problems.Count > 0)
            {
                // A usage/format error, not a device error: the wrong snapshot file was
                // supplied. Exit 2 (like the other "bad argument" paths), device untouched.
                // The thunk is never called: an incompatible snapshot can never be applied, so
                // there is nothing to protect a backup against here either.
                Console.Error.WriteLine("error: snapshot does not match this device");
                foreach (var problem in problems) Console.Error.WriteLine($"  {problem}");
                return 2;
            }

            // Computed once, from connect-time state, and iterated as a fixed list — never
            // re-derived from client.State mid-loop. client.State is updated by whatever the
            // device echoes back (which can arrive before or after the ACK depending on
            // firmware), so re-querying it partway through applying changes would make the
            // remaining work order- and timing-dependent instead of a plan decided up front.
            var changes = RestorePlan.Compute(snapshot, client.State);

            // The backup is spent only once we know at least one change is actually going to be
            // applied — a no-op restore (changes.Count == 0) or a dry run must not spend it. The
            // thunk is called here, before the apply loop's first mutation, so the snapshot it
            // captures is still the device's pre-change state (see WithDeferredBackupClientAsync).
            string? backup = null;
            if (changes.Count > 0 && !dryRun) backup = await ensureBackup();

            var applied = 0;
            try
            {
                foreach (var change in changes)
                {
                    if (dryRun)
                    {
                        if (!json) Console.WriteLine($"would {change.Describe()}");
                        continue;
                    }
                    switch (change.Kind)
                    {
                        case RestoreChangeKind.InputLabel:
                            await client.RenameInputAsync(change.N, change.To);
                            break;
                        case RestoreChangeKind.OutputLabel:
                            await client.RenameOutputAsync(change.N, change.To);
                            break;
                        default:
                            await client.SetRouteAsync(change.N, change.TargetInput);
                            break;
                    }
                    applied++;
                    if (!json) Console.WriteLine(change.Describe());
                }
            }
            catch (TimeoutException)
            {
                // A timeout leaves the block possibly half-sent and the reader mid-stream —
                // framing on this connection is undefined from here on, so we must stop
                // immediately rather than risk sending another block. Report progress and
                // let the caller re-run: the next connection recomputes the diff from fresh
                // state, so it resumes rather than repeats.
                Console.Error.WriteLine(
                    $"error: timed out; applied {applied} of {changes.Count} changes; re-run to resume");
                return 1;
            }
            catch (VideohubCommandRejectedException ex)
            {
                // A NAK, unlike a timeout, leaves the connection's framing well-defined (the
                // device replied, it just refused) — but the loop still must not continue,
                // since the rejected change was never applied and later changes may depend on
                // device state the rejection left unmoved. Report progress the same way as the
                // timeout path so the applied count is never silently lost on this path either.
                Console.Error.WriteLine(
                    $"error: {ex.Message}; applied {applied} of {changes.Count} changes before stopping");
                return 1;
            }
            catch (Exception ex) when (ex is IOException or SocketException or VideohubProtocolException)
            {
                // The hub going away mid-restore — a dropped socket (IOException/SocketException)
                // or the connection closing cleanly (VideohubProtocolException) — is the likeliest
                // real mid-restore failure. Framing on this connection is now undefined, exactly
                // as after a timeout, so stop immediately and report progress the same way: the
                // underlying reason, the applied count, and that re-running resumes.
                Console.Error.WriteLine(
                    $"error: {ex.Message}; applied {applied} of {changes.Count} changes; re-run to resume");
                return 1;
            }

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(
                    new VideohubRestoreResult(
                        file, snapshot.Device, changes.Count, applied, dryRun, backup,
                        changes.Select(c => new RestoreChangeResult(KindWord(c.Kind), c.N, c.From, c.To)).ToArray()),
                    BmdJsonContext.Default.VideohubRestoreResult));
            else if (changes.Count == 0)
                Console.WriteLine("Already matches the snapshot; nothing to do.");
            else if (dryRun)
                Console.WriteLine($"{changes.Count} change(s) would be applied from {file}.");
            else
            {
                Console.WriteLine($"Restored {applied} change(s) from {file}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });
    }

    static string KindWord(RestoreChangeKind kind) => kind switch
    {
        RestoreChangeKind.InputLabel => "inputLabel",
        RestoreChangeKind.OutputLabel => "outputLabel",
        _ => "route",
    };

    /// <summary>Route an input to an output (both 1-based, matching the device's front panel).</summary>
    /// <param name="output">Output to change (1-based).</param>
    /// <param name="input">Input to route to it (1-based).</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> RouteSet(
        [Argument] int output, [Argument] int input,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
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

    /// <summary>Rename an input (1-based).</summary>
    /// <param name="input">Input to rename (1-based).</param>
    /// <param name="label">New label. Must not contain newlines.</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> InputRename(
        [Argument] int input, [Argument] string label,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var device = client.State.Device;
            if (input < 1 || input > device.VideoInputs)
            {
                Console.Error.WriteLine($"error: input must be between 1 and {device.VideoInputs}");
                return 2;
            }
            if (!TryValidateLabel(label, out var labelError))
            {
                Console.Error.WriteLine(labelError);
                return 2;
            }

            var previousLabel = client.State.GetInputLabel(input);
            await client.RenameInputAsync(input, label);

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(
                    new VideohubRenameResult("input", input, previousLabel, label, backup),
                    BmdJsonContext.Default.VideohubRenameResult));
            else
            {
                Console.WriteLine($"input {input}: {previousLabel} → {label}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>Rename an output (1-based).</summary>
    /// <param name="output">Output to rename (1-based).</param>
    /// <param name="label">New label. Must not contain newlines.</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> OutputRename(
        [Argument] int output, [Argument] string label,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var device = client.State.Device;
            if (output < 1 || output > device.VideoOutputs)
            {
                Console.Error.WriteLine($"error: output must be between 1 and {device.VideoOutputs}");
                return 2;
            }
            if (!TryValidateLabel(label, out var labelError))
            {
                Console.Error.WriteLine(labelError);
                return 2;
            }

            var previousLabel = client.State.GetOutputLabel(output);
            await client.RenameOutputAsync(output, label);

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(
                    new VideohubRenameResult("output", output, previousLabel, label, backup),
                    BmdJsonContext.Default.VideohubRenameResult));
            else
            {
                Console.WriteLine($"output {output}: {previousLabel} → {label}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>Take the lock on an output (1-based), preventing other controllers from routing it or taking it over without --force.</summary>
    /// <param name="output">Output to lock (1-based).</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> OutputLock(
        [Argument] int output,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var device = client.State.Device;
            if (output < 1 || output > device.VideoOutputs)
            {
                Console.Error.WriteLine($"error: output must be between 1 and {device.VideoOutputs}");
                return 2;
            }

            var outputLabel = client.State.GetOutputLabel(output);
            var previousLock = LockWord(client.State.GetLock(output));
            await client.LockOutputAsync(output);
            // Finding 2: report from the operation, not from re-reading State — a device that
            // ACKs before broadcasting the update would otherwise leave State stale here.
            var lockWord = LockWord(LockState.Owned);

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(
                    new VideohubLockResult(output, outputLabel, lockWord, previousLock, backup),
                    BmdJsonContext.Default.VideohubLockResult));
            else
            {
                Console.WriteLine($"output {output} ({outputLabel}): {previousLock} → {lockWord}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>Release the lock on an output (1-based). Without --force, unlocking an output locked by another controller is left to the device to accept or refuse.</summary>
    /// <param name="output">Output to unlock (1-based).</param>
    /// <param name="force">Clear a lock held by another controller.</param>
    /// <param name="host">Device address; defaults to config videohub.host.</param>
    /// <param name="port">Device TCP port; defaults to config videohub.port, else 9990.</param>
    /// <param name="timeout">Connection and command timeout in seconds; defaults to config videohub.timeout, else 5.</param>
    /// <param name="noBackup">Skip the automatic pre-change backup.</param>
    /// <param name="json">Emit the result as JSON on stdout.</param>
    public Task<int> OutputUnlock(
        [Argument] int output, bool force = false,
        string? host = null, int? port = null, int? timeout = null,
        bool noBackup = false, bool json = false)
        => _session.WithBackedUpClientAsync(host, port, timeout, noBackup, async (client, backup) =>
        {
            var device = client.State.Device;
            if (output < 1 || output > device.VideoOutputs)
            {
                Console.Error.WriteLine($"error: output must be between 1 and {device.VideoOutputs}");
                return 2;
            }

            var outputLabel = client.State.GetOutputLabel(output);
            var previousLock = LockWord(client.State.GetLock(output));
            await client.UnlockOutputAsync(output, force);
            // Finding 2: report from the operation, not from re-reading State — see OutputLock.
            var lockWord = LockWord(LockState.Unlocked);

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(
                    new VideohubLockResult(output, outputLabel, lockWord, previousLock, backup),
                    BmdJsonContext.Default.VideohubLockResult));
            else
            {
                Console.WriteLine($"output {output} ({outputLabel}): {previousLock} → {lockWord}");
                Console.WriteLine($"Backup: {backup ?? "skipped"}");
            }
            return 0;
        });

    /// <summary>Validates a label before it is sent to the device. Newlines would break the
    /// line-oriented protocol, so they are rejected here rather than left to the device.</summary>
    static bool TryValidateLabel(string label, out string error)
    {
        if (label.Contains('\n') || label.Contains('\r'))
        {
            error = "error: label must not contain newlines";
            return false;
        }
        error = "";
        return true;
    }

    static string LockWord(LockState lockState) => VideohubUpdate.Word(lockState);

    /// <summary>Test seam: exercises the backup path with a supplied action.</summary>
    internal Task<int> BackupProbeAsync(
        string host, int port, bool noBackup, Func<VideohubClient, string?, Task<int>> action)
        => _session.WithBackedUpClientAsync(host, port, null, noBackup, action);

    /// <summary>Test seam: runs the shared failure filter directly against a supplied exception.</summary>
    internal Task<int> ThrowingProbeAsync(Exception exception)
        => DeviceSession.RunCatchingAsync(() => throw exception);
}
