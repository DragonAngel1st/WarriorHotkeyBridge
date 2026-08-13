using System.Text.Json;
using System.Text.Json.Nodes;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Covers the editor writing back to the operator's own configuration file.
/// </summary>
/// <remarks>
/// This is the only code in the product that modifies a file the operator hand-maintains, and
/// that file holds live trading bindings. The tests here are less about JSON than about what must
/// never happen to it: settings silently dropped, a damaged file overwritten, or a partial write
/// left behind.
/// </remarks>
public class UserConfigurationWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whb-writer-" + Guid.NewGuid().ToString("N"));

    private AppPaths Paths => AppPaths.CreateAndEnsure(_root);

    [Fact]
    public void WritesBindingsWhenNoFileExists()
    {
        var writer = new UserConfigurationWriter(Paths);

        Assert.Null(writer.TryWriteBindings(new Dictionary<string, HotkeyBindingConfig>
        {
            ["F13"] = new() { Send = "Shift+Digit1", Label = "Buy 100" },
        }));

        JsonObject root = ReadBack();
        JsonNode binding = root["Hotkeys"]!["Bindings"]!["F13"]!;

        Assert.Equal("Shift+Digit1", (string?)binding["Send"]);
        Assert.Equal("Buy 100", (string?)binding["Label"]);
    }

    /// <summary>
    /// The central guarantee. That file carries documentation keys, an optional Chrome block and
    /// anything else the operator has added; round-tripping it through the typed options model
    /// would delete every setting this editor does not know about.
    /// </summary>
    [Fact]
    public void PreservesEverythingItDoesNotOwn()
    {
        File.WriteAllText(Paths.UserConfigFile, """
            {
              "//": "my notes",
              "Chrome": { "AutoLaunch": true, "CdpEndpoint": "http://127.0.0.1:9333" },
              "Hotkeys": { "Bindings": { "F13": { "Send": "Shift+Digit1" } } },
              "SomethingThisVersionHasNeverHeardOf": { "nested": [1, 2, 3] }
            }
            """);

        var writer = new UserConfigurationWriter(Paths);

        Assert.Null(writer.TryWriteBindings(new Dictionary<string, HotkeyBindingConfig>
        {
            ["F14"] = new() { Send = "Shift+Digit2" },
        }));

        JsonObject root = ReadBack();

        Assert.Equal("my notes", (string?)root["//"]);
        Assert.True((bool?)root["Chrome"]!["AutoLaunch"]);
        Assert.Equal("http://127.0.0.1:9333", (string?)root["Chrome"]!["CdpEndpoint"]);
        Assert.Equal(3, root["SomethingThisVersionHasNeverHeardOf"]!["nested"]!.AsArray().Count);

        // And the bindings really were replaced, not merged - the editor shows the whole set, so
        // what it saves is the whole set.
        JsonObject bindings = root["Hotkeys"]!["Bindings"]!.AsObject();
        Assert.False(bindings.ContainsKey("F13"));
        Assert.True(bindings.ContainsKey("F14"));
    }

    /// <summary>
    /// A file that will not parse may still hold work whose only fault is a trailing comma.
    /// Overwriting it to "fix" the problem destroys that, so the writer refuses and says why.
    /// </summary>
    [Fact]
    public void RefusesToOverwriteAFileThatDoesNotParse()
    {
        const string Damaged = "{ \"Hotkeys\": { \"Bindings\": ";
        File.WriteAllText(Paths.UserConfigFile, Damaged);

        string? error = new UserConfigurationWriter(Paths).TryWriteBindings(
            new Dictionary<string, HotkeyBindingConfig> { ["F13"] = new() { Send = "A" } });

        Assert.NotNull(error);
        Assert.Equal(Damaged, File.ReadAllText(Paths.UserConfigFile));
    }

    [Fact]
    public void OmitsEmptyFieldsSoTheFileStaysReadable()
    {
        var writer = new UserConfigurationWriter(Paths);

        writer.TryWriteBindings(new Dictionary<string, HotkeyBindingConfig>
        {
            ["F13"] = new() { Send = "Shift+Digit1" },
            ["F24"] = new() { Action = "Diagnostics", Label = "report", Level2Index = 2 },
        });

        JsonObject bindings = ReadBack()["Hotkeys"]!["Bindings"]!.AsObject();

        JsonObject simple = bindings["F13"]!.AsObject();
        Assert.False(simple.ContainsKey("Label"));
        Assert.False(simple.ContainsKey("Action"));
        Assert.False(simple.ContainsKey("Level2Index"));

        JsonObject full = bindings["F24"]!.AsObject();
        Assert.Equal("Diagnostics", (string?)full["Action"]);
        Assert.Equal(2, (int?)full["Level2Index"]);
    }

    [Fact]
    public void LeavesNoTemporaryFileBehind()
    {
        new UserConfigurationWriter(Paths).TryWriteBindings(
            new Dictionary<string, HotkeyBindingConfig> { ["F13"] = new() { Send = "A" } });

        Assert.Empty(Directory.GetFiles(Paths.Configuration, "*.tmp"));
    }

    /// <summary>
    /// Function keys sort by number, not by text. A file listing F10 between F1 and F2 is one the
    /// operator has to read twice.
    /// </summary>
    [Fact]
    public void OrdersFunctionKeysNumerically()
    {
        var writer = new UserConfigurationWriter(Paths);

        writer.TryWriteBindings(new Dictionary<string, HotkeyBindingConfig>
        {
            ["F22"] = new() { Send = "A" },
            ["F13"] = new() { Send = "B" },
            ["F2"] = new() { Send = "C" },
        });

        string[] order = [.. ReadBack()["Hotkeys"]!["Bindings"]!.AsObject().Select(p => p.Key)];

        Assert.Equal(["F2", "F13", "F22"], order);
    }

    private JsonObject ReadBack() =>
        JsonNode.Parse(File.ReadAllText(Paths.UserConfigFile))!.AsObject();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
