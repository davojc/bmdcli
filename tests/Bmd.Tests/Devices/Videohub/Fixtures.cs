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
}
