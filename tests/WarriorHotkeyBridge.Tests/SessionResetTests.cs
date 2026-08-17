using System.Text.Json;
using System.Text.Json.Nodes;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// What a clean reinstall removes, and - far more importantly - what it must never remove.
/// </summary>
/// <remarks>
/// This runs from an installer, on a machine whose owner cannot be talked through a recovery.
/// The configuration file holds live trading bindings and the presets folder holds saved layouts;
/// either may exist nowhere else. Everything here exists to make deleting them fail the build.
///
/// Uses an explicit root throughout. The parameterless AppPaths overload asks the Windows shell,
/// which ignores the LOCALAPPDATA environment variable - so a test that tried to redirect it would
/// operate on the real profile and could delete the operator's own Chrome profile and hotkeys.
/// </remarks>
public class SessionResetTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 16, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void ConfigurationAndPresetsSurvive()
    {
        Run(root =>
        {
            AppPaths paths = AppPaths.CreateAndEnsure(root);

            File.WriteAllText(paths.UserConfigFile, """{"Hotkeys":{"Bindings":{"F13":{"Send":"Shift+Digit1"}}}}""");

            string mine = Path.Combine(paths.Presets, "mine.json");
            const string Layout = """{"name":"Mine","bindings":{"F14":{"Send":"Shift+Digit2"}}}""";
            File.WriteAllText(mine, Layout);

            SessionReset.Run(paths, At);

            Assert.True(File.Exists(paths.UserConfigFile), "The configuration file was deleted.");
            Assert.True(File.Exists(mine), "A saved preset was deleted.");
            Assert.Equal(Layout, File.ReadAllText(mine));
            Assert.True(Directory.Exists(paths.Configuration));
            Assert.True(Directory.Exists(paths.Presets));
        });
    }

    [Fact]
    public void StaleStateIsRemoved()
    {
        Run(root =>
        {
            AppPaths paths = AppPaths.CreateAndEnsure(root);

            Directory.CreateDirectory(paths.ChromeProfile);
            File.WriteAllText(Path.Combine(paths.ChromeProfile, "Cookies"), "stale");
            File.WriteAllText(Path.Combine(paths.Logs, "bridge-20260101.log"), "old");
            File.WriteAllText(Path.Combine(paths.Diagnostics, "report.txt"), "old");
            File.WriteAllText(Path.Combine(paths.State, "startup.json"), "{}");

            SessionReset.Run(paths, At);

            Assert.False(Directory.Exists(paths.ChromeProfile), "The Chrome profile survived.");
            Assert.False(Directory.Exists(paths.Logs), "The logs survived.");
            Assert.False(Directory.Exists(paths.Diagnostics), "The diagnostics reports survived.");
            Assert.False(Directory.Exists(paths.State), "The startup preference survived.");
        });
    }

    /// <summary>
    /// The bindings are copied into presets first, so they are recoverable from the editor even if
    /// something later goes wrong with the configuration file.
    /// </summary>
    [Fact]
    public void BindingsAreSnapshottedIntoPresets()
    {
        Run(root =>
        {
            AppPaths paths = AppPaths.CreateAndEnsure(root);

            File.WriteAllText(paths.UserConfigFile, """
                {
                  "//": "a comment key, as the starter template writes",
                  "Hotkeys": { "Bindings": {
                    "F13": { "Send": "Shift+Digit1", "Label": "Buy 1000" },
                    "F14": { "Send": "Shift+Digit2", "Label": "Sell half" }
                  } }
                }
                """);

            SessionReset.Run(paths, At);

            string backup = Assert.Single(Directory.GetFiles(paths.Presets, "backup-before-reset-*.json"));
            JsonNode? preset = JsonNode.Parse(File.ReadAllText(backup));

            Assert.NotNull(preset);
            Assert.False(string.IsNullOrWhiteSpace(preset!["name"]?.GetValue<string>()));

            JsonObject bindings = Assert.IsType<JsonObject>(preset["bindings"]);
            Assert.Equal(2, bindings.Count);
            Assert.Equal("Shift+Digit1", bindings["F13"]!["Send"]!.GetValue<string>());
            Assert.Equal("Shift+Digit2", bindings["F14"]!["Send"]!.GetValue<string>());
        });
    }

    /// <summary>A first install has none of these folders, and that is not an error.</summary>
    [Fact]
    public void NothingToRemoveIsNotAFailure()
    {
        Run(root =>
        {
            var paths = AppPaths.CreateAndEnsure(root);
            Directory.Delete(root, recursive: true);

            IReadOnlyList<string> report = SessionReset.Run(paths, At);

            Assert.NotEmpty(report);
        });
    }

    /// <summary>
    /// A malformed configuration file must not stop the reset, and must not cost the operator the
    /// file itself - it is the only copy of their bindings.
    /// </summary>
    [Fact]
    public void AnUnreadableConfigurationIsReportedAndKept()
    {
        Run(root =>
        {
            AppPaths paths = AppPaths.CreateAndEnsure(root);
            File.WriteAllText(paths.UserConfigFile, "{ this is not json");

            IReadOnlyList<string> report = SessionReset.Run(paths, At);

            Assert.True(File.Exists(paths.UserConfigFile));
            Assert.Contains(report, line => line.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
        });
    }

    private static void Run(Action<string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "whb-reset-" + Guid.NewGuid().ToString("N"));

        try
        {
            test(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
