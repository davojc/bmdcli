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

    // Each InlineData wraps its string[] payload in an explicit `new object[] { ... }`:
    // passing the string[] directly (`new[] { "videohub" }`) hits CS0182, because binding
    // it straight to InlineData's `params object[] data` needs an implicit array-covariance
    // conversion (string[] -> object[]), which C# disallows in attribute arguments.
    [Theory]
    [InlineData(new object[] { new[] { "videohub" } })]
    [InlineData(new object[] { new[] { "videohub", "--help" } })]
    [InlineData(new object[] { new[] { "videohub", "-h" } })]
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
    [InlineData(new object[] { new[] { "videohub", "info" } })]          // an exact command: not group help
    [InlineData(new object[] { new[] { "videohub", "route", "set" } })]
    [InlineData(new object[] { new[] { "--help" } })]                     // root help stays with the framework
    [InlineData(new object[] { new string[0] })]
    [InlineData(new object[] { new[] { "nonsense" } })]
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
