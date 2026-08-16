using System.Text.Json;
using System.Text.Json.Serialization;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Configuration;

/// <summary>
/// A named, ready-made set of hotkey bindings the operator can load in one step.
/// </summary>
internal sealed record HotkeyPreset
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Shown under the name in the editor, e.g. who it came from and what it assumes.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("bindings")]
    public Dictionary<string, HotkeyBindingConfig> Bindings { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False for presets shipped with the product, which must not be edited in place.</summary>
    [JsonIgnore]
    public bool IsUserSupplied { get; init; }

    [JsonIgnore]
    public string? SourceFile { get; init; }
}

internal interface IHotkeyPresetProvider
{
    /// <summary>Every preset found, shipped ones first. Never throws; unreadable files are skipped.</summary>
    IReadOnlyList<HotkeyPreset> Load();

    /// <summary>Where the operator's own presets are kept.</summary>
    string UserPresetDirectory { get; }

    /// <summary>
    /// Saves the bindings as a named preset the operator owns.
    /// </summary>
    /// <param name="overwrite">
    /// False makes an existing preset of the same name an error rather than a silent replacement.
    /// </param>
    /// <returns>Null on success, or a message describing why it could not be saved.</returns>
    string? TrySave(
        string name,
        string? description,
        IReadOnlyDictionary<string, HotkeyBindingConfig> bindings,
        bool overwrite);

    /// <summary>Whether a preset of this name already exists, and whether it may be replaced.</summary>
    (bool Exists, bool IsShipped) Describe(string name);
}

/// <summary>
/// Finds presets on disk rather than compiling them in.
/// </summary>
/// <remarks>
/// <para>
/// Presets are data, so they live in files. Adding one is dropping in a JSON file rather than
/// changing code and shipping a release, which matters because the useful presets are other
/// people's - a SIM's factory defaults, a trader's own layout - and none of them are knowable
/// when the code is written.
/// </para>
/// <para>
/// Two locations, and the distinction is deliberate. Those beside the executable ship with the
/// product and are replaced on upgrade, so they are read-only in the editor. Those under the
/// operator's data folder are theirs, survive upgrades, and can be shared by sending a file.
/// </para>
/// </remarks>
internal sealed class HotkeyPresetProvider : IHotkeyPresetProvider
{
    private const string FolderName = "Presets";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <remarks>
    /// Indented and relaxed-escaped because these files are meant to be opened, read and shared
    /// by people - an escaped apostrophe in "Ross's" helps nobody.
    /// </remarks>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AppPaths _paths;

    public HotkeyPresetProvider(AppPaths paths) => _paths = paths;

    /// <summary>Where the operator's own presets go. Created on demand, not at startup.</summary>
    public string UserPresetDirectory => _paths.Presets;

    public IReadOnlyList<HotkeyPreset> Load()
    {
        List<HotkeyPreset> presets = [];

        presets.AddRange(LoadFrom(Path.Combine(_paths.InstallDirectory, FolderName), userSupplied: false));
        presets.AddRange(LoadFrom(UserPresetDirectory, userSupplied: true));

        return presets;
    }

    private static List<HotkeyPreset> LoadFrom(string directory, bool userSupplied)
    {
        List<HotkeyPreset> presets = [];

        try
        {
            if (!Directory.Exists(directory))
            {
                return presets;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                HotkeyPreset? preset = TryRead(file, userSupplied);

                if (preset is not null)
                {
                    presets.Add(preset);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Presets are a convenience. An unreadable directory means the operator sees a
            // shorter list, never a bridge that fails to start.
        }

        return presets;
    }

    public (bool Exists, bool IsShipped) Describe(string name)
    {
        HotkeyPreset? match = Load()
            .FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        return match is null ? (false, false) : (true, !match.IsUserSupplied);
    }

    public string? TrySave(
        string name,
        string? description,
        IReadOnlyDictionary<string, HotkeyBindingConfig> bindings,
        bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        name = name?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            return "A preset needs a name.";
        }

        if (bindings.Count == 0)
        {
            return "A preset with no hotkeys would wipe the operator's keys when loaded.";
        }

        (bool exists, bool isShipped) = Describe(name);

        // Shipped presets are replaced wholesale on upgrade, so a user preset that shadowed one
        // would appear to work and then silently revert. Rejecting the name is clearer than
        // allowing a duplicate the list cannot distinguish.
        if (exists && isShipped)
        {
            return $"\"{name}\" is a preset that ships with the application. Choose a different name.";
        }

        if (exists && !overwrite)
        {
            return $"A preset named \"{name}\" already exists.";
        }

        try
        {
            Directory.CreateDirectory(UserPresetDirectory);

            string path = ResolveFilePath(name);

            var preset = new HotkeyPreset
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Bindings = new Dictionary<string, HotkeyBindingConfig>(bindings, StringComparer.OrdinalIgnoreCase),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(preset, WriteOptions));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Turns a display name into a file path inside the preset directory.
    /// </summary>
    /// <remarks>
    /// The name is typed by a person into a dialog, so it is untrusted input being turned into a
    /// path. Everything outside a small safe set is stripped rather than escaped, and the result
    /// is then checked to be a direct child of the preset directory - so a name like
    /// <c>..\..\startup</c> cannot write outside it, however the stripping is later changed.
    /// </remarks>
    private string ResolveFilePath(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        int length = 0;

        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
            else if ((c is ' ' or '-' or '_') && length > 0 && buffer[length - 1] != '-')
            {
                buffer[length++] = '-';
            }
        }

        string slug = new string(buffer[..length]).Trim('-');

        if (slug.Length == 0)
        {
            slug = "preset";
        }

        string directory = Path.GetFullPath(UserPresetDirectory);
        string path = Path.GetFullPath(Path.Combine(directory, slug + ".json"));

        // Belt and braces. The slug cannot contain a separator today; this makes that a checked
        // property rather than a thing a future edit could quietly remove.
        if (!string.Equals(Path.GetDirectoryName(path), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' does not produce a usable file name.", nameof(name));
        }

        return path;
    }

    private static HotkeyPreset? TryRead(string file, bool userSupplied)
    {
        try
        {
            HotkeyPreset? preset = JsonSerializer.Deserialize<HotkeyPreset>(File.ReadAllText(file), ReadOptions);

            // A preset with no name cannot be offered in a list, and one with no bindings would
            // silently wipe the operator's keys if they selected it.
            if (preset is null || string.IsNullOrWhiteSpace(preset.Name) || preset.Bindings.Count == 0)
            {
                return null;
            }

            return preset with { IsUserSupplied = userSupplied, SourceFile = file };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
