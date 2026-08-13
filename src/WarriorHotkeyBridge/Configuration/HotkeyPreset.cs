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

    private readonly AppPaths _paths;

    public HotkeyPresetProvider(AppPaths paths) => _paths = paths;

    /// <summary>Where the operator's own presets go. Created on demand, not at startup.</summary>
    public string UserPresetDirectory => Path.Combine(_paths.Root, FolderName);

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
