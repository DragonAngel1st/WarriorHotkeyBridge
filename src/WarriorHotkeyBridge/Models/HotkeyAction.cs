namespace WarriorHotkeyBridge.Models;

/// <summary>
/// What a hotkey does once the Level 2 component has been located and selected.
/// </summary>
internal enum HotkeyActionKind
{
    /// <summary>
    /// Deliver a keyboard shortcut into the page. The meaning of that shortcut belongs to
    /// Warrior SIM's own hotkey settings, not to this application.
    /// </summary>
    SendKeys,

    /// <summary>Run the full targeting pipeline and report timing. Sends nothing. Always safe.</summary>
    Test,

    /// <summary>Write a diagnostic report to the log. Sends nothing.</summary>
    Diagnostics,
}

/// <summary>
/// The payload of a hotkey binding.
/// </summary>
/// <remarks>
/// <para>
/// This bridge is deliberately a transport, not a trading model. It does not know what
/// "buy 75% of buying power" means and must not pretend to: that mapping lives in the SIM's
/// own hotkey configuration, where the operator already maintains it. Encoding trade
/// semantics here would mean two sources of truth that can silently disagree.
/// </para>
/// <para>
/// What the bridge does own is everything Warrior cannot do for itself - delivering the chord
/// to the right page and the right Level 2 component while Windows focus is somewhere else
/// entirely.
/// </para>
/// </remarks>
internal sealed record HotkeyAction
{
    public required HotkeyActionKind Kind { get; init; }

    /// <summary>
    /// Normalised Playwright key expression, e.g. <c>"Shift+1"</c>. Non-null exactly when
    /// <see cref="Kind"/> is <see cref="HotkeyActionKind.SendKeys"/>.
    /// </summary>
    public string? Keys { get; init; }

    /// <summary>
    /// Operator-supplied description, e.g. "Buy 75% BP". Purely for logs and the tray menu -
    /// the bridge never interprets it.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Which Level 2 component to target when more than one is open, in document order.
    /// Zero is the first, which matches the single-panel case.
    /// </summary>
    public int Level2Index { get; init; }

    /// <summary>True when firing this action puts input into the page.</summary>
    public bool DispatchesInput => Kind is HotkeyActionKind.SendKeys;

    /// <summary>
    /// True when firing this action should bring the SIM tab and its window to the front.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Includes <see cref="HotkeyActionKind.Test"/>, which sends nothing. That is the whole point
    /// of it: Test exists to rehearse what a trading key does, so the operator can confirm the
    /// path works without risking an order. A rehearsal that quietly skips a step is worse than
    /// none, because it reports success for a path it never took - and raising the window is
    /// exactly the step most likely to fail on a given machine, since it depends on window
    /// matching across monitors with different DPI scaling.
    /// </para>
    /// <para>
    /// Excludes <see cref="HotkeyActionKind.Diagnostics"/>. That writes a report and has no
    /// relationship to the trading window; pulling Chrome in front of whatever the operator was
    /// reading would be an unrelated side effect.
    /// </para>
    /// </remarks>
    public bool ActivatesWindow => Kind is HotkeyActionKind.SendKeys or HotkeyActionKind.Test;

    /// <summary>Short form for logs and the tray, e.g. <c>Shift+1 (Buy 75% BP)</c>.</summary>
    public string Describe() => Kind switch
    {
        HotkeyActionKind.SendKeys when !string.IsNullOrWhiteSpace(Label) => $"{Keys} ({Label})",
        HotkeyActionKind.SendKeys => Keys ?? "send",
        _ when !string.IsNullOrWhiteSpace(Label) => $"{Kind} ({Label})",
        _ => Kind.ToString(),
    };
}
