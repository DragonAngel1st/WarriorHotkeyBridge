using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// The presets folder exists from startup, and starting up never disturbs what is in it.
/// </summary>
/// <remarks>
/// It used to be created only when a preset was saved from the editor, so a fresh install had
/// nowhere to put a preset you already had - restoring a backup, or carrying a layout from another
/// machine. The folder is created by the application rather than the installer on purpose: an MSI
/// that creates a directory owns it, and an owned directory can be removed on uninstall and during
/// the remove-then-install half of every upgrade this package performs.
///
/// Uses an explicit root. The parameterless overload asks the Windows shell, which ignores the
/// LOCALAPPDATA environment variable - so a test that tried to redirect it would silently operate
/// on the real profile and could write over live trading bindings.
/// </remarks>
public class PresetFolderTests
{
    [Fact]
    public void StartupCreatesThePresetsFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "whb-presets-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppPaths paths = AppPaths.CreateAndEnsure(root);

            Assert.True(Directory.Exists(paths.Presets), $"{paths.Presets} was not created.");
            Assert.Equal(Path.Combine(root, "Presets"), paths.Presets);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The case that matters on every upgrade: an existing folder full of the operator's own
    /// presets must come through untouched.
    /// </summary>
    [Fact]
    public void StartupLeavesExistingPresetsAlone()
    {
        string root = Path.Combine(Path.GetTempPath(), "whb-presets-" + Guid.NewGuid().ToString("N"));

        try
        {
            string presets = Path.Combine(root, "Presets");
            Directory.CreateDirectory(presets);

            string mine = Path.Combine(presets, "mine.json");
            const string Content = """{"name":"Mine","bindings":{"F13":{"Send":"Shift+Digit1"}}}""";
            File.WriteAllText(mine, Content);

            // Twice, because startup runs on every launch and an upgrade is just another launch.
            AppPaths.CreateAndEnsure(root);
            AppPaths.CreateAndEnsure(root);

            Assert.True(File.Exists(mine), "The operator's preset was removed.");
            Assert.Equal(Content, File.ReadAllText(mine));
            Assert.Single(Directory.GetFiles(presets));
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
