using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Covers saving and reloading operator-created presets.
/// </summary>
/// <remarks>
/// The preset name is typed by a person into a dialog and then turned into a file path, which
/// makes it untrusted input reaching the filesystem. Most of what is pinned here is about that.
/// </remarks>
public class HotkeyPresetProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whb-presets-" + Guid.NewGuid().ToString("N"));

    private HotkeyPresetProvider Provider => new(AppPaths.CreateAndEnsure(_root));

    private static Dictionary<string, HotkeyBindingConfig> SampleBindings => new(StringComparer.OrdinalIgnoreCase)
    {
        ["F13"] = new() { Send = "Shift+Digit1", Label = "Buy 100" },
        ["F24"] = new() { Action = "Diagnostics" },
    };

    [Fact]
    public void SavedPresetIsFoundAgainWithItsBindingsIntact()
    {
        HotkeyPresetProvider provider = Provider;

        Assert.Null(provider.TrySave("My Layout", "notes here", SampleBindings, overwrite: false));

        HotkeyPreset preset = Assert.Single(provider.Load(), p => p.IsUserSupplied);

        Assert.Equal("My Layout", preset.Name);
        Assert.Equal("notes here", preset.Description);
        Assert.True(preset.IsUserSupplied);
        Assert.Equal(2, preset.Bindings.Count);
        Assert.Equal("Shift+Digit1", preset.Bindings["F13"].Send);
        Assert.Equal("Diagnostics", preset.Bindings["F24"].Action);
    }

    /// <summary>
    /// The name reaches the filesystem, so a name that walks out of the folder must not be able
    /// to write anywhere else - however the sanitising is later changed.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("../../evil")]
    [InlineData(@"C:\Windows\System32\evil")]
    [InlineData("con")]
    [InlineData("....")]
    [InlineData("///")]
    public void ANameCannotWriteOutsideThePresetFolder(string name)
    {
        HotkeyPresetProvider provider = Provider;
        provider.TrySave(name, null, SampleBindings, overwrite: true);

        // Whatever it did or refused to do, every file it produced is a direct child of the
        // preset directory.
        string presetDirectory = Path.GetFullPath(provider.UserPresetDirectory);

        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && file.Contains("Presets", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal(presetDirectory, Path.GetDirectoryName(Path.GetFullPath(file)));
            }
        }
    }

    [Fact]
    public void RefusesAnEmptyName() =>
        Assert.NotNull(Provider.TrySave("   ", null, SampleBindings, overwrite: true));

    /// <summary>A preset with nothing in it would silently clear the operator's keys when loaded.</summary>
    [Fact]
    public void RefusesAPresetWithNoBindings() =>
        Assert.NotNull(Provider.TrySave("Empty", null, new Dictionary<string, HotkeyBindingConfig>(), overwrite: true));

    [Fact]
    public void RefusesToReplaceAnExistingPresetUnlessAsked()
    {
        HotkeyPresetProvider provider = Provider;

        Assert.Null(provider.TrySave("Mine", null, SampleBindings, overwrite: false));
        Assert.NotNull(provider.TrySave("Mine", null, SampleBindings, overwrite: false));
        Assert.Null(provider.TrySave("Mine", null, SampleBindings, overwrite: true));

        Assert.Single(provider.Load(), p => p.IsUserSupplied);
    }

    /// <summary>
    /// Names differing only by case, punctuation or spacing land on the same file, so treating
    /// them as different presets would produce two entries that overwrite one another.
    /// </summary>
    [Fact]
    public void NamesThatCollideOnDiskAreTreatedAsTheSamePreset()
    {
        HotkeyPresetProvider provider = Provider;

        Assert.Null(provider.TrySave("Ross Sim Default", null, SampleBindings, overwrite: true));
        Assert.Null(provider.TrySave("ross  sim--default", null, SampleBindings, overwrite: true));

        Assert.Single(provider.Load(), p => p.IsUserSupplied);
    }

    /// <summary>
    /// Presets shipped beside the executable are found and flagged as not the operator's, which
    /// is what stops the editor offering to overwrite something an upgrade will replace anyway.
    /// </summary>
    [Fact]
    public void ShippedPresetsAreDiscoveredAndMarkedReadOnly()
    {
        HotkeyPreset[] shipped = [.. Provider.Load().Where(p => !p.IsUserSupplied)];

        Assert.NotEmpty(shipped);
        Assert.All(shipped, p => Assert.NotEmpty(p.Bindings));
    }

    /// <summary>A user preset must not shadow one that the next upgrade will overwrite.</summary>
    [Fact]
    public void RefusesToShadowAShippedPreset()
    {
        HotkeyPresetProvider provider = Provider;
        HotkeyPreset shipped = provider.Load().First(p => !p.IsUserSupplied);

        Assert.NotNull(provider.TrySave(shipped.Name, null, SampleBindings, overwrite: true));
    }

    [Fact]
    public void DescribeReportsWhetherAPresetExists()
    {
        HotkeyPresetProvider provider = Provider;
        provider.TrySave("Mine", null, SampleBindings, overwrite: true);

        Assert.Equal((true, false), provider.Describe("mine"));
        Assert.Equal((false, false), provider.Describe("something else"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
