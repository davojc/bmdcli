using Bmd.Commands;
using Bmd.Config;
using Bmd.Output;

namespace Bmd.Tests.Output;

public class DiagramTests
{
    static readonly DateTimeOffset Stamp = new(2026, 8, 31, 14, 30, 0, TimeSpan.Zero);

    static DiagramDevice Device(string label = "Cam 1") => new(
        "Studio Hub", "videohub · Smart Videohub 12x12", "videohub", "192.168.1.50",
        Sources: [new DiagramNode("vh-in-1", "IN 1", label)],
        Destinations: [new DiagramNode("vh-out-1", "OUT 1", "Projector"),
                       new DiagramNode("vh-out-2", "OUT 2", "Stage Screen")],
        Links: [new DiagramLink("vh-in-1", "vh-out-1"), new DiagramLink("vh-in-1", "vh-out-2")],
        Facts: ["12 inputs", "12 outputs"]);

    [Fact]
    public void Render_FetchesNothing()
    {
        // Emailed, opened from a USB stick in a rack room, printed and taped inside a cabinet
        // door. Anything it has to fetch is a way for it to stop working later. Inline script is
        // not a violation of that — a single file still works offline.
        var html = Diagram.Render([Device()], Stamp);

        // Checking for resource references rather than for the string "http", because the SVG
        // namespace identifier is a URI that is never dereferenced and would fail a blunter test.
        Assert.DoesNotContain("src=\"http", html);
        Assert.DoesNotContain("href=\"http", html);
        Assert.DoesNotContain("@import", html);
        Assert.DoesNotContain("url(http", html);
        Assert.DoesNotContain("fetch(", html);
        Assert.Contains("<style>", html);
        Assert.Contains("<script>", html);
    }

    [Fact]
    public void Render_EscapesLabelsIntoMarkup()
    {
        // Every label is text an operator typed into a front panel, and this opens in a browser.
        var html = Diagram.Render([Device("""<img src=x onerror=alert(1)> & "q" """)], Stamp);

        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;img", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void Render_KeepsLinkDataAsDataNotCode()
    {
        // The link payload sits inside a <script> element. If a device-supplied string could
        // reach it unescaped, a label could close the element and start running.
        var html = Diagram.Render([Device()], Stamp);

        Assert.Contains("""<script type="application/json" class="wire-data">""", html);
        Assert.Contains("""["vh-in-1","vh-out-1"]""", html);
    }

    [Fact]
    public void Render_EscapesAnAngleBracketInsideTheScriptBlock()
    {
        // Ids are generated, but the escaper is what guarantees that stays safe rather than the
        // convention that generates them.
        var device = Device() with { Links = [new DiagramLink("</script><b>", "vh-out-1")] };

        var html = Diagram.Render([device], Stamp);

        Assert.DoesNotContain("</script><b>", html);
        Assert.Contains("\\u003c/script", html);
    }

    [Fact]
    public void Render_MarksAnUnreachableDeviceRatherThanDroppingIt()
    {
        // A diagram of a rig is more useful with a gap marked in it than with a device silently
        // missing — the absence is the thing worth seeing.
        var down = new DiagramDevice("Gallery switcher", "atem", "atem", "192.168.1.31",
            [], [], [], [], "could not reach it: timed out");

        var html = Diagram.Render([Device(), down], Stamp);

        Assert.Contains("class=\"unreachable\"", html);
        Assert.Contains("timed out", html);
        Assert.Contains("Gallery switcher", html);
        // The reachable one is still fully drawn.
        Assert.Contains("Stage Screen", html);
    }

    [Fact]
    public void Render_CountsOnlyTheDevicesItReached()
    {
        var down = new DiagramDevice("Down", "atem", "atem", "h", [], [], [], [], "nope");
        Assert.Contains("<h1>1 device</h1>", Diagram.Render([Device(), down], Stamp));
        Assert.Contains("<h1>2 devices</h1>", Diagram.Render([Device(), Device()], Stamp));
    }

    [Fact]
    public void Render_SaysWhatItCannotKnow()
    {
        // A device reports its own crosspoints and nothing about what is plugged into its inputs,
        // so a line between two boxes would be a guess dressed as a measurement.
        var html = Diagram.Render([Device()], Stamp);
        Assert.Contains("cabling between devices", html);
        Assert.Contains("2026-08-31 14:30", html);
    }

    [Fact]
    public void Render_IsStableBetweenRuns()
    {
        Assert.Equal(Diagram.Render([Device()], Stamp), Diagram.Render([Device()], Stamp));
    }

    // ---- which devices get drawn -------------------------------------------------------

    [Fact]
    public void Targets_CoverEveryTypeAndEveryContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bmd-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var config = Path.Combine(directory, "config");
            File.WriteAllText(config, """
                [videohub]
                host = 10.0.0.1

                [atem]
                host = 10.0.0.2

                [atem "gallery"]
                host = 10.0.0.3

                [multiview]
                port = 9990
                """);

            var targets = DiagramCommands.Targets(ConfigStore.Load(config, directory));

            // The MultiView has a port but no host, so it is not a device — a rig is rarely all
            // three types and a missing one should contribute nothing rather than an error.
            Assert.Equal(
                [("videohub", null, "10.0.0.1"), ("atem", null, "10.0.0.2"), ("atem", "gallery", "10.0.0.3")],
                targets);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
