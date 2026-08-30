namespace Bmd.Tests.Devices.Videohub;

public static class Fixtures
{
    /// <summary>Realistic 4x4 initial dump. Wire indices are 0-based:
    /// output0←input3, output1←input1, output2←input0, output3←input2.</summary>
    public const string Dump4x4 =
        "PROTOCOL PREAMBLE:\n" +
        "Version: 2.8\n" +
        "\n" +
        "VIDEOHUB DEVICE:\n" +
        "Device present: true\n" +
        "Model name: Blackmagic Smart Videohub\n" +
        "Friendly name: Test Hub\n" +
        "Video inputs: 4\n" +
        "Video outputs: 4\n" +
        "\n" +
        "INPUT LABELS:\n" +
        "0 Cam 1\n" +
        "1 Cam 2\n" +
        "2 Cam 3\n" +
        "3 Cam 4\n" +
        "\n" +
        "OUTPUT LABELS:\n" +
        "0 Program\n" +
        "1 Preview\n" +
        "2 Monitor\n" +
        "3 Aux\n" +
        "\n" +
        "VIDEO OUTPUT LOCKS:\n" +
        "0 U\n" +
        "1 O\n" +
        "2 L\n" +
        "3 U\n" +
        "\n" +
        "VIDEO OUTPUT ROUTING:\n" +
        "0 3\n" +
        "1 1\n" +
        "2 0\n" +
        "3 2\n" +
        "\n" +
        "END PRELUDE:\n" +
        "\n";

    /// <summary>A real Blackmagic MultiView 4 dump (firmware 2.2.5), captured read-only from
    /// hardware. Six "outputs": four multiview windows plus Solo Input and Audio Input. Carries
    /// the CONFIGURATION block, which no Videohub sends.</summary>
    public const string DumpMultiView4 =
        "PROTOCOL PREAMBLE:\nVersion: 2.8\n\n" +
        "VIDEOHUB DEVICE:\n" +
        "Device present: true\n" +
        "Model name: Blackmagic MultiView 4\n" +
        "Friendly name: AV Multiview\n" +
        "Unique ID: 000000000000\n" +
        "Video inputs: 4\n" +
        "Video processing units: 0\n" +
        "Video outputs: 6\n" +
        "Video monitoring outputs: 0\n" +
        "Serial ports: 0\n\n" +
        "INPUT LABELS:\n0 Stream\n1 Screens\n2 Presenter\n3 Confidence\n\n" +
        "OUTPUT LABELS:\n0 View 1\n1 View 2\n2 View 3\n3 View 4\n4 Solo Input\n5 Audio Input\n\n" +
        "VIDEO OUTPUT LOCKS:\n0 U\n1 U\n2 U\n3 U\n4 U\n5 U\n\n" +
        "VIDEO OUTPUT ROUTING:\n0 0\n1 1\n2 2\n3 3\n4 2\n5 0\n\n" +
        "CONFIGURATION:\n" +
        "Layout: 2x2\n" +
        "Output format: 1080i5994\n" +
        "Solo enabled: false\n" +
        "Widescreen SD enabled: true\n" +
        "Display border: true\n" +
        "Display labels: true\n" +
        "Display audio meters: false\n" +
        "Display SDI tally: false\n" +
        "Take Mode: true\n\n" +
        "END PRELUDE:\n\n";
}
