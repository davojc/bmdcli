using System.Net;
using System.Text;
using Bmd.Devices.Videohub;

namespace Bmd.Output;

/// <summary>Renders a router's current state as one self-contained HTML page.
///
/// <para>Self-contained is the requirement that shapes everything else: the page is emailed to a
/// colleague, opened from a USB stick in a rack room, or printed and taped inside a cabinet door.
/// So no scripts, no web fonts, no external stylesheet — a single file that renders identically
/// offline in five years. That also rules out drawing the flow with computed SVG coordinates,
/// which would need text metrics the generator does not have; the layout is CSS instead, and the
/// fan-out is shown by grouping rather than by lines that would cross.</para>
///
/// <para>Every label on the page comes from the device, which means it is untrusted text: an
/// operator can name an input <c>&lt;script&gt;</c> from the front panel. Everything interpolated
/// here goes through <see cref="Text"/>.</para></summary>
public static class RoutingPage
{
    /// <summary>One source and every output it currently feeds.</summary>
    public readonly record struct Feed(int Input, string Label, IReadOnlyList<int> Outputs);

    /// <summary>Sources actually routed somewhere, busiest first — the ordering is the point, since
    /// "what is this hub mostly doing" is answered by which source feeds fifteen outputs and which
    /// feeds one. Ties break on input number so the page is stable between runs.</summary>
    public static IReadOnlyList<Feed> Feeds(VideohubState state)
    {
        var byInput = new Dictionary<int, List<int>>();
        for (var output = 1; output <= state.Device.VideoOutputs; output++)
        {
            var input = state.GetRoute(output);
            if (!byInput.TryGetValue(input, out var outputs)) byInput[input] = outputs = [];
            outputs.Add(output);
        }

        return [.. byInput
            .OrderByDescending(pair => pair.Value.Count)
            .ThenBy(pair => pair.Key)
            .Select(pair => new Feed(pair.Key, state.GetInputLabel(pair.Key), pair.Value))];
    }

    /// <summary>Inputs routed nowhere. Worth showing: on a 40-way hub most of the patch is usually
    /// idle, and "plugged in but going nowhere" is a question people actually ask.</summary>
    public static IReadOnlyList<int> IdleInputs(VideohubState state)
    {
        var used = new HashSet<int>();
        for (var output = 1; output <= state.Device.VideoOutputs; output++) used.Add(state.GetRoute(output));
        return [.. Enumerable.Range(1, state.Device.VideoInputs).Where(input => !used.Contains(input))];
    }

    public static string Render(VideohubState state, string host, DateTimeOffset generatedAt)
    {
        var device = state.Device;
        var feeds = Feeds(state);
        var idle = IdleInputs(state);
        var page = new StringBuilder();

        page.Append($"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{Text(Title(device))}</title>
            <style>
            {Css}
            </style>
            </head>
            <body>
            <main>
              <header>
                <p class="eyebrow">Routing</p>
                <h1>{Text(Title(device))}</h1>
                <p class="sub">{Text(device.ModelName)} at {Text(host)} &middot; protocol {Text(device.ProtocolVersion)}</p>
                <p class="stamp">As at {Text(generatedAt.ToString("yyyy-MM-dd HH:mm"))} UTC</p>
              </header>

              <ul class="stats">
                <li><b>{device.VideoOutputs}</b><span>outputs</span></li>
                <li><b>{feeds.Count}</b><span>sources in use</span></li>
                <li><b>{idle.Count}</b><span>inputs idle</span></li>
              </ul>

            """);

        page.Append("""
              <section>
                <h2>What feeds what</h2>
                <p class="note">Each source with every output it is currently routed to, busiest first.</p>
                <div class="feeds">

            """);

        foreach (var feed in feeds)
        {
            page.Append($"""
                  <article class="feed">
                    <div class="src">
                      <span class="num">IN {feed.Input}</span>
                      <span class="lbl">{Text(Label(feed.Label, feed.Input))}</span>
                      <span class="count">{feed.Outputs.Count} {(feed.Outputs.Count == 1 ? "output" : "outputs")}</span>
                    </div>
                    <ul class="dests">

                """);
            foreach (var output in feed.Outputs)
            {
                page.Append($"""
                      <li><span class="num">OUT {output}</span> <span class="lbl">{Text(Label(state.GetOutputLabel(output), output))}</span></li>

                    """);
            }
            page.Append("""
                    </ul>
                  </article>

                """);
        }

        page.Append("""
                </div>
              </section>

              <section>
                <h2>Every output</h2>
                <p class="note">In output order, for looking one up.</p>
                <table>
                  <thead><tr><th>Out</th><th>Output label</th><th>In</th><th>Input label</th></tr></thead>
                  <tbody>

            """);

        for (var output = 1; output <= device.VideoOutputs; output++)
        {
            var input = state.GetRoute(output);
            page.Append($"""
                    <tr><td class="n">{output}</td><td>{Text(Label(state.GetOutputLabel(output), output))}</td><td class="n">{input}</td><td>{Text(Label(state.GetInputLabel(input), input))}</td></tr>

                """);
        }

        page.Append("""
                  </tbody>
                </table>
              </section>

            """);

        if (idle.Count > 0)
        {
            page.Append("""
              <section>
                <h2>Idle inputs</h2>
                <p class="note">Connected to the hub, currently routed nowhere.</p>
                <ul class="idle">

            """);
            foreach (var input in idle)
            {
                page.Append($"""
                    <li><span class="num">IN {input}</span> <span class="lbl">{Text(Label(state.GetInputLabel(input), input))}</span></li>

                """);
            }
            page.Append("""
                </ul>
              </section>

            """);
        }

        page.Append("""
              <footer>Generated by bmd. Locks and any change made after the timestamp above are not shown.</footer>
            </main>
            </body>
            </html>

            """);

        return page.ToString();
    }

    static string Title(VideohubDeviceInfo device) =>
        string.IsNullOrWhiteSpace(device.FriendlyName) ? device.ModelName : device.FriendlyName!;

    /// <summary>An unlabelled channel shows as its number rather than as a blank cell, so a row
    /// never looks like a rendering fault when it is really just an unnamed input.</summary>
    static string Label(string label, int number) =>
        string.IsNullOrWhiteSpace(label) ? $"#{number}" : label;

    /// <summary>Device-supplied text is untrusted: an operator can name an input anything the
    /// front panel accepts, and this page is opened in a browser.</summary>
    static string Text(string value) => WebUtility.HtmlEncode(value);

    /// <summary>Deliberately no web font. The page has to render the same on a rack-room machine
    /// with no network, so it asks for IBM Plex — which matches the rest of the project where it
    /// happens to be installed — and falls back through to whatever the system has.</summary>
    const string Css = """
        :root {
          --bg: #0c0d10; --panel: #14161a; --line: #24272e; --line-soft: #1c1f25;
          --fg: #e6e8ec; --dim: #989fab; --faint: #79818e; --signal: #f2542d;
          --mono: "IBM Plex Mono", ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace;
          --sans: "IBM Plex Sans", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
        }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--fg); font-family: var(--sans); line-height: 1.55; }
        main { max-width: 70rem; margin: 0 auto; padding: 2.5rem 1.25rem 4rem; }
        .eyebrow { margin: 0 0 .5rem; font-family: var(--mono); font-size: .68rem; font-weight: 600;
          letter-spacing: .2em; text-transform: uppercase; color: var(--faint); }
        h1 { margin: 0; font-size: clamp(1.9rem, 5vw, 2.9rem); line-height: 1.05; letter-spacing: -.02em; }
        .sub { margin: .5rem 0 0; color: var(--dim); }
        .stamp { margin: .2rem 0 0; font-family: var(--mono); font-size: .78rem; color: var(--faint); }
        .stats { list-style: none; display: flex; flex-wrap: wrap; gap: .75rem; margin: 2rem 0 0; padding: 0; }
        .stats li { flex: 1 1 8rem; padding: .9rem 1rem; background: var(--panel);
          border: 1px solid var(--line-soft); border-left: 2px solid var(--signal); border-radius: 10px; }
        .stats b { display: block; font-size: 1.6rem; font-variant-numeric: tabular-nums; }
        .stats span { font-family: var(--mono); font-size: .68rem; letter-spacing: .12em;
          text-transform: uppercase; color: var(--faint); }
        section { margin-top: 3rem; }
        h2 { margin: 0 0 .3rem; font-size: 1.35rem; letter-spacing: -.01em; }
        .note { margin: 0 0 1.25rem; color: var(--dim); font-size: .95rem; }
        .feeds { display: grid; gap: .85rem; grid-template-columns: repeat(auto-fill, minmax(19rem, 1fr)); }
        .feed { background: var(--panel); border: 1px solid var(--line-soft); border-radius: 12px;
          padding: .9rem 1rem 1rem; }
        .src { display: flex; align-items: baseline; gap: .5rem; flex-wrap: wrap;
          padding-bottom: .6rem; border-bottom: 1px solid var(--line); }
        .src .lbl { font-weight: 600; }
        .src .count { margin-left: auto; font-family: var(--mono); font-size: .68rem; color: var(--faint); }
        .num { font-family: var(--mono); font-size: .66rem; letter-spacing: .06em; color: var(--signal);
          border: 1px solid var(--line); border-radius: 4px; padding: .1rem .32rem; white-space: nowrap;
          font-variant-numeric: tabular-nums; }
        .dests, .idle { list-style: none; margin: .6rem 0 0; padding: 0; display: grid; gap: .3rem; }
        .dests li, .idle li { display: flex; align-items: baseline; gap: .5rem; font-size: .93rem; color: var(--dim); }
        .dests .num, .idle .num { color: var(--faint); }
        .idle { grid-template-columns: repeat(auto-fill, minmax(15rem, 1fr)); }
        table { width: 100%; border-collapse: collapse; font-size: .93rem; }
        th, td { text-align: left; padding: .42rem .6rem; border-bottom: 1px solid var(--line-soft); }
        th { font-family: var(--mono); font-size: .66rem; letter-spacing: .12em; text-transform: uppercase;
          color: var(--faint); border-bottom-color: var(--line); }
        td.n { font-family: var(--mono); font-variant-numeric: tabular-nums; color: var(--faint); width: 3.5rem; }
        tbody tr:hover { background: var(--panel); }
        footer { margin-top: 3rem; padding-top: 1rem; border-top: 1px solid var(--line-soft);
          color: var(--faint); font-size: .82rem; }
        @media print {
          body { background: #fff; color: #000; }
          .feed, .stats li, tbody tr:hover { background: transparent; }
          .num { color: #000; }
          main { max-width: none; padding: 0; }
        }
        """;
}
