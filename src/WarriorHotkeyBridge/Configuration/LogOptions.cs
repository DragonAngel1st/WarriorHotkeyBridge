using System.ComponentModel.DataAnnotations;

namespace WarriorHotkeyBridge.Configuration;

/// <summary>
/// Rolling-file logging settings. Named "Log" rather than "Logging" so it cannot be
/// confused with the Microsoft.Extensions.Logging configuration section.
/// </summary>
internal sealed class LogOptions
{
    public const string SectionName = "Log";

    /// <summary>Minimum level in normal mode. <c>--debug</c> always lowers this to Debug.</summary>
    [Required(AllowEmptyStrings = false)]
    public string MinimumLevel { get; init; } = "Information";

    /// <summary>Daily log files to keep.</summary>
    [Range(1, 365)]
    public int RetainedFileCountLimit { get; init; } = 14;

    /// <summary>Per-file cap, so a reconnect storm can never fill the disk.</summary>
    [Range(1_048_576, 1_073_741_824)]
    public long FileSizeLimitBytes { get; init; } = 32 * 1024 * 1024;
}
