using System.ComponentModel.DataAnnotations;

namespace WarriorHotkeyBridge.Configuration;

/// <summary>
/// Safety limits on the command path.
/// </summary>
internal sealed class CommandOptions
{
    public const string SectionName = "Commands";

    /// <summary>
    /// How long a press may wait before it is discarded instead of executed, in milliseconds.
    /// </summary>
    /// <remarks>
    /// A trading chord is only meaningful close to when it was pressed. If a slow DOM
    /// interaction stalls the queue, the operator will assume nothing happened and press again;
    /// without this limit the backlog then fires every one of those presses at a market that has
    /// since moved. Discarding a late command is recoverable — pressing again costs a second.
    /// Executing a stale one is not.
    /// </remarks>
    [Range(250, 60_000)]
    public int MaxCommandAgeMs { get; init; } = 5_000;
}
