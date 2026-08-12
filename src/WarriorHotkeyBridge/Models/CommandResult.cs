using System.Diagnostics;
using WarriorHotkeyBridge.Hotkeys;

namespace WarriorHotkeyBridge.Models;

/// <summary>One hotkey press, on its way through the queue.</summary>
/// <param name="ReceivedTimestamp">
/// <see cref="Stopwatch.GetTimestamp"/> taken inside the window procedure, so measured latency
/// covers everything from the moment Windows handed us the key.
/// </param>
internal sealed record CommandRequest(
    HotkeyRegistration Registration,
    long ReceivedTimestamp,
    long QueuedTimestamp)
{
    public HotkeyAction Action => Registration.Action;
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
