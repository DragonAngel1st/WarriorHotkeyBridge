using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Who is allowed to open Chrome, and what the sign-in entry launches.
/// </summary>
/// <remarks>
/// Both settled the same complaint from opposite directions: Chrome must not appear on its own,
/// and must appear the moment Start is pressed. Getting either backwards produces a bridge that
/// either fights the operator or does nothing when asked.
/// </remarks>
public class StartupArgumentsTests
{
    /// <summary>
    /// AutoLaunch governs only the watchdog putting Chrome back mid-session. It is off by default
    /// so that closing the browser keeps it closed.
    /// </summary>
    [Fact]
    public void AutoLaunchIsOffByDefault() => Assert.False(new ChromeOptions().AutoLaunch);

    /// <summary>
    /// The sign-in entry has to carry the parked switch, or signing in arms a session and opens
    /// Chrome - which is what the switch exists to prevent.
    /// </summary>
    [Fact]
    public void SignInEntryStartsParked() =>
        Assert.Contains(
            StartupCommand.ParkedSwitch,
            StartupCommand.Format(@"C:\Apps\WarriorHotkeyBridge.exe"),
            StringComparison.Ordinal);

    /// <summary>
    /// An entry written by an older build names the same executable, so the "points elsewhere"
    /// check reads it as healthy and leaves it alone. Only comparing the whole command catches it,
    /// which is what an upgrade needs to do to repair itself.
    /// </summary>
    [Fact]
    public void AStaleEntryPointsAtUsButDoesNotMatchTheExpectedCommand()
    {
        const string Exe = @"C:\Apps\WarriorHotkeyBridge.exe";
        string stale = $"\"{Exe}\"";
        string expected = StartupCommand.Format(Exe);

        Assert.True(StartupCommand.PointsAt(stale, Exe));
        Assert.NotEqual(expected, stale);
    }
}
