using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Hotkeys;

/// <summary>A configured gesture resolved to an action.</summary>
/// <param name="ConfiguredGesture">The raw configuration key, kept for error messages.</param>
internal sealed record HotkeyBinding(HotkeyGesture Gesture, HotkeyAction Action, string ConfiguredGesture);

/// <summary>
/// Outcome of resolving the configured bindings.
/// </summary>
/// <remarks>
/// Problems are collected rather than thrown: one mistyped entry should cost the operator that
/// one hotkey and a clear log line, not the whole bridge.
/// </remarks>
internal sealed record HotkeyBindingResolution(
    IReadOnlyList<HotkeyBinding> Bindings,
    IReadOnlyList<string> Problems);
