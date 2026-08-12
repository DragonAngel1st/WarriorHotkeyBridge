using System.Diagnostics.CodeAnalysis;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Hotkeys;

/// <summary>
/// Turns the configured gesture -> binding map into validated bindings.
/// </summary>
/// <remarks>
/// Pure and Win32-free so the whole mapping layer is unit testable without a message loop.
/// </remarks>
internal static class HotkeyBindingResolver
{
    /// <summary>
    /// The only values <see cref="HotkeyBindingConfig.Action"/> may name.
    /// </summary>
    /// <remarks>
    /// <see cref="HotkeyActionKind.SendKeys"/> is deliberately absent: it is selected by
    /// supplying <c>Send</c>, and accepting it here would allow a binding that claims to send
    /// keys while carrying none.
    /// </remarks>
    private static readonly Dictionary<string, HotkeyActionKind> NamedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(HotkeyActionKind.Test)] = HotkeyActionKind.Test,
        [nameof(HotkeyActionKind.Diagnostics)] = HotkeyActionKind.Diagnostics,
    };

    public static HotkeyBindingResolution Resolve(IReadOnlyDictionary<string, HotkeyBindingConfig> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);

        List<HotkeyBinding> bindings = [];
        List<string> problems = [];
        Dictionary<HotkeyGesture, string> claimed = [];

        foreach ((string gestureText, HotkeyBindingConfig config) in configured)
        {
            if (!HotkeyGesture.TryParse(gestureText, out HotkeyGesture gesture, out string? gestureError))
            {
                problems.Add($"Hotkey '{gestureText}' is invalid: {gestureError}");
                continue;
            }

            if (!TryResolveAction(gestureText, config, out HotkeyAction? action, out string? actionError))
            {
                problems.Add(actionError);
                continue;
            }

            // Two configuration keys can differ textually but mean the same gesture
            // ("F13" and "f13"), and Windows would only grant the first.
            if (claimed.TryGetValue(gesture, out string? existing))
            {
                problems.Add(
                    $"Hotkey '{gestureText}' duplicates '{existing}' ({gesture.Display}); the first binding is kept.");
                continue;
            }

            claimed[gesture] = gestureText;
            bindings.Add(new HotkeyBinding(gesture, action, gestureText));
        }

        return new HotkeyBindingResolution(bindings, problems);
    }

    private static bool TryResolveAction(
        string gestureText,
        HotkeyBindingConfig? config,
        [NotNullWhen(true)] out HotkeyAction? action,
        [NotNullWhen(false)] out string? error)
    {
        action = null;

        if (config is null)
        {
            error = $"Hotkey '{gestureText}' has no binding.";
            return false;
        }

        bool hasSend = !string.IsNullOrWhiteSpace(config.Send);
        bool hasAction = !string.IsNullOrWhiteSpace(config.Action);

        // Rejecting "both" rather than silently preferring one: a binding that specifies a
        // trading chord AND a no-op action is ambiguous, and guessing could either fire an
        // unintended order or silently swallow an intended one.
        if (hasSend && hasAction)
        {
            error = $"Hotkey '{gestureText}' sets both 'Send' and 'Action'; specify exactly one.";
            return false;
        }

        if (!hasSend && !hasAction)
        {
            error = $"Hotkey '{gestureText}' sets neither 'Send' nor 'Action'; specify exactly one.";
            return false;
        }

        if (config.Level2Index < 0)
        {
            error = $"Hotkey '{gestureText}' has a negative Level2Index ({config.Level2Index}).";
            return false;
        }

        if (hasSend)
        {
            if (!PlaywrightKeys.TryNormalize(config.Send, out string? keys, out string? keysError))
            {
                error = $"Hotkey '{gestureText}' has an invalid Send value '{config.Send}': {keysError}";
                return false;
            }

            action = new HotkeyAction
            {
                Kind = HotkeyActionKind.SendKeys,
                Keys = keys,
                Label = config.Label?.Trim(),
                Level2Index = config.Level2Index,
            };

            error = null;
            return true;
        }

        // Matched against an explicit table rather than Enum.TryParse, which would also accept
        // the underlying number ("1" -> Diagnostics), a signed number, and comma-separated
        // name lists whose values get OR-ed together even for a non-flags enum.
        if (!NamedActions.TryGetValue(config.Action!.Trim(), out HotkeyActionKind kind))
        {
            error = $"Hotkey '{gestureText}' names unknown action '{config.Action}'. "
                + $"Valid actions: {string.Join(", ", NamedActions.Keys)}. "
                + "To send a keyboard shortcut to Warrior SIM, use 'Send' instead.";
            return false;
        }

        action = new HotkeyAction
        {
            Kind = kind,
            Label = config.Label?.Trim(),
            Level2Index = config.Level2Index,
        };

        error = null;
        return true;
    }
}
