using WarriorHotkeyBridge.Chrome;
using WarriorHotkeyBridge.Configuration;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Finding Chrome on a machine that is not the developer's.
/// </summary>
/// <remarks>
/// The configured path is only a default and it names one of at least four real locations. A
/// 32-bit Chrome installs under Program Files (x86); a per-user install - the kind needing no
/// administrator, and so the kind found on a machine somebody set up for a relative - installs
/// under the user's own AppData. A single hard-coded path meant Start could never work there, and
/// the only remedy was hand-editing JSON.
/// </remarks>
public class ChromeDiscoveryTests
{
    [Fact]
    public void TheDefaultPathIsTheSixtyFourBitInstall() =>
        Assert.Equal(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            new ChromeOptions().ExecutablePath);

    /// <summary>
    /// The 32-bit and per-user locations must both be searched, since those are the two that a
    /// default-path-only lookup misses.
    /// </summary>
    [Fact]
    public void BothInstallLocationsTheDefaultMissesAreSearched()
    {
        string[] candidates = [.. ChromeLauncher.CandidateExecutables()];

        Assert.Contains(candidates, c =>
            c.Contains("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
            && c.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase));

        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        Assert.Contains(candidates, c =>
            c.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase)
            && c.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Enumeration must not throw on a machine where the registry read is refused, because it runs
    /// on the path that a Start button is waiting on.
    /// </summary>
    [Fact]
    public void EnumeratingNeverThrows()
    {
        string[] candidates = [.. ChromeLauncher.CandidateExecutables()];

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }
}
