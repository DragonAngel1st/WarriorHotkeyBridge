namespace WarriorHotkeyBridge.Configuration;

/// <summary>
/// Maps global hotkey gestures to what the bridge should do with them.
/// </summary>
internal sealed class HotkeyOptions
{
    public const string SectionName = "Hotkeys";

    /// <summary>
    /// Gesture -> binding. The gesture is a key name with optional modifiers, e.g. <c>"F13"</c>
    /// or <c>"Ctrl+Alt+F13"</c>.
    /// </summary>
    public Dictionary<string, HotkeyBindingConfig> Bindings { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How often, in seconds, to re-attempt hotkeys another application was holding.
    /// </summary>
    /// <remarks>
    /// A contested key is released by a person closing an application, so this is measured in
    /// tens of seconds rather than the health check's few. Retrying costs one Win32 call per
    /// failed key.
    /// </remarks>
    [System.ComponentModel.DataAnnotations.Range(5, 3600)]
    public int RetryIntervalSeconds { get; init; } = 30;

    public TimeSpan RetryInterval => TimeSpan.FromSeconds(RetryIntervalSeconds);
}

/// <summary>
/// What one hotkey does.
/// </summary>
/// <remarks>
/// Bound as loose strings on purpose: a typo should cost the operator that one hotkey plus an
/// actionable log line, not stop the bridge from starting. Validation happens in
/// <see cref="Hotkeys.HotkeyBindingResolver"/>, which can report every problem at once.
/// </remarks>
internal sealed class HotkeyBindingConfig
{
    /// <summary>
    /// The keyboard shortcut to deliver into the Level 2 component, e.g. <c>"Shift+1"</c>.
    /// This must match whatever you have configured inside Warrior SIM's own hotkey settings;
    /// the bridge only transports it and never interprets what it does.
    /// </summary>
    public string? Send { get; init; }

    /// <summary>
    /// A built-in action instead of sending a shortcut: <c>Test</c> or <c>Diagnostics</c>.
    /// Mutually exclusive with <see cref="Send"/>.
    /// </summary>
    public string? Action { get; init; }

    /// <summary>
    /// Free-text description shown in logs and the tray, e.g. "Buy 75% BP". Never interpreted.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Which Level 2 component to target when several are open, in document order.
    /// Defaults to the first, which is the single-panel case.
    /// </summary>
    public int Level2Index { get; init; }
}
