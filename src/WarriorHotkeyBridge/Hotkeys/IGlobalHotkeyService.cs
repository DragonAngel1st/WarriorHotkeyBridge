using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Hotkeys;

/// <summary>A hotkey registration attempt and its outcome.</summary>
internal sealed record HotkeyRegistration(
    int Id,
    HotkeyGesture Gesture,
    HotkeyAction Action,
    bool Succeeded,
    string? Error)
{
    /// <summary>
    /// Rendered once at registration. The keypress path logs the gesture and action on every
    /// press, and formatting them there would allocate on the latency-critical path.
    /// </summary>
    public string GestureDisplay { get; } = Gesture.Display;

    /// <inheritdoc cref="GestureDisplay"/>
    public string ActionDescription { get; } = Action.Describe();
}

internal sealed class HotkeyPressedEventArgs(HotkeyRegistration registration, long receivedTimestamp) : EventArgs
{
    public HotkeyRegistration Registration { get; } = registration;

    /// <summary>
    /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> captured the instant the message
    /// was received, so later phases can measure true hotkey-to-dispatch latency rather than
    /// starting the clock once the command is already being processed.
    /// </summary>
    public long ReceivedTimestamp { get; } = receivedTimestamp;
}

/// <summary>
/// Registers the configured global hotkeys and reports presses.
/// </summary>
/// <remarks>
/// Intentionally knows nothing about Chrome, Playwright or command execution: it resolves a
/// press to a <see cref="HotkeyAction"/> and raises an event. Phase 5 attaches the command
/// queue to that event.
/// </remarks>
internal interface IGlobalHotkeyService
{
    /// <summary>Every registration attempted, successful or not. Used by diagnostics.</summary>
    IReadOnlyList<HotkeyRegistration> Registrations { get; }

    /// <summary>Raised on the UI thread. Handlers must not block.</summary>
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>Registers all configured hotkeys. Must be called on the UI thread.</summary>
    void RegisterAll();

    /// <summary>Releases all hotkeys. Must be called on the UI thread that registered them.</summary>
    void UnregisterAll();
}
