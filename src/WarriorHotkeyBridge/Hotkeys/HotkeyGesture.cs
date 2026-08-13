using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace WarriorHotkeyBridge.Hotkeys;

[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// A parsed global hotkey gesture, e.g. <c>F13</c> or <c>Ctrl+Alt+F13</c>.
/// </summary>
/// <remarks>
/// Parsing is deliberately separate from registration so the configured gestures can be
/// validated and unit tested without touching Win32 or the message loop.
/// </remarks>
internal readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, Keys Key)
{
    /// <summary>Human-readable form, used in logs and diagnostics.</summary>
    public string Display
    {
        get
        {
            List<string> parts = [];

            if (Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                parts.Add("Shift");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Windows))
            {
                parts.Add("Win");
            }

            parts.Add(Key.ToString());
            return string.Join('+', parts);
        }
    }

    /// <summary>
    /// The <c>fsModifiers</c> argument for <c>RegisterHotKey</c>.
    /// <see cref="NativeMethods.ModNoRepeat"/> is always included: a held key must produce
    /// exactly one command.
    /// </summary>
    public uint ToWin32Modifiers()
    {
        uint value = NativeMethods.ModNoRepeat;

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            value |= NativeMethods.ModAlt;
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            value |= NativeMethods.ModControl;
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            value |= NativeMethods.ModShift;
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            value |= NativeMethods.ModWin;
        }

        return value;
    }

    /// <summary>
    /// The virtual-key code. The low byte of <see cref="Keys"/> is by definition the VK code.
    /// </summary>
    public uint VirtualKeyCode => (uint)(Key & Keys.KeyCode);

    /// <summary>
    /// Parses a gesture such as <c>"F13"</c>, <c>"Ctrl+Alt+F13"</c> or <c>"ctrl + shift + 1"</c>.
    /// </summary>
    /// <returns>True on success; otherwise <paramref name="error"/> explains why.</returns>
    public static bool TryParse(
        string? text,
        out HotkeyGesture gesture,
        [NotNullWhen(false)] out string? error)
    {
        gesture = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The gesture is empty.";
            return false;
        }

        string[] tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            error = $"'{text}' does not contain a key.";
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;

        for (int i = 0; i < tokens.Length - 1; i++)
        {
            HotkeyModifiers? modifier = ParseModifier(tokens[i]);

            if (modifier is null)
            {
                error = $"'{tokens[i]}' in '{text}' is not a recognised modifier (use Ctrl, Alt, Shift or Win).";
                return false;
            }

            modifiers |= modifier.Value;
        }

        string keyToken = tokens[^1];

        // A trailing modifier means the gesture has no actual key, e.g. "Ctrl+Alt".
        if (ParseModifier(keyToken) is not null)
        {
            error = $"'{text}' ends with a modifier and specifies no key.";
            return false;
        }

        if (!TryParseKey(keyToken, out Keys key))
        {
            // Names the two vocabularies explicitly. The columns either side of this value use
            // different ones - Windows key names here, browser event.code names for what gets
            // sent - and F13 is spelled the same in both, so nothing on screen reveals the
            // difference until something is rejected.
            error = $"'{keyToken}' in '{text}' is not a recognised key name. Use a Windows key "
                + "name such as F13, D, NumPad5, Up or Escape. Browser names like KeyD, Digit1 "
                + "and ArrowUp are accepted too, but only where they name a real key.";

            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        error = null;
        return true;
    }

    private static HotkeyModifiers? ParseModifier(string token) => token.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HotkeyModifiers.Control,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" or "super" or "meta" => HotkeyModifiers.Windows,
        _ => null,
    };

    private static bool TryParseKey(string token, out Keys key)
    {
        // Bare digits are the one common case the Keys enum does not name intuitively:
        // the member is D1, not 1.
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = Keys.D0 + (token[0] - '0');
            return true;
        }

        if (!Enum.TryParse(token, ignoreCase: true, out key) || !Enum.IsDefined(key))
        {
            // Fall back to the browser vocabulary the Sends column uses, so KeyD, Digit1 and
            // ArrowUp work here as well. Tried second, so the Windows names keep their existing
            // meaning exactly and nothing that parsed before can change interpretation.
            if (WindowsKeyTranslator.TryFromBrowserName(token, out Keys aliased))
            {
                key = aliased;
                return true;
            }

            key = Keys.None;
            return false;
        }

        // Reject numeric input such as "124": it would parse but almost certainly reflects a
        // configuration mistake rather than an intended key name.
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            key = Keys.None;
            return false;
        }

        // A modifier used as the key itself can never be registered on its own.
        if (key is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu
            or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin)
        {
            key = Keys.None;
            return false;
        }

        return true;
    }
}
