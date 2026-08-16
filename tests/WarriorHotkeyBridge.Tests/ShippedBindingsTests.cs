using System.Text.Json;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// The application's own configuration must ship no hotkey bindings at all.
/// </summary>
/// <remarks>
/// Configuration layers merge per key, not per object. A binding shipped in the application's
/// appsettings.json and rebound in the operator's user file merges into one entry carrying both
/// Send and Action, which the resolver rejects - so rebinding it silently costs them the key, and
/// the shipped default reappears on every restart.
///
/// F23 and F24 shipped as Test and Diagnostics on the assumption that nobody would rebind spare
/// keys. Someone did, lost the keys, and had their own bindings overwritten each time the
/// application started. Both actions are reachable from the UI - Test targeting in the hotkey
/// editor, Run Diagnostics in the tray - so neither needs to cost a deck key.
///
/// Reads the file that actually ships rather than a copy, so adding a binding back cannot pass.
/// </remarks>
public class ShippedBindingsTests
{
    [Fact]
    public void TheShippedConfigurationBindsNoKeys()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"The shipped appsettings.json was not found at {path}.");

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        if (!document.RootElement.TryGetProperty("Hotkeys", out JsonElement hotkeys)
            || !hotkeys.TryGetProperty("Bindings", out JsonElement bindings))
        {
            // No section at all is the strongest possible form of "binds nothing".
            return;
        }

        string[] bound = [.. bindings.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => !n.StartsWith("//", StringComparison.Ordinal))];

        Assert.True(
            bound.Length == 0,
            "The shipped appsettings.json binds " + string.Join(", ", bound)
            + ". Any binding here merges with the operator's own on the same key, producing an "
            + "entry with both Send and Action that the resolver rejects - which costs them the key "
            + "and reverts their edit on every restart.");
    }
}
