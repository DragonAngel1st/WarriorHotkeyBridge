using System.Diagnostics;

namespace WarriorHotkeyBridge.Models;

/// <summary>One command, on its way through the queue.</summary>
/// <param name="Source">
/// Where the command came from - the gesture display for a keypress, a short description for
/// anything started from the user interface. Logging only; nothing branches on it.
/// </param>
/// <param name="ReceivedTimestamp">
/// <see cref="Stopwatch.GetTimestamp"/> taken inside the window procedure, so measured latency
/// covers everything from the moment Windows handed us the key.
/// </param>
internal sealed record CommandRequest(
    HotkeyAction Action,
    string Source,
    long ReceivedTimestamp,
    long QueuedTimestamp)
{
    /// <summary>
    /// Set when the caller wants the outcome back, not merely for the command to happen.
    /// </summary>
    /// <remarks>
    /// This is what lets a window report the result of a command it started while the command
    /// itself still goes through the one queue every hotkey uses. Calling the executor directly
    /// instead would let, say, a targeting test overlap a trading command against the same page -
    /// which is the exact race the single-consumer queue exists to prevent.
    /// </remarks>
    public TaskCompletionSource<CommandResult>? Completion { get; init; }
}

/// <summary>
/// Where the time went. Each stage is measured separately so a regression can be attributed
/// rather than guessed at.
/// </summary>
internal sealed record CommandTimings
{
    /// <summary>Hotkey message received to enqueued.</summary>
    public TimeSpan ToQueue { get; init; }

    /// <summary>Enqueued to picked up by the worker.</summary>
    public TimeSpan QueueWait { get; init; }

    /// <summary>Verifying Chrome, the page and Level 2, including selecting Level 2.</summary>
    public TimeSpan Targeting { get; init; }

    /// <summary>Bringing the tab and the Chrome window to the front.</summary>
    public TimeSpan Activation { get; init; }

    /// <summary>The keyboard dispatch itself.</summary>
    public TimeSpan Dispatch { get; init; }

    public TimeSpan Total { get; init; }

    public string Describe() =>
        $"total {Total.TotalMilliseconds:0.#}ms (queue {ToQueue.TotalMilliseconds:0.#}+{QueueWait.TotalMilliseconds:0.#}, "
        + $"target {Targeting.TotalMilliseconds:0.#}, activate {Activation.TotalMilliseconds:0.#}, "
        + $"dispatch {Dispatch.TotalMilliseconds:0.#})";
}

internal sealed record CommandResult
{
    public required CommandOutcome Outcome { get; init; }

    public required string ActionDescription { get; init; }

    /// <summary>
    /// True once the keystroke has actually been handed to the page.
    /// </summary>
    /// <remarks>
    /// Load-bearing for safety: once this is true the command is never retried, because a
    /// Playwright call that timed out may still have delivered the keystroke, and repeating it
    /// could double an order.
    /// </remarks>
    public bool Dispatched { get; init; }

    public string? FailureReason { get; init; }

    public CommandTimings Timings { get; init; } = new();
}
