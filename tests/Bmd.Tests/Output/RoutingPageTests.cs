using Bmd.Devices.Videohub;
using Bmd.Output;
using Bmd.Tests.Devices.Videohub;

namespace Bmd.Tests.Output;

public class RoutingPageTests
{
    static readonly DateTimeOffset Stamp = new(2026, 8, 31, 14, 30, 0, TimeSpan.Zero);

    static VideohubState State() => DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4));

    static string Render() => RoutingPage.Render(State(), "192.168.1.50", Stamp);

    [Fact]
    public void Render_IsSelfContained()
    {
        // The page is emailed, opened from a USB stick in a rack room, or printed and taped
        // inside a cabinet door. Anything it has to fetch is a way for it to stop working later.
        var html = Render();

        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("<style>", html);
    }

    [Fact]
    public void Render_EscapesLabelsThatCameFromTheDevice()
    {
        // Every label on this page is text an operator typed into a front panel, and the page is
        // opened in a browser. A hub labelled from a hostile or careless source must not be able
        // to put markup into it.
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4.Replace(
            "Cam 1", """<script>alert('x')</script> & "quoted" """)));

        var html = RoutingPage.Render(state, "192.168.1.50", Stamp);

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void Render_EscapesTheHostToo()
    {
        // The host comes from a command-line flag or config, so it is no more trusted than a label.
        var html = RoutingPage.Render(State(), "<b>not-a-host</b>", Stamp);

        Assert.DoesNotContain("<b>not-a-host", html);
        Assert.Contains("&lt;b&gt;not-a-host", html);
    }

    [Fact]
    public void Feeds_GroupOutputsBySourceBusiestFirst()
    {
        // The ordering is the information: "what is this hub mostly doing" is answered by which
        // source feeds many outputs and which feeds one.
        var feeds = RoutingPage.Feeds(State());

        Assert.NotEmpty(feeds);
        var counts = feeds.Select(f => f.Outputs.Count).ToList();
        Assert.Equal(counts.OrderByDescending(c => c), counts);

        // Every output is accounted for exactly once, across all sources.
        var outputs = feeds.SelectMany(f => f.Outputs).OrderBy(o => o).ToList();
        Assert.Equal(Enumerable.Range(1, State().Device.VideoOutputs), outputs);
    }

    [Fact]
    public void Feeds_AreStableBetweenRuns()
    {
        // A page regenerated with no change to the hub should be byte-identical apart from its
        // timestamp, so a diff between two of them shows only what actually moved.
        Assert.Equal(
            RoutingPage.Feeds(State()).Select(f => f.Input),
            RoutingPage.Feeds(State()).Select(f => f.Input));
        Assert.Equal(RoutingPage.Render(State(), "h", Stamp), RoutingPage.Render(State(), "h", Stamp));
    }

    [Fact]
    public void IdleInputs_AreTheOnesRoutedNowhere()
    {
        var state = State();
        var idle = RoutingPage.IdleInputs(state);
        var routed = Enumerable.Range(1, state.Device.VideoOutputs).Select(state.GetRoute).ToHashSet();

        Assert.All(idle, input => Assert.DoesNotContain(input, routed));
        Assert.Equal(state.Device.VideoInputs - routed.Count, idle.Count);
    }

    [Fact]
    public void Render_ShowsEveryOutputInTheTable()
    {
        var state = State();
        var html = Render();

        // One row per output, whatever the grouping above it did.
        Assert.Equal(state.Device.VideoOutputs, CountOccurrences(html, "<tr><td class=\"n\">"));
        for (var output = 1; output <= state.Device.VideoOutputs; output++)
            Assert.Contains($"OUT {output}", html);
    }

    [Fact]
    public void Render_ShowsAnUnlabelledChannelAsItsNumber()
    {
        // A blank cell reads as a rendering fault rather than as an unnamed input.
        var state = DumpParser.Parse(BlockReader.ReadBlocks(Fixtures.Dump4x4.Replace("Cam 1", "")));
        Assert.Contains("#1", RoutingPage.Render(state, "h", Stamp));
    }

    [Fact]
    public void Render_SaysWhatItDoesNotShow()
    {
        // The page is a snapshot of a device other controllers can change, and it does not carry
        // locks. Saying so is the difference between a document and a misleading one.
        var html = Render();
        Assert.Contains("Locks", html);
        Assert.Contains("2026-08-31 14:30", html);
    }

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
