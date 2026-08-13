using System.Text.Json;
using System.Text.Json.Nodes;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Configuration;

internal interface IUserConfigurationWriter
{
    /// <summary>
    /// Writes the hotkey bindings into the user configuration file, leaving everything else in it
    /// exactly as it was.
    /// </summary>
    /// <returns>Null on success, or a message describing why it could not be written.</returns>
    string? TryWriteBindings(IReadOnlyDictionary<string, HotkeyBindingConfig> bindings);
}

/// <summary>
/// Edits the operator's configuration file surgically.
/// </summary>
/// <remarks>
/// <para>
/// Serialising a bound options object back over the file would be simpler and wrong. That file is
/// hand-maintained: it carries "//" documentation keys, an optional Chrome block, and whatever
/// else the operator has put there. Round-tripping through the strongly-typed model would silently
/// delete every setting this application does not happen to know about, and rewrite the rest in an
/// order and style the operator did not choose.
/// </para>
/// <para>
/// So the file is parsed as a JSON tree, the single <c>Hotkeys:Bindings</c> node is replaced, and
/// the tree is written back. Everything the editor does not own survives untouched.
/// </para>
/// </remarks>
internal sealed class UserConfigurationWriter : IUserConfigurationWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // The file is read by people. An escaped forward slash or a ' in a label is noise.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly AppPaths _paths;

    public UserConfigurationWriter(AppPaths paths) => _paths = paths;

    public string? TryWriteBindings(IReadOnlyDictionary<string, HotkeyBindingConfig> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        try
        {
            JsonObject root = ReadOrCreateRoot();

            if (root["Hotkeys"] is not JsonObject hotkeys)
            {
                hotkeys = [];
                root["Hotkeys"] = hotkeys;
            }

            hotkeys["Bindings"] = BuildBindings(bindings);

            Directory.CreateDirectory(_paths.Configuration);

            // Written to a sibling file and then moved into place. A crash or a full disk midway
            // through rewriting the real file would leave it truncated, and a truncated
            // configuration file is one the bridge refuses to start with - which for a trading
            // tool means discovering it at the worst moment. File.Move with overwrite is atomic
            // enough on NTFS for this purpose.
            string temporary = _paths.UserConfigFile + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(WriteOptions));
            File.Move(temporary, _paths.UserConfigFile, overwrite: true);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ex.Message;
        }
    }

    /// <remarks>
    /// A file that exists but does not parse is not overwritten. It is the operator's, it may hold
    /// settings this editor never showed them, and silently replacing it would destroy work whose
    /// only fault is a trailing comma. They are told to fix or move it instead.
    /// </remarks>
    private JsonObject ReadOrCreateRoot()
    {
        if (!File.Exists(_paths.UserConfigFile))
        {
            return [];
        }

        string existing = File.ReadAllText(_paths.UserConfigFile);

        if (string.IsNullOrWhiteSpace(existing))
        {
            return [];
        }

        JsonNode? parsed = JsonNode.Parse(existing, NodeOptions, DocumentOptions);

        return parsed as JsonObject
            ?? throw new JsonException(
                "The configuration file does not contain a JSON object. Fix or move "
                + $"{_paths.UserConfigFile} and try again.");
    }

    private static JsonObject BuildBindings(IReadOnlyDictionary<string, HotkeyBindingConfig> bindings)
    {
        var result = new JsonObject();

        // Ordered so a hand-inspected file and a diff both read predictably; a dictionary's
        // enumeration order is not something to expose in a file people read.
        foreach ((string gesture, HotkeyBindingConfig binding) in bindings.OrderBy(b => b.Key, HotkeyGestureOrder.Instance))
        {
            var entry = new JsonObject();

            if (!string.IsNullOrWhiteSpace(binding.Send))
            {
                entry["Send"] = binding.Send;
            }

            if (!string.IsNullOrWhiteSpace(binding.Action))
            {
                entry["Action"] = binding.Action;
            }

            if (!string.IsNullOrWhiteSpace(binding.Label))
            {
                entry["Label"] = binding.Label;
            }

            // Only when it is not the default, so the common single-panel case stays uncluttered.
            if (binding.Level2Index != 0)
            {
                entry["Level2Index"] = binding.Level2Index;
            }

            result[gesture] = entry;
        }

        return result;
    }
}

/// <summary>
/// Orders gestures the way a keyboard does: F2 before F10, not after it.
/// </summary>
internal sealed class HotkeyGestureOrder : IComparer<string>
{
    public static readonly HotkeyGestureOrder Instance = new();

    private HotkeyGestureOrder()
    {
    }

    public int Compare(string? x, string? y)
    {
        (string modifiersX, int numberX) = Split(x);
        (string modifiersY, int numberY) = Split(y);

        int byModifiers = string.Compare(modifiersX, modifiersY, StringComparison.OrdinalIgnoreCase);

        if (byModifiers != 0)
        {
            return byModifiers;
        }

        if (numberX != numberY && numberX >= 0 && numberY >= 0)
        {
            return numberX.CompareTo(numberY);
        }

        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits "Ctrl+F13" into its modifier prefix and the trailing function-key number.</summary>
    private static (string Modifiers, int Number) Split(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return (string.Empty, -1);
        }

        int lastPlus = gesture.LastIndexOf('+');
        string modifiers = lastPlus < 0 ? string.Empty : gesture[..lastPlus];
        ReadOnlySpan<char> key = gesture.AsSpan(lastPlus + 1).Trim();

        return key.Length > 1 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out int number)
            ? (modifiers, number)
            : (modifiers, -1);
    }
}
