using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tests;

public class CommandLineOptionsTests
{
    [Theory]
    [InlineData("--debug")]
    [InlineData("-d")]
    [InlineData("/debug")]
    [InlineData("--DEBUG")]
    public void Parse_RecognisesDebugFlagInEveryAcceptedForm(string arg) =>
        Assert.True(CommandLineOptions.Parse([arg]).Debug);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void Parse_RecognisesHelpFlag(string arg) =>
        Assert.True(CommandLineOptions.Parse([arg]).ShowHelp);

    [Fact]
    public void Parse_RecognisesVersionFlag() =>
        Assert.True(CommandLineOptions.Parse(["--version"]).ShowVersion);

    [Fact]
    public void Parse_WithNoArguments_SelectsNormalMode()
    {
        CommandLineOptions options = CommandLineOptions.Parse([]);

        Assert.False(options.Debug);
        Assert.False(options.ShowHelp);
        Assert.False(options.ShowVersion);
        Assert.Empty(options.ConfigurationArgs);
        Assert.Empty(options.UnknownArgs);
    }

    /// <summary>
    /// Only fully-qualified overrides are forwarded to the configuration binder, which throws
    /// on bare flags such as --debug.
    /// </summary>
    [Fact]
    public void Parse_ForwardsQualifiedConfigurationOverrides()
    {
        CommandLineOptions options = CommandLineOptions.Parse(
            ["--debug", "--Chrome:CdpEndpoint=http://127.0.0.1:9333"]);

        Assert.True(options.Debug);
        Assert.Equal("--Chrome:CdpEndpoint=http://127.0.0.1:9333", Assert.Single(options.ConfigurationArgs));
        Assert.Empty(options.UnknownArgs);
    }

    [Fact]
    public void Parse_CollectsUnknownArgumentsRatherThanIgnoringThem()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--nonsense", "stray"]);

        Assert.Equal(2, options.UnknownArgs.Count);
        Assert.False(options.Debug);
    }

    [Fact]
    public void Parse_AcceptsSeveralFlagsTogether()
    {
        CommandLineOptions options = CommandLineOptions.Parse(["--debug", "--version"]);

        Assert.True(options.Debug);
        Assert.True(options.ShowVersion);
    }
}
