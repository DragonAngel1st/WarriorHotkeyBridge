using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Remembers whether the operator has ever been asked about starting with Windows.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the installer does not have to write the Run key itself. An MSI that installs
/// a registry value <em>owns</em> it, so a later repair or upgrade would restore a value the
/// operator had deliberately switched off - which contradicts the requirement that upgrades
/// preserve preferences. Letting the application register itself once, and recording that it
/// has done so, keeps a single owner and makes the tray toggle authoritative.
/// </para>
/// <para>
/// Stored under %LOCALAPPDATA%, so it survives upgrade and uninstall alike.
/// </para>
/// </remarks>
internal sealed record StartupPreference
{
    /// <summary>What the operator last chose, or what the first run chose on their behalf.</summary>
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; init; }

    [JsonPropertyName("decidedAt")]
    public DateTimeOffset DecidedAt { get; init; }

    /// <summary>
    /// Product version in effect when the choice was made, or null for a preference written
    /// before this field existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes "ask again after an update" possible without nagging. Comparing the
    /// stored version against the running one distinguishes "the operator turned startup off and
    /// nothing has changed since" from "the operator turned it off, and an update has landed
    /// since then" - only the second is worth a question, and only once per update.
    /// </para>
    /// <para>
    /// Null is treated as "unknown, do not ask": a preference recorded by an older build carries
    /// no information about which version it referred to, so prompting on it would fire once for
    /// every existing user purely because the field was added.
    /// </para>
    /// </remarks>
    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; init; }
}

internal interface IStartupPreferenceStore
{
    /// <summary>The stored preference, or null when there is none or it cannot be read.</summary>
    StartupPreference? Read();

    /// <summary>
    /// Whether a preference file is present, regardless of whether it can be parsed.
    /// </summary>
    /// <remarks>
    /// Paired with <see cref="Read"/> to tell "never decided" from "decided, but the record is
    /// damaged". Both return null from Read, and the two must not be handled alike: the first
    /// invites the bridge to choose on the operator's behalf, while the second would let a
    /// half-written file silently reverse a choice they made.
    /// </remarks>
    bool Exists { get; }

    void Write(StartupPreference preference);
}

internal sealed class StartupPreferenceStore : IStartupPreferenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;

    public StartupPreferenceStore(AppPaths paths) =>
        _path = Path.Combine(paths.State, "startup.json");

    public bool Exists
    {
        get
        {
            try
            {
                return File.Exists(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unable to tell. Reporting true is the cautious answer: it keeps the caller on
                // the path that changes nothing rather than the one that enables startup.
                return true;
            }
        }
    }

    public StartupPreference? Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<StartupPreference>(File.ReadAllText(_path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable preference is treated as "never decided". The worst outcome is that
            // startup gets enabled once more, which is recoverable from the tray.
            return null;
        }
    }

    public void Write(StartupPreference preference)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(preference, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the record only means the next launch may re-offer startup; not worth
            // failing a command path or startup over.
        }
    }
}
