using System.Diagnostics.CodeAnalysis;

namespace WarriorHotkeyBridge.Hotkeys;

/// <summary>
/// Turns a keypress in a Windows dialog into the key expression Playwright will send.
/// </summary>
/// <remarks>
/// <para>
/// This exists to remove the single most confusing thing about configuring the bridge. Typing the
/// chord by hand means knowing that a physical Shift+1 is written <c>Shift+Digit1</c> and not
/// <c>Shift+1</c>, that Q is <c>KeyQ</c>, that 1 is <c>Digit1</c> - distinctions that are
/// documented in three places and still get typed wrong. Pressing the key produces the faithful
/// spelling automatically, because that spelling is derived from the physical key rather than from
/// the character it happens to produce.
/// </para>
/// <para>
/// Known limitation, and it is inherent rather than an oversight: the browser's
/// <c>KeyboardEvent.code</c> identifies a key by physical POSITION, while a Windows virtual key is
/// mapped through the active keyboard layout. On a US layout the two agree. On AZERTY the key
/// labelled A reports <c>Keys.A</c> but sits where a US keyboard has Q, so this would produce
/// <c>KeyA</c> where the browser will report <c>KeyQ</c>. Resolving that properly means going
/// through scan codes with MapVirtualKey. Until then, capture is a convenience and the text field
/// remains authoritative - which is also why capture never replaces typing.
/// </para>
/// </remarks>
internal static class WindowsKeyTranslator
{
    /// <summary>True when nothing but modifier keys are held, so there is no key to record yet.</summary>
    public static bool IsModifierOnly(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.None
            or Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.LWin or Keys.RWin;

    /// <summary>
    /// Converts a WinForms key, modifiers included, into a normalised expression.
    /// </summary>
    /// <returns>True on success; otherwise <paramref name="error"/> says why it cannot be sent.</returns>
    public static bool TryTranslate(
        Keys keyData,
        [NotNullWhen(true)] out string? expression,
        [NotNullWhen(false)] out string? error)
    {
        expression = null;

        if (IsModifierOnly(keyData))
        {
            error = "Only modifier keys are held.";
            return false;
        }

        Keys code = keyData & Keys.KeyCode;

        if (!TryMapKey(code, out string? key, out error))
        {
            return false;
        }

        List<string> parts = [];

        // Fixed order regardless of the order they were pressed in, so the same chord always
        // produces the same text and two identical bindings never look different.
        if (keyData.HasFlag(Keys.Control))
        {
            parts.Add("Control");
        }

        if (keyData.HasFlag(Keys.Alt))
        {
            parts.Add("Alt");
        }

        if (keyData.HasFlag(Keys.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(key);
        string candidate = string.Join('+', parts);

        // Run the result through the same validator the configuration uses. Capture must be
        // incapable of producing something the bridge would then reject - a convenience that can
        // emit invalid input is worse than no convenience.
        if (!PlaywrightKeys.TryNormalize(candidate, out string? normalized, out string? normalizeError))
        {
            error = normalizeError;
            return false;
        }

        expression = normalized;
        error = null;
        return true;
    }

    private static bool TryMapKey(Keys code, [NotNullWhen(true)] out string? key, [NotNullWhen(false)] out string? error)
    {
        key = null;

        if (code is >= Keys.D0 and <= Keys.D9)
        {
            key = "Digit" + (char)('0' + (code - Keys.D0));
        }
        else if (code is >= Keys.A and <= Keys.Z)
        {
            key = "Key" + (char)('A' + (code - Keys.A));
        }
        else if (code is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            key = "Numpad" + (char)('0' + (code - Keys.NumPad0));
        }
        else if (code is >= Keys.F1 and <= Keys.F12)
        {
            key = "F" + (1 + (code - Keys.F1));
        }
        else if (code is >= Keys.F13 and <= Keys.F24)
        {
            // Worth its own message rather than a generic rejection: these are exactly the keys a
            // Stream Deck sends, so an operator will press one here expecting it to work.
            error = $"F{13 + (code - Keys.F13)} is a bridge hotkey, not something that can be sent "
                + "into the page - the browser key map stops at F12. Put it in the Hotkey column "
                + "instead, and press the shortcut Warrior SIM expects here.";
            return false;
        }
        else if (!NamedKeys.TryGetValue(code, out key))
        {
            error = $"'{code}' has no browser key equivalent.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Everything that is not a contiguous range.
    /// </summary>
    /// <remarks>
    /// The OEM entries are the punctuation keys, whose Windows names describe their US-layout
    /// meaning and whose browser names describe their position. They agree on a US layout, which
    /// is the caveat in the class remarks made concrete.
    /// </remarks>
    private static readonly Dictionary<Keys, string> NamedKeys = new()
    {
        [Keys.Enter] = "Enter",
        [Keys.Tab] = "Tab",
        [Keys.Escape] = "Escape",
        [Keys.Space] = "Space",
        [Keys.Back] = "Backspace",
        [Keys.Delete] = "Delete",
        [Keys.Insert] = "Insert",
        [Keys.Home] = "Home",
        [Keys.End] = "End",
        [Keys.PageUp] = "PageUp",
        [Keys.PageDown] = "PageDown",
        [Keys.Left] = "ArrowLeft",
        [Keys.Right] = "ArrowRight",
        [Keys.Up] = "ArrowUp",
        [Keys.Down] = "ArrowDown",
        [Keys.CapsLock] = "CapsLock",
        [Keys.NumLock] = "NumLock",
        [Keys.Scroll] = "ScrollLock",
        [Keys.Pause] = "Pause",
        [Keys.PrintScreen] = "PrintScreen",
        [Keys.Apps] = "ContextMenu",

        [Keys.Multiply] = "NumpadMultiply",
        [Keys.Add] = "NumpadAdd",
        [Keys.Subtract] = "NumpadSubtract",
        [Keys.Divide] = "NumpadDivide",
        [Keys.Decimal] = "NumpadDecimal",

        [Keys.OemSemicolon] = "Semicolon",   // Oem1
        [Keys.Oemplus] = "Equal",
        [Keys.Oemcomma] = "Comma",
        [Keys.OemMinus] = "Minus",
        [Keys.OemPeriod] = "Period",
        [Keys.OemQuestion] = "Slash",        // Oem2
        [Keys.Oemtilde] = "Backquote",       // Oem3
        [Keys.OemOpenBrackets] = "BracketLeft",   // Oem4
        [Keys.OemPipe] = "Backslash",             // Oem5
        [Keys.OemCloseBrackets] = "BracketRight", // Oem6
        [Keys.OemQuotes] = "Quote",               // Oem7
        [Keys.OemBackslash] = "IntlBackslash",    // Oem102
    };

    /// <summary>
    /// Plain-English description of what a captured expression will actually deliver.
    /// </summary>
    /// <remarks>
    /// Shown beside the captured value so the operator can confirm it is the chord they meant,
    /// without having to know that Digit1 with Shift produces an exclamation mark.
    /// </remarks>
    public static string Describe(string expression)
    {
        string[] tokens = expression.Split('+');
        string key = tokens[^1];
        bool shift = tokens.AsSpan(0, tokens.Length - 1).Contains("Shift");

        string plain = key switch
        {
            ['D', 'i', 'g', 'i', 't', var d] => shift ? $"Shift and {d} - delivers '{ShiftedDigit(d)}'" : $"the {d} key",
            ['K', 'e', 'y', var c] => shift ? $"Shift and {c}" : $"the {c} key",
            ['N', 'u', 'm', 'p', 'a', 'd', var d] when char.IsAsciiDigit(d) => $"numpad {d}",
            _ => $"the {key} key",
        };

        string modifiers = tokens.Length > 1
            ? string.Join(" + ", tokens[..^1]) + " + "
            : string.Empty;

        return $"{modifiers}{plain}";
    }

    /// <summary>What a US layout produces for Shift plus a digit.</summary>
    private static char ShiftedDigit(char digit) => digit switch
    {
        '1' => '!',
        '2' => '@',
        '3' => '#',
        '4' => '$',
        '5' => '%',
        '6' => '^',
        '7' => '&',
        '8' => '*',
        '9' => '(',
        '0' => ')',
        _ => digit,
    };
}
