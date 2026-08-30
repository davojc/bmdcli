using Bmd.Devices.Atem;

namespace Bmd.Tests.Devices.Atem;

/// <summary>The UDP session, exercised against <see cref="FakeAtem"/> replaying the captured dump.
///
/// These are the weakest evidence in the ATEM support and should be read as such: they prove the
/// client matches our model of a switcher, since the fake is the other half of that model. What
/// constrains the model is the capture the fake replays — real packets, real retransmissions,
/// real keepalives — rather than packets written to match the parser.</summary>
public class AtemClientTests
{
    static readonly TimeSpan Patient = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Connect_CompletesTheHandshakeAndReadsTheDump()
    {
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Patient);

        Assert.Equal("ATEM Television Studio HD", client.State.ProductName);
        Assert.Equal(24, client.State.Sources.Count);
        Assert.Equal(6, client.State.ProgramSource);
        Assert.Single(client.State.Auxes);
    }

    [Fact]
    public async Task Connect_AdoptsTheSessionIdTheSwitcherAssigns()
    {
        // We open with our own id; the switcher echoes it in the handshake then uses its own.
        // Keep sending ours and it stops understanding us.
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Patient);

        var afterHandshake = fake.Received.Skip(2).ToList();
        Assert.NotEmpty(afterHandshake);
        Assert.All(afterHandshake, h => Assert.Equal(fake.AssignedSession, h.Session));
    }

    [Fact]
    public async Task Connect_AcknowledgesEveryPacketThatAsksForIt()
    {
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Patient);

        var acks = fake.Received.Count(h => h.Flags.HasFlag(AtemFlags.Ack));
        Assert.True(acks >= 5, $"expected at least 5 acknowledgements for 5 dump packets, saw {acks}");
    }

    [Fact]
    public async Task Connect_IgnoresARetransmittedPacketItAlreadyApplied()
    {
        // Retransmission is routine: the capture holds two inside six seconds. Applying a resent
        // packet twice must not double-apply its blocks.
        await using var fake = FakeAtem.Start(resendPacketIndex: 2);
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Patient);

        Assert.Equal(24, client.State.Sources.Count);
        Assert.Equal(24, client.State.Sources.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public async Task Connect_KeepsAcknowledgingKeepalivesAfterTheDump()
    {
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Patient);

        var before = fake.Received.Count;
        await Task.Delay(300);
        Assert.True(fake.Received.Count > before,
            "the client stopped acknowledging keepalives, so a real switcher would drop the session");
    }

    [Fact]
    public async Task Connect_TimesOutCleanlyWhenTheSwitcherGoesQuiet()
    {
        await using var fake = FakeAtem.StartSilent();
        await Assert.ThrowsAsync<TimeoutException>(
            () => AtemClient.ConnectAsync("127.0.0.1", fake.Port, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task Connect_SaysSoWhenNothingIsListening()
    {
        // A closed UDP port answers with ICMP port-unreachable rather than silence, so this
        // fails fast instead of timing out. The message has to be worth reading: the usual
        // cause is pointing atem.host at a Videohub or MultiView, which have nothing on 9910.
        var ex = await Assert.ThrowsAsync<AtemProtocolException>(
            () => AtemClient.ConnectAsync("127.0.0.1", 1, TimeSpan.FromSeconds(2)));

        Assert.Contains("nothing is listening", ex.Message);
        Assert.Contains("9910", ex.Message);
    }

    [Fact]
    public async Task SendCommand_WaitsForTheDeviceToReportTheChange()
    {
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Patient);

        await client.SendCommandAsync(
            "CInL", AtemChanges.SetInputName(2, "Camera Two", "CAM2"),
            s => s.FindSource(2)?.LongName == "Camera Two", Patient);

        Assert.Equal("Camera Two", client.State.FindSource(2)!.LongName);
        Assert.Equal("CAM2", client.State.FindSource(2)!.ShortName);
        Assert.Equal("CInL", Assert.Single(fake.Commands).Name);
    }

    [Fact]
    public async Task SendCommand_SendsNothingWhenTheStateAlreadyMatches()
    {
        // Input 1 is already "Presenter" in the capture. A command that would change nothing must
        // not be sent — on a live switcher that is a needless write to a production device.
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Patient);

        await client.SendCommandAsync(
            "CInL", AtemChanges.SetInputName(1, "Presenter", null),
            s => s.FindSource(1)?.LongName == "Presenter", Patient);

        Assert.Empty(fake.Commands);
    }

    [Fact]
    public async Task SendCommand_TimesOutWhenTheSwitcherIgnoresTheCommand()
    {
        // The protocol has no NAK: a switcher that does not understand a command simply says
        // nothing. That is exactly what a wrong payload layout would look like, so it must
        // surface as an error rather than as a success nobody checked.
        await using var fake = FakeAtem.Start(ignoreCommands: true);
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Patient);

        await Assert.ThrowsAsync<TimeoutException>(() => client.SendCommandAsync(
            "CInL", AtemChanges.SetInputName(2, "Camera Two", null),
            s => s.FindSource(2)?.LongName == "Camera Two", TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task SendCommand_RoutesAnAux()
    {
        await using var fake = FakeAtem.Start();
        await using var client = await AtemClient.ConnectAsync("127.0.0.1", fake.Port, Patient);
        Assert.Equal(6, client.State.Auxes[0].Source);

        await client.SendCommandAsync(
            "CAuS", AtemChanges.SetAuxSource(0, 1),
            s => s.AuxById.TryGetValue(0, out var a) && a.Source == 1, Patient);

        Assert.Equal(1, client.State.Auxes[0].Source);
    }
}
