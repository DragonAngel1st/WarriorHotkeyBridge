using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tests;

public class StartupCommandTests
{
    /// <summary>
    /// The real install path is <c>%LOCALAPPDATA%\Programs\WarriorHotkeyBridge\</c> and the
    /// development path is "...\Warrior Sim Hotkey Connector\...". Both a user name and a
    /// project folder can contain spaces, so quoting is not optional.
    /// </summary>
    [Fact]
    public void Format_AlwaysQuotes()
    {
        Assert.Equal("\"C:\\Apps\\WarriorHotkeyBridge.exe\"", StartupCommand.Format(@"C:\Apps\WarriorHotkeyBridge.exe"));

        Assert.Equal(
            "\"C:\\Program Files\\Warrior Hotkey Bridge\\WarriorHotkeyBridge.exe\"",
            StartupCommand.Format(@"C:\Program Files\Warrior Hotkey Bridge\WarriorHotkeyBridge.exe"));
    }

    [Fact]
    public void Format_DoesNotDoubleQuoteAnAlreadyQuotedPath()
    {
        Assert.Equal("\"C:\\Apps\\x.exe\"", StartupCommand.Format("\"C:\\Apps\\x.exe\""));
    }

    [Theory]
    [InlineData("\"C:\\Apps\\x.exe\"", @"C:\Apps\x.exe")]
    [InlineData("\"C:\\Program Files\\A B\\x.exe\" --flag", @"C:\Program Files\A B\x.exe")]
    [InlineData(@"C:\Apps\x.exe", @"C:\Apps\x.exe")]
    [InlineData(@"C:\Apps\x.exe -silent", @"C:\Apps\x.exe")]
    public void ParseExecutablePath_HandlesTheFormsFoundInARealRunKey(string command, string expected)
    {
        // These shapes are all present in a real HKCU Run key: quoted with args, quoted without,
        // and bare paths with and without arguments.
        Assert.Equal(expected, StartupCommand.ParseExecutablePath(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated")]
    public void ParseExecutablePath_ReturnsNullForUnusableInput(string? command) =>
        Assert.Null(StartupCommand.ParseExecutablePath(command));

    [Fact]
    public void PointsAt_MatchesRegardlessOfQuotingCaseAndSeparators()
    {
        const string Exe = @"C:\Apps\Bridge\WarriorHotkeyBridge.exe";

        Assert.True(StartupCommand.PointsAt("\"C:\\Apps\\Bridge\\WarriorHotkeyBridge.exe\"", Exe));
        Assert.True(StartupCommand.PointsAt("\"c:\\apps\\bridge\\warriorhotkeybridge.exe\"", Exe));
        Assert.True(StartupCommand.PointsAt("\"C:\\Apps\\Bridge\\..\\Bridge\\WarriorHotkeyBridge.exe\"", Exe));
    }

    /// <summary>
    /// An entry left behind by a previous install location is the case that matters: it looks
    /// enabled but launches the wrong copy, or nothing at all.
    /// </summary>
    [Fact]
    public void PointsAt_RejectsADifferentInstall()
    {
        const string Exe = @"C:\Users\me\AppData\Local\Programs\WarriorHotkeyBridge\WarriorHotkeyBridge.exe";

        Assert.False(StartupCommand.PointsAt("\"C:\\Old\\WarriorHotkeyBridge.exe\"", Exe));
        Assert.False(StartupCommand.PointsAt("\"C:\\Apps\\SomethingElse.exe\"", Exe));
        Assert.False(StartupCommand.PointsAt(null, Exe));
    }

    /// <summary>
    /// A path with spaces stored without quotes is already broken - Windows would launch
    /// "C:\Program" - so it must not be reported as a match.
    /// </summary>
    [Fact]
    public void PointsAt_RejectsAnUnquotedPathWithSpaces() =>
        Assert.False(StartupCommand.PointsAt(
            @"C:\Program Files\Bridge\WarriorHotkeyBridge.exe",
            @"C:\Program Files\Bridge\WarriorHotkeyBridge.exe"));

    [Fact]
    public void FormatAndParse_RoundTrip()
    {
        const string Exe = @"C:\Program Files\Warrior Hotkey Bridge\WarriorHotkeyBridge.exe";

        Assert.Equal(Exe, StartupCommand.ParseExecutablePath(StartupCommand.Format(Exe)));
        Assert.True(StartupCommand.PointsAt(StartupCommand.Format(Exe), Exe));
    }
}
