using System.Diagnostics.CodeAnalysis;

namespace WarriorHotkeyBridge.Hotkeys;

/// <summary>
/// Validates and normalises the key expression that will be handed to Playwright's
/// <c>Keyboard.PressAsync</c>.
/// </summary>
/// <remarks>
/// Only the modifier names are rewritten. The key token itself is passed through untouched,
/// because Playwright accepts several equally valid spellings (<c>1</c>, <c>Digit1</c>,
/// <c>Enter</c>, <c>F1</c>) and which one a given web application actually reacts to depends
/// on whether it inspects <c>event.key</c> or <c>event.code</c>. Guessing here would silently
/// break shortcuts; Phase 6 settles it against the real page.
/// </remarks>
internal static class PlaywrightKeys
{
    /// <summary>Playwright's modifier names, plus the spellings operators actually type.</summary>
    private static readonly Dictionary<string, string> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = "Control",
        ["control"] = "Control",
        ["alt"] = "Alt",
        ["option"] = "Alt",
        ["shift"] = "Shift",
        ["meta"] = "Meta",
        ["cmd"] = "Meta",
        ["command"] = "Meta",
        ["win"] = "Meta",
        ["windows"] = "Meta",
    };

    /// <summary>
    /// Rewrites <c>"ctrl+shift+1"</c> to <c>"Control+Shift+1"</c>.
    /// </summary>
    /// <returns>True on success; otherwise <paramref name="error"/> explains why.</returns>
    public static bool TryNormalize(
        string? expression,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "The key expression is empty.";
            return false;
        }

        string[] tokens = expression.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            error = $"'{expression}' does not contain a key.";
            return false;
        }

        List<string> parts = new(tokens.Length);

        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (!ModifierAliases.TryGetValue(tokens[i], out string? modifier))
            {
                error = $"'{tokens[i]}' in '{expression}' is not a recognised modifier "
                    + "(use Ctrl, Alt, Shift or Meta).";
                return false;
            }

            parts.Add(modifier);
        }

        string key = tokens[^1];

        // A trailing modifier means there is no key to press.
        if (ModifierAliases.ContainsKey(key))
        {
            error = $"'{expression}' ends with a modifier and specifies no key.";
            return false;
        }

        if (!IsKnownKey(key))
        {
            // Rejected at configuration time rather than at dispatch. Playwright validates key
            // names only when the key is actually pressed, so without this an unusable chord
            // registers cleanly and then fails the first time it matters.
            error = key.StartsWith('F') && key.Length > 1 && char.IsAsciiDigit(key[1])
                ? $"'{key}' is not a key Playwright can send. Function keys above F12 do not exist "
                    + "in the browser key map - F13-F24 work as bridge hotkeys but cannot be sent "
                    + "into the page."
                : $"'{key}' is not a recognised key name. Use a single character (e.g. 1, a) or a "
                    + "key name such as Digit1, KeyA, F1, Enter, Tab, Escape, ArrowUp, Space.";

            return false;
        }

        parts.Add(key);
        normalized = string.Join('+', parts);
        error = null;
        return true;
    }

    /// <summary>
    /// Key names Playwright accepts, beyond any single character.
    /// </summary>
    /// <remarks>
    /// Mirrors the browser's <c>KeyboardEvent.code</c> vocabulary. Notably it stops at F12:
    /// F13-F24 exist as Windows virtual keys - which is exactly why they make good Stream Deck
    /// hotkeys - but have no browser key mapping, so they cannot be sent into a page.
    /// </remarks>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "Backquote", "Minus", "Equal", "Backslash", "Backspace", "Tab", "Delete", "Escape",
        "ArrowDown", "ArrowLeft", "ArrowRight", "ArrowUp", "End", "Enter", "Home", "Insert",
        "PageDown", "PageUp", "Space", "CapsLock", "NumLock", "ScrollLock", "Pause",
        "PrintScreen", "ContextMenu", "IntlBackslash", "Semicolon", "Quote", "Comma", "Period",
        "Slash", "BracketLeft", "BracketRight",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Digit0", "Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "Digit8", "Digit9",
        "KeyA", "KeyB", "KeyC", "KeyD", "KeyE", "KeyF", "KeyG", "KeyH", "KeyI", "KeyJ", "KeyK",
        "KeyL", "KeyM", "KeyN", "KeyO", "KeyP", "KeyQ", "KeyR", "KeyS", "KeyT", "KeyU", "KeyV",
        "KeyW", "KeyX", "KeyY", "KeyZ",
        "Numpad0", "Numpad1", "Numpad2", "Numpad3", "Numpad4", "Numpad5", "Numpad6", "Numpad7",
        "Numpad8", "Numpad9", "NumpadAdd", "NumpadSubtract", "NumpadMultiply", "NumpadDivide",
        "NumpadDecimal", "NumpadEnter",
    };

    /// <summary>A single character, or one of the names the browser key map defines.</summary>
    private static bool IsKnownKey(string key) => key.Length == 1 || KnownKeys.Contains(key);

    /// <summary>
    /// Flags the Shift-plus-single-character trap, or null when the expression is unambiguous.
    /// </summary>
    /// <remarks>
    /// Measured against Chrome: Playwright treats a bare character as "produce exactly this
    /// character", but a named code as "press that physical key" and applies the layout. So
    /// <c>Shift+1</c> delivers <c>event.key === "1"</c> with the shift flag set, while
    /// <c>Shift+Digit1</c> delivers <c>event.key === "!"</c> - and a real keyboard produces
    /// <c>"!"</c>. Both give <c>event.code === "Digit1"</c>.
    /// <para>
    /// Either can be correct depending on what the page inspects, so this warns rather than
    /// rewrites: silently changing which character a trading shortcut delivers would be far
    /// worse than an explanatory log line.
    /// </para>
    /// </remarks>
    public static string? DescribeAmbiguity(string normalizedExpression)
    {
        string[] tokens = normalizedExpression.Split('+');
        string key = tokens[^1];
        bool hasShift = tokens.Length > 1 && tokens.AsSpan(0, tokens.Length - 1).Contains("Shift");

        // An unshifted numpad key carries its *navigation* meaning, exactly as it does on a real
        // keyboard with NumLock off: Numpad1 arrives as End, Numpad2 as ArrowDown, Numpad5 as
        // Clear. A binding written as "Numpad1" therefore never delivers a 1, and worse, the
        // navigation key it does deliver may itself be bound to something in the Level 2 grid.
        if (!hasShift && NumpadNavigationEquivalents.TryGetValue(key, out string? navigation))
        {
            return $"'{key}' delivers event.key='{navigation}', not a digit, because an unshifted "
                + $"numpad key carries its navigation meaning. Use 'Digit{key[^1]}' for the digit, "
                + $"or 'Shift+{key}' if Warrior SIM really is bound to the numpad key itself.";
        }

        if (!hasShift || key.Length != 1 || !char.IsAsciiDigit(key[0]))
        {
            return null;
        }

        return $"'{normalizedExpression}' delivers event.key='{key}' with Shift held, which a real "
            + $"keyboard never produces. If Warrior SIM does not respond, try 'Shift+Digit{key}' "
            + "instead, which reproduces a physical keypress exactly.";
    }

    /// <summary>What each unshifted numpad key actually reports as <c>event.key</c>.</summary>
    private static readonly Dictionary<string, string> NumpadNavigationEquivalents = new(StringComparer.Ordinal)
    {
        ["Numpad0"] = "Insert",
        ["Numpad1"] = "End",
        ["Numpad2"] = "ArrowDown",
        ["Numpad3"] = "PageDown",
        ["Numpad4"] = "ArrowLeft",
        ["Numpad5"] = "Clear",
        ["Numpad6"] = "ArrowRight",
        ["Numpad7"] = "Home",
        ["Numpad8"] = "ArrowUp",
        ["Numpad9"] = "PageUp",
        ["NumpadDecimal"] = "Delete",
    };
}
